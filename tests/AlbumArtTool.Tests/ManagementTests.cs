using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using Encoder = AlbumArtTool.Management.Encoder;

internal static partial class Tests
{
    internal static LibraryOptions Options(string root, string backups) => new LibraryOptions { Root = root, BackupRoot = backups, EncoderPath = Environment.GetEnvironmentVariable("ALBUMART_FFMPEG") };

    internal static void AddRectangleArt(string path)
    {
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures = new TagLib.IPicture[] {
                new TagLib.Picture(new TagLib.ByteVector(RectangleArt())) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png", Description = "Front" },
                new TagLib.Picture(new TagLib.ByteVector(RectangleArt(600, 400))) { Type = TagLib.PictureType.BackCover, MimeType = "image/png", Description = "Back" } };
            file.Save();
        }
    }

    internal static Dictionary<string, string> TreeHashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p.Substring(root.Length), CoverEditor.Hash);

    private static void ManagementTests()
    {
        byte[] image = RectangleArt(); byte[] cropped = Artwork.Square(image);
        using (var square = Artwork.Decode(cropped))
        {
            var pixel = square.GetPixel(0, 0);
            Assert(square.Width == 1000 && square.Height == 1000 && pixel.Green > 100 && pixel.Red < 30 && pixel.Blue < 30, "square crop uses the center at original resolution, without resizing");
        }
        Assert(Artwork.Square(cropped).SequenceEqual(cropped), "already-square artwork remains byte identical");
        foreach (string extension in new[] { ".mp3", ".m4a" })
            Assert(!LibraryPaths.ShouldConvert(new Track { Path = "x" + extension, Bitrate = 256 }, new LibraryOptions { Convert = true }), "default conversion does not upscale lower-bitrate lossy " + extension);
        Assert(!LibraryPaths.ShouldConvert(new Track { Path = "video.mp4", HasVideo = true, Bitrate = 500 }, new LibraryOptions { Convert = true, ConvertOtherLossy = true }), "video containers are not silently replaced with audio-only files");
        Assert(!LibraryPaths.ShouldConvert(new Track { Path = "x.mp3", Bitrate = 320 }, new LibraryOptions { Convert = true }), "existing MP3 320 is not re-encoded");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.flac", Bitrate = 100, Lossless = true }, new LibraryOptions { Convert = true }), "lossless source is convertible even at a low compressed bitrate");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.m4a", Bitrate = 400 }, new LibraryOptions { Convert = true }), "over-320 lossy source is convertible");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.m4a", Bitrate = 128 }, new LibraryOptions { Convert = true, ConvertOtherLossy = true }), "explicit compatibility mode converts other non-MP3 audio");
        Assert(LibraryPaths.Component("AC/DC: Live?") == "AC_DC- Live_" && LibraryPaths.Component("CON") == "_CON", "Windows folder names are sanitized without creating extra directory levels");
        foreach (string fixture in Fixtures()) SquareRoundTrip(fixture);
        ManagementCombined(); ManagementConflicts(); ManagementStaleAndCancel(); ManagementRecovery(); ManagementUnknownDuplicates(); JournalCompatibility(); ShrinkTests(); SinglePictureTests(); Id3StandardTests();
        FilenameTests();
    }

    private static void SquareRoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture), root = NewFolder("manager-square-" + extension.Substring(1)), backups = NewFolder("manager-square-backup-" + extension.Substring(1));
        string path = CopyTrack(root, "one" + extension); AddRectangleArt(path);
        string original = CoverEditor.Hash(path), audio = AudioHash(path), metadata = Metadata(path);
        TagLib.PictureType[] originalTypes;
        using (var file = TagLib.File.Create(path)) originalTypes = file.Tag.Pictures.Select(p => p.Type).ToArray();
        var options = Options(root, backups); options.Square = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(a => a.Square) == 1 && CoverEditor.Hash(path) == original, extension + " crop preview leaves original untouched");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.Single().State == "Committed", extension + " square crop commits successfully: " + string.Join(";", journal.Issues));
        using (var file = TagLib.File.Create(path))
        {
            Assert(file.Tag.Pictures.Select(p => p.Type).SequenceEqual(originalTypes), extension + " square crop preserves embedded image count, types and order");
            Assert(Size(file.Tag.Pictures[0].Data.Data) == (1000, 1000), extension + " full-size front artwork is square");
            Assert(Size(file.Tag.Pictures[1].Data.Data) == (400, 400), extension + " other embedded artwork is square too");
        }
        Assert(AudioHash(path) == audio && Metadata(path) == metadata, extension + " crop preserves decoded audio and music tags");
        int restored = LibraryRunner.Undo(journal, CancellationToken.None);
        Assert(restored == 1 && CoverEditor.Hash(path) == original, extension + " persisted library Undo restores byte-exact original art and file");
    }

    private static void ManagementCombined()
    {
        string root = NewFolder("manager-combined"), backups = NewFolder("manager-combined-backups");
        string nested = Path.Combine(root, "Artist", "Artist", "2019", "Album - An album", "Disc 1"), copy = Path.Combine(root, "Copies", "An album", "Disc 1"), loose = Path.Combine(root, "Downloads", "Mixed");
        Directory.CreateDirectory(nested); Directory.CreateDirectory(copy); Directory.CreateDirectory(loose);
        string first = CopyTrack(nested, "01 - Song.flac", "An album", "Real Artist"); AddRectangleArt(first);
        using (var audio = TagLib.File.Create(first)) { audio.Tag.Disc = 1; audio.Tag.DiscCount = 2; audio.Save(); }
        string duplicate = Path.Combine(copy, "01 - Song.flac"); File.Copy(first, duplicate);
        string lower = CopyTrack(loose, "quiet.mp3", "Lower album", "Quiet Artist");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(nested), "booklet.pdf"), "Test booklet");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(nested), "cover.png"), RectangleArt());
        string missing = CopyTrack(root, "untagged.mp3");
        using (var audio = TagLib.File.Create(missing)) { audio.RemoveTags(TagLib.TagTypes.AllTags); audio.Save(); }
        string oldBackups = Path.Combine(nested, ".album-art-backups", "old-run"); Directory.CreateDirectory(oldBackups); File.Copy(lower, Path.Combine(oldBackups, "old.mp3"));
        var original = TreeHashes(root);
        var options = Options(root, backups); options.Convert = options.Square = options.Organize = options.Duplicates = options.ArchiveOldBackups = true;
        var updates = new List<ProgressInfo>(); var plan = LibraryPlanner.Build(options, CancellationToken.None, new DirectProgress<ProgressInfo>(u => { lock (updates) updates.Add(u); }));
        Assert(original.All(x => CoverEditor.Hash(root + x.Key) == x.Value), "combined preview does not move, remove or edit any file");
        Assert(plan.Actions.Count(a => a.Keeper != null) == 1 && plan.Actions.Count(a => a.Convert) == 1, "duplicate lossless album copy is removed once, keeper alone is converted");
        Assert(plan.Actions.Single(a => a.Source == missing).Target == missing, "missing artist/album tags leave the loose file in place");
        var journal = LibraryRunner.Run(plan, CancellationToken.None, new DirectProgress<ProgressInfo>(u => { lock (updates) updates.Add(u); }));
        Assert(journal.Issues.Count == 0, "combined run completes without failures: " + string.Join(";", journal.Issues));
        string target = Path.Combine(root, "Real Artist", "An album", "Disc 1", "01 - Song.mp3");
        Assert(File.Exists(target) && !File.Exists(first) && !File.Exists(duplicate), "nested Artist/Artist/Year layouts collapse into canonical tagged album/disc folders with one MP3");
        using (var file = TagLib.File.Create(target))
        {
            Assert(file.Properties.AudioBitrate >= 318 && file.Tag.Album == "An album" && file.Tag.FirstPerformer == "Real Artist", "converted MP3 is 320 kbps and retains main metadata");
            Assert(Size(file.Tag.Pictures[0].Data.Data) == (1000, 1000), "combined conversion retains and squares original full-resolution artwork");
        }
        Assert(File.Exists(Path.Combine(root, "Real Artist", "An album", "booklet.pdf")), "booklet stays with recognized album during recursive organization");
        Assert(CoverEditor.Hash(Path.Combine(root, "Quiet Artist", "Lower album", "quiet.mp3")) == original[lower.Substring(root.Length)], "lower-bitrate MP3 is moved without any re-encoding or metadata edits");
        Assert(!Directory.Exists(oldBackups) && journal.Entries.Any(e => e.Archive && e.Source.Contains(".album-art-backups")), "legacy playable backups are archived outside the active player tree");
        Assert(LibraryJournal.Last().FilePath == journal.FilePath, "last library run journal survives reconstruction from disk");
        int restored = LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None);
        Assert(restored == journal.Entries.Count && original.Count == TreeHashes(root).Count && original.All(x => CoverEditor.Hash(root + x.Key) == x.Value), "combined conversion/removal/crop/move Undo restores the entire original tree and exact bytes");
    }

    private static void ManagementConflicts()
    {
        string root = NewFolder("manager-conflicts"), backups = NewFolder("manager-conflict-backups"), incoming = Path.Combine(root, "Unsorted"), canonical = Path.Combine(root, "Artist", "Album");
        Directory.CreateDirectory(incoming); Directory.CreateDirectory(canonical);
        string first = CopyTrack(incoming, "song.mp3", "Album", "Artist"), existing = CopyTrack(canonical, "song.mp3", "Album", "Artist");
        using (var file = TagLib.File.Create(existing)) { file.Tag.Title = "A different version"; file.Save(); }
        string hash = CoverEditor.Hash(existing);
        var options = Options(root, backups); options.Organize = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Single(a => a.Source == first).Target.EndsWith("song (2).mp3"), "filename conflicts allocate another name rather than overwriting a different file");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && CoverEditor.Hash(existing) == hash, "existing destination stays byte identical during a conflicting move");
        using (var file = TagLib.File.Create(plan.Actions.Single(a => a.Source == first).Target)) { file.Tag.Title = "Later user edit"; file.Save(); }
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 0 && journal.Issues.Count > 0, "Undo refuses to overwrite a later edit to a moved track");
    }

    private static void ManagementStaleAndCancel()
    {
        string root = NewFolder("manager-stale"), backups = NewFolder("manager-stale-backups");
        string path = CopyTrack(root, "source.flac"); AddRectangleArt(path);
        var options = Options(root, backups); options.Convert = options.Square = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10)); string hash = CoverEditor.Hash(path);
        var result = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(result.Issues.Count == 1 && CoverEditor.Hash(path) == hash && !File.Exists(Path.ChangeExtension(path, ".mp3")), "files changed after preview are skipped before replacement/removal");
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); result = LibraryRunner.Run(plan, cancel.Token);
            Assert(result.Stopped && result.Entries.Count == 0 && CoverEditor.Hash(path) == hash, "stopping before processing leaves original files untouched");
        }
        options.BackupRoot = Path.Combine(root, "bad-backups");
        Assert(Throws(() => LibraryPlanner.Build(options, CancellationToken.None)), "backups inside the active music/player tree are rejected");
    }

    private static void ManagementUnknownDuplicates()
    {
        string root = NewFolder("manager-unknown-duplicates"), backup = NewFolder("manager-unknown-duplicate-backups"), a = Path.Combine(root, "Unknown album A"), b = Path.Combine(root, "Unknown album B");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string track = CopyTrack(a, "01.mp3");
        using (var audio = TagLib.File.Create(track)) { audio.RemoveTags(TagLib.TagTypes.AllTags); audio.Save(); }
        File.Copy(track, Path.Combine(b, "01.mp3"));
        var options = Options(root, backup); options.Duplicates = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.All(x => !x.Archive), "untagged identical audio in different unknown albums is not automatically removed");
        File.Copy(track, Path.Combine(a, "01 (1).mp3"));
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(x => x.Archive) == 1, "an obvious same-folder untagged file copy can still be removed safely");
        Assert(plan.Actions.Single(x => x.Archive).KeeperHash == CoverEditor.Hash(track), "the retained copy's recorded hash is the real file hash");
    }

    private static void ManagementRecovery()
    {
        string root = NewFolder("manager-recovery"), backups = NewFolder("manager-recovery-backup");
        string first = CopyTrack(root, "one.mp3"), target = Path.Combine(root, "Nested", "one.mp3");
        var journal = new LibraryJournal { Root = root, Directory = Path.Combine(backups, "run") }; journal.Save();
        Directory.CreateDirectory(BackupSettings.AppData); File.WriteAllText(LibraryJournal.LastFile, journal.FilePath);
        var entry = new JournalEntry { Index = 0, Source = first, Target = target, OriginalHash = CoverEditor.Hash(first), WrittenHash = CoverEditor.Hash(first), MoveOnly = true, State = "Prepared" };
        journal.Entries.Add(entry); journal.Save(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(first, target);
        File.AppendAllText(Path.Combine(journal.Directory, "events.jsonl"), "{unfinished");
        var recovered = LibraryJournal.Last();
        Assert(recovered.Entries.Count == 1 && recovered.Entries[0].State == "Prepared", "recovery replays write-ahead records and ignores a partially written tail");
        Assert(LibraryRunner.Undo(recovered, CancellationToken.None) == 1 && File.Exists(first) && !File.Exists(target), "Undo recovers a move interrupted before its final completion record");
        string other = Path.Combine(root, "must-keep.txt"); File.WriteAllText(other, "keep");
        Assert(Throws(() => LibraryRunner.Copy(first, other, CancellationToken.None)) && File.ReadAllText(other) == "keep", "failed CreateNew copy never deletes an existing destination");
    }

    // Journals written by v2.x (JavaScriptSerializer) must still load for Undo after upgrading.
    private static void JournalCompatibility()
    {
        string root = NewFolder("journal-compat"), backups = NewFolder("journal-compat-backups"), directory = Path.Combine(backups, "library-old");
        string source = CopyTrack(root, "old.mp3"), moved = Path.Combine(root, "Moved", "old.mp3");
        string hash = CoverEditor.Hash(source);
        Directory.CreateDirectory(Path.GetDirectoryName(moved)); File.Move(source, moved);
        Directory.CreateDirectory(directory);
        string Escape(string value) => value.Replace("\\", "\\\\");
        string json = "{\"Root\":\"" + Escape(root) + "\",\"Directory\":\"" + Escape(directory) + "\",\"Entries\":[{\"Index\":0,\"Source\":\"" + Escape(source) + "\",\"Target\":\"" + Escape(moved) +
            "\",\"Backup\":null,\"OriginalHash\":\"" + hash + "\",\"WrittenHash\":\"" + hash + "\",\"State\":\"Committed\",\"Error\":null,\"Archive\":false,\"MoveOnly\":true}],\"Issues\":[],\"Stopped\":false}";
        File.WriteAllText(Path.Combine(directory, "journal.json"), json);
        File.WriteAllText(LibraryJournal.LastFile, Path.Combine(directory, "journal.json"));
        var journal = LibraryJournal.Last();
        Assert(journal.Entries.Count == 1 && journal.Entries[0].MoveOnly, "a v2.x journal loads with all fields");
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 1 && File.Exists(source) && CoverEditor.Hash(source) == hash, "a v2.x library run can be undone by the new version");
    }

    private static async Task<int> LiveEncoderSetup()
    {
        using (var setup = new EncoderSetup())
        {
            string exe = await setup.InstallAsync(null, CancellationToken.None);
            Encoder.Validate(exe, CancellationToken.None);
            string folder = NewFolder("live-encoder-conversion"), input = CopyTrack(folder, "input.flac"); AddRectangleArt(input);
            Encoder.Convert(exe, ScanAlbum(folder).Tracks.Single(), Path.Combine(folder, "verified.mp3"), CancellationToken.None, null);
            Console.WriteLine("LIVE ENCODER: verified upstream checksum, license extraction, startup and actual MP3 320 conversion with retained artwork.");
        }
        return 0;
    }
}

