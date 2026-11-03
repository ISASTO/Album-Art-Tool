using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using SkiaSharp;

internal static partial class Tests
{
    private static void ArtworkTests()
    {
        using (var decoded = Artwork.Decode(Cover)) Assert(decoded.Width == 600 && decoded.Height == 600, "JPEG covers decode at full size");
        var thumb = Artwork.Thumbnail(Cover);
        Assert(Size(thumb) == (128, 128) && thumb[0] == 0x89, "thumbnails are 128 px PNGs");
        var wide = RectangleArt(3200, 1800);
        Assert(Size(Artwork.Normalize(wide)) == (1600, 900), "large images are scaled to 1600 px without cropping");
        Assert(Size(Artwork.Normalize(Cover)) == (600, 600), "small images are never enlarged");
        var normalized = Artwork.Normalize(wide);
        Assert(normalized[0] == 0xff && normalized[1] == 0xd8, "embedded covers are JPEG");
        foreach (var kind in new[] { ImageKind.Png, ImageKind.Bmp, ImageKind.Gif, ImageKind.Jpeg })
        {
            var bytes = Artwork.Normalize(Cover, 256, kind);
            using (var image = Artwork.Decode(bytes)) Assert(image.Width == 256 && image.Height == 256, kind + " sidecar covers encode and decode");
            Assert(Artwork.FormatForPath("folder." + kind.ToString().ToLowerInvariant()) == kind || kind == ImageKind.Jpeg, kind + " sidecar format follows its extension");
        }
        Assert(Artwork.FormatForPath("cover.jpeg") == ImageKind.Jpeg, "jpeg extension maps to JPEG");
        // Transparency is flattened onto white.
        using (var clear = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            clear.Erase(SKColors.Transparent);
            using (var flat = Artwork.Decode(Artwork.Normalize(Encode(clear, SKEncodedImageFormat.Png, 100))))
            { var pixel = flat.GetPixel(5, 5); Assert(pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245, "transparency is flattened onto white"); }
        }
        Assert(Throws(() => Artwork.Decode(new byte[] { 1, 2, 3 })) && !Artwork.IsValid(new byte[] { 1, 2, 3 }), "invalid image data is rejected");
        Assert(Throws(() => Artwork.Decode(new byte[Artwork.MaxImageBytes + 1])), "images over 30 MB are rejected");
        Assert(Artwork.MimeType(thumb) == "image/png" && Artwork.MimeType(Cover) == "image/jpeg", "picture MIME types follow their bytes");
        var truncated = Cover.Take(Cover.Length / 3).ToArray();
        Assert(Throws(() => Artwork.Decode(new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 2 })), "a header-only JPEG fails validation");
        Assert(!Artwork.IsValid(truncated) || Artwork.IsValid(truncated), "truncated JPEGs never crash validation");
        OrientationTest();
    }

    // EXIF orientation 6 (rotate 90° clockwise) is honored before metadata is stripped.
    private static void OrientationTest()
    {
        byte[] jpeg;
        using (var bitmap = new SKBitmap(200, 100))
        using (var canvas = new SKCanvas(bitmap))
        using (var red = new SKPaint { Color = SKColors.Red })
        { canvas.Clear(SKColors.Blue); canvas.DrawRect(0, 0, 100, 100, red); canvas.Flush(); jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg, 95); }
        // Insert an APP1 Exif segment with Orientation = 6 right after SOI.
        byte[] exif = { 0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0 };
        var rotated = jpeg.Take(2).Concat(exif).Concat(jpeg.Skip(2)).ToArray();
        Assert(Size(rotated) == (100, 200), "EXIF orientation swaps the reported dimensions");
        using (var image = Artwork.Decode(rotated))
        {
            Assert(image.Width == 100 && image.Height == 200, "EXIF-rotated image decodes upright");
            var top = image.GetPixel(50, 20);
            Assert(top.Red > 200 && top.Blue < 60, "rotated content moves the left half to the top");
        }
    }

    private static void RoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture);
        string directory = NewFolder("roundtrip-" + extension.Substring(1));
        string path = CopyTrack(directory, "track" + extension);
        string original = CoverEditor.Hash(path), metadata = Metadata(path), audio = AudioHash(path);
        var album = ScanAlbum(directory);
        Assert(album.MissingCount == 1, extension + " missing cover detected");
        var result = Editor.Apply(album, Cover, true, false);
        Assert(result.TracksWritten == 1 && result.Issues.Count == 0, extension + " embed: " + string.Join(";", result.Issues));
        Assert(ScanAlbum(directory).MissingCount == 0, extension + " cover persisted after reopening");
        Assert(Metadata(path) == metadata, extension + " existing metadata preserved");
        Assert(AudioHash(path) == audio, extension + " decoded audio is unchanged");
        Assert(Scanner.Scan(directory, CancellationToken.None).FilesRead == 1, "backups excluded from scan");
        var undone = Editor.Undo(result);
        Assert(undone.Issues.Count == 0 && CoverEditor.Hash(path) == original, extension + " byte-for-byte undo");
    }

    private static void PartialAlbums()
    {
        string root = NewFolder("partial");
        string first = CopyTrack(root, "one.mp3");
        CopyTrack(root, "two.mp3");
        using (var file = TagLib.File.Create(first))
        {
            file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(Cover)) { Type = TagLib.PictureType.FrontCover },
                new TagLib.Picture(new TagLib.ByteVector(Cover)) { Type = TagLib.PictureType.BackCover, Description = "Back cover" } };
            file.Save();
        }
        var album = ScanAlbum(root);
        Assert(album.Tracks.Count == 2 && album.MissingCount == 1 && album.HasAnyCover, "partial album belongs in both views");
        string hash = CoverEditor.Hash(first);
        var result = Editor.Apply(album, MakeCover(SKColors.Orange), true, false);
        Assert(result.TracksWritten == 1 && CoverEditor.Hash(first) == hash, "fill-only keeps existing art untouched");
        result = Editor.Apply(ScanAlbum(root), MakeCover(SKColors.BlueViolet), false, false);
        Assert(result.TracksWritten == 2, "replacement updates every track");
        using (var file = TagLib.File.Create(first))
            Assert(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Type == TagLib.PictureType.FrontCover && Artwork.IsJpeg(file.Tag.Pictures[0].Data.Data) &&
                Size(file.Tag.Pictures[0].Data.Data) == (600, 600), "a new cover leaves exactly one 600 px JPEG front cover; other pictures are removed");
    }

    private static void MixedAlbums()
    {
        string root = NewFolder("mixed");
        CopyTrack(root, "a.mp3", "First album"); CopyTrack(root, "b.mp3", "Second album");
        var child = Path.Combine(root, "Nested", "Disc 1"); Directory.CreateDirectory(child);
        CopyTrack(child, "c.flac", "First album");
        File.WriteAllText(Path.Combine(root, "broken.mp3"), "This is not an MP3.");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "ignore me");
        var result = Scanner.Scan(root, CancellationToken.None);
        Assert(result.Albums.Count == 3 && result.FilesRead == 3 && result.Issues.Count == 1,
            "recursive scan, same-folder albums, duplicate editions and corrupt-file reporting: " +
            result.Albums.Count + " albums, " + result.FilesRead + " tracks, " + result.Issues.Count + " issues");
        string compilation = NewFolder("compilation");
        string a = CopyTrack(compilation, "one.mp3"); string b = CopyTrack(compilation, "two.mp3");
        using (var file = TagLib.File.Create(a)) { file.Tag.AlbumArtists = new string[0]; file.Tag.Performers = new[] { "Artist A" }; file.Save(); }
        using (var file = TagLib.File.Create(b)) { file.Tag.AlbumArtists = new string[0]; file.Tag.Performers = new[] { "Artist B" }; file.Save(); }
        Assert(ScanAlbum(compilation).Artist == "Various artists", "compilations are not split by track artist");
    }

    private static void Guards()
    {
        string root = NewFolder("guards"); string path = CopyTrack(root, "track.mp3"); var album = ScanAlbum(root);
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = "Changed outside the app"; file.Save(); }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        string current = CoverEditor.Hash(path);
        var blocked = Editor.Apply(album, Cover, false, false);
        Assert(blocked.TracksWritten == 0 && blocked.Issues.Count == 1 && CoverEditor.Hash(path) == current, "stale scan cannot overwrite external edits");
        album = ScanAlbum(root); File.SetAttributes(path, FileAttributes.ReadOnly);
        blocked = Editor.Apply(album, Cover, false, false);
        File.SetAttributes(path, FileAttributes.Normal);
        Assert(blocked.TracksWritten == 0 && blocked.Issues.Count == 1, "read-only file is skipped");
        var edit = Editor.Apply(ScanAlbum(root), Cover, false, false);
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = "Later edit"; file.Save(); }
        current = CoverEditor.Hash(path);
        var undo = Editor.Undo(edit);
        Assert(undo.Restored == 0 && undo.Issues.Count == 1 && CoverEditor.Hash(path) == current && File.Exists(edit.Entries[0].Backup), "undo protects later edits and retains recovery backup");
        Assert(!Directory.GetFiles(root).Any(p => Path.GetFileName(p).StartsWith(".aat-")), "temporary copies cleaned up");
    }

    private static void FolderArt()
    {
        string root = NewFolder("folder-cover"); CopyTrack(root, "track.mp3");
        string cover = Path.Combine(root, "folder.png"); File.WriteAllBytes(cover, Artwork.Normalize(Cover, 256, ImageKind.Png));
        var album = ScanAlbum(root);
        Assert(album.HasAnyCover && album.MissingCount == 1 && album.FolderCovers.Count == 1 && album.Thumbnail != null, "folder art is previewed but missing embedded art is still flagged");
        string original = CoverEditor.Hash(cover);
        var result = Editor.Apply(album, MakeCover(SKColors.Salmon), true, true);
        Assert(result.Entries.Count == 2 && CoverEditor.Hash(cover) != original, "folder cover updated alongside track");
        var bytes = File.ReadAllBytes(cover);
        Assert(bytes[0] == 0x89 && bytes[1] == 0x50, "sidecar retains its correct image encoding");
        Editor.Undo(result);
        Assert(CoverEditor.Hash(cover) == original, "undo restores exact folder image");
        File.Delete(cover);
        result = Editor.Apply(ScanAlbum(root), Cover, true, true);
        Assert(File.Exists(Path.Combine(root, "cover.jpg")), "missing sidecar created");
        Editor.Undo(result);
        Assert(!File.Exists(Path.Combine(root, "cover.jpg")), "undo removes newly created sidecar");
    }

    private static void CancelScan()
    {
        using (var token = new CancellationTokenSource())
        { token.Cancel(); Assert(Scanner.Scan(workspace, token.Token).Cancelled, "scan cancellation"); }
    }

    private static void InvalidArt()
    {
        string root = NewFolder("invalid-cover"); string path = CopyTrack(root, "track.mp3"); string hash = CoverEditor.Hash(path);
        bool rejected = false;
        try { Editor.Apply(ScanAlbum(root), new byte[] { 1, 2, 3 }, false, false); } catch { rejected = true; }
        Assert(rejected && hash == CoverEditor.Hash(path), "invalid images rejected before writes");
        using (var file = TagLib.File.Create(path))
        { file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0xff, 0xd8, 1, 2, 3, 4 })) { Type = TagLib.PictureType.FrontCover } }; file.Save(); }
        Assert(ScanAlbum(root).MissingCount == 1, "a damaged embedded picture does not count as a cover");
    }

    private static void UntaggedMp3()
    {
        string root = NewFolder("untagged"); string path = CopyTrack(root, "track.mp3");
        using (var file = TagLib.File.Create(path)) { file.RemoveTags(TagLib.TagTypes.AllTags); file.Save(); }
        var album = ScanAlbum(root);
        Assert(album.Title == "untagged", "untagged files use folder name");
        var result = Editor.Apply(album, Cover, false, false);
        Assert(result.TracksWritten == 1 && ScanAlbum(root).MissingCount == 0, "cover added to previously untagged MP3");
    }

    private static void CountedScanProgress()
    {
        string root = NewFolder("scan-progress"), album = Path.Combine(root, "A"), broken = Path.Combine(root, "B");
        Directory.CreateDirectory(album); Directory.CreateDirectory(broken);
        CopyTrack(album, "one.mp3"); CopyTrack(album, "two.mp3");
        File.WriteAllText(Path.Combine(broken, "broken.mp3"), "not an audio stream");
        CopyTrack(root, ".aat-temporary.mp3"); File.WriteAllText(Path.Combine(root, "notes.txt"), "not music");
        File.WriteAllBytes(Path.Combine(root, "cover.jpg"), Cover);
        string backup = Path.Combine(root, ".album-art-backups"); Directory.CreateDirectory(backup); CopyTrack(backup, "saved.mp3");
        var updates = new List<ProgressInfo>();
        var result = Scanner.Scan(root, CancellationToken.None, new DirectProgress<ProgressInfo>(u => { lock (updates) updates.Add(u); }), useCache: false);
        Assert(updates[0].Total == 0 && updates[0].Message.StartsWith("Counting"), "scan starts with a separate counting phase");
        var reading = updates.Where(p => p.Total > 0).ToArray();
        Assert(reading.All(p => p.Total == 3), "scan counts supported tracks and excludes backups, temporary copies and images");
        Assert(reading.First().Completed == 0 && reading.Any(p => p.Completed > 0 && p.Completed < p.Total), "scan reports a determinate start and intermediate progress");
        Assert(reading.Last().Completed == 3 && result.FilesRead == 2 && result.Issues.Count == 1, "unreadable tracks count as checked so progress reaches 100 percent");
        Assert(reading.Select(p => p.Completed).SequenceEqual(reading.Select(p => p.Completed).OrderBy(n => n)), "scan counter never moves backwards, even with parallel folders");
        Assert(reading.Last().Message.StartsWith("3/3 tracks scanned"), "scan status includes the numeric checked/total tracker");
        using (var cancel = new CancellationTokenSource())
        {
            result = Scanner.Scan(root, cancel.Token, new DirectProgress<ProgressInfo>(p => { if (p.Total > 0 && p.Completed == 1) cancel.Cancel(); }), useCache: false);
            Assert(result.Cancelled && result.FilesRead == 0 && result.Albums.Count == 0, "cancelling the counted scan excludes a partly scanned album");
        }
        using (var cancel = new CancellationTokenSource())
        {
            result = Scanner.Scan(root, cancel.Token, new DirectProgress<ProgressInfo>(p => cancel.Cancel()));
            Assert(result.Cancelled && result.FilesRead == 0, "counting can be cancelled before tags are opened");
        }
    }

    private static void PerformanceTests()
    {
        string root = NewFolder("scan-performance");
        byte[] cover = MakeCover(SKColors.MediumPurple);
        for (int n = 0; n < 72; n++) CopyTrack(root, n + ".mp3", "One album", "Example artist");
        Editor.Apply(ScanAlbum(root), cover, false, false);
        ScanCache.Clear();
        var timer = Stopwatch.StartNew(); var cold = Scanner.Scan(root, CancellationToken.None); long coldMs = timer.ElapsedMilliseconds;
        Assert(cold.FilesOpened == 72 && cold.ArtworkImagesDecoded == 1, "a cold scan validates identical embedded covers once, while checking every music file");
        timer.Restart(); var warm = Scanner.Scan(root, CancellationToken.None); long warmMs = timer.ElapsedMilliseconds;
        Assert(warm.FilesRead == 72 && warm.CachedFiles == 72 && warm.FilesOpened == 0 && warm.ArtworkImagesDecoded == 0, "unchanged folders reuse tags and covers without reopening music files");
        warm.Albums[0].Tracks[0].Performers[0] = "A UI edit"; warm.Albums[0].Tracks.Clear();
        Assert(Scanner.Scan(root, CancellationToken.None).Albums[0].Tracks.Count == 72 && Scanner.Scan(root, CancellationToken.None).Albums[0].Artist == "Example artist", "cached albums are isolated from mutable UI results");
        // The cache survives an app restart.
        ScanCache.Flush(); ScanCache.Unload();
        timer.Restart(); var restarted = Scanner.Scan(root, CancellationToken.None); long restartMs = timer.ElapsedMilliseconds;
        Assert(restarted.FilesOpened == 0 && restarted.FilesRead == 72 && restarted.Albums[0].Thumbnail != null && restarted.Albums[0].MissingCount == 0,
            "a saved cache lets an unchanged library open after a restart without reopening music files");
        File.WriteAllText(ScanCache.FilePath, "damaged"); ScanCache.Unload();
        Assert(Scanner.Scan(root, CancellationToken.None).FilesOpened == 72, "a damaged saved cache is ignored and the library is read normally");
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
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = Scanner.Scan(root, cancellation.Token, new DirectProgress<ProgressInfo>(p => { if (p.Total > 0) cancellation.Cancel(); }));
            Assert(cancelled.Cancelled && cancelled.Albums.Count == 0, "cached scans honor cancellation before exposing an album");
        }
        string copy = Path.Combine(NewFolder("hashed-copy"), "copy.mp3");
        string digest = LibraryRunner.CopyAndHash(changed, copy, CancellationToken.None);
        Assert(digest == CoverEditor.Hash(changed) && digest == CoverEditor.Hash(copy), "single-pass backup copying computes the same complete SHA-256 digest as independent verification");
        var album = ScanAlbum(root);
        int before = album.ArtistChangeCount;
        timer.Restart(); for (int i = 0; i < 20000; i++) _ = album.ArtistChangeCount + album.MissingCount; long countMs = timer.ElapsedMilliseconds;
        Assert(before == album.ArtistChangeCount && countMs < 500, "per-row album counts are cached instead of rescanning every track");
        Console.WriteLine("SCAN: 72 real MP3 files, cold=" + coldMs + " ms, unchanged=" + warmMs + " ms, after restart=" + restartMs + " ms.");
    }

    // Larger synthetic library for timing (run with --benchmark). Not part of the pass/fail suite.
    private static void Benchmark()
    {
        string root = NewFolder("benchmark");
        byte[] cover = Artwork.Normalize(MakeCover(SKColors.SteelBlue, 1400));
        for (int a = 0; a < 60; a++)
        {
            string folder = Path.Combine(root, "Artist " + a % 12, "Album " + a); Directory.CreateDirectory(folder);
            for (int t = 0; t < 12; t++) CopyTrack(folder, t.ToString("D2") + ".mp3", "Album " + a, "Artist " + a % 12);
            Editor.Apply(ScanAlbum(folder), cover, false, false);
        }
        foreach (int degree in new[] { 1, Scanner.Parallelism })
        {
            Scanner.Parallelism = degree; ScanCache.Clear();
            var clock = Stopwatch.StartNew(); var result = Scanner.Scan(root, CancellationToken.None);
            Console.WriteLine("BENCHMARK cold scan, " + degree + " reader(s): " + result.FilesRead + " tracks in " + clock.ElapsedMilliseconds + " ms");
        }
        var warm = Stopwatch.StartNew(); Scanner.Scan(root, CancellationToken.None);
        Console.WriteLine("BENCHMARK unchanged rescan: " + warm.ElapsedMilliseconds + " ms");
        ScanCache.Flush(); ScanCache.Unload();
        var restart = Stopwatch.StartNew(); Scanner.Scan(root, CancellationToken.None);
        Console.WriteLine("BENCHMARK first scan after restart (saved cache): " + restart.ElapsedMilliseconds + " ms");
        var library = Scanner.Scan(root, CancellationToken.None);
        var dup = Stopwatch.StartNew(); new DuplicateScanner().Scan(library.Albums, CancellationToken.None);
        Console.WriteLine("BENCHMARK duplicate scan: " + dup.ElapsedMilliseconds + " ms");
    }
}
