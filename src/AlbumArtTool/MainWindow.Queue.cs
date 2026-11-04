using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed partial class MainWindow
    {
        private readonly Button queueButton = Kit.Button("Queue (0)", "chrome");
        private readonly Queue<CoverJob> waitingJobs = new Queue<CoverJob>();
        private readonly HashSet<string> queuedAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> queueHistory = new List<string>();
        private readonly ObservableCollection<string> queueLines = new ObservableCollection<string>();
        private CoverJob currentJob;
        private bool queueRunning;
        private int jobsFinished, jobsWithIssues;
        private Task queueTask = Task.CompletedTask;
        private Window queueWindow;
        private Button clearWaiting;
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
                var reporter = new Progress<ProgressInfo>(p =>
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
            queueButton.Content = "Queue (" + QueuedCount + ")" + (queueRunning ? " · working" : jobsFinished > 0 ? " · " + jobsFinished + " finished" : "");
            queueButton.Tip("View work in progress, waiting albums, and recent results. Waiting jobs can be cleared.");
            if (queueWindow == null) return;
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

        private void ShowQueue()
        {
            if (queueWindow != null) { queueWindow.Activate(); return; }
            var window = queueWindow = new Window { Title = "Album work queue", Width = 720, Height = 350, MinWidth = 500, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var list = new ListBox { ItemsSource = queueLines, Classes = { "grid" } }.Named("Queued albums and recent results");
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((line, scope) =>
                new TextBlock { Text = line, Margin = new Thickness(12, 0), Height = 36, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 9, 0, 0) });
            clearWaiting = Kit.Button("Clear waiting jobs");
            clearWaiting.Width = 165; clearWaiting.HorizontalAlignment = HorizontalAlignment.Right;
            clearWaiting.Click += (s, e) => ClearWaitingJobs();
            var card = Kit.Frost(list, new Thickness(10));
            var layout = Kit.Grid("*", "*,50").Add(card.Cell(0, 0), clearWaiting.Cell(1, 0));
            layout.Margin = new Thickness(14);
            Kit.Prepare(window, layout);
            window.Closed += (s, e) => { queueWindow = null; clearWaiting = null; };
            UpdateQueueDisplay(); window.Show(this);
        }
    }
}
