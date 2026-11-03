using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed class LibraryManagerWindow : Window
    {
        private readonly string root;
        private readonly Func<bool> canStart;
        private readonly Action<bool> exclusive;
        private readonly Func<string, Task> refreshLibrary;
        private readonly CheckBox convert = Kit.Check("Convert lossless / over 320 kbps to MP3 320", true), square = Kit.Check("Center-crop embedded artwork to squares", true),
            duplicates = Kit.Check("Remove confirmed duplicate songs / albums", true), organize = Kit.Check("Organize all folders into Artist / Album", true),
            archive = Kit.Check("Move old tool backups off the music player", true), compatibility = Kit.Check("Also convert other non-MP3 audio (compatibility)", false),
            rename = Kit.Check("Rename song files from Title tags", false), combine = Kit.Check("Combine multi-disc albums", false),
            cleanTitles = Kit.Check("Remove featured artists from titles", false);
        private readonly TextBox backup = new TextBox();
        private readonly TextBlock state = Kit.Label("", Kit.Body), encoderState = Kit.Label("", Kit.Body, cls: "muted");
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 2) };
        private readonly Button preview = Kit.Button("Preview changes"), apply = Kit.Button("Apply selected changes", "primary"), undo = Kit.Button("Preview Undo"), stop = Kit.Button("Stop"),
            download = Kit.Button("Download FFmpeg"), chooseEncoder = Kit.Button("Choose FFmpeg…"), chooseBackup = Kit.Button("Choose backup…"), openBackup = Kit.Button("Open backups");
        private readonly ListBox files = new ListBox { Classes = { "grid" } };
        private readonly Control optionsPanel;
        private List<FileRow> rows = new List<FileRow>();
        private Dictionary<string, List<FileRow>> mergeGroups = new Dictionary<string, List<FileRow>>();
        private CancellationTokenSource source;
        private LibraryPlan plan;
        private LibraryJournal undoJournal;
        private string encoder;
        private bool syncingMergeSelection;
        internal bool IsWorking { get; private set; }
        internal LibraryPlan CurrentPlan => plan;
        internal IReadOnlyList<FileRow> Rows => rows;

        internal sealed class FileRow
        {
            internal object Tag;
            internal string Action, Source, Destination, Details, Tooltip;
            internal bool Muted, HasCheck = true;
            internal bool IsChecked { get; private set; }
            internal event Action Changed;
            internal Func<FileRow, bool, bool> Toggle;
            internal void SetChecked(bool value) { if (IsChecked == value) return; IsChecked = value; Changed?.Invoke(); }
            internal bool RequestChecked(bool value) => Toggle == null || Toggle(this, value);
        }

        internal LibraryManagerWindow(string musicRoot, Func<bool> ready, Action<bool> setExclusive, Func<string, Task> refresh)
        {
            root = musicRoot; canStart = ready; exclusive = setExclusive; refreshLibrary = refresh;
            Title = "Manage music library"; Width = 1000; Height = 660; MinWidth = 960; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var path = Kit.Label("Music folder: " + root, Kit.Body);
            rename.Named("Rename song files from Title tags"); combine.Named("Combine multi-disc albums"); cleanTitles.Named("Remove featured artists from titles");
            var hint = Kit.Label("Cleans feat. / ft. / featuring credits", Kit.Small, cls: "muted"); hint.Margin = new Thickness(3);
            var options = Kit.Grid("*,*", "*,*,*,*,*").Add(convert.Cell(0, 0), square.Cell(0, 1), duplicates.Cell(1, 0), organize.Cell(1, 1),
                archive.Cell(2, 0), compatibility.Cell(2, 1), rename.Cell(3, 0), combine.Cell(3, 1), cleanTitles.Cell(4, 0), hint.Cell(4, 1)).Named("Library management options");
            optionsPanel = options;
            var optionsCard = Kit.Frost(options, new Thickness(10, 5, 10, 5)); optionsCard.Margin = new Thickness(0, 0, 0, 5);
            foreach (var check in new[] { convert, square, duplicates, organize, archive, compatibility, rename, combine, cleanTitles }) check.IsCheckedChanged += (s, e) => InvalidatePlan();
            var tools = Kit.Grid("*,165,160", "*").Add(encoderState.Cell(0, 0), download.Cell(0, 1), chooseEncoder.Cell(0, 2));
            backup.Text = BackupSettings.Root; backup.Named("PC backup folder"); backup.FontSize = Kit.Small;
            backup.TextChanged += (s, e) => InvalidatePlan();
            var backups = Kit.Grid("76,*,150", "*").Add(Kit.Label("Backups", Kit.Body).Cell(0, 0), backup.Cell(0, 1), chooseBackup.Cell(0, 2));
            var actions = Kit.Grid("165,180,145,140,*,100", "*").Add(preview.Cell(0, 0), apply.Cell(0, 1), undo.Cell(0, 2), openBackup.Cell(0, 3), stop.Cell(0, 5));
            state.Text = "Preview first, then check the changes to apply. Originals and removed copies are saved in the PC backup folder, outside your music tree.";
            state.TextWrapping = TextWrapping.Wrap; state.TextTrimming = TextTrimming.CharacterEllipsis; state.MaxLines = 2;
            state.Named("Library management status");
            files.Named("Planned library changes");
            files.ItemTemplate = new FuncDataTemplate<FileRow>((row, scope) => new FileRowView(), true);
            var header = Kit.Grid("17*,24*,25*,34*", "28").Add(Kit.Label("Action", Kit.Small, cls: "muted").Cell(0, 0), Kit.Label("Source", Kit.Small, cls: "muted").Cell(0, 1),
                Kit.Label("Destination / retained copy", Kit.Small, cls: "muted").Cell(0, 2), Kit.Label("Details", Kit.Small, cls: "muted").Cell(0, 3));
            header.Margin = new Thickness(34, 0, 6, 0);
            var headerBar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
            headerBar.Brush(Border.BorderBrushProperty, "Border"); headerBar.Brush(Border.BackgroundProperty, "InsetHeader");
            var table = Kit.Inset(Kit.Grid("*", "Auto,*").Add(headerBar.Cell(0, 0), files.Cell(1, 0))); table.Padding = new Thickness(0);
            var filesCard = Kit.Frost(table, new Thickness(10)); filesCard.Margin = new Thickness(0, 2, 0, 0);
            progress.Named("Library management progress"); progress.IsVisible = false;
            var layout = Kit.Grid("*", "38,162,38,38,44,48,*,28").Add(path.Cell(0, 0), optionsCard.Cell(1, 0), tools.Cell(2, 0), backups.Cell(3, 0),
                actions.Cell(4, 0), state.Cell(5, 0), filesCard.Cell(6, 0), progress.Cell(7, 0));
            layout.Margin = new Thickness(12);
            Kit.Prepare(this, layout);
            preview.Click += async (s, e) => await PreviewAsync(); apply.Click += async (s, e) => await ApplyAsync();
            undo.Click += (s, e) => PreviewUndo(); stop.Click += (s, e) => source?.Cancel();
            download.Click += async (s, e) => await DownloadAsync();
            chooseEncoder.Click += async (s, e) =>
            {
                var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose ffmpeg.exe", AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("FFmpeg") { Patterns = new[] { Management.Encoder.ExecutableName } }, new FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } } } });
                string picked = result.Count > 0 ? result[0].TryGetLocalPath() : null;
                if (picked == null) return;
                encoder = picked; Management.Encoder.Remember(encoder); UpdateEncoder(); InvalidatePlan();
            };
            chooseBackup.Click += async (s, e) =>
            {
                var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a backup folder on your PC, outside the music folder", AllowMultiple = false });
                string picked = result.Count > 0 ? result[0].TryGetLocalPath() : null;
                if (picked != null) backup.Text = picked;
            };
            openBackup.Click += (s, e) => { try { Directory.CreateDirectory(backup.Text); using (Process.Start(new ProcessStartInfo(backup.Text) { UseShellExecute = true })) { } } catch (Exception ex) { state.Text = ex.Message; } };
            Closing += (s, e) => { if (IsWorking) { e.Cancel = true; state.Text = "Task is running. Click Stop; the current file is kept safe before stopping."; } };
            encoder = Management.Encoder.Find(); UpdateEncoder(); SetWorking(false);
        }

        private sealed class FileRowView : Border
        {
            private readonly CheckBox check = new CheckBox { Margin = new Thickness(6, 0, 0, 0), MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock action = Cell(), from = Cell(), to = Cell(), details = Cell();
            private FileRow row;
            private bool updating;
            private static TextBlock Cell() => new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12.5 };
            internal FileRowView()
            {
                Height = 30; Background = Brushes.Transparent;
                Child = Kit.Grid("28,17*,24*,25*,34*", "*").Add(check.Cell(0, 0), action.Cell(0, 1), from.Cell(0, 2), to.Cell(0, 3), details.Cell(0, 4));
                check.IsCheckedChanged += (s, e) =>
                {
                    if (updating || row == null) return;
                    if (!row.RequestChecked(check.IsChecked == true)) { updating = true; check.IsChecked = row.IsChecked; updating = false; }
                };
                DataContextChanged += (s, e) =>
                {
                    if (row != null) row.Changed -= Update;
                    row = DataContext as FileRow;
                    if (row != null) row.Changed += Update;
                    Update();
                };
            }
            private void Update()
            {
                if (row == null) return;
                updating = true; check.IsVisible = row.HasCheck; check.IsChecked = row.IsChecked; updating = false;
                action.Text = row.Action; from.Text = row.Source; to.Text = row.Destination; details.Text = row.Details;
                foreach (var cell in new[] { action, from, to, details }) cell.Classes.Set("muted", row.Muted);
                ToolTip.SetTip(this, row.Tooltip);
            }
        }

        private string Relative(string path) => BackupSettings.Within(path, root) ? path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar) : path;
        private void UpdateEncoder() => encoderState.Text = encoder == null ? "Conversion needs FFmpeg. Download once or choose your copy." : "FFmpeg: " + encoder;
        private void InvalidatePlan() { if (IsWorking) return; plan = null; undoJournal = null; SetRows(new List<FileRow>()); UpdateApply(); }

        private void SetRows(List<FileRow> next)
        {
            rows = next;
            mergeGroups = rows.Where(r => r.Tag is LibraryAction a && !string.IsNullOrEmpty(a.MergeGroup)).GroupBy(r => ((LibraryAction)r.Tag).MergeGroup).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var row in rows) row.Toggle = OnToggle;
            files.ItemsSource = rows;
        }

        // Rows stay readable and scrollable while work runs, but selections are locked.
        // A multi-disc merge is selected or cleared as one complete album.
        private bool OnToggle(FileRow row, bool value)
        {
            if (IsWorking) return false;
            row.SetChecked(value);
            if (!syncingMergeSelection && row.Tag is LibraryAction action && !string.IsNullOrEmpty(action.MergeGroup) && mergeGroups.TryGetValue(action.MergeGroup, out var group))
            {
                syncingMergeSelection = true;
                try { foreach (var other in group) other.SetChecked(value); }
                finally { syncingMergeSelection = false; }
            }
            UpdateApply();
            return true;
        }

        internal void SetRowChecked(int index, bool value) => rows[index].RequestChecked(value);
        internal int CheckedCount => rows.Count(r => r.HasCheck && r.IsChecked);

        private void SetWorking(bool value)
        {
            IsWorking = value;
            foreach (Control control in new Control[] { convert, square, duplicates, organize, archive, compatibility, rename, combine, cleanTitles, backup, preview, undo, download, chooseEncoder, chooseBackup }) control.IsEnabled = !value;
            stop.IsEnabled = value; progress.IsVisible = value; UpdateApply();
        }

        private void UpdateApply()
        {
            apply.IsEnabled = !IsWorking && (plan != null || undoJournal != null) && rows.Any(r => r.IsChecked && (r.Tag is LibraryAction a && a.HasWork || r.Tag is JournalEntry));
            apply.Content = undoJournal != null ? "Undo selected changes" : "Apply selected changes";
        }

        internal IProgress<ProgressInfo> Reporter()
        {
            return new Progress<ProgressInfo>(p =>
            {
                if (!IsWorking) return;
                progress.IsIndeterminate = p.Total <= 0;
                progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, (int)(100 * (p.Completed + p.Fraction) / p.Total))) : 0;
                state.Text = p.Message + (p.Total > 0 ? " · " + (int)progress.Value + "%" : "");
            });
        }

        internal async Task PreviewAsync()
        {
            if (IsWorking || !Ready()) return;
            var options = new LibraryOptions { Root = root, BackupRoot = backup.Text, EncoderPath = encoder, Convert = convert.Checked(), ConvertOtherLossy = compatibility.Checked(),
                Square = square.Checked(), Duplicates = duplicates.Checked(), Organize = organize.Checked(), ArchiveOldBackups = archive.Checked(), RenameFromTitle = rename.Checked(), CombineDiscs = combine.Checked(), RemoveFeaturedTitles = cleanTitles.Checked() };
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            SetWorking(true); exclusive(true); SetRows(new List<FileRow>()); plan = null; undoJournal = null;
            try
            {
                var reporter = Reporter(); var built = await Task.Run(() => LibraryPlanner.Build(options, token, reporter));
                plan = built;
                var next = new List<FileRow>(built.Actions.Count + built.Issues.Count);
                foreach (var action in built.Actions)
                {
                    var row = new FileRow { Tag = action, Action = action.HasWork ? action.Description : "Keep", Source = Relative(action.Source),
                        Destination = action.Keeper != null ? "Keep: " + Relative(action.Keeper) : action.Target != null ? Relative(action.Target) : "PC backup folder",
                        Details = action.Reason ?? "", Muted = !action.HasWork, Tooltip = action.Source + "\n" + (action.Target ?? action.Keeper ?? "PC backup folder") + "\n" + (action.Reason ?? "") };
                    row.SetChecked(action.HasWork); next.Add(row);
                }
                foreach (string issue in built.Issues) next.Add(new FileRow { Action = "Skipped", Source = issue, Muted = true, Tooltip = issue });
                SetRows(next);
                long bytes = built.Actions.Where(a => a.HasWork && (a.Archive || a.Convert || a.Square || a.ChangesTags)).Sum(a => a.Stamp.Length);
                state.Text = built.Actions.Count(a => a.HasWork) + " changes across " + built.Actions.Count + " files · originals need about " + (bytes / 1048576.0).ToString("N0") + " MB in the backup folder · " + built.Issues.Count + " skipped/problems. Review, uncheck unwanted changes, then Apply.";
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
            if (plan != null) foreach (var row in rows) if (row.Tag is LibraryAction action) action.Selected = row.IsChecked;
            var selectedUndo = new HashSet<JournalEntry>(rows.Where(r => r.IsChecked && r.Tag is JournalEntry).Select(r => (JournalEntry)r.Tag));
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            SetWorking(true); exclusive(true); var reporter = Reporter();
            bool wrote = false;
            try
            {
                if (undoJournal != null)
                {
                    var journal = undoJournal; int before = journal.Issues.Count;
                    int restored = await Task.Run(() => LibraryRunner.Undo(journal, token, reporter, selectedUndo)); wrote = restored > 0;
                    state.Text = restored + " changes restored · " + (journal.Issues.Count - before) + " issues. See the backup journal for details.";
                }
                else
                {
                    var current = plan;
                    BackupSettings.Root = current.Options.BackupRoot;
                    var result = await Task.Run(() => LibraryRunner.Run(current, token, reporter));
                    wrote = result.Entries.Any(e => e.State == "Committed" || e.State == "TargetCreated");
                    state.Text = (result.Stopped ? "Stopped safely. " : "Finished. ") + result.Entries.Count(e => e.State == "Committed") + " changes completed · " + result.Issues.Count + " issues. Backups and Undo journal: " + result.Directory;
                }
            }
            catch (OperationCanceledException) { state.Text = "Stopped. Completed changes remain recorded in the backup journal."; wrote = true; }
            catch (Exception e) { state.Text = "Task could not finish: " + e.Message + " Completed changes can be reviewed in the backup journal."; wrote = true; }
            finally
            {
                plan = null; undoJournal = null; exclusive(false); SetWorking(false); apply.IsEnabled = false;
                if (wrote) await refreshLibrary(root);
            }
        }

        private void PreviewUndo()
        {
            try
            {
                undoJournal = LibraryJournal.Last(); plan = null; SetRows(new List<FileRow>());
                if (undoJournal == null) { state.Text = "There is no saved library run to undo on this PC."; return; }
                var next = new List<FileRow>();
                foreach (var entry in undoJournal.Entries.Where(e => e.State == "Committed" || e.State == "Prepared" || e.State == "TargetCreated"))
                {
                    var row = new FileRow { Tag = entry, Action = "Restore original", Source = entry.Target ?? entry.Backup, Destination = entry.Source, Details = entry.Error ?? "Verified backup / move history" };
                    row.SetChecked(true); next.Add(row);
                }
                SetRows(next);
                state.Text = "Undo preview for " + undoJournal.Root + ". Only files unchanged since that run will be restored. Check the changes to undo, then click Undo selected changes.";
            }
            catch (Exception e) { undoJournal = null; state.Text = "Could not read the last library journal: " + e.Message; }
            finally { UpdateApply(); }
        }

        private async Task DownloadAsync()
        {
            if (IsWorking) return;
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token; SetWorking(true);
            try
            {
                var reporter = new Progress<int>(n => { if (IsWorking) { progress.IsIndeterminate = false; progress.Value = n; state.Text = "Downloading FFmpeg · " + n + "% (one-time setup; archive is roughly 170 MB)"; } });
                using (var setup = new EncoderSetup())
                    encoder = await Task.Run(() => setup.InstallAsync(reporter, token));
                UpdateEncoder(); state.Text = "FFmpeg is ready. Preview your library changes.";
            }
            catch (OperationCanceledException) { state.Text = "Encoder setup cancelled."; }
            catch (Exception e) { state.Text = "Encoder setup could not finish: " + e.Message; }
            finally { SetWorking(false); }
        }

        internal void SetWorkingForTest(bool value) => SetWorking(value);
        internal Control OptionsPanel => optionsPanel;
    }
}