internal static partial class Tests
{
    // Photographic-style detail so a PNG is genuinely large and a JPEG genuinely smaller.
    internal static byte[] NoisyArt(int width, int height)
    {
        var random = new Random(width * 31 + height);
        using (var bitmap = new SkiaSharp.SKBitmap(width, height))
        {
            var pixels = new SkiaSharp.SKColor[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new SkiaSharp.SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            bitmap.Pixels = pixels;
            return Encode(bitmap, SkiaSharp.SKEncodedImageFormat.Png, 100);
        }
    }

    private static void ShrinkTests()
    {
        byte[] large = NoisyArt(1800, 1200), small = MakeCover(SkiaSharp.SKColors.Gray, 500);
        byte[] standard = Artwork.Standardize(large);
        Assert(Size(standard) == (600, 400) && Artwork.IsJpeg(standard) && standard.Length < large.Length / 4, "large pictures become 600 px JPEGs with the same shape");
        byte[] fitted = Artwork.Standardize(standard);
        Assert(ReferenceEquals(fitted, standard), "a 600 px quality-88 JPEG is already standard and kept byte for byte");
        byte[] png = Artwork.Standardize(small);
        Assert(Artwork.IsJpeg(small) || Artwork.IsJpeg(png) && Size(png) == (500, 500), "a smaller non-JPEG becomes a JPEG without being enlarged");
        byte[] fine = Artwork.Normalize(NoisyArt(400, 400), 600, ImageKind.Jpeg, 100);
        byte[] lowered = Artwork.Standardize(fine);
        Assert(!ReferenceEquals(lowered, fine) && Size(lowered) == (400, 400) && lowered.Length < fine.Length, "a small JPEG saved above quality 88 is re-encoded at 88 without upscaling");
        foreach (string fixture in Fixtures()) ShrinkRoundTrip(fixture, large, small);
        // Square crop and the artwork standard together: crop at full resolution first, then re-encode.
        string root = NewFolder("shrink-square"), path = CopyTrack(root, "one.flac");
        using (var file = TagLib.File.Create(path)) { file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(NoisyArt(2400, 1800))) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" } }; file.Save(); }
        var options = Options(root, NewFolder("shrink-square-backups")); options.Square = options.Shrink = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Single().Square && plan.Actions.Single().Shrink && plan.Actions.Single().Description == "Square art + Resize art", "square and resize are previewed together");
        Assert(LibraryRunner.Run(plan, CancellationToken.None).Issues.Count == 0, "square crop and resize apply together");
        using (var file = TagLib.File.Create(path)) Assert(Size(file.Tag.Pictures[0].Data.Data) == (600, 600) && file.Tag.Pictures[0].MimeType == "image/jpeg", "cropped artwork is then reduced to a 600 × 600 JPEG");
        // Files that already meet the standard are left alone.
        string plain = NewFolder("shrink-nothing"); string normal = CopyTrack(plain, "one.mp3");
        using (var file = TagLib.File.Create(normal)) { file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(standard)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" } }; file.Save(); }
        options = Options(plain, NewFolder("shrink-nothing-backups")); options.Shrink = true;
        Assert(LibraryPlanner.Build(options, CancellationToken.None).Actions.All(a => !a.HasWork), "a single standard front cover is not re-encoded");
    }

    private static void SinglePictureTests()
    {
        byte[] small = MakeCover(SkiaSharp.SKColors.Gray, 300), keeper = NoisyArt(900, 900), back = NoisyArt(1800, 1200);
        TagLib.IPicture P(byte[] data, TagLib.PictureType type) => new TagLib.Picture(new TagLib.ByteVector(data)) { Type = type, MimeType = data[0] == 0x89 ? "image/png" : "image/jpeg" };
        Assert(Artwork.BestPictureIndex(new[] { P(small, TagLib.PictureType.FrontCover), P(back, TagLib.PictureType.BackCover), P(keeper, TagLib.PictureType.FrontCover) }) == 2,
            "the largest front cover is kept over a smaller one and a bigger back cover");
        Assert(Artwork.BestPictureIndex(new[] { P(back, TagLib.PictureType.BackCover), P(small, TagLib.PictureType.Other) }) == 1 &&
            Artwork.BestPictureIndex(new[] { P(new byte[] { 1, 2, 3 }, TagLib.PictureType.FrontCover), P(small, TagLib.PictureType.Media) }) == 1 &&
            Artwork.BestPictureIndex(new TagLib.IPicture[0]) == -1, "unlabeled pictures beat other kinds and broken images lose to any readable picture");
        foreach (string fixture in Fixtures())
        {
            string extension = Path.GetExtension(fixture), name = extension.Substring(1);
            string root = NewFolder("one-picture-" + name), path = CopyTrack(root, "one" + extension), single = CopyTrack(root, "two" + extension);
            byte[] standard = Artwork.EmbedCover(small);
            using (var file = TagLib.File.Create(path))
            { file.Tag.Pictures = new[] { P(small, TagLib.PictureType.FrontCover), P(back, TagLib.PictureType.BackCover), P(keeper, TagLib.PictureType.FrontCover) }; file.Save(); }
            using (var file = TagLib.File.Create(single)) { file.Tag.Pictures = new[] { P(standard, TagLib.PictureType.FrontCover) }; file.Save(); }
            // Formats that do not store picture types (MP4) report every picture as a front cover, so the largest wins there.
            // APE tags (WavPack) hold only one picture per slot, so the formats differ in what is available to keep.
            byte[] kept, expected;
            using (var file = TagLib.File.Create(path)) kept = file.Tag.Pictures[Artwork.BestPictureIndex(file.Tag.Pictures)].Data.Data;
            expected = Artwork.Standardize(kept); bool resized = !ReferenceEquals(expected, kept);
            string original = CoverEditor.Hash(path), singleHash = CoverEditor.Hash(single), metadata = Metadata(path), audio = AudioHash(path); long before = new FileInfo(path).Length;
            var options = Options(root, NewFolder("one-picture-backup-" + name)); options.EncoderPath = null; options.Shrink = true;
            var plan = LibraryPlanner.Build(options, CancellationToken.None);
            var work = plan.Actions.Where(a => a.HasWork).ToArray();
            string detail = string.Join(" | ", work.Select(a => Path.GetFileName(a.Source) + ": " + a.Description + " — " + a.Reason)) + " | issues: " + string.Join(";", plan.Issues);
            var row = work.FirstOrDefault(a => a.Source == path);
            Assert(work.Length == 1 && row != null && row.SinglePicture && row.Shrink == resized && row.Description == (resized ? "One front cover + Resize art" : "One front cover") && row.Reason.Contains("keeps only the"), extension + " only the song with several pictures is fixed, and the preview names the kept picture: " + detail);
            Assert(LibraryRunner.Run(plan, CancellationToken.None).Issues.Count == 0, extension + " the artwork standard applies cleanly");
            using (var file = TagLib.File.Create(path))
                Assert(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Type == TagLib.PictureType.FrontCover && file.Tag.Pictures[0].Data.Data.AsSpan().SequenceEqual(expected) &&
                    Math.Max(Size(expected).Width, Size(expected).Height) <= 600 && Artwork.IsJpeg(expected), extension + " exactly one JPEG front cover of at most 600 px remains");
            long after = new FileInfo(path).Length;
            Console.WriteLine("ONE PICTURE " + extension + ": " + before + " -> " + after + " bytes");
            Assert(after < before - back.Length / 2, extension + " removing the extra pictures makes the file smaller");
            Assert(Metadata(path) == metadata && AudioHash(path) == audio, extension + " audio and other tags are unchanged");
            Assert(CoverEditor.Hash(single) == singleHash, extension + " songs that already meet the standard are untouched");
            Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == 1 && CoverEditor.Hash(path) == original, extension + " Undo restores every original picture exactly");
        }
        // A lone back cover or unlabeled picture is moved to the front-cover slot.
        string mixed = NewFolder("one-picture-slot"), song = CopyTrack(mixed, "song.mp3");
        byte[] fit = Artwork.EmbedCover(small);
        using (var file = TagLib.File.Create(song)) { file.Tag.Pictures = new[] { P(fit, TagLib.PictureType.BackCover) }; file.Save(); }
        var only = Options(mixed, NewFolder("one-picture-slot-backups")); only.EncoderPath = null; only.Shrink = true;
        var moved = LibraryPlanner.Build(only, CancellationToken.None);
        Assert(moved.Actions.Single().Description == "One front cover" && LibraryRunner.Run(moved, CancellationToken.None).Issues.Count == 0, "a lone back cover is moved, not re-encoded");
        using (var file = TagLib.File.Create(song))
            Assert(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Type == TagLib.PictureType.FrontCover && file.Tag.Pictures[0].Data.Data.AsSpan().SequenceEqual(fit), "the picture now sits in the front-cover slot, byte for byte");
    }

