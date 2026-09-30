using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

internal static partial class Tests
{
    private static void QueueGuiSmoke(string output)
    {
        string root = NewFolder("queue-gui");
        string[] names = { "A Folder cover", "B Local cover", "C Online cover", "D Preparing", "E Cancelled" };
        var paths = names.Select(name => Path.Combine(root, name)).ToArray();
        foreach (string path in paths) { Directory.CreateDirectory(path); CopyTrack(path, "one.mp3", Path.GetFileName(path)); }
        CopyTrack(paths[0], "two.mp3", names[0]);
        string sourceCover = Path.Combine(paths[0], "cover.jpg"); File.WriteAllBytes(sourceCover, Cover);
        string localImage = Path.Combine(root, "local.jpg"), nextImage = Path.Combine(root, "next.jpg");
        File.WriteAllBytes(localImage, MakeCover(Color.Coral)); File.WriteAllBytes(nextImage, MakeCover(Color.MediumPurple));
        var service = new GatedCoverSearch();
        Exception failure = null;
        using (var form = new MainForm(root, false, service))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    await form.ScanAsync();
                    var list = Descendants(form).OfType<AlbumList>().Single();
                    var apply = Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Apply cover"));
                    var checks = Descendants(form).OfType<CheckBox>().ToArray();
                    var sidecar = checks.Single(c => c.Text == "Update folder cover images too");
                    void Select(int index) { list.SelectedIndices.Clear(); list.SelectedIndices.Add(index); }
                    Select(2);
                    var online = form.ApplyOnlineCoverAsync(list.SelectedAlbum.Key, new CoverCandidate { Source = "Test source", ImageUrl = "gated" });
                    await service.Started.Task;
                    Assert(!online.IsCompleted && list.Enabled, "album list stays interactive during a pending cover download");
                    Assert(Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Change existing album art")).Enabled, "tabs stay interactive while artwork is being applied");
                    Select(0);
                    Assert(apply.Enabled && apply.Text == "Apply cover to 2 tracks", "a detected folder cover can be applied without choosing or dragging an image");
                    sidecar.Checked = false;
                    var folderJob = form.ApplyAsync();
                    Select(1); form.StageImage(localImage);
                    var localJob = form.ApplyAsync();
                    Assert(form.QueuedCount == 3 && !folderJob.IsCompleted && !localJob.IsCompleted, "multiple albums queue immediately behind active work");
                    Select(2);
                    await form.ApplyOnlineCoverAsync(list.SelectedAlbum.Key, new CoverCandidate { Source = "Duplicate", ImageUrl = "gated" });
                    Assert(form.QueuedCount == 3, "duplicate clicks cannot queue the same album twice");
                    Select(3); form.StageImage(nextImage);
                    sidecar.Checked = true;
                    string expectedStagedCaption = Descendants(form).OfType<Label>().Single(l => l.Text == "New cover · ready to apply").Text;
                    Directory.CreateDirectory(output);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "artwork-queue.png"), ImageFormat.Png); }
                    service.Release.TrySetResult(true);
                    await Task.WhenAll(online, folderJob, localJob); await form.QueueCompletion;
                    Assert(list.SelectedAlbum.Title == names[3] && apply.Enabled && Descendants(form).OfType<Label>().Any(l => l.Text == expectedStagedCaption),
                        "completed jobs preserve another album's selection and staged cover");
                    foreach (string track in Directory.GetFiles(paths[0], "*.mp3"))
                    {
                        using (var audio = TagLib.File.Create(track))
                        using (var artwork = Artwork.Decode(Artwork.GetCover(audio.Tag)))
                            Assert(artwork.Width == 600, "folder-cover shortcut embeds the full image, not the scan thumbnail");
                    }
                    Assert(File.ReadAllBytes(sourceCover).SequenceEqual(Cover), "queued checkbox settings do not change when the user changes options on another album");
                    using (var audio = TagLib.File.Create(Path.Combine(paths[1], "one.mp3")))
                        Assert(Artwork.GetCover(audio.Tag).SequenceEqual(Artwork.Normalize(File.ReadAllBytes(localImage))), "queued local cover remains bound to its original album and image");
                    Assert(ScanAlbum(paths[2]).MissingCount == 0 && ScanAlbum(paths[3]).MissingCount == 1, "online work updates its captured target and leaves unqueued albums alone");
                    Assert(Descendants(form).OfType<Button>().Single(b => b.Text == "Undo last change").Enabled, "Undo becomes available when the queue finishes");

                    // A failed download must not stop a later local-cover job.
                    var failed = form.ApplyOnlineCoverAsync(list.SelectedAlbum.Key, new CoverCandidate { Source = "Broken", ImageUrl = "invalid" });
                    Select(1); form.StageImage(localImage); var following = form.ApplyAsync();
                    await Task.WhenAll(failed, following); await form.QueueCompletion;
                    Assert(ScanAlbum(paths[3]).MissingCount == 1 && ScanAlbum(paths[4]).MissingCount == 0, "failed queue jobs leave their files unchanged and later jobs continue");

                    // Clear waiting work while an active job is paused, without cancelling the active edit.
                    var existing = Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Change existing album art"));
                    existing.PerformClick(); Select(0);
                    service.ResetGate();
                    var active = form.ApplyOnlineCoverAsync(list.SelectedAlbum.Key, new CoverCandidate { Source = "Test source", ImageUrl = "gated" });
                    await service.Started.Task;
                    Select(1); form.StageImage(nextImage); string unchanged = CoverEditor.Hash(list.SelectedAlbum.Tracks[0].Path);
                    var waiting = form.ApplyAsync(); form.ClearWaitingJobs();
                    Assert(form.QueuedCount == 1 && waiting.IsCompleted && !active.IsCompleted, "clearing the waiting queue preserves the active job");
                    service.Release.TrySetResult(true); await active; await form.QueueCompletion;
                    Assert(CoverEditor.Hash(list.SelectedAlbum.Tracks[0].Path) == unchanged, "cancelled waiting jobs do not change their target files");
                }
                catch (Exception ex) { failure = ex; }
                finally { service.Release.TrySetResult(true); form.ClearWaitingJobs(); await form.QueueCompletion; form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }

    private sealed class GatedCoverSearch : ICoverSearch
    {
        private readonly FakeCoverSearch previews = new FakeCoverSearch(Cover);
        internal TaskCompletionSource<bool> Started, Release;
        internal GatedCoverSearch() { ResetGate(); }
        internal void ResetGate()
        {
            Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token) => previews.SearchAsync(query, refresh, progress, token);
        public async Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token)
        {
            if (candidate.ImageUrl == "invalid") throw new IOException("Simulated download failure.");
            if (candidate.ImageUrl == "gated") { Started.TrySetResult(true); await Release.Task; }
            return Artwork.Normalize(MakeCover(Color.Teal));
        }
        public void Dispose() { previews.Dispose(); }
    }
}
