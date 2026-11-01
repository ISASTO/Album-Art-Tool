using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Design;
using AlbumArtTool.Core;

namespace AlbumArtTool
{
    internal sealed class DuplicateWindow : Form
    {
        private readonly Album[] albums;
        private readonly Func<Album[]> currentAlbums;
        private readonly TreeView matches = new TreeView();
        private readonly Label message = new Label();
        private readonly ProgressBar progress = new ThemeProgressBar();
        private readonly Button scan = new ThemeButton(), save = new ThemeButton(), open = new ThemeButton();
        private CancellationTokenSource source;
        internal DuplicateResult Result { get; private set; }
        internal bool IsScanning { get; private set; }

        internal DuplicateWindow(Album[] snapshot, Func<Album[]> latest = null)
        {
            albums = snapshot; currentAlbums = latest;
            Text = "Find duplicates"; BackColor = MainForm.Surface; ForeColor = MainForm.Ink; Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(960, 570); MinimumSize = new Size(720, 460); StartPosition = FormStartPosition.CenterParent;
            var layout = new FrostCanvas { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            var help = new Label { Text = "Review song and album copies below. Confirmed matches use file/audio hashes; possible matches use tags and duration. This scanner does not delete files.", Dock = DockStyle.Fill, ForeColor = MainForm.Muted };
            message.Dock = DockStyle.Fill; message.ForeColor = MainForm.Ink; message.TextAlign = ContentAlignment.MiddleLeft;
            message.AccessibleName = "Duplicate scan status";
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 3, 0, 6); progress.AccessibleName = "Duplicate scan progress";
            matches.Dock = DockStyle.Fill; matches.BackColor = MainForm.Surface; matches.ForeColor = MainForm.Ink;
            matches.BorderStyle = BorderStyle.FixedSingle; matches.ShowNodeToolTips = true; matches.HideSelection = false;
            matches.AccessibleName = "Duplicate songs and albums"; matches.AfterSelect += (s, e) => open.Enabled = e.Node.Tag is string;
            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            foreach (var b in new[] { scan, save, open }) { b.Dock = DockStyle.Fill; b.ForeColor = MainForm.Ink; b.BackColor = MainForm.Surface; b.Font = new Font("Segoe UI", 9); b.Margin = new Padding(3, 6, 3, 0); }
            scan.Text = "Scan again"; scan.Click += async (s, e) => { if (IsScanning) source.Cancel(); else await ScanAsync(); };
            save.Text = "Save report…"; save.Enabled = false; save.Click += (s, e) => SaveReport();
            open.Text = "Open selected folder"; open.Enabled = false; open.Click += (s, e) => OpenSelected();
            buttons.Controls.Add(scan, 0, 0); buttons.Controls.Add(save, 1, 0); buttons.Controls.Add(open, 3, 0);
            layout.Controls.Add(help, 0, 0); layout.Controls.Add(message, 0, 1); layout.Controls.Add(progress, 0, 2); layout.Controls.Add(matches, 0, 3); layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);
            AppTheme.Apply(this);
            FormClosed += (s, e) => source?.Cancel();
        }

        internal async Task ScanAsync()
        {
            if (IsScanning) return;
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            IsScanning = true; Result = null; save.Enabled = open.Enabled = false; scan.Text = "Stop scan";
            matches.Nodes.Clear(); progress.Visible = true; progress.Value = 0; message.Text = "Finding duplicate candidates…";
            var reporter = new Progress<ProgressInfo>(p =>
            {
                if (IsDisposed || !IsScanning || token.IsCancellationRequested) return;
                progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total))) : 0;
                message.Text = p.Message + " · " + progress.Value + "%";
            });
            try
            {
                var snapshot = currentAlbums == null ? albums : currentAlbums();
                Result = await Task.Run(() => new DuplicateScanner().Scan(snapshot, token, reporter), token);
                if (IsDisposed) return;
                ShowMatches();
                message.Text = "Song groups: " + Result.Songs.Count(g => g.Confirmed) + " confirmed, " + Result.Songs.Count(g => !g.Confirmed) +
                    " possible · Album groups: " + Result.Albums.Count + (Result.Issues.Count > 0 ? " · " + Result.Issues.Count + " files skipped (see below)" : "");
                save.Enabled = true;
            }
            catch (OperationCanceledException) { if (!IsDisposed) message.Text = "Duplicate scan stopped. Run it again to compare the whole snapshot."; }
            catch (Exception e) { if (!IsDisposed) message.Text = "Duplicate scan could not finish. " + e.Message; }
            finally { IsScanning = false; if (!IsDisposed) { scan.Text = "Scan again"; progress.Visible = false; } }
        }

        private void ShowMatches()
        {
            matches.BeginUpdate();
            foreach (var group in Result.Albums)
            {
                var node = new TreeNode((group.Confirmed ? "Confirmed album copies" : "Possible album copies") + " · " + group.Name + " · " + group.Albums.Count + " folders") { ToolTipText = group.Basis };
                foreach (var album in group.Albums) node.Nodes.Add(new TreeNode(album.Folder + " · " + album.Tracks.Count + " tracks") { Tag = album.Folder, ToolTipText = group.Basis });
                matches.Nodes.Add(node); node.Expand();
            }
            foreach (var group in Result.Songs)
            {
                var node = new TreeNode((group.Confirmed ? "Confirmed song copies" : "Possible song copies") + " · " + group.Name + " · " + group.Tracks.Count + " files") { ToolTipText = group.Basis };
                foreach (var track in group.Tracks) node.Nodes.Add(new TreeNode(track.Path + " · " + track.Stamp.Length.ToString("N0") + " bytes") { Tag = track.Path, ToolTipText = group.Basis });
                matches.Nodes.Add(node); node.Expand();
            }
            if (Result.Issues.Count > 0) { var node = matches.Nodes.Add("Skipped files"); foreach (string issue in Result.Issues) node.Nodes.Add(issue); }
            if (matches.Nodes.Count == 0) matches.Nodes.Add("No duplicate candidates found in the scanned tracks.");
            matches.EndUpdate();
        }
        private void OpenSelected()
        {
            if (!(matches.SelectedNode?.Tag is string path)) return;
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Could not open folder"); }
        }
        private void SaveReport()
        {
            using (var dialog = new SaveFileDialog { Filter = "CSV report|*.csv", FileName = "music-duplicates.csv", Title = "Save duplicate report" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { System.IO.File.WriteAllText(dialog.FileName, Csv(Result), new UTF8Encoding(true)); }
                    catch (Exception e) { MessageBox.Show(this, e.Message, "Could not save report"); }
        }
        internal static string Csv(DuplicateResult result)
        {
            var text = new StringBuilder("Kind,Group,Confirmed,Basis,Artist,Title,Path\r\n"); int number = 0;
            string Escape(string value)
            {
                value = value ?? "";
                if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            foreach (var group in result.Albums.Concat(result.Songs))
            {
                number++;
                if (group.Albums.Count > 0)
                    foreach (var album in group.Albums) text.AppendLine(string.Join(",", new[] { "Album", number.ToString(), group.Confirmed.ToString(), group.Basis, album.Artist, album.Title, album.Folder }.Select(Escape)));
                else foreach (var track in group.Tracks) text.AppendLine(string.Join(",", new[] { "Song", number.ToString(), group.Confirmed.ToString(), group.Basis, track.ArtistText, track.Title, track.Path }.Select(Escape)));
            }
            foreach (string issue in result.Issues) text.AppendLine("Issue,,,,,," + Escape(issue));
            return text.ToString();
        }
    }
}
