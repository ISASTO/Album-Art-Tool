using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed partial class MainWindow
    {
        private readonly ObservableCollection<string> issueLines = new ObservableCollection<string>();
        private readonly TextBlock lastChange = Kit.Label("Nothing to undo yet in this session.", Kit.Body), problemsSummary = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Button undoFromActivity = Kit.IconButton(Icons.Undo, "Undo last change"), openOrganizeUndo = Kit.Button("Undo an Organize run…", "link"),
            openBackups = Kit.IconButton(Icons.Folder, "Open backups", "ghost");
        private readonly Queue<CoverJob> waitingJobs = new Queue<CoverJob>();
        private readonly HashSet<string> queuedAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> queueHistory = new List<string>();
        private readonly ObservableCollection<string> queueLines = new ObservableCollection<string>();
        private CoverJob currentJob;
        private bool queueRunning;
        private int jobsFinished, jobsWithIssues;
        private Task queueTask = Task.CompletedTask;
        private readonly Button clearWaiting = Kit.Button("Clear waiting jobs");
        internal int QueuedCount => waitingJobs.Count + (currentJob == null ? 0 : 1);
        internal Task QueueCompletion => queueTask;

        private sealed class CoverJob
        {
            internal Album Album;
            internal string Source, Artist, Phase = "Waiting";
            internal bool OnlyMissing, FolderCover;
            internal Func<Task<byte[]>> LoadImage;
            internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string Name => Album.Artist + " · " + Album.Title;
        }

        private Task EnqueueCover(Album album, string source, Func<Task<byte[]>> loadImage)
        {
            if (busy || maintenanceRunning || queuedAlbums.Contains(album.Key)) return Task.CompletedTask;
            // Freeze this album, image source and options before the user moves to another row.
            var job = new CoverJob { Album = album, Source = source, LoadImage = loadImage,
                OnlyMissing = onlyMissing.On(), FolderCover = folderCover.IsEnabled && folderCover.On() };
            return EnqueueJob(job);
        }

        private Task EnqueueJob(CoverJob job)
        {
            var album = job.Album;
            if (busy || maintenanceRunning || queuedAlbums.Contains(album.Key)) return Task.CompletedTask;
            if (!queueRunning) { jobsFinished = jobsWithIssues = 0; issues.Clear(); }
            waitingJobs.Enqueue(job); queuedAlbums.Add(album.Key);
            status.Text = "Queued " + album.Title + ". You can continue with another album.";
            if (!queueRunning)
            {
                queueRunning = true;
                queueTask = ProcessQueueAsync();
            }
            UpdateQueueControls();
            return job.Completion.Task;
        }

        private async Task ProcessQueueAsync()
        {
            // Always return from the click handler before starting image reads or file writes.
            await Task.Yield();
            while (waitingJobs.Count > 0)
            {
                var job = currentJob = waitingJobs.Dequeue();
                bool succeeded = false;
                job.Phase = job.Artist == null ? "Loading cover" : "Updating artists";
                progress.Value = 0; progress.IsIndeterminate = true; progressPercent.Text = "";
                status.Text = job.Phase + " · " + job.Name;
                UpdateQueueControls();
                var reporter = new Throttled<ProgressInfo>(p =>
                {
                    if (currentJob != job) return;
                    status.Text = job.Album.Title + " · " + p.Message + (waitingJobs.Count > 0 ? " · " + waitingJobs.Count + " waiting" : "");
                    if (p.Total > 0)
                    {
                        progress.IsIndeterminate = false;
                        progress.Value = Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total)));
                        progressPercent.Text = (int)progress.Value + "%";
                    }
                });
                try
                {
                    EditResult result;
                    if (job.Artist != null)
                        result = await Task.Run(() => editor.ApplyArtist(job.Album, job.Artist, reporter));
                    else
                    {
                        byte[] image = await Task.Run(job.LoadImage);
                        job.Phase = "Saving covers"; UpdateQueueDisplay();
                        result = await Task.Run(() => editor.Apply(job.Album, image, job.OnlyMissing, job.FolderCover, reporter));
                    }
                    issues.AddRange(result.Issues);
                    if (result.Entries.Count > 0) { lastEdit = result; lastEditedFolder = job.Album.Folder; }
                    if (job.Artist == null && albums.SelectedAlbum?.Key == job.Album.Key) pending = null;
                    // Refresh only this folder, preserving whatever the user is preparing elsewhere.
                    await RefreshFolderAsync(job.Album.Folder, null, preserveStaged: true);
                    succeeded = result.Issues.Count == 0;
                    RememberJob((succeeded ? "Done" : "Needs attention") + " · " + job.Name + " · " + result.TracksWritten + " tracks" + (job.Artist != null ? " · artist: " + job.Artist : ""));
                }
                catch (Exception e)
                {
                    issues.Add(job.Name + ": " + e.Message);
                    RememberJob("Failed · " + job.Name + " · " + e.Message);
                }
                finally
                {
                    // A failed download or write must not stop the remaining queued albums.
                    jobsFinished++; if (!succeeded) jobsWithIssues++;
                    job.LoadImage = null; queuedAlbums.Remove(job.Album.Key); currentJob = null;
                    UpdateQueueControls(); job.Completion.TrySetResult(succeeded);
                }
            }
            queueRunning = false;
            progress.IsVisible = progressPercent.IsVisible = false;
            status.Text = "Queue finished · " + jobsFinished + " albums processed" +
                (jobsWithIssues > 0 ? " · " + jobsWithIssues + " need attention (see Issues)." : " · Backups saved. Undo is available.");
            UpdateQueueControls();
        }

        private void UpdateQueueControls()
        {
            undoFromActivity.IsEnabled = !busy && !queueRunning && !maintenanceRunning && lastEdit != null && lastEdit.Entries.Count > 0;
            lastChange.Text = lastEdit != null && lastEdit.Entries.Count > 0 ? "Last change: " + (lastEditedFolder ?? "") + " · " + lastEdit.Entries.Count + (lastEdit.Entries.Count == 1 ? " file" : " files") : "Nothing to undo yet in this session.";
            lastChange.Tip(lastChange.Text);
            browse.IsEnabled = !busy && !queueRunning && !maintenanceRunning;
            scan.IsEnabled = !maintenanceRunning && !writing && (!queueRunning || busy);
            undo.IsEnabled = !busy && !queueRunning && !maintenanceRunning && lastEdit != null && lastEdit.Entries.Count > 0;
            UpdateIssues();
            var selected = albums.SelectedAlbum;
            bool canEdit = !busy && !maintenanceRunning && selected != null && !queuedAlbums.Contains(selected.Key);
            choose.IsEnabled = onlyMissing.IsEnabled = canEdit;
            folderCover.IsEnabled = canEdit && !SharedFolder(selected);
            online.SetActive(canEdit && !showingArtists);
            if (queueRunning) { progress.IsVisible = true; progressPercent.IsVisible = true; }
            UpdateApplyButton(); UpdateLibraryTools(); albums.RefreshBadges(); UpdateQueueDisplay();
        }

        private void RememberJob(string message)
        {
            queueHistory.Insert(0, message);
            if (queueHistory.Count > 80) queueHistory.RemoveAt(queueHistory.Count - 1);
        }

        private void UpdateQueueDisplay()
        {
            UpdateBadges();
            var lines = new List<string>();
            if (currentJob != null) lines.Add(currentJob.Phase + " · " + currentJob.Name);
            foreach (var job in waitingJobs) lines.Add("Waiting · " + job.Name);
            if (QueuedCount == 0) lines.Add("No albums waiting.");
            lines.AddRange(queueHistory);
            queueLines.Clear(); foreach (string line in lines) queueLines.Add(line);
            clearWaiting.IsEnabled = waitingJobs.Count > 0;
        }

        internal void ClearWaitingJobs()
        {
            while (waitingJobs.Count > 0)
            {
                var job = waitingJobs.Dequeue(); queuedAlbums.Remove(job.Album.Key);
                job.LoadImage = null; RememberJob("Cancelled · " + job.Name); job.Completion.TrySetResult(false);
            }
            UpdateQueueControls();
        }

        private void ShowQueue() => Navigate(Page.Activity);

        // Activity: the work queue and recent results on the left, Undo and problems on the right.
        private Control BuildActivityPage()
        {
            var list = new ListBox { ItemsSource = queueLines, Classes = { "grid" } }.Named("Queued albums and recent results");
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((line, scope) =>
                new TextBlock { Text = line, Margin = new Thickness(12, 0), Height = 34, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 8, 0, 0), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            clearWaiting.HorizontalAlignment = HorizontalAlignment.Left; clearWaiting.Padding = new Thickness(14, 2); clearWaiting.Height = 32; clearWaiting.Margin = new Thickness(0, 8, 0, 0);
            clearWaiting.Click += (s, e) => ClearWaitingJobs();
            clearWaiting.Tip("Remove the albums still waiting in the queue. The album being updated now finishes safely.");
            var queueTitle = Kit.Caption("Queue and recent results"); queueTitle.Margin = new Thickness(0, 0, 0, 8);
            var queueCard = Kit.Frost(Kit.Grid("*", "Auto,*,Auto").Add(queueTitle.Cell(0, 0), Kit.Inset(list).Cell(1, 0), clearWaiting.Cell(2, 0)), new Thickness(14, 12, 14, 12), 14);
            queueCard.Margin = new Thickness(0, 0, 14, 0);

            undoFromActivity.Named("Undo from Activity").Tip("Restore the files from your most recent cover or artist edit in this session, byte for byte (Ctrl+Z).");
            undoFromActivity.Click += async (s, e) => await UndoAsync();
            undoFromActivity.HorizontalAlignment = HorizontalAlignment.Left; undoFromActivity.Padding = new Thickness(14, 2); undoFromActivity.Height = 32;
            openOrganizeUndo.Tip("Organize keeps a journal of its last run, even after restarting the app. Open Organize and click Preview Undo to choose what to restore.");
            openOrganizeUndo.Click += (s, e) => Navigate(Page.Organize);
            openBackups.Tip("Open the PC backup folder in File Explorer. Each run's originals, journal.json and report.csv are kept there.");
            openBackups.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(BackupSettings.Root); StartExplorer(new System.Diagnostics.ProcessStartInfo(BackupSettings.Root) { UseShellExecute = true }); }
                catch (Exception ex) { status.Text = "Could not open the backup folder: " + ex.Message; }
            };
            lastChange.TextWrapping = Avalonia.Media.TextWrapping.Wrap; lastChange.MaxLines = 2;
            var undoStack = new StackPanel { Spacing = 6 };
            undoStack.Children.Add(Kit.Caption("Undo"));
            undoStack.Children.Add(lastChange); undoStack.Children.Add(undoFromActivity);
            var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            more.Children.Add(openOrganizeUndo); more.Children.Add(openBackups);
            undoStack.Children.Add(more);
            var undoCard = Kit.Frost(undoStack, new Thickness(14, 12, 14, 12), 14);

            var problems = new ListBox { ItemsSource = issueLines, Classes = { "grid" } }.Named("Problems from the last scan or edit");
            problems.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((line, scope) =>
            {
                var text = new TextBlock { Text = line, Margin = new Thickness(12, 7), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                ToolTip.SetTip(text, line);
                return text;
            });
            var problemsTitle = Kit.Caption("Problems"); problemsTitle.Margin = new Thickness(0, 0, 0, 2);
            problemsSummary.Margin = new Thickness(0, 0, 0, 8);
            problemsSummary.Text = "No problems in the last scan or edit.";
            var problemsCard = Kit.Frost(Kit.Grid("*", "Auto,Auto,*").Add(problemsTitle.Cell(0, 0), problemsSummary.Cell(1, 0), Kit.Inset(problems).Cell(2, 0)), new Thickness(14, 12, 14, 12), 14);
            problemsCard.Margin = new Thickness(0, 14, 0, 0);
            var right = Kit.Grid("*", "Auto,*").Add(undoCard.Cell(0, 0), problemsCard.Cell(1, 0));
            return Kit.Grid("1.2*,*", "*").Add(queueCard.Cell(0, 0), right.Cell(0, 1));
        }

        private void SyncIssues()
        {
            if (issueLines.Count == issues.Count && issueLines.SequenceEqual(issues)) return;
            issueLines.Clear(); foreach (string issue in issues) issueLines.Add(issue);
            problemsSummary.Text = issues.Count == 0 ? "No problems in the last scan or edit." : issues.Count + (issues.Count == 1 ? " file" : " files") + " could not be read or changed. Each line says why.";
        }
    }
}
