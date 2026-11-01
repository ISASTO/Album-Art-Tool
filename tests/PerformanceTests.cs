using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Management;

internal static partial class Tests
{
    private static void PerformanceTests()
    {
        string root = NewFolder("scan-performance");
        byte[] cover = MakeCover(Color.MediumPurple);
        for (int n = 0; n < 72; n++) CopyTrack(root, n + ".mp3", "One album", "Example artist");
        Editor.Apply(ScanAlbum(root), cover, false, false);
        ScanCache.Clear();
        var timer = Stopwatch.StartNew(); var cold = Scanner.Scan(root, CancellationToken.None); long coldMs = timer.ElapsedMilliseconds;
        Assert(cold.FilesOpened == 72 && cold.ArtworkImagesDecoded == 1, "a cold scan decodes identical embedded covers once, while checking every music file");
        timer.Restart(); var warm = Scanner.Scan(root, CancellationToken.None); long warmMs = timer.ElapsedMilliseconds;
        Assert(warm.FilesRead == 72 && warm.CachedFiles == 72 && warm.FilesOpened == 0 && warm.ArtworkImagesDecoded == 0, "unchanged folders reuse tags and covers without reopening music files");
        warm.Albums[0].Tracks[0].Performers[0] = "A UI edit"; warm.Albums[0].Tracks.Clear();
        Assert(Scanner.Scan(root, CancellationToken.None).Albums[0].Tracks.Count == 72 && Scanner.Scan(root, CancellationToken.None).Albums[0].Artist == "Example artist", "cached albums are isolated from mutable UI results");
        string changed = Path.Combine(root, "0.mp3");
        using (var file = TagLib.File.Create(changed)) { file.Tag.Title = "New title"; file.Save(); }
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(2));
        var refreshed = Scanner.Scan(root, CancellationToken.None);
        Assert(refreshed.CachedFiles == 0 && refreshed.Albums.SelectMany(a => a.Tracks).Single(t => t.Path == changed).Title == "New title", "external tag edits invalidate their folder before a cached rescan");
        File.WriteAllBytes(Path.Combine(root, "cover.jpg"), cover);
        Assert(Scanner.Scan(root, CancellationToken.None).Albums[0].FolderCovers.Count == 1, "adding folder artwork invalidates cached cover discovery");
        File.Delete(Path.Combine(root, "1.mp3"));
        Assert(Scanner.Scan(root, CancellationToken.None).FilesRead == 71, "deleted tracks do not survive cached scans");
        var forced = Scanner.Scan(root, CancellationToken.None, useCache: false);
        Assert(forced.CachedFiles == 0 && forced.FilesOpened == 71, "a full rescan bypasses cached metadata");
        var cancellation = new CancellationTokenSource();
        var cancelled = Scanner.Scan(root, cancellation.Token, new DirectProgress<ProgressInfo>(p => { if (p.Total > 0) cancellation.Cancel(); }));
        Assert(cancelled.Cancelled && cancelled.Albums.Count == 0, "cached scans honor cancellation before exposing an album");
        string copy = Path.Combine(NewFolder("hashed-copy"), "copy.mp3");
        string digest = LibraryRunner.CopyAndHash(changed, copy, CancellationToken.None);
        Assert(digest == CoverEditor.Hash(changed) && digest == CoverEditor.Hash(copy), "single-pass backup copying computes the same complete SHA-256 digest as independent verification");
        Console.WriteLine("SCAN BENCHMARK: 72 real MP3 files, cold=" + coldMs + " ms, unchanged=" + warmMs + " ms; unchanged scan opened 0 audio files. This fixture is not a prediction for a user's storage device.");
    }
}
