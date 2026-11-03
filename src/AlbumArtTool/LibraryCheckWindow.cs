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
        private readonly Func<Task> fillTitles;
        private readonly Button fill = Kit.Button("Fill titles from filenames…");
        private readonly ListBox list = new ListBox { Classes = { "grid" } };
        private readonly TextBlock summary = Kit.Label("", Kit.Body);
        private readonly ComboBox threshold = new ComboBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button refresh = Kit.Button("Check again", "primary"), save = Kit.Button("Save report…"), show = Kit.Button("Show selected file");
        internal LibraryCheckResult Result { get; private set; }
        internal IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

        internal sealed class Row
        {
            internal string Text, Detail, Path, Tip;
            internal bool Header;
        }

        internal LibraryCheckWindow(Func<Album[]> albums, Action<ProcessStartInfo> openFolder, Func<Task> fillTitles = null)
        {
            currentAlbums = albums; this.openFolder = openFolder; this.fillTitles = fillTitles;
            Title = "Check library"; Width = 960; Height = 570; MinWidth = 780; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var help = Kit.Label("Songs without a Title tag show their filename in players. Songs with several embedded pictures can show the wrong cover or carry extra artwork. Low-bitrate lossy songs may be worth downloading again. Results use the last library scan; nothing is changed.", Kit.Body, cls: "muted");
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None;
            summary.Named("Tag check summary");
            foreach (int kbps in LibraryChecks.BitrateChoices) threshold.Items.Add("Below " + kbps + " kbps");
            threshold.SelectedIndex = Array.IndexOf(LibraryChecks.BitrateChoices, LibraryChecks.DefaultLowBitrate);
            threshold.Named("Low bitrate threshold").Tip("Lossy songs (MP3, AAC, Ogg, Opus, WMA…) below this bitrate are listed. Lossless files are never listed.");
            threshold.SelectionChanged += (s, e) => { if (Result != null) Check(); };
            list.Named("Tag check results");
            list.ItemTemplate = new FuncDataTemplate<Row>((row, scope) => new RowView(), true);
            list.SelectionChanged += (s, e) => show.IsEnabled = (list.SelectedItem as Row)?.Path != null;
            refresh.Click += (s, e) => Check();
            save.Click += async (s, e) => await SaveAsync();
            show.IsEnabled = false; show.Click += (s, e) => ShowSelected();
            fill.Named("Fill titles from filenames").Tip("Opens Manage library with only \"Fill missing Title tags from filenames\" selected and previews the changes. Nothing is written until you apply them.");
            fill.Click += async (s, e) => { if (this.fillTitles != null) await this.fillTitles(); };
            foreach (var b in new[] { refresh, save, fill, show }) b.Margin = new Thickness(3, 6, 3, 0);
            var buttons = Kit.Grid("165,180,*,215,165", "*").Add(refresh.Cell(0, 0), save.Cell(0, 1), fill.Cell(0, 3), show.Cell(0, 4));
            var card = Kit.Frost(Kit.Inset(list), new Thickness(10)); card.Margin = new Thickness(0, 2, 0, 0);
            var top = Kit.Grid("*,Auto,Auto", "*").Add(summary.Cell(0, 0), Kit.Label("Low bitrate:", Kit.Small, cls: "muted").Cell(0, 1), threshold.Cell(0, 2));
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
            rows.Add(new Row { Header = true, Text = "Lossy songs below " + limit + " kbps", Detail = Count(Result.LowBitrate.Count) });
            if (Result.LowBitrate.Count == 0) rows.Add(new Row { Text = "No lossy song is below " + limit + " kbps." });
            foreach (var t in Result.LowBitrate)
                rows.Add(new Row { Text = t.Path, Detail = t.Bitrate + " kbps " + LibraryChecks.Format(t), Path = t.Path,
                    Tip = t.Path + "\n" + t.Bitrate + " kbps · " + (t.Codec ?? "") + (string.IsNullOrWhiteSpace(Describe(t)) ? "" : "\n" + Describe(t)) });
            Rows = rows; list.ItemsSource = rows; show.IsEnabled = false;
            summary.Text = Result.MissingTitles.Count + " missing a Title tag · " + Result.MultiplePictures.Count + " with several embedded pictures (" +
                Result.MultiplePictures.Count(t => t.CoverPictures > 1) + " with several covers) · " + Result.LowBitrate.Count + " below " + limit + " kbps · " + Result.TracksChecked + " songs checked";
            fill.IsEnabled = fillTitles != null && Result.MissingTitles.Count > 0;
            save.IsEnabled = Result.MissingTitles.Count + Result.MultiplePictures.Count + Result.LowBitrate.Count > 0;
        }

        private static string Count(int n) => n + (n == 1 ? " song" : " songs");
        private static string Describe(Track t) => string.Join(" · ", new[] { t.ArtistText, t.AlbumTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

        internal void Select(int index) => list.SelectedIndex = index;

        private void ShowSelected()
        {
            if (!(list.SelectedItem is Row { Path: string path })) return;
            try { openFolder(new ProcessStartInfo("explorer.exe", "/select," + Updates.UpdateInstaller.QuoteArgument(path)) { UseShellExecute = true }); }
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
