using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using SkiaSharp;

internal static partial class Tests
{
    internal static void SetArtists(string path, string[] performers, string[] albumArtists, string title = null)
    {
        using (var audio = TagLib.File.Create(path))
        {
            audio.Tag.Performers = performers; audio.Tag.AlbumArtists = albumArtists;
            if (title != null) audio.Tag.Title = title;
            audio.Save();
        }
    }

    private static void ArtistRulesTests()
    {
        string root = NewFolder("artist-rules");
        string[] credits = { "BENEE", "BENEE", "BENEE, Guest 1", "BENEE, Guest 2", "BENEE, Guest 3, Guest 4", "BENEE, Guest 2" };
        for (int i = 0; i < credits.Length; i++) SetArtists(CopyTrack(root, i + ".mp3", "BENEE album", "BENEE"), new[] { credits[i] }, new[] { "BENEE" }, "Song " + i);
        var album = ScanAlbum(root);
        Assert(album.MainArtist == "BENEE" && album.ArtistChangeCount == 4, "BENEE featured-artist example suggests BENEE and counts four changed tracks");
        foreach (var track in album.Tracks) SetArtists(track.Path, track.Performers, new string[0]);
        album = ScanAlbum(root);
        Assert(album.MainArtist == "BENEE" && album.Artist == "BENEE", "common lead artist is inferred when album artist tags are absent");
        var edit = Editor.ApplyArtist(album, "BENEE");
        Assert(edit.TracksWritten == 4 && ScanAlbum(root).ArtistChangeCount == 0, "artist cleanup changes guest credits without rewriting already-correct tracks with missing album artist tags");
        string clean = NewFolder("artist-already-correct"), cleanPath = CopyTrack(clean, "one.mp3", "10,000 gecs", "100 gecs");
        SetArtists(cleanPath, new[] { "100 gecs" }, new string[0]);
        string cleanHash = CoverEditor.Hash(cleanPath); var cleanAlbum = ScanAlbum(clean);
        Assert(cleanAlbum.ArtistChangeCount == 0 && Editor.ApplyArtist(cleanAlbum, "100 gecs").TracksWritten == 0 && CoverEditor.Hash(cleanPath) == cleanHash,
            "matching track artists with missing Album artist metadata are not offered or rewritten as cleanup");
        Assert(!ArtistRules.NeedsChange(new Track { Performers = new[] { " 100  GECS " }, AlbumArtists = new[] { "A different album credit" } }, "100 gecs"),
            "capitalization, whitespace and unrelated album credits alone do not create track-artist cleanup");
        Assert(ArtistRules.NeedsChange(new Track { Performers = new[] { "100 gecs", "Guest" }, AlbumArtists = new[] { "100 gecs" } }, "100 gecs"),
            "real additional track-artist credits still require cleanup");
        Assert(ArtistRules.Normalize(" a\t  b ") == "A B" && ArtistRules.Normalize(null) == "", "artist normalization collapses all whitespace and ignores case");
        var cached = new Track { Performers = new[] { "First" } };
        Assert(!ArtistRules.NeedsChange(cached, "first"), "normalized credits are compared");
        cached.Performers = new[] { "Second" };
        Assert(ArtistRules.NeedsChange(cached, "first"), "the cached normalized credit follows a changed artist");
        string bands = NewFolder("artist-band-name");
        SetArtists(CopyTrack(bands, "one.mp3"), new[] { "Earth, Wind & Fire" }, new string[0]);
        SetArtists(CopyTrack(bands, "two.mp3"), new[] { "Earth, Wind & Fire" }, new string[0]);
        Assert(ScanAlbum(bands).MainArtist == "Earth, Wind & Fire", "a band's comma and ampersand are preserved");
        string compilation = NewFolder("artist-compilation");
        SetArtists(CopyTrack(compilation, "one.mp3"), new[] { "First Artist" }, new string[0]);
        SetArtists(CopyTrack(compilation, "two.mp3"), new[] { "Second Artist" }, new string[0]);
        Assert(ScanAlbum(compilation).MainArtist == "", "mixed-artist compilations are not guessed as one artist");
        string before = CoverEditor.Hash(album.Tracks[0].Path);
        Assert(Throws(() => Editor.ApplyArtist(album, "")) && CoverEditor.Hash(album.Tracks[0].Path) == before, "empty artist cannot modify a track");
        var stale = ScanAlbum(root);
        using (var audio = TagLib.File.Create(stale.Tracks[0].Path)) { audio.Tag.Title = "An outside edit"; audio.Save(); }
        File.SetLastWriteTimeUtc(stale.Tracks[0].Path, DateTime.UtcNow.AddSeconds(3));
        edit = Editor.ApplyArtist(stale, "Different name");
        Assert(edit.TracksWritten == 5 && edit.Issues.Count == 1, "artist cleanup skips stale tracks and reports partial failures");
    }