    private static void ShrinkRoundTrip(string fixture, byte[] large, byte[] small)
    {
        string extension = Path.GetExtension(fixture), root = NewFolder("shrink-" + extension.Substring(1)), backups = NewFolder("shrink-backup-" + extension.Substring(1));
        string path = CopyTrack(root, "one" + extension);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(large)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png", Description = "Front" } };
            file.Save();
        }
        string original = CoverEditor.Hash(path), audio = AudioHash(path), metadata = Metadata(path);
        long before = new FileInfo(path).Length;
        var options = Options(root, backups); options.Shrink = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var row = plan.Actions.Single();
        Assert(row.Shrink && !row.SinglePicture && row.Reason.Contains("1800 × 1200 PNG") && CoverEditor.Hash(path) == original, extension + " resize preview names the large picture and changes nothing");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.Single().State == "Committed", extension + " resize commits: " + string.Join(";", journal.Issues));
        using (var file = TagLib.File.Create(path))
        {
            var pictures = file.Tag.Pictures;
            Assert(pictures.Length == 1 && pictures[0].Type == TagLib.PictureType.FrontCover, extension + " one front cover remains");
            Assert(Size(pictures[0].Data.Data) == (600, 400) && pictures[0].MimeType == "image/jpeg", extension + " the large front cover becomes a 600 px JPEG");
        }
        long after = new FileInfo(path).Length;
        Console.WriteLine("SHRINK " + extension + ": " + before + " -> " + after + " bytes");
        Assert(after < before - large.Length / 2, extension + " the file itself gets substantially smaller, not just padded: " + before + " -> " + after);
        Assert(AudioHash(path) == audio && Metadata(path) == metadata, extension + " resizing preserves decoded audio and music tags");
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 1 && CoverEditor.Hash(path) == original, extension + " Undo restores the exact original artwork and file");
    }

    private static void Id3StandardTests()
    {
        string root = NewFolder("id3-standard"), modern = CopyTrack(root, "modern.mp3"), legacy = CopyTrack(root, "legacy.mp3");
        CopyTrack(root, "other.flac");
        using (var file = TagLib.File.Create(modern))
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            tag.Version = 4; tag.Title = "Rêverie – 星"; tag.Year = 2024; tag.Comment = "Keep this comment"; tag.Performers = new[] { "Ünïcode Artist" };
            foreach (var frame in tag.GetFrames<TagLib.Id3v2.TextInformationFrame>().ToArray()) frame.TextEncoding = TagLib.StringType.UTF8;
            file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(Artwork.EmbedCover(Cover))) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" } };
            file.Save();
        }
        using (var file = TagLib.File.Create(legacy))
        {
            file.RemoveTags(TagLib.TagTypes.Id3v2);
            var v1 = file.GetTag(TagLib.TagTypes.Id3v1, true); v1.Title = "Old title"; v1.Album = "Old album"; v1.Performers = new[] { "Old artist" }; v1.Year = 1999;
            file.Save();
        }
        string modernMeta = Metadata(modern), modernAudio = AudioHash(modern), modernHash = CoverEditor.Hash(modern), legacyHash = CoverEditor.Hash(legacy);
        byte[] picture; using (var file = TagLib.File.Create(modern)) picture = file.Tag.Pictures[0].Data.Data;
        var options = Options(root, NewFolder("id3-standard-backups")); options.EncoderPath = null; options.Id3v23 = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var work = plan.Actions.Where(a => a.HasWork).ToArray();
        Assert(work.Length == 2 && work.All(a => a.Id3v23 && a.Description == "ID3v2.3" && a.Source.EndsWith(".mp3")), "only MP3s are standardized");
        Assert(work.Single(a => a.Source == modern).Reason.Contains("ID3v2.4") && work.Single(a => a.Source == legacy).Reason.Contains("no ID3v2 tag"), "the preview names the current tag form");
        Assert(LibraryRunner.Run(plan, CancellationToken.None).Issues.Count == 0, "ID3v2.3 conversion applies cleanly");
        using (var file = TagLib.File.Create(modern))
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
            Assert(tag.Version == 3 && Id3Standard.Verify(file) && tag.GetFrames<TagLib.Id3v2.TextInformationFrame>().All(f => f.TextEncoding == TagLib.StringType.UTF16), "tags are saved as ID3v2.3 with UTF-16 text");
            Assert(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Data.Data.AsSpan().SequenceEqual(picture), "the embedded cover is unchanged");
        }
        string raw = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(modern), 0, 4096);
        Assert(raw.StartsWith("ID3\u0003") && raw.Contains("TYER") && !raw.Contains("TDRC"), "the year is written as an ID3v2.3 TYER frame");
        Assert(Metadata(modern) == modernMeta && AudioHash(modern) == modernAudio, "Unicode titles, artists, year and comment survive the conversion: " + Metadata(modern));
        using (var file = TagLib.File.Create(legacy))
            Assert(file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag { Version: 3 } created && created.Title == "Old title" && created.Album == "Old album" && created.Year == 1999 && created.FirstPerformer == "Old artist",
                "an MP3 with only an ID3v1 tag gets an ID3v2.3 tag carrying the same fields");
        Assert(LibraryPlanner.Build(options, CancellationToken.None).Actions.All(a => !a.HasWork), "standardized files are left alone the next time");
        Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == 2 && CoverEditor.Hash(modern) == modernHash && CoverEditor.Hash(legacy) == legacyHash, "Undo restores the original tags exactly");
    }
}
