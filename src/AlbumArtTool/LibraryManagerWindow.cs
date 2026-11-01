using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Core;
using AlbumArtTool.Management;

namespace AlbumArtTool
{
    internal sealed class LibraryManagerWindow : Form
    {
        private readonly string root;
        private readonly Func<bool> canStart;
        private readonly Action<bool> exclusive;
        private readonly Func<string, Task> refreshLibrary;
        private readonly CheckBox convert = Check("Convert lossless / over 320 kbps to MP3 320", true), square = Check("Center-crop embedded artwork to squares", true),
            duplicates = Check("Remove confirmed duplicate songs / albums", true), organize = Check("Organize all folders into Artist / Album", true),
            archive = Check("Move old tool backups off the music player", true), compatibility = Check("Also convert other non-MP3 audio (compatibility)", false);
        private readonly TextBox backup = new TextBox();
        private readonly Label state = new Label(), encoderState = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button preview = Button("Preview changes"), apply = Button("Apply selected changes"), undo = Button("Preview Undo"), stop = Button("Stop"),
            download = Button("Download FFmpeg"), chooseEncoder = Button("Choose FFmpeg…"), chooseBackup = Button("Choose backup…"), openBackup = Button("Open backups");
        private readonly ListView files = new ListView();
        private CancellationTokenSource source;
        private LibraryPlan plan;
        private LibraryJournal undoJournal;
        private string encoder;
        internal bool IsWorking { get; private set; }
        internal LibraryPlan CurrentPlan => plan;