    private static void ArtistRoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture), root = NewFolder("artist-roundtrip-" + extension.Substring(1));
        string path = CopyTrack(root, "track" + extension, "An album", "BENEE");
        SetArtists(path, new[] { "BENEE", "A guest" }, new[] { "BENEE" });
        Editor.Apply(ScanAlbum(root), Cover, false, false);
        using (var audio = TagLib.File.Create(path)) { audio.Tag.PerformersSort = new[] { "BENEE; A guest" }; audio.Save(); }
        string original = CoverEditor.Hash(path), audioHash = AudioHash(path), metadata = NonArtistMetadata(path);
        var edit = Editor.ApplyArtist(ScanAlbum(root), "BENEE");
        Assert(edit.TracksWritten == 1 && edit.Issues.Count == 0, extension + " artist fields can be simplified");
        using (var audio = TagLib.File.Create(path))
        {
            Assert(ArtistRules.Single(audio.Tag.Performers, "BENEE") && ArtistRules.Single(audio.Tag.AlbumArtists, "BENEE"), extension + " artist and album artist persist as BENEE");
            Assert(audio.Tag.PerformersSort.Length == 0 || ArtistRules.Single(audio.Tag.PerformersSort, "BENEE"), extension + " existing artist sort field no longer contains guests");
        }
        Assert(AudioHash(path) == audioHash && NonArtistMetadata(path) == metadata, extension + " artist cleanup preserves decoded audio, covers and other tags");
        Editor.Undo(edit);
        Assert(CoverEditor.Hash(path) == original, extension + " artist Undo restores the exact original file");
    }

    private static string NonArtistMetadata(string path)
    {
        using (var file = TagLib.File.Create(path)) return string.Join("|", file.Tag.Title, file.Tag.Album, file.Tag.Year, file.Tag.Track, file.Tag.Comment,
            string.Join(";", file.Tag.Genres), string.Join(";", file.Tag.Pictures.Select(p => p.Type + ":" + Convert.ToBase64String(p.Data.Data))));
    }

    private static void DuplicateTests()
    {
        string root = NewFolder("duplicates"), a = Path.Combine(root, "Original album"), b = Path.Combine(root, "Album copy");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string first = CopyTrack(a, "one.mp3", "An album", "BENEE"); SetArtists(first, new[] { "BENEE" }, new[] { "BENEE" }, "One");
        string second = CopyTrack(a, "two.flac", "An album", "BENEE"); SetArtists(second, new[] { "BENEE" }, new[] { "BENEE" }, "Two");
        File.Copy(first, Path.Combine(b, "renamed.mp3")); File.Copy(second, Path.Combine(b, "renamed.flac"));
        var scanner = new DuplicateScanner();
        var library = Scanner.Scan(root, CancellationToken.None);
        var exact = scanner.Scan(library.Albums, CancellationToken.None);
        Assert(exact.Songs.Count(g => g.Confirmed) == 2 && exact.Albums.Count(g => g.Confirmed) == 1, "exact song and complete album copies are found across different folder/file names");
        Assert(exact.FileHashes[first] == CoverEditor.Hash(first), "duplicate scan records full-file hashes in the journal format");
        var copyAlbum = ScanAlbum(b);
        Editor.Apply(copyAlbum, MakeCover(SKColors.Coral), false, false);
        library = Scanner.Scan(root, CancellationToken.None);
        var changes = new List<ProgressInfo>();
        var tagged = scanner.Scan(library.Albums, CancellationToken.None, new DirectProgress<ProgressInfo>(u => { lock (changes) changes.Add(u); }));
        Assert(tagged.Songs.Count(g => g.Confirmed && g.Basis.StartsWith("Identical audio")) == 2 && tagged.Albums.Count(g => g.Confirmed) == 1,
            "MP3 and FLAC copies remain confirmed after artwork changes");
        Assert(changes.Last().Completed == 4 && changes.Last().Total == 4, "duplicate progress finishes with an exact checked/total counter");
        Assert(tagged.Songs.All(g => g.Tracks.All(t => !t.Path.Contains(".album-art-backups"))), "duplicate scan excludes artwork backups");
        string report = DuplicateWindow.Csv(tagged);
        Assert(report.Contains("Confirmed") && report.Contains(first) && report.Contains("Same complete set"), "report includes paths, confirmation and album match basis");
        Assert(DuplicateWindow.Csv(new DuplicateResult { Issues = { "=cmd|' /C calc'!A0" } }).Contains("\"'=cmd"), "report cells cannot start spreadsheet formulas");
        string[] hashes = library.Albums.SelectMany(x => x.Tracks).Select(t => CoverEditor.Hash(t.Path)).ToArray();
        scanner.Scan(library.Albums, CancellationToken.None);
        Assert(hashes.SequenceEqual(library.Albums.SelectMany(x => x.Tracks).Select(t => CoverEditor.Hash(t.Path))), "duplicate scan never changes music files");
        File.Delete(Path.Combine(b, "renamed.flac"));
        var incomplete = scanner.Scan(Scanner.Scan(root, CancellationToken.None).Albums, CancellationToken.None);
        Assert(incomplete.Albums.Count == 0, "a partial album copy is not reported as a complete duplicate album");
        using (var cancel = new CancellationTokenSource())
        { cancel.Cancel(); Assert(Throws(() => scanner.Scan(library.Albums, cancel.Token)), "duplicate scan honors cancellation"); }
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
        Assert(scanner.Scan(library.Albums, CancellationToken.None).Issues.Count >= 1, "changed or missing files are skipped instead of being compared from stale data");

        // Same metadata and duration, genuinely different encoded audio: only a possible match.
        string possible = NewFolder("duplicate-possible");
        CopyTrack(possible, "one.mp3");
        string other = Path.Combine(possible, "other.mp3");
        var info = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=0.3", "-c:a", "libmp3lame", other }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)) { process.WaitForExit(); Assert(process.ExitCode == 0, "second distinct test recording generated"); }
        SetArtists(other, new[] { "Test artist" }, new[] { "Test artist" }, "Keep this title");
        var candidates = scanner.Scan(Scanner.Scan(possible, CancellationToken.None).Albums, CancellationToken.None);
        Assert(candidates.Songs.Count == 1 && !candidates.Songs[0].Confirmed, "matching tags and duration do not prove different recordings are identical");
    }

    private static void AlbumCleanupTests()
    {
        var examples = new Dictionary<string, string> {
            { "Song (feat. Guest)", "Song" }, { "Song [FT Guest]", "Song" }, { "Song {featuring Guest}", "Song" },
            { "Song（Feat.Guest）", "Song" }, { "Song (ft: Guest & Second)", "Song" }, { "Song feat. Guest", "Song" },
            { "Song - FT Guest", "Song" }, { "Song (feat Guest) (Live)", "Song (Live)" },
            { "Song feat Guest (2014 Remaster)", "Song (2014 Remaster)" }, { "Song feat Guest - Radio Edit", "Song - Radio Edit" },
            { "Song (Live feat. Guest)", "Song (Live)" }, { "Song (feat. Guest (and friends)) (Remix)", "Song (Remix)" },
            { "Song [Feat. First] (ft. Second)", "Song" }, { "Song (Acoustic)", "Song (Acoustic)" },
            { "Life (With You)", "Life (With You)" }, { "Dance with Me", "Dance with Me" }, { "The Features", "The Features" },
            { "  A  deliberate title -", "  A  deliberate title -" }, { "feat. Guest", "feat. Guest" }
        };
        foreach (var example in examples) Assert(TitleCleanup.Clean(example.Key) == example.Value, "featured-title rule: " + example.Key + " → " + TitleCleanup.Clean(example.Key));
        foreach (string extension in Fixtures().Select(Path.GetExtension)) AlbumCleanupRoundTrip(extension);
        ManyDiscs(); DiscTitleVariants(); CleanupWithoutTitleTags(); PartialMergeSelection(); CombinedAlbumCleanup();
    }

    internal static string DiscTrack(string folder, string filename, uint disc, uint number, string title, string album = "A multi-disc album")
    {
        Directory.CreateDirectory(folder); string path = CopyTrack(folder, filename, album, "Example artist");
        using (var file = TagLib.File.Create(path)) { file.Tag.Disc = disc; file.Tag.DiscCount = 13; file.Tag.Track = number; file.Tag.TrackCount = 2; file.Tag.Title = title; file.Save(); }
        return path;
    }

    private static void AlbumCleanupRoundTrip(string extension)
    {
        string root = NewFolder("album-cleanup-" + extension.Trim('.')), backups = NewFolder("album-cleanup-backups-" + extension.Trim('.'));
        string album = Path.Combine(root, "Example artist", "A multi-disc album");
        for (uint disc = 1; disc <= 2; disc++) for (uint number = 1; number <= 2; number++)
        {
            string path = DiscTrack(Path.Combine(album, "Disc " + disc), number.ToString("D2") + " - Song (feat. Guest)" + extension, disc, number, "Song " + disc + "-" + number + " (feat. Guest) (Live)");
            Editor.Apply(ScanAlbum(Path.GetDirectoryName(path)), Cover, false, false);
        }
        File.WriteAllText(Path.Combine(album, "Disc 1", "booklet.txt"), "The original booklet");
        var originals = TreeHashes(root); var audio = Directory.GetFiles(root, "*" + extension, SearchOption.AllDirectories).ToDictionary(p => p, AudioHash);
        var options = Options(root, backups); options.CombineDiscs = options.RemoveFeaturedTitles = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var rows = plan.Actions.Where(a => a.MergeDiscs).OrderBy(a => a.MergedTrack).ToArray();
        Assert(rows.Length == 4 && rows.Select(a => a.MergedTrack).SequenceEqual(new uint[] { 1, 2, 3, 4 }) && rows.All(a => Path.GetDirectoryName(a.Target) == album), extension + " preview combines both discs into one folder with consecutive numbers");
        Assert(originals.Count == TreeHashes(root).Count && originals.All(p => CoverEditor.Hash(root + p.Key) == p.Value), extension + " disc and title preview is read-only");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0, extension + " merge/title cleanup succeeded: " + string.Join(";", journal.Issues));
        foreach (var row in rows)
        {
            using (var file = TagLib.File.Create(row.Target))
            {
                Assert(file.Tag.Track == row.MergedTrack && file.Tag.TrackCount == 4 && file.Tag.Disc == 1 && file.Tag.DiscCount == 1, extension + " merged track/disc tags persist");
                Assert(file.Tag.Album == "A multi-disc album" && file.Tag.Title.EndsWith("(Live)") && !file.Tag.Title.Contains("feat") && file.Tag.FirstPerformer == "Example artist", extension + " removes feature credits while preserving version and artist");
                Assert(file.Tag.Year == 2024 && file.Tag.Comment == "Keep this comment" && file.Tag.Genres.SequenceEqual(new[] { "Test genre" }) && Artwork.GetCover(file.Tag).SequenceEqual(Cover), extension + " preserves unrelated tags and exact embedded artwork");
            }
            Assert(AudioHash(row.Target) == audio[row.Source] && !Path.GetFileName(row.Target).Contains("feat"), extension + " keeps decoded audio intact and cleans the filename");
        }
        Assert(File.Exists(Path.Combine(album, "booklet.txt")) && !Directory.Exists(Path.Combine(album, "Disc 1")) && !Directory.Exists(Path.Combine(album, "Disc 2")), extension + " sidecars follow the album and empty disc folders disappear");
        int restored = LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None);
        Assert(restored == journal.Entries.Count && originals.Count == TreeHashes(root).Count && originals.All(p => CoverEditor.Hash(root + p.Key) == p.Value), extension + " persistent Undo restores every original filename, tag, cover and byte");
    }

    private static void ManyDiscs()
    {
        string root = NewFolder("thirteen-discs"), album = Path.Combine(root, "A multi-disc album");
        for (uint disc = 1; disc <= 13; disc++) DiscTrack(Path.Combine(album, "dIsC " + disc), "01 - Song.mp3", disc, 1, "Song from disc " + disc);
        var options = Options(root, NewFolder("thirteen-disc-backups")); options.CombineDiscs = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var rows = plan.Actions.Where(a => a.MergeDiscs).OrderBy(a => a.MergedTrack).ToArray();
        Assert(rows.Length == 13 && rows[9].Track.Disc == 10 && rows[12].MergedTrack == 13 && rows.All(a => a.MergedTrackCount == 13), "13-disc sets use numeric order and have no two-disc limit");
        Assert(rows[0].Target == Path.Combine(album, "01 - Song.mp3") && rows.Skip(1).All(a => Path.GetFileName(a.Target).StartsWith("01 - Song (")),
            "combining discs keeps each song's filename and only adds a suffix when two discs use the same name");
        string plain = NewFolder("merge-keeps-names"), plainAlbum = Path.Combine(plain, "A multi-disc album");
        DiscTrack(Path.Combine(plainAlbum, "Disc 1"), "Opening Song.mp3", 1, 1, "Opening Song"); DiscTrack(Path.Combine(plainAlbum, "Disc 2"), "Closing Song.mp3", 2, 1, "Closing Song");
        var keep = Options(plain, NewFolder("merge-keeps-names-backups")); keep.CombineDiscs = true;
        Assert(LibraryPlanner.Build(keep, CancellationToken.None).Actions.Where(a => a.MergeDiscs).Select(a => Path.GetFileName(a.Target)).OrderBy(n => n).SequenceEqual(new[] { "Closing Song.mp3", "Opening Song.mp3" }),
            "combining discs does not add track numbers to filenames");
        var second = Options(root, NewFolder("thirteen-title-filenames")); second.CombineDiscs = second.RenameFromTitle = true;
        Assert(LibraryPlanner.Build(second, CancellationToken.None).Actions.Where(a => a.MergeDiscs).All(a => Path.GetFileNameWithoutExtension(a.Target) == a.Track.Title), "title-only filenames remain an explicit choice when combining discs");
    }

    private static void DiscTitleVariants()
    {
        string root = NewFolder("disc-title-variants");
        DiscTrack(Path.Combine(root, "A multi-disc album (Disc 1)"), "one.mp3", 0, 1, "One", "A multi-disc album (Disc 1)");
        DiscTrack(Path.Combine(root, "A multi-disc album (Disc 2)"), "two.mp3", 0, 1, "Two", "A multi-disc album (Disc 2)");
        // A separate single-disc album and another copy must remain separate.
        DiscTrack(Path.Combine(root, "Unrelated", "A multi-disc album"), "one.mp3", 1, 1, "A separate copy");
        var options = Options(root, NewFolder("disc-title-backups")); options.CombineDiscs = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(a => a.MergeDiscs) == 2 && plan.Actions.Where(a => a.MergeDiscs).All(a => a.MergedAlbum == "A multi-disc album"), "matching disc suffixes in album tags and folder names combine while unrelated copies stay separate");
        string tagged = NewFolder("tagged-discs-same-folder");
        DiscTrack(tagged, "1-01 One.mp3", 1, 1, "One"); DiscTrack(tagged, "2-01 Two.mp3", 2, 1, "Two");
        options = Options(tagged, NewFolder("tagged-disc-backups")); options.CombineDiscs = true;
        Assert(LibraryPlanner.Build(options, CancellationToken.None).Actions.Count(a => a.MergeDiscs) == 2, "disc metadata is detected even when every song is already in one folder");
    }

    private static void CleanupWithoutTitleTags()
    {
        string root = NewFolder("filename-feature-only"), source = CopyTrack(root, "01 - Song [FT Guest].mp3");
        using (var file = TagLib.File.Create(source)) { file.Tag.Title = null; file.Save(); }
        var options = Options(root, NewFolder("filename-feature-backups")); options.RemoveFeaturedTitles = true;
        var row = LibraryPlanner.Build(options, CancellationToken.None).Actions.Single();
        Assert(row.NewTitle == null && Path.GetFileName(row.Target) == "01 - Song.mp3", "filename-only credits are cleaned without inventing a missing Title tag");
    }

    private static void PartialMergeSelection()
    {
        string root = NewFolder("merge-selection"), album = Path.Combine(root, "A multi-disc album");
        DiscTrack(Path.Combine(album, "CD1"), "one.mp3", 1, 1, "One"); DiscTrack(Path.Combine(album, "CD2"), "two.mp3", 2, 1, "Two");
        var options = Options(root, NewFolder("merge-selection-backups")); options.CombineDiscs = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None); var before = TreeHashes(root); plan.Actions.First(a => a.MergeDiscs).Selected = false;
        Assert(Throws(() => LibraryRunner.Run(plan, CancellationToken.None)) && before.All(p => CoverEditor.Hash(root + p.Key) == p.Value), "incomplete album selection is rejected before any merge changes a file");
    }

    private static void CombinedAlbumCleanup()
    {
        string root = NewFolder("combined-album-cleanup"), album = Path.Combine(root, "Nested", "A multi-disc album");
        string first = DiscTrack(Path.Combine(album, "Disc 1"), "one.flac", 1, 1, "One (feat. Guest)");
        string second = DiscTrack(Path.Combine(album, "Disc 2"), "two.flac", 2, 1, "Two (FT Guest)");
        AddRectangleArt(first); AddRectangleArt(second);
        File.Copy(second, Path.Combine(Path.GetDirectoryName(second), "two (1).flac"));
        File.WriteAllText(Path.Combine(album, "booklet.pdf"), "A booklet"); var original = TreeHashes(root);
        var options = Options(root, NewFolder("combined-album-cleanup-backups"));
        options.Convert = options.Square = options.Duplicates = options.Organize = options.RenameFromTitle = options.CombineDiscs = options.RemoveFeaturedTitles = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(a => a.MergeDiscs) == 2 && plan.Actions.Count(a => a.Archive) == 1 && plan.Actions.Where(a => a.MergeDiscs).All(a => a.Convert && a.Square && a.NewTitle != null),
            "disc merging, title cleanup, cropping, conversion, organization and duplicate removal preview together");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0, "all album operations run together without failed writes: " + string.Join(";", journal.Issues));
        string target = Path.Combine(root, "Example artist", "A multi-disc album");
        foreach (string path in Directory.GetFiles(target, "*.mp3"))
            using (var file = TagLib.File.Create(path))
            {
                Assert(file.Tag.DiscCount == 1 && file.Tag.TrackCount == 2 && !file.Tag.Title.Contains("feat") && !file.Tag.Title.Contains("FT") && file.Properties.AudioBitrate >= 318,
                    "combined output has one numbered album, clean titles and MP3 320 audio");
                foreach (var picture in file.Tag.Pictures) { var size = Size(picture.Data.Data); Assert(size.Width == size.Height, "combined output retains all cropped embedded pictures"); }
            }
        Assert(Directory.GetFiles(target, "*.mp3").Length == 2 && File.Exists(Path.Combine(target, "booklet.pdf")), "the combined album contains only the retained songs and its booklet");
        Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == journal.Entries.Count && original.Count == TreeHashes(root).Count && original.All(p => CoverEditor.Hash(root + p.Key) == p.Value),
            "one persistent Undo restores the exact pre-conversion, pre-merge and pre-cleanup library");
    }
}
