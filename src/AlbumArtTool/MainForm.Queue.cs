using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Core;

namespace AlbumArtTool
{
    internal sealed partial class MainForm
    {
        private readonly Button queueButton = Button("Queue (0)");
        private readonly Queue<CoverJob> waitingJobs = new Queue<CoverJob>();
        private readonly HashSet<string> queuedAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> queueHistory = new List<string>();
        private CoverJob currentJob;
        private bool queueRunning;
        private int jobsFinished, jobsWithIssues;
        private Task queueTask = Task.CompletedTask;
        private Form queueWindow;
        private ListBox queueList;
        private Button clearWaiting;
        internal int QueuedCount => waitingJobs.Count + (currentJob == null ? 0 : 1);
        internal Task QueueCompletion => queueTask;

        private sealed class CoverJob
        {
            internal Album Album;
            internal string Source, Phase = "Waiting";
            internal bool OnlyMissing, FolderCover;
            internal Func<Task<byte[]>> LoadImage;
            internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string Name => Album.Artist + " · " + Album.Title;
        }

        private Task EnqueueCover(Album album, string source, Func<Task<byte[]>> loadImage)
        {
            if (busy || queuedAlbums.Contains(album.Key)) return Task.CompletedTask;
            // Freeze this album, image source and options before the user moves to another row.
            var job = new CoverJob { Album = album, Source = source, LoadImage = loadImage,
                OnlyMissing = onlyMissing.Checked, FolderCover = folderCover.Enabled && folderCover.Checked };
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
                job.Phase = "Loading cover";
                progress.Value = 0; progress.Style = ProgressBarStyle.Marquee; progressPercent.Text = "";
                status.Text = "Loading cover · " + job.Name;
                UpdateQueueControls();
                var reporter = new Progress<ProgressInfo>(p =>
                {
                    if (IsDisposed || currentJob != job) return;
                    status.Text = job.Album.Title + " · " + p.Message + (waitingJobs.Count > 0 ? " · " + waitingJobs.Count + " waiting" : "");
                    if (p.Total > 0)
                    {
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total)));
                        progressPercent.Text = progress.Value + "%";
                    }
                });
                try
                {
                    byte[] image = await Task.Run(job.LoadImage);
                    job.Phase = "Saving covers"; UpdateQueueDisplay();
                    var result = await Task.Run(() => editor.Apply(job.Album, image, job.OnlyMissing, job.FolderCover, reporter));
                    issues.AddRange(result.Issues);
                    if (result.Entries.Count > 0) { lastEdit = result; lastEditedFolder = job.Album.Folder; }
                    if (albums.SelectedAlbum?.Key == job.Album.Key) pending = null;
                    // Refresh only this folder, preserving whatever the user is preparing elsewhere.
                    await RefreshFolderAsync(job.Album.Folder, null, preserveStaged: true);
                    succeeded = result.Issues.Count == 0;
                    RememberJob((succeeded ? "Done" : "Needs attention") + " · " + job.Name + " · " + result.TracksWritten + " tracks");
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
            progress.Visible = progressPercent.Visible = false;
            status.Text = "Queue finished · " + jobsFinished + " albums processed" +
                (jobsWithIssues > 0 ? " · " + jobsWithIssues + " need attention (Details)." : " · Backups saved. Undo is available.");
            UpdateQueueControls();
        }

        private void UpdateQueueControls()
        {
            if (IsDisposed) return;
            browse.Enabled = !busy && !queueRunning;
            scan.Enabled = !writing && (!queueRunning || busy);
            undo.Enabled = !busy && !queueRunning && lastEdit != null && lastEdit.Entries.Count > 0;
            issuesButton.Enabled = issues.Count > 0;
            var selected = albums.SelectedAlbum;
            bool canEdit = !busy && selected != null && !queuedAlbums.Contains(selected.Key);
            choose.Enabled = onlyMissing.Enabled = canEdit;
            bool shared = selected != null && library.Albums.Count(a => string.Equals(a.Folder, selected.Folder, StringComparison.OrdinalIgnoreCase)) > 1;
            folderCover.Enabled = canEdit && !shared;
            online.SetActive(canEdit);
            if (queueRunning) { progress.Visible = true; progressPercent.Visible = true; }
            UpdateApplyButton(); albums.Invalidate(); UpdateQueueDisplay();
        }

        private void RememberJob(string message)
        {
            queueHistory.Insert(0, message);
            if (queueHistory.Count > 80) queueHistory.RemoveAt(queueHistory.Count - 1);
        }

        private void UpdateQueueDisplay()
        {
            queueButton.Text = "Queue (" + QueuedCount + ")" + (queueRunning ? " · working" : jobsFinished > 0 ? " · " + jobsFinished + " finished" : "");
            tips.SetToolTip(queueButton, "View work in progress, waiting albums, and recent results. Waiting jobs can be cleared.");
            if (queueWindow == null || queueWindow.IsDisposed) return;
            queueList.BeginUpdate(); queueList.Items.Clear();
            if (currentJob != null) queueList.Items.Add(currentJob.Phase + " · " + currentJob.Name);
            foreach (var job in waitingJobs) queueList.Items.Add("Waiting · " + job.Name);
            if (QueuedCount == 0) queueList.Items.Add("No albums waiting.");
            foreach (string entry in queueHistory) queueList.Items.Add(entry);
            queueList.EndUpdate(); clearWaiting.Enabled = waitingJobs.Count > 0;
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
            if (queueWindow != null && !queueWindow.IsDisposed) { queueWindow.Activate(); return; }
            queueWindow = new Form { Text = "Album artwork queue", ClientSize = new Size(720, 350), MinimumSize = new Size(500, 250),
                StartPosition = FormStartPosition.CenterParent, BackColor = Surface, ForeColor = Ink, Font = new Font("Segoe UI", 10) };
            queueList = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.None,
                HorizontalScrollbar = true, IntegralHeight = false, AccessibleName = "Queued albums and recent results" };
            clearWaiting = Button("Clear waiting jobs");
            clearWaiting.Dock = DockStyle.Right; clearWaiting.Width = 165;
            clearWaiting.Click += (s, e) => ClearWaitingJobs();
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(5) };
            footer.Controls.Add(clearWaiting);
            queueWindow.Controls.Add(queueList); queueWindow.Controls.Add(footer);
            queueWindow.FormClosed += (s, e) => { queueWindow = null; queueList = null; clearWaiting = null; };
            UpdateQueueDisplay(); queueWindow.Show(this);
        }
    }
}