        internal LibraryManagerWindow(string musicRoot, Func<bool> ready, Action<bool> setExclusive, Func<string, Task> refresh)
        {
            root = musicRoot; canStart = ready; exclusive = setExclusive; refreshLibrary = refresh;
            Text = "Manage music library"; ClientSize = new Size(1000, 660); MinimumSize = new Size(960, 620);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; BackColor = MainForm.Surface; ForeColor = MainForm.Ink; Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12) };
            foreach (int height in new[] { 38, 94, 38, 38, 44, 48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var path = new Label { Text = "Music folder: " + root, Dock = DockStyle.Fill, ForeColor = MainForm.Ink, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            foreach (int unused in new[] { 1, 2, 3 }) options.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            options.Controls.Add(convert, 0, 0); options.Controls.Add(square, 1, 0); options.Controls.Add(duplicates, 0, 1); options.Controls.Add(organize, 1, 1);
            options.Controls.Add(archive, 0, 2); options.Controls.Add(compatibility, 1, 2);
            foreach (var check in new[] { convert, square, duplicates, organize, archive, compatibility }) check.CheckedChanged += (s, e) => InvalidatePlan();
            var tools = Row(165, 160); encoderState.ForeColor = MainForm.Muted; encoderState.Dock = DockStyle.Fill; encoderState.TextAlign = ContentAlignment.MiddleLeft; encoderState.AutoEllipsis = true;
            tools.Controls.Add(encoderState, 0, 0); tools.Controls.Add(download, 1, 0); tools.Controls.Add(chooseEncoder, 2, 0);
            var backups = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            backups.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); backups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); backups.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            backups.Controls.Add(new Label { Text = "Backups", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MainForm.Ink }, 0, 0);
            backup.Text = BackupSettings.Root; backup.Dock = DockStyle.Fill; backup.BackColor = MainForm.Surface; backup.ForeColor = MainForm.Ink; backup.BorderStyle = BorderStyle.FixedSingle;
            backup.Font = new Font("Segoe UI", 9); backup.Margin = new Padding(3, 8, 3, 3); backup.AccessibleName = "PC backup folder";
            backup.TextChanged += (s, e) => InvalidatePlan(); backups.Controls.Add(backup, 1, 0); backups.Controls.Add(chooseBackup, 2, 0);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty };
            foreach (int width in new[] { 165, 180, 145, 140 }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            actions.Controls.Add(preview, 0, 0); actions.Controls.Add(apply, 1, 0); actions.Controls.Add(undo, 2, 0); actions.Controls.Add(openBackup, 3, 0); actions.Controls.Add(stop, 5, 0);
            state.Dock = DockStyle.Fill; state.ForeColor = MainForm.Ink; state.Text = "Preview first, then check the changes to apply. Originals and removed copies are saved in the PC backup folder, outside your music tree.";
            state.AccessibleName = "Library management status";
            files.View = View.Details; files.CheckBoxes = true; files.FullRowSelect = true; files.GridLines = false; files.Dock = DockStyle.Fill;
            files.BackColor = MainForm.Surface; files.ForeColor = MainForm.Ink; files.BorderStyle = BorderStyle.FixedSingle; files.HideSelection = false;
            files.Columns.Add("Action", 205); files.Columns.Add("Source", 285); files.Columns.Add("Destination / retained copy", 300); files.Columns.Add("Details", 420);
            files.AccessibleName = "Planned library changes"; files.ShowItemToolTips = true; files.ItemChecked += (s, e) => UpdateApply();
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 8, 0, 2); progress.Visible = false; progress.AccessibleName = "Library management progress";
            layout.Controls.Add(path, 0, 0); layout.Controls.Add(options, 0, 1); layout.Controls.Add(tools, 0, 2); layout.Controls.Add(backups, 0, 3);
            layout.Controls.Add(actions, 0, 4); layout.Controls.Add(state, 0, 5); layout.Controls.Add(files, 0, 6); layout.Controls.Add(progress, 0, 7); Controls.Add(layout);
            preview.Click += async (s, e) => await PreviewAsync(); apply.Click += async (s, e) => await ApplyAsync();
            undo.Click += (s, e) => PreviewUndo(); stop.Click += (s, e) => source?.Cancel();
            download.Click += async (s, e) => await DownloadAsync();
            chooseEncoder.Click += (s, e) => { using (var dialog = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe|Executables|*.exe", Title = "Choose ffmpeg.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) { encoder = dialog.FileName; Directory.CreateDirectory(BackupSettings.AppData); File.WriteAllText(Path.Combine(BackupSettings.AppData, "encoder-path.txt"), encoder); UpdateEncoder(); InvalidatePlan(); } };
            chooseBackup.Click += (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "Choose a backup folder on your PC, outside the music folder", SelectedPath = backup.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) backup.Text = dialog.SelectedPath; };
            openBackup.Click += (s, e) => { try { Directory.CreateDirectory(backup.Text); Process.Start(new ProcessStartInfo(backup.Text) { UseShellExecute = true }); } catch (Exception ex) { state.Text = ex.Message; } };
            FormClosing += (s, e) => { if (IsWorking) { e.Cancel = true; state.Text = "Task is running. Click Stop; the current file is kept safe before stopping."; } };
            encoder = Encoder.Find(); UpdateEncoder(); SetWorking(false);
        }
        private static CheckBox Check(string text, bool value) => new ThemeCheckBox { Text = text, Checked = value, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9), ForeColor = MainForm.Ink, Margin = new Padding(3) };
        private static Button Button(string text) => new ThemeButton { Text = text, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9), ForeColor = MainForm.Ink, BackColor = MainForm.Surface, Margin = new Padding(3) };
        private static TableLayoutPanel Row(int second, int third)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, second)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, third)); return panel;
        }
        private string Relative(string path) => BackupSettings.Within(path, root) ? path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar) : path;
        private void UpdateEncoder() => encoderState.Text = encoder == null ? "Conversion needs FFmpeg. Download once or choose your copy." : "FFmpeg: " + encoder;
        private void InvalidatePlan() { if (IsWorking) return; plan = null; undoJournal = null; files.Items.Clear(); UpdateApply(); }
        private void SetWorking(bool value)
        {
            IsWorking = value;
            foreach (Control control in new Control[] { convert, square, duplicates, organize, archive, compatibility, backup, preview, undo, download, chooseEncoder, chooseBackup, files }) control.Enabled = !value;
            stop.Enabled = value; progress.Visible = value; UpdateApply();
        }
        private void UpdateApply()
        {
            apply.Enabled = !IsWorking && (plan != null || undoJournal != null) && files.CheckedItems.Cast<ListViewItem>().Any(i => i.Tag is LibraryAction a && a.HasWork || i.Tag is JournalEntry);
            apply.Text = undoJournal != null ? "Undo selected changes" : "Apply selected changes";
        }
        private IProgress<ProgressInfo> Reporter()
        {
            return new Progress<ProgressInfo>(p =>
            {
                if (IsDisposed || !IsWorking) return;
                progress.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, (int)(100 * (p.Completed + p.Fraction) / p.Total))) : 0;
                state.Text = p.Message + (p.Total > 0 ? " · " + progress.Value + "%" : "");
            });
        }
        internal async Task PreviewAsync()
        {
            if (IsWorking || !Ready()) return;
            var options = new LibraryOptions { Root = root, BackupRoot = backup.Text, EncoderPath = encoder, Convert = convert.Checked, ConvertOtherLossy = compatibility.Checked,
                Square = square.Checked, Duplicates = duplicates.Checked, Organize = organize.Checked, ArchiveOldBackups = archive.Checked };
            source?.Dispose(); source = new CancellationTokenSource(); SetWorking(true); exclusive(true); files.Items.Clear(); plan = null; undoJournal = null;
            try
            {
                var reporter = Reporter(); plan = await Task.Run(() => LibraryPlanner.Build(options, source.Token, reporter));
                files.BeginUpdate();
                foreach (var action in plan.Actions)
                {
                    var item = new ListViewItem(action.HasWork ? action.Description : "Keep") { Tag = action, Checked = action.HasWork, ToolTipText = action.Source + "\n" + (action.Target ?? action.Keeper ?? "PC backup folder") + "\n" + (action.Reason ?? "") };
                    item.SubItems.Add(Relative(action.Source)); item.SubItems.Add(action.Keeper != null ? "Keep: " + Relative(action.Keeper) : action.Target != null ? Relative(action.Target) : "PC backup folder"); item.SubItems.Add(action.Reason ?? "");
                    if (!action.HasWork) item.ForeColor = MainForm.Muted; files.Items.Add(item);
                }
                foreach (string issue in plan.Issues) { var item = new ListViewItem("Skipped") { ForeColor = MainForm.Muted }; item.SubItems.Add(issue); files.Items.Add(item); }
                files.EndUpdate();
                long bytes = plan.Actions.Where(a => a.HasWork && (a.Archive || a.Convert || a.Square)).Sum(a => a.Stamp.Length);
                state.Text = plan.Actions.Count(a => a.HasWork) + " changes across " + plan.Actions.Count + " files · originals need about " + (bytes / 1048576.0).ToString("N0") + " MB in the backup folder · " + plan.Issues.Count + " skipped/problems. Review, uncheck unwanted changes, then Apply.";
            }
            catch (OperationCanceledException) { state.Text = "Preview stopped. No files were changed."; }
            catch (Exception e) { state.Text = "Preview could not finish: " + e.Message; }
            finally { exclusive(false); SetWorking(false); }
        }
        private bool Ready()
        { if (canStart()) return true; state.Text = "Wait for scans, artwork/artist jobs and duplicate scans to finish. Apply or discard any staged cover in the main window first."; return false; }
        internal async Task ApplyAsync()
        {
            if (IsWorking || !Ready()) return;
            if (plan == null && undoJournal == null) return;
            if (plan != null) foreach (ListViewItem item in files.Items) if (item.Tag is LibraryAction action) action.Selected = item.Checked;
            var selectedUndo = files.CheckedItems.Cast<ListViewItem>().Where(i => i.Tag is JournalEntry).Select(i => (JournalEntry)i.Tag).ToHashSetCompat();
            source?.Dispose(); source = new CancellationTokenSource(); SetWorking(true); exclusive(true); var reporter = Reporter();
            bool wrote = false;
            try
            {
                if (undoJournal != null)
                {
                    int before = undoJournal.Issues.Count;
                    int restored = await Task.Run(() => LibraryRunner.Undo(undoJournal, source.Token, reporter, selectedUndo)); wrote = restored > 0;
                    state.Text = restored + " changes restored · " + (undoJournal.Issues.Count - before) + " issues. See the backup journal for details.";
                }
                else
                {
                    BackupSettings.Root = plan.Options.BackupRoot;
                    var result = await Task.Run(() => LibraryRunner.Run(plan, source.Token, reporter));
                    wrote = result.Entries.Any(e => e.State == "Committed" || e.State == "TargetCreated");
                    state.Text = (result.Stopped ? "Stopped safely. " : "Finished. ") + result.Entries.Count(e => e.State == "Committed") + " changes completed · " + result.Issues.Count + " issues. Backups and Undo journal: " + result.Directory;
                }
            }
            catch (OperationCanceledException) { state.Text = "Stopped. Completed changes remain recorded in the backup journal."; wrote = true; }
            catch (Exception e) { state.Text = "Task could not finish: " + e.Message + " Completed changes can be reviewed in the backup journal."; wrote = true; }
            finally
            {
                plan = null; undoJournal = null; exclusive(false); SetWorking(false); apply.Enabled = false;
                if (wrote) await refreshLibrary(root);
            }
        }
        private void PreviewUndo()
        {
            try
            {
                undoJournal = LibraryJournal.Last(); plan = null; files.Items.Clear();
                if (undoJournal == null) { state.Text = "There is no saved library run to undo on this PC."; return; }
                foreach (var entry in undoJournal.Entries.Where(e => new[] { "Committed", "Prepared", "TargetCreated" }.Contains(e.State)))
                {
                    var item = new ListViewItem("Restore original") { Tag = entry, Checked = true };
                    item.SubItems.Add(entry.Target ?? entry.Backup); item.SubItems.Add(entry.Source); item.SubItems.Add(entry.Error ?? "Verified backup / move history"); files.Items.Add(item);
                }
                state.Text = "Undo preview for " + undoJournal.Root + ". Only files unchanged since that run will be restored. Check the changes to undo, then click Undo selected changes.";
            }
            catch (Exception e) { undoJournal = null; state.Text = "Could not read the last library journal: " + e.Message; }
            finally { UpdateApply(); }
        }
        private async Task DownloadAsync()
        {
            if (IsWorking) return;
            source?.Dispose(); source = new CancellationTokenSource(); SetWorking(true);
            try
            {
                using (var setup = new EncoderSetup())
                    encoder = await Task.Run(() => setup.InstallAsync(new Progress<int>(n => { if (!IsDisposed && IsWorking) { progress.Style = ProgressBarStyle.Continuous; progress.Value = n; state.Text = "Downloading FFmpeg · " + n + "% (one-time setup; archive is roughly 170 MB)"; } }), source.Token));
                UpdateEncoder(); state.Text = "FFmpeg is ready. Preview your library changes.";
            }
            catch (OperationCanceledException) { state.Text = "Encoder setup cancelled."; }
            catch (Exception e) { state.Text = "Encoder setup could not finish: " + e.Message; }
            finally { SetWorking(false); }
        }
    }
    internal static class SetCompatibility
    { internal static System.Collections.Generic.HashSet<T> ToHashSetCompat<T>(this System.Collections.Generic.IEnumerable<T> values) => new System.Collections.Generic.HashSet<T>(values); }
}
