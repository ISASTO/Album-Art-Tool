using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Lists songs without a Title tag and songs with more than one embedded picture.
    // Works from the main window's scan, so it opens instantly and never changes a file.
    internal sealed class LibraryCheckWindow : Window
    {
        private readonly Func<Album[]> currentAlbums;
        private readonly Action<ProcessStartInfo> openFolder;
        private readonly Func<Task> fillTitles, fixPictures;
        private readonly Func<LibraryCheckResult, Task> replaceLow;
        private readonly Button replace = Kit.Button("Replace from YouTube Music…", "primary");
        private readonly Button fill = Kit.Button("Fill titles from filenames…"), onePicture = Kit.Button("Fix embedded pictures…");
        private readonly ListBox list = new ListBox { Classes = { "grid" } };
        private readonly TextBlock summary = Kit.Label("", Kit.Body);
        private readonly ComboBox threshold = new ComboBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button refresh = Kit.Button("Check again", "primary"), save = Kit.Button("Save report…"), show = Kit.Button("Show selected");
        internal LibraryCheckResult Result { get; private set; }
        internal IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
        /// <summary>The bitrate measurement started by the last check (completed when there is nothing to measure).</summary>
        internal Task Verification { get; private set; } = Task.CompletedTask;
        private System.Threading.CancellationTokenSource measuring;
        private string measureStatus = "";

        internal sealed class Row
        {
            internal string Text, Detail, Path, Tip;
            internal bool Header, Folder;
        }

        internal LibraryCheckWindow(Func<Album[]> albums, Action<ProcessStartInfo> openFolder, Func<Task> fillTitles = null, Func<Task> fixPictures = null, Func<LibraryCheckResult, Task> replaceLow = null)
        {
            currentAlbums = albums; this.openFolder = openFolder; this.fillTitles = fillTitles; this.fixPictures = fixPictures; this.replaceLow = replaceLow;
            Title = "Check library"; Width = 980; Height = 570; MinWidth = 900; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var help = Kit.Label("Songs without a Title tag show their filename in players. Songs with several embedded pictures can show the wrong cover or carry extra artwork. Albums with low-bitrate lossy songs (measured, not just the file header) may be worth downloading again. Results use the last library scan; nothing is changed.", Kit.Body, cls: "muted");
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None;
            summary.Named("Tag check summary");
            foreach (int kbps in LibraryChecks.BitrateChoices) threshold.Items.Add("Below " + kbps + " kbps");
            threshold.SelectedIndex = Array.IndexOf(LibraryChecks.BitrateChoices, LibraryChecks.DefaultLowBitrate);
            threshold.Named("Low bitrate threshold").Tip("Albums with lossy songs (MP3, AAC, Ogg, Opus, WMA…) below this bitrate are listed. Lossless files are never listed.\n\n" +
                "The bitrate in a file's header can be wrong (common with VBR MP3s), so each candidate is measured with ffprobe/FFmpeg: the total size of its audio packets divided by their duration. " +
                "Songs whose real bitrate is high enough are dropped from the list. Without FFmpeg, header bitrates are shown and marked as unverified.");
            threshold.SelectionChanged += (s, e) => { if (Result != null) Check(); };
            list.Named("Tag check results");
            list.ItemTemplate = new FuncDataTemplate<Row>((row, scope) => new RowView(), true);
            list.SelectionChanged += (s, e) => show.IsEnabled = (list.SelectedItem as Row)?.Path != null;
            refresh.Click += (s, e) => Check();
            save.Click += async (s, e) => await SaveAsync();
            show.IsEnabled = false; show.Click += (s, e) => ShowSelected();
            show.Tip("Highlights the selected song in File Explorer, or opens the selected album's folder.");
            Closed += (s, e) => measuring?.Cancel();
            fill.Named("Fill titles from filenames").Tip("Opens Manage library with only \"Fill missing Title tags from filenames\" selected and previews the changes. Nothing is written until you apply them.");
            fill.Click += async (s, e) => { if (this.fillTitles != null) await this.fillTitles(); };
            onePicture.Named("Fix embedded pictures").Tip("Opens Manage library with only \"Standardize embedded artwork\" selected (one " + Artwork.EmbedSide + " px JPEG front cover per song) and previews the changes. Nothing is written until you apply them.");
            onePicture.Click += async (s, e) => { if (this.fixPictures != null) await this.fixPictures(); };
            foreach (var b in new[] { refresh, save, fill, onePicture, show }) b.Margin = new Thickness(3, 6, 3, 0);
            var buttons = Kit.Grid("130,140,*,215,215,160", "*").Add(refresh.Cell(0, 0), save.Cell(0, 1), fill.Cell(0, 3), onePicture.Cell(0, 4), show.Cell(0, 5));
            var card = Kit.Frost(Kit.Inset(list), new Thickness(10)); card.Margin = new Thickness(0, 2, 0, 0);
            replace.Named("Replace from YouTube Music").Tip("Opens a window that finds each low-bitrate album on YouTube Music and replaces the low songs with fresh downloads (best free stream, saved as MP3 320 with your tags and cover). " +
                "Nothing is replaced until you check songs and click Replace; originals are backed up and can be restored.");
            replace.Margin = new Thickness(10, 0, 0, 0); replace.IsEnabled = false;
            replace.Click += async (s, e) => { if (this.replaceLow != null && Result != null) await this.replaceLow(Result); };
            var top = Kit.Grid("*,Auto,Auto,Auto", "*").Add(summary.Cell(0, 0), Kit.Label("Low bitrate:", Kit.Small, cls: "muted").Cell(0, 1), threshold.Cell(0, 2), replace.Cell(0, 3));
            threshold.Margin = new Thickness(8, 0, 0, 0);
            var layout = Kit.Grid("*", "48,36,*,45").Add(help.Cell(0, 0), top.Cell(1, 0), card.Cell(2, 0), buttons.Cell(3, 0));
            layout.Margin = new Thickness(16);
            Kit.Prepare(this, layout);
            Check();
        }

        private sealed class RowView : Border
        {
            private readonly TextBlock text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock detail = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            internal RowView()
            {
                Height = 30; Padding = new Thickness(10, 0, 8, 0); Background = Brushes.Transparent;
                detail.Classes.Add("muted");
                Child = Kit.Grid("*,Auto", "*").Add(text.Cell(0, 0), detail.Cell(0, 1));
                DataContextChanged += (s, e) =>
                {
                    if (!(DataContext is Row row)) return;
                    text.Text = row.Text; detail.Text = row.Detail;
                    text.FontWeight = row.Header ? FontWeight.Bold : FontWeight.Normal;
                    Padding = new Thickness(row.Header ? 10 : 26, 0, 8, 0);
                    ToolTip.SetTip(this, row.Tip);
                };
            }
        }

        internal void Check()
        {
            int limit = LibraryChecks.BitrateChoices[Math.Max(0, threshold.SelectedIndex)];
            Result = LibraryChecks.Run(currentAlbums(), limit);
            measuring?.Cancel();
            measureStatus = Result.LowBitrate.Count > 0 ? "measuring " + Result.LowBitrate.Count + " songs…" : "";
            ShowRows();
            Verification = Result.LowBitrate.Count > 0 ? VerifyAsync(Result) : Task.CompletedTask;
        }

        // Header bitrates are shown at once; the real bitrates replace them when measured (in the background, cancelled by a new check).
        private async Task VerifyAsync(LibraryCheckResult result)
        {
            var source = measuring = new System.Threading.CancellationTokenSource();
            var progress = new Progress<ProgressInfo>(p => { if (ReferenceEquals(Result, result) && !source.IsCancellationRequested) { measureStatus = "measuring " + p.Completed + "/" + p.Total + "…"; summary.Text = Summary(); } });
            var candidates = result.LowBitrate.ToArray();
            LibraryChecks.Measurements measured;
            try { measured = await Task.Run(() => LibraryChecks.Measure(candidates, source.Token, progress)); }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(Result, result) || source.IsCancellationRequested) return;
            LibraryChecks.Apply(result, measured);
            measureStatus = result.Verified ? "" : "header bitrates (set up FFmpeg in Manage library to verify)";
            ShowRows();
        }

        private void ShowRows()
        {
            int limit = Result.LowBitrateLimit;
            var rows = new List<Row>();
            rows.Add(new Row { Header = true, Text = "Songs missing a Title tag", Detail = Count(Result.MissingTitles.Count) });
            if (Result.MissingTitles.Count == 0) rows.Add(new Row { Text = "Every song has a Title tag." });
            foreach (var t in Result.MissingTitles)
                rows.Add(new Row { Text = t.Path, Detail = Describe(t), Path = t.Path, Tip = t.Path + "\nShown as: " + Path.GetFileNameWithoutExtension(t.Path) + "\nFilled title would be: " + LibraryChecks.SuggestedTitle(t.Path) });
            rows.Add(new Row { Header = true, Text = "Songs with more than one embedded picture", Detail = Count(Result.MultiplePictures.Count) });
            if (Result.MultiplePictures.Count == 0) rows.Add(new Row { Text = "No song has more than one embedded picture." });
            foreach (var t in Result.MultiplePictures)
                rows.Add(new Row { Text = t.Path, Detail = t.PictureSummary, Path = t.Path,
                    Tip = t.Path + "\n" + t.PictureSummary + (t.CoverPictures > 1 ? "\nSeveral front/unlabeled covers: players may show any of them." : "") });
            var albums = Result.LowBitrateAlbums();
            string basis = Result.Verified ? "measured" : "header";
            rows.Add(new Row { Header = true, Text = "Albums with lossy songs below " + limit + " kbps", Detail = Count(albums.Count, "album") + " · " + Count(Result.LowBitrate.Count) });
            if (albums.Count == 0) rows.Add(new Row { Text = Result.Verified ? "No lossy song is really below " + limit + " kbps." : "No lossy song is below " + limit + " kbps." });
            foreach (var album in albums)
            {
                string name = string.Join(" — ", new[] { album.Artist, album.Album }.Where(v => !string.IsNullOrWhiteSpace(v)));
                string formats = string.Join("/", album.Tracks.Select(LibraryChecks.Format).Distinct());
                var songs = album.Tracks.Select(t => "  " + Result.Bitrate(t) + " kbps" + (Result.Measured.TryGetValue(t.Path, out int m) && Math.Abs(m - t.Bitrate) > 2 ? " (header said " + t.Bitrate + ")" : "") +
                    "  " + System.IO.Path.GetFileName(t.Path));
                rows.Add(new Row { Text = name.Length > 0 ? name : album.Folder, Detail = Count(album.Tracks.Count) + " · " + album.Range + " " + basis + " · " + formats,
                    Path = album.Folder, Folder = true, Tip = album.Folder + "\n" + string.Join("\n", songs.Take(40)) + (album.Tracks.Count > 40 ? "\n  …" : "") });
            }
            if (Result.HeaderWrong.Count > 0)
                rows.Add(new Row { Text = Count(Result.HeaderWrong.Count) + " not listed: their headers reported a low bitrate, but they measure at least " + limit + " kbps", Tip =
                    string.Join("\n", Result.HeaderWrong.Take(40).Select(t => "header " + t.Bitrate + " kbps → measured " + Result.Bitrate(t) + " kbps  " + t.Path)) + (Result.HeaderWrong.Count > 40 ? "\n…" : "") });
            if (Result.MeasureIssues.Count > 0)
                rows.Add(new Row { Text = Count(Result.MeasureIssues.Count) + " could not be measured and show their header bitrate", Tip = string.Join("\n", Result.MeasureIssues.Take(40)) });
            Rows = rows; list.ItemsSource = rows; show.IsEnabled = false;
            summary.Text = Summary();
            // Offered once the bitrates are measured (or known to be header-only), so the list it opens is final.
            replace.IsEnabled = replaceLow != null && albums.Count > 0 && (measureStatus.Length == 0 || measureStatus.StartsWith("header", StringComparison.Ordinal));
            fill.IsEnabled = fillTitles != null && Result.MissingTitles.Count > 0;
            onePicture.IsEnabled = fixPictures != null && Result.MultiplePictures.Count > 0;
            save.IsEnabled = Result.MissingTitles.Count + Result.MultiplePictures.Count + Result.LowBitrate.Count > 0;
        }

        private string Summary() => Result.MissingTitles.Count + " missing a Title tag · " + Result.MultiplePictures.Count + " with several embedded pictures (" +
            Result.MultiplePictures.Count(t => t.CoverPictures > 1) + " with several covers) · " + Count(Result.LowBitrateAlbums().Count, "album") + " below " + Result.LowBitrateLimit + " kbps" +
            (measureStatus.Length > 0 ? " (" + measureStatus + ")" : Result.Verified ? " (measured)" : "") + " · " + Result.TracksChecked + " songs checked";

        private static string Count(int n, string noun = "song") => n + " " + noun + (n == 1 ? "" : "s");
        private static string Describe(Track t) => string.Join(" · ", new[] { t.ArtistText, t.AlbumTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

        internal void Select(int index) => list.SelectedIndex = index;

        private void ShowSelected()
        {
            if (!(list.SelectedItem is Row { Path: string path } row)) return;
            string arguments = row.Folder ? Updates.UpdateInstaller.QuoteArgument(path) : "/select," + Updates.UpdateInstaller.QuoteArgument(path);
            try { openFolder(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
            catch (Exception e) { _ = Dialogs.ShowMessage(this, "Could not open folder", e.Message); }
        }

        private async Task SaveAsync()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save library check report", SuggestedFileName = "music-library-check.csv", DefaultExtension = "csv",
              FileTypeChoices = new[] { new FilePickerFileType("CSV report") { Patterns = new[] { "*.csv" } } } });
            string path = file?.TryGetLocalPath();
            if (path == null) return;
            try { File.WriteAllText(path, LibraryChecks.Csv(Result), new UTF8Encoding(true)); }
            catch (Exception e) { await Dialogs.ShowMessage(this, "Could not save report", e.Message); }
        }
    }
}
