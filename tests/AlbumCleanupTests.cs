using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;

internal static partial class Tests
{
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
        foreach (var example in examples) Assert(TitleCleanup.Clean(example.Key) == example.Value, "featured-title rule: " + example.Key);
        foreach (string extension in Directory.GetFiles(fixtures).Where(p => Scanner.Extensions.Contains(Path.GetExtension(p))).Select(Path.GetExtension))
            AlbumCleanupRoundTrip(extension);
        ManyDiscs(); DiscTitleVariants(); CleanupWithoutTitleTags(); PartialMergeSelection();
    }
    private static string DiscTrack(string folder, string filename, uint disc, uint number, string title, string album = "A multi-disc album")
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
        var loaded = LibraryJournal.Last(); int restored = LibraryRunner.Undo(loaded, CancellationToken.None);
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
    private static void AlbumCleanupGuiSmoke(string output)
    {
        guiStage = "album cleanup GUI";
        string root = NewFolder("album-cleanup-gui"), album = Path.Combine(root, "Example artist", "A multi-disc album");
        DiscTrack(Path.Combine(album, "Disc 1"), "one.mp3", 1, 1, "One (feat. Guest)"); DiscTrack(Path.Combine(album, "Disc 2"), "two.mp3", 2, 1, "Two");
        Exception failure = null;
        using (var form = new LibraryManagerWindow(root, () => true, _ => { }, _ => Task.CompletedTask))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    foreach (var box in Descendants(form).OfType<CheckBox>()) box.Checked = false;
                    Descendants(form).OfType<TextBox>().Single(t => t.AccessibleName == "PC backup folder").Text = NewFolder("album-cleanup-gui-backups");
                    var merge = Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Combine multi-disc albums");
                    var clean = Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Remove featured artists from titles");
                    merge.Checked = clean.Checked = true; await form.PreviewAsync();
                    Assert(form.CurrentPlan.Actions.Count(a => a.MergeDiscs) == 2 && form.CurrentPlan.Actions.Any(a => a.NewTitle == "One"), "the manager exposes both new opt-in operations in one preview");
                    var files = Descendants(form).OfType<ListView>().Single(l => l.AccessibleName == "Planned library changes");
                    files.Items[0].Checked = false; Assert(files.CheckedItems.Count == 0, "clearing one merge row clears the complete album");
                    files.Items[0].Checked = true; Assert(files.CheckedItems.Count == 2, "selecting a merge row selects the complete album");
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "album-cleanup-options.png"), ImageFormat.Png); }
                    await form.ApplyAsync();
                    using (var file = TagLib.File.Create(Path.Combine(album, "01 - One.mp3"))) Assert(file.Tag.Title == "One" && file.Tag.TrackCount == 2 && file.Tag.DiscCount == 1, "the new manager operations apply their previewed title and disc tags");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
