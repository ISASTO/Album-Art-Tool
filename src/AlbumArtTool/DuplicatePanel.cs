using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    internal sealed class DuplicatePanel : UserControl
    {
        private readonly Album[] albums;
        private readonly Func<Album[]> currentAlbums;
        private readonly Action<ProcessStartInfo> openFolder;
        private readonly TreeView matches = new TreeView();
        private readonly TextBlock message = Kit.Label("", Kit.Body);
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 3, 0, 6) };
        private readonly Button scan = Kit.IconButton(Icons.Refresh, "Scan again", "ghost"), save = Kit.Button("Save report…"), open = Kit.IconButton(Icons.Open, "Open selected folder");
        private CancellationTokenSource source;
        internal DuplicateResult Result { get; private set; }
        internal bool IsScanning { get; private set; }
        internal IReadOnlyList<Node> Nodes { get; private set; } = Array.Empty<Node>();

        internal sealed class Node
        {
            public string Text { get; set; }
            public string Tip { get; set; }
            public string Path { get; set; }
            public List<Node> Children { get; } = new List<Node>();
        }

        internal DuplicatePanel(Album[] snapshot, Func<Album[]> latest = null, Action<ProcessStartInfo> openFolder = null)
        {
            albums = snapshot; currentAlbums = latest;
            this.openFolder = openFolder ?? (info => { using (Process.Start(info)) { } });
            var help = Kit.Label("Confirmed copies have identical audio; possible copies share tags and length. Nothing is deleted here: remove confirmed copies with Organize › Remove confirmed duplicates.", Kit.Small, cls: "muted");
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None;
            message.Named("Duplicate scan status");
            progress.Named("Duplicate scan progress");
            matches.Named("Duplicate songs and albums");
            matches.ItemTemplate = new FuncTreeDataTemplate<Node>((node, scope) =>
            {
                var text = new TextBlock { Text = node.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(text, node.Tip);
                return text;
            }, node => node.Children);
            matches.SelectionChanged += (s, e) => open.IsEnabled = (matches.SelectedItem as Node)?.Path != null;
            scan.Click += async (s, e) => { if (IsScanning) source?.Cancel(); else await ScanAsync(); };
            scan.Tip("Compare every song again using the latest library scan. Click again while it runs to stop.");
            save.Tip("Save every duplicate group as a CSV report you can open in Excel.");
            open.Tip("Open the folder of the selected album copy, or the folder holding the selected song.");
            save.IsEnabled = false; save.Click += async (s, e) => await SaveReportAsync();
            open.IsEnabled = false; open.Click += (s, e) => OpenSelected();
            foreach (var b in new[] { scan, save, open }) { b.Margin = new Thickness(3, 8, 3, 0); b.Padding = new Thickness(14, 2); b.Height = 34; }
            var buttons = Kit.Grid("Auto,Auto,*,Auto", "*").Add(scan.Cell(0, 0), save.Cell(0, 1), open.Cell(0, 3));
            message.FontSize = Kit.Small; message.Margin = new Thickness(0, 6, 0, 0);
            var table = Kit.Inset(matches); table.Margin = new Thickness(0, 4, 0, 0);
            var layout = Kit.Grid("*", "Auto,Auto,Auto,*,Auto").Add(help.Cell(0, 0), message.Cell(1, 0), progress.Cell(2, 0), table.Cell(3, 0), buttons.Cell(4, 0));
            Content = layout;
        }

        internal void Stop() => source?.Cancel();
        private Window Owner => TopLevel.GetTopLevel(this) as Window;

        internal async Task ScanAsync()
        {
            if (IsScanning) return;
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            IsScanning = true; Result = null; save.IsEnabled = open.IsEnabled = false; scan.SetText("Stop scan");
            Nodes = Array.Empty<Node>(); matches.ItemsSource = null;
            progress.IsVisible = true; progress.Value = 0; message.Text = "Finding duplicate candidates…";
            var reporter = new Progress<ProgressInfo>(p =>
            {
                if (!IsScanning || token.IsCancellationRequested) return;
                progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total))) : 0;
                message.Text = p.Message + " · " + (int)progress.Value + "%";
            });
            try
            {
                var snapshot = currentAlbums == null ? albums : currentAlbums();
                Result = await Task.Run(() => new DuplicateScanner().Scan(snapshot, token, reporter), token);
                ShowMatches();
                message.Text = "Song groups: " + Result.Songs.Count(g => g.Confirmed) + " confirmed, " + Result.Songs.Count(g => !g.Confirmed) +
                    " possible · Album groups: " + Result.Albums.Count + (Result.Issues.Count > 0 ? " · " + Result.Issues.Count + " files skipped (see below)" : "");
                save.IsEnabled = true;
            }
            catch (OperationCanceledException) { message.Text = "Duplicate scan stopped. Run it again to compare the whole snapshot."; }
            catch (Exception e) { message.Text = "Duplicate scan could not finish. " + e.Message; }
            finally { IsScanning = false; scan.SetText("Scan again"); progress.IsVisible = false; }
        }

        private void ShowMatches()
        {
            var nodes = new List<Node>();
            foreach (var group in Result.Albums)
            {
                var node = new Node { Text = (group.Confirmed ? "Confirmed album copies" : "Possible album copies") + " · " + group.Name + " · " + group.Albums.Count + " folders", Tip = group.Basis };
                foreach (var album in group.Albums) node.Children.Add(new Node { Text = album.Folder + " · " + album.Tracks.Count + " tracks", Path = album.Folder, Tip = group.Basis });
                nodes.Add(node);
            }
            foreach (var group in Result.Songs)
            {
                var node = new Node { Text = (group.Confirmed ? "Confirmed song copies" : "Possible song copies") + " · " + group.Name + " · " + group.Tracks.Count + " files", Tip = group.Basis };
                foreach (var track in group.Tracks) node.Children.Add(new Node { Text = track.Path + " · " + track.Stamp.Length.ToString("N0") + " bytes", Path = track.Path, Tip = group.Basis });
                nodes.Add(node);
            }
            if (Result.Issues.Count > 0)
            {
                var node = new Node { Text = "Skipped files" };
                foreach (string issue in Result.Issues) node.Children.Add(new Node { Text = issue, Tip = issue });
                nodes.Add(node);
            }
            if (nodes.Count == 0) nodes.Add(new Node { Text = "No duplicate candidates found in the scanned tracks." });
            Nodes = nodes;
            matches.ItemsSource = nodes;
            // Expand groups once their containers exist.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var node in nodes.Where(n => n.Children.Count > 0 && n.Text != "Skipped files"))
                    if (matches.ContainerFromItem(node) is TreeViewItem item) item.IsExpanded = true;
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void OpenSelected()
        {
            if (!(matches.SelectedItem is Node { Path: string path })) return;
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            try { openFolder(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception e) { _ = Dialogs.ShowMessage(Owner, "Could not open folder", e.Message); }
        }

        private async Task SaveReportAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save duplicate report", SuggestedFileName = "music-duplicates.csv", DefaultExtension = "csv",
              FileTypeChoices = new[] { new FilePickerFileType("CSV report") { Patterns = new[] { "*.csv" } } } });
            string path = file?.TryGetLocalPath();
            if (path == null) return;
            try { File.WriteAllText(path, Csv(Result), new UTF8Encoding(true)); }
            catch (Exception e) { await Dialogs.ShowMessage(Owner, "Could not save report", e.Message); }
        }

        internal static string Csv(DuplicateResult result)
        {
            var text = new StringBuilder("Kind,Group,Confirmed,Basis,Artist,Title,Path\r\n"); int number = 0;
            string Escape(string value)
            {
                value = value ?? "";
                // Spreadsheet formula injection guard.
                if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            foreach (var group in result.Albums.Concat(result.Songs))
            {
                number++;
                if (group.Albums.Count > 0)
                    foreach (var album in group.Albums) text.Append(string.Join(",", new[] { "Album", number.ToString(), group.Confirmed.ToString(), group.Basis, album.Artist, album.Title, album.Folder }.Select(Escape))).Append("\r\n");
                else foreach (var track in group.Tracks) text.Append(string.Join(",", new[] { "Song", number.ToString(), group.Confirmed.ToString(), group.Basis, track.ArtistText, track.Title, track.Path }.Select(Escape))).Append("\r\n");
            }
            foreach (string issue in result.Issues) text.Append("Issue,,,,,,").Append(Escape(issue)).Append("\r\n");
            return text.ToString();
        }
    }
}
