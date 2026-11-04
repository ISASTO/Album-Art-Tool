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
using Avalonia.VisualTree;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Library health: songs without a Title tag, songs with several embedded pictures, albums with
    // low-bitrate songs and duplicate copies. Works from the main window's scan and never changes a file;
    // each card hands its fix to the page that makes it (Organize or YouTube Music).
    internal sealed class HealthPage : UserControl
    {
        private readonly Func<Album[]> currentAlbums;
        private readonly Action<ProcessStartInfo> openFolder;
        private readonly Func<Task> fillTitles, fixPictures, fillAlbumArtists;
        private readonly Button fillArtists = Kit.Button("Fill from artist…");
        private readonly TextBlock albumArtistsCount = Metric(), albumArtistsDetail = Detail();
        private int albumArtistsHeader;
        private readonly Func<LibraryCheckResult, Task> replaceLow;
        // One row of equal-weight actions; each tooltip explains what the short label does.
        private readonly Button replace = Kit.Button("Replace from YouTube Music…");
        private readonly Button fill = Kit.Button("Fill from filenames…"), onePicture = Kit.Button("Keep one cover…");
        private readonly ListBox list = new ListBox { Classes = { "grid" } };
        private readonly TextBlock summary = Kit.Label("", Kit.Body);
        private readonly ComboBox threshold = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button refresh = Kit.IconButton(Icons.Refresh, "Check again", "ghost"), save = Kit.Button("Save report…"), show = Kit.IconButton(Icons.Open, "Show selected");
        // One summary card per check, each with its own fix.
        private readonly TextBlock titlesCount = Metric(), picturesCount = Metric(), bitrateCount = Metric();
        private readonly TextBlock titlesDetail = Detail(), picturesDetail = Detail(), bitrateDetail = Detail();
        private int titlesHeader, picturesHeader, bitrateHeader;
        private readonly TextBlock duplicatesCount = Metric(), duplicatesDetail = Detail();
        private readonly Button findDuplicates = Kit.Button("Find duplicates");
        private readonly Button findingsTab = Kit.Button("Findings", "chip"), duplicatesTab = Kit.Button("Duplicates", "chip");
        private readonly Panel content = new Panel();
        private Control findingsView;
        private readonly Func<bool> canFindDuplicates;
        internal DuplicatePanel Duplicates { get; private set; }
        internal bool IsScanning => Duplicates != null && Duplicates.IsScanning;
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

        internal HealthPage(Func<Album[]> albums, Action<ProcessStartInfo> openFolder, Func<Task> fillTitles = null, Func<Task> fixPictures = null, Func<LibraryCheckResult, Task> replaceLow = null, Func<bool> canFindDuplicates = null, Func<Task> fillAlbumArtists = null)
        {
            this.fillAlbumArtists = fillAlbumArtists;
            fillArtists.Named("Fill album artists from the artist").Tip("Opens Organize with only \"Fill missing album artists from the artist\" selected and previews the changes. Nothing is written until you apply them.");
            fillArtists.Click += async (s, e) => { if (this.fillAlbumArtists != null) await this.fillAlbumArtists(); };
            currentAlbums = albums; this.openFolder = openFolder; this.fillTitles = fillTitles; this.fixPictures = fixPictures; this.replaceLow = replaceLow;
            this.canFindDuplicates = canFindDuplicates ?? (() => true);
            summary.Named("Tag check summary"); summary.Classes.Add("muted"); summary.FontSize = Kit.Small;
            foreach (int kbps in LibraryChecks.BitrateChoices) threshold.Items.Add("Below " + kbps + " kbps");
            threshold.SelectedIndex = Array.IndexOf(LibraryChecks.BitrateChoices, LibraryChecks.DefaultLowBitrate);
            threshold.Named("Low bitrate threshold").Tip("Albums with lossy songs (MP3, AAC, Ogg, Opus, WMA…) below this bitrate are listed. Lossless files are never listed.\n\n" +
                "The bitrate in a file's header can be wrong (common with VBR MP3s), so each candidate is measured with ffprobe/FFmpeg: the total size of its audio packets divided by their duration. " +
                "Songs whose real bitrate is high enough are dropped from the list. Without FFmpeg, header bitrates are shown and marked as unverified.");
            threshold.SelectionChanged += (s, e) => { if (Result != null) Check(); };
            list.Named("Tag check results");
            list.ItemTemplate = new FuncDataTemplate<Row>((row, scope) => new RowView(), true);
            list.SelectionChanged += (s, e) => show.IsEnabled = (list.SelectedItem as Row)?.Path != null;
            refresh.Tip("Check again using the latest library scan (rescan first if you changed files outside the app).");
            refresh.Click += (s, e) => Check();
            save.Tip("Save every finding as a CSV report you can open in Excel.");
            save.Click += async (s, e) => await SaveAsync();
            show.IsEnabled = false; show.Click += (s, e) => ShowSelected();
            show.Tip("Highlights the selected song in File Explorer, or opens the selected album's folder.");
            findDuplicates.Named("Find duplicates").Tip("Compare every song's audio to find confirmed duplicate songs and album copies, plus possible copies that share tags and length. Nothing is deleted.");
            findDuplicates.Click += async (s, e) => await FindDuplicatesAsync();
            findingsTab.Named("Show findings").Tip("Songs and albums found by the three checks.");
            duplicatesTab.Named("Show duplicates").Tip("Duplicate songs and album copies from the last duplicate scan.");
            findingsTab.Click += (s, e) => ShowView(false);
            duplicatesTab.Click += (s, e) => { if (Duplicates == null) _ = FindDuplicatesAsync(); else ShowView(true); };
            fill.Named("Fill titles from filenames").Tip("Opens Organize with only \"Fill missing titles from filenames\" selected and previews the changes. Nothing is written until you apply them.");
            fill.Click += async (s, e) => { if (this.fillTitles != null) await this.fillTitles(); };
            onePicture.Named("Fix embedded pictures").Tip("Opens Organize with only \"One front cover per song\" selected (one " + Artwork.EmbedSide + " px JPEG front cover per song) and previews the changes. Nothing is written until you apply them.");
            onePicture.Click += async (s, e) => { if (this.fixPictures != null) await this.fixPictures(); };
            replace.Named("Replace from YouTube Music").Tip("Opens a page that finds each low-bitrate album on YouTube Music and replaces the low songs with fresh downloads (best free stream, saved as MP3 320 with your tags and cover). " +
                "Nothing is replaced until you check songs and click Replace; originals are backed up and can be restored.");
            replace.IsEnabled = false;
            replace.Click += async (s, e) => { if (this.replaceLow != null && Result != null) await this.replaceLow(Result); };
            threshold.Width = 150; threshold.MinHeight = 24; threshold.FontSize = 11.5; threshold.Margin = new Thickness(0, -4, 0, 0);
            var cards = Kit.Grid("*,*,*,1.3*,*", "Auto").Add(
                Card("Missing titles", titlesCount, titlesDetail, fill, () => ShowFindings(titlesHeader)).Cell(0, 0),
                Card("No album artist", albumArtistsCount, albumArtistsDetail, fillArtists, () => ShowFindings(albumArtistsHeader)).Cell(0, 1),
                Card("Extra pictures", picturesCount, picturesDetail, onePicture, () => ShowFindings(picturesHeader)).Cell(0, 2),
                Card("Bitrate", bitrateCount, bitrateDetail, replace, () => ShowFindings(bitrateHeader), threshold).Cell(0, 3),
                Card("Duplicates", duplicatesCount, duplicatesDetail, findDuplicates, () => { if (Duplicates != null) ShowView(true); }).Cell(0, 4));
            cards.Margin = new Thickness(-5, 0, -5, 10);
            duplicatesCount.Text = "Not checked"; duplicatesDetail.Text = "Compares audio, not just names.";
            var buttons = Kit.Grid("Auto,Auto,*,Auto", "*").Add(refresh.Cell(0, 0), save.Cell(0, 1), show.Cell(0, 3));
            foreach (var b in new[] { refresh, save, show }) { b.Margin = new Thickness(3, 8, 3, 0); b.Padding = new Thickness(14, 2); b.Height = 34; }
            var table = Kit.Inset(list); table.Margin = new Thickness(0, 4, 0, 0);
            findingsView = Kit.Grid("*", "Auto,*,Auto").Add(summary.Cell(0, 0), table.Cell(1, 0), buttons.Cell(2, 0));
            content.Children.Add(findingsView);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
            tabs.Children.Add(findingsTab); tabs.Children.Add(duplicatesTab);
            var layout = Kit.Grid("*", "Auto,Auto,*").Add(cards.Cell(0, 0), tabs.Cell(1, 0), content.Cell(2, 0));
            Content = layout;
            ShowView(false);
            Check();
        }

        private Window Owner => TopLevel.GetTopLevel(this) as Window;
        internal void Stop() { measuring?.Cancel(); Duplicates?.Stop(); }
        internal bool ShowingDuplicates => Duplicates != null && Duplicates.IsVisible;

        private void ShowView(bool duplicates)
        {
            duplicates &= Duplicates != null;
            findingsView.IsVisible = !duplicates;
            if (Duplicates != null) Duplicates.IsVisible = duplicates;
            findingsTab.Classes.Set("selected", !duplicates); duplicatesTab.Classes.Set("selected", duplicates);
        }

        private void ShowFindings(int header)
        {
            ShowView(false);
            if (header >= 0 && header < Rows.Count) { list.ScrollIntoView(Rows[Rows.Count - 1]); list.ScrollIntoView(Rows[header]); }
        }

        /// <summary>Shows the duplicates view and scans (again) from the latest library scan.</summary>
        internal async Task FindDuplicatesAsync()
        {
            if (IsScanning) { ShowView(true); return; }
            if (!canFindDuplicates()) { duplicatesDetail.Text = "Wait for the current scan or task to finish."; return; }
            if (Duplicates == null)
            {
                Duplicates = new DuplicatePanel(currentAlbums(), currentAlbums, openFolder);
                content.Children.Add(Duplicates);
            }
            ShowView(true);
            findDuplicates.IsEnabled = false; duplicatesCount.Text = "Checking…"; duplicatesCount.Classes.Set("positive", false);
            try { await Duplicates.ScanAsync(); }
            finally { findDuplicates.IsEnabled = true; UpdateDuplicatesCard(); }
        }

        private void UpdateDuplicatesCard()
        {
            var result = Duplicates?.Result;
            findDuplicates.SetText(result == null ? "Find duplicates" : "Scan again");
            if (result == null) { duplicatesCount.Text = "Not checked"; duplicatesDetail.Text = "The scan was stopped before it finished."; return; }
            int confirmed = result.Songs.Count(g => g.Confirmed) + result.Albums.Count(g => g.Confirmed);
            int possible = result.Songs.Count(g => !g.Confirmed) + result.Albums.Count(g => !g.Confirmed);
            duplicatesCount.Text = confirmed + possible == 0 ? "All good" : Count(confirmed + possible, "group");
            duplicatesCount.Classes.Set("positive", confirmed + possible == 0);
            duplicatesDetail.Text = confirmed + possible == 0 ? "No copies found." : confirmed + " confirmed · " + possible + " possible";
        }

        private static TextBlock Metric() => Kit.Label("", 22, true);
        private static TextBlock Detail() { var d = Kit.Label("", Kit.Small, cls: "muted"); d.Margin = new Thickness(0, 0, 0, 8); return d; }

        // Clicking a card shows that check's findings.
        private Control Card(string title, TextBlock metric, TextBlock detail, Button action, Action select, Control option = null)
        {
            var stack = new StackPanel { Spacing = 2 };
            var top = Kit.Grid("*,Auto", "Auto").Add(Kit.Caption(title).Cell(0, 0));
            if (option != null) top.Add(option.Cell(0, 1));
            stack.Children.Add(top); stack.Children.Add(metric); stack.Children.Add(detail); stack.Children.Add(action);
            action.HorizontalAlignment = HorizontalAlignment.Left; action.Padding = new Thickness(14, 2); action.Height = 32; action.Margin = new Thickness(0);
            detail.TextWrapping = TextWrapping.Wrap; detail.MaxLines = 2; detail.MinHeight = 32;
            var card = Kit.Frost(stack, new Thickness(14, 12, 14, 12), 14);
            card.Margin = new Thickness(5, 0);
            card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            card.PointerPressed += (s, e) =>
            {
                if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null || e.Source is Visual c && c.FindAncestorOfType<ComboBox>(true) != null) return;
                select();
            };
            return card;
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
            measureStatus = result.Verified ? "" : "header bitrates (set up FFmpeg in Settings to verify)";
            ShowRows();
        }

        private void ShowRows()
        {
            int limit = Result.LowBitrateLimit;
            var rows = new List<Row>();
            titlesHeader = rows.Count;
            rows.Add(new Row { Header = true, Text = "Songs missing a Title tag", Detail = Count(Result.MissingTitles.Count) });
            if (Result.MissingTitles.Count == 0) rows.Add(new Row { Text = "Every song has a Title tag." });
            foreach (var t in Result.MissingTitles)
                rows.Add(new Row { Text = t.Path, Detail = Describe(t), Path = t.Path, Tip = t.Path + "\nShown as: " + Path.GetFileNameWithoutExtension(t.Path) + "\nFilled title would be: " + LibraryChecks.SuggestedTitle(t.Path) });
            albumArtistsHeader = rows.Count;
            rows.Add(new Row { Header = true, Text = "Songs without an Album Artist tag", Detail = Count(Result.MissingAlbumArtists.Count) });
            if (Result.MissingAlbumArtists.Count == 0) rows.Add(new Row { Text = "Every song with an artist also has an album artist." });
            foreach (var t in Result.MissingAlbumArtists)
                rows.Add(new Row { Text = t.Path, Detail = Describe(t), Path = t.Path, Tip = t.Path + "\nArtist: " + t.ArtistText + "\nNo Album Artist tag: players may split this album or list it under a guest artist." });
            picturesHeader = rows.Count;
            rows.Add(new Row { Header = true, Text = "Songs with more than one embedded picture", Detail = Count(Result.MultiplePictures.Count) });
            if (Result.MultiplePictures.Count == 0) rows.Add(new Row { Text = "No song has more than one embedded picture." });
            foreach (var t in Result.MultiplePictures)
                rows.Add(new Row { Text = t.Path, Detail = t.PictureSummary, Path = t.Path,
                    Tip = t.Path + "\n" + t.PictureSummary + (t.CoverPictures > 1 ? "\nSeveral front/unlabeled covers: players may show any of them." : "") });
            var albums = Result.LowBitrateAlbums();
            string basis = Result.Verified ? "measured" : "header";
            bitrateHeader = rows.Count;
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
            SetCard(titlesCount, titlesDetail, Result.MissingTitles.Count, "song", "Players show the filename instead of a title.", "Every song has a title.");
            SetCard(albumArtistsCount, albumArtistsDetail, Result.MissingAlbumArtists.Count, "song", "Players may split these albums or file them under a guest.", "Every song has an album artist.");
            int covers = Result.MultiplePictures.Count(t => t.CoverPictures > 1);
            SetCard(picturesCount, picturesDetail, Result.MultiplePictures.Count, "song", covers > 0 ? covers + " with several covers: players may show any of them." : "Extra pictures make files larger.", "One picture per song.");
            SetCard(bitrateCount, bitrateDetail, albums.Count, "album", Count(Result.LowBitrate.Count) + " below " + limit + " kbps · " + (measureStatus.Length > 0 ? measureStatus : basis), "Nothing below " + limit + " kbps.");
            // Offered once the bitrates are measured (or known to be header-only), so the list it opens is final.
            replace.IsEnabled = replaceLow != null && albums.Count > 0 && (measureStatus.Length == 0 || measureStatus.StartsWith("header", StringComparison.Ordinal));
            fill.IsEnabled = fillTitles != null && Result.MissingTitles.Count > 0;
            onePicture.IsEnabled = fixPictures != null && Result.MultiplePictures.Count > 0;
            // A card with nothing to fix shows no fix.
            fillArtists.IsEnabled = fillAlbumArtists != null && Result.MissingAlbumArtists.Count > 0; fillArtists.IsVisible = Result.MissingAlbumArtists.Count > 0;
            fill.IsVisible = Result.MissingTitles.Count > 0; onePicture.IsVisible = Result.MultiplePictures.Count > 0; replace.IsVisible = albums.Count > 0;
            save.IsEnabled = Result.MissingTitles.Count + Result.MissingAlbumArtists.Count + Result.MultiplePictures.Count + Result.LowBitrate.Count > 0;
        }

        private static void SetCard(TextBlock metric, TextBlock detail, int count, string noun, string found, string clean)
        {
            metric.Text = count == 0 ? "All good" : Count(count, noun);
            metric.Classes.Set("positive", count == 0);
            detail.Text = count == 0 ? clean : found; detail.Tip(detail.Text);
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
            catch (Exception e) { _ = Dialogs.ShowMessage(Owner, "Could not open folder", e.Message); }
        }

        private async Task SaveAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save library check report", SuggestedFileName = "music-library-check.csv", DefaultExtension = "csv",
              FileTypeChoices = new[] { new FilePickerFileType("CSV report") { Patterns = new[] { "*.csv" } } } });
            string path = file?.TryGetLocalPath();
            if (path == null) return;
            try { File.WriteAllText(path, LibraryChecks.Csv(Result), new UTF8Encoding(true)); }
            catch (Exception e) { await Dialogs.ShowMessage(Owner, "Could not save report", e.Message); }
        }
    }
}
