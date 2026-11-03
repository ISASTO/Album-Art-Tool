using System;
using System.IO;
using System.Linq;
using System.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Management;

internal static partial class Tests
{
    internal static void SetFilenameTitle(string path, string title)
    {
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = title; file.Save(); }
    }

    private static void FilenameTests()
    {
        foreach (string fixture in Fixtures())
        {
            string extension = Path.GetExtension(fixture), root = NewFolder("filename-" + extension.Substring(1)), backups = NewFolder("filename-backup-" + extension.Substring(1));
            string source = CopyTrack(root, "01 - Wrong name [download id]" + extension); SetFilenameTitle(source, "Rêverie – 星");
            string original = CoverEditor.Hash(source), target = Path.Combine(root, "Rêverie – 星" + extension);
            var options = Options(root, backups); options.EncoderPath = null; options.RenameFromTitle = true;
            var plan = LibraryPlanner.Build(options, CancellationToken.None);
            Assert(plan.Actions.Single().Rename && plan.Actions.Single().Target == target && File.Exists(source) && !File.Exists(target), extension + " filename preview uses the real Unicode Title without changing any files or requiring FFmpeg");
            var journal = LibraryRunner.Run(plan, CancellationToken.None);
            Assert(journal.Issues.Count == 0 && File.Exists(target) && !File.Exists(source) && CoverEditor.Hash(target) == original, extension + " filename-only cleanup preserves every audio, artwork and metadata byte");
            Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == 1 && File.Exists(source) && !File.Exists(target) && CoverEditor.Hash(source) == original, extension + " persisted Undo restores the original filename and exact bytes");
        }
        FilenameConflicts(); FilenameCase(false); FilenameCase(true); FilenameGuards();
        string limited = LibraryPaths.Component(new string('a', 119) + "🎵 more");
        Assert(limited.Length <= 120 && !char.IsHighSurrogate(limited.Last()), "long Unicode titles never end in a broken surrogate pair");
    }

    private static void FilenameConflicts()
    {
        string root = NewFolder("filename-conflicts"), folder = Path.Combine(root, "Ashnikko", "WEEDKILLER"), backups = NewFolder("filename-conflict-backups"); Directory.CreateDirectory(folder);
        string first = CopyTrack(folder, "01 - World Eater.mp3"), second = CopyTrack(folder, "02 - Another name.mp3"), existing = CopyTrack(folder, "World Eater.mp3");
        foreach (string path in new[] { first, second, existing }) SetFilenameTitle(path, "World Eater");
        string invalid = CopyTrack(folder, "03 - download.mp3"), reserved = CopyTrack(folder, "04 - download.mp3"), blocked = CopyTrack(folder, "05 - download.mp3");
        SetFilenameTitle(invalid, "Dying Star: live / acoustic?"); SetFilenameTitle(reserved, "CON"); SetFilenameTitle(blocked, "Intro"); Directory.CreateDirectory(Path.Combine(folder, "Intro.mp3"));
        string missing = CopyTrack(folder, "06 - Untitled.mp3"), blank = CopyTrack(folder, "07 - Blank.mp3"), unusable = CopyTrack(folder, "08 - Dots.mp3");
        SetFilenameTitle(missing, null); SetFilenameTitle(blank, "   "); SetFilenameTitle(unusable, "...");
        var originals = TreeHashes(root); var options = Options(root, backups); options.RenameFromTitle = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Single(a => a.Source == existing).Target == existing && !plan.Actions.Single(a => a.Source == existing).HasWork &&
            plan.Actions.Where(a => a.Source == first || a.Source == second).Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "already-correct titles are reserved and duplicate titles receive distinct destinations");
        Assert(plan.Actions.Single(a => a.Source == invalid).Target == Path.Combine(folder, "Dying Star- live _ acoustic_.mp3") &&
            plan.Actions.Single(a => a.Source == reserved).Target == Path.Combine(folder, "_CON.mp3") &&
            plan.Actions.Single(a => a.Source == blocked).Target == Path.Combine(folder, "Intro (2).mp3"), "invalid Windows characters, reserved device names and existing directories cannot create unsafe destinations");
        Assert(new[] { missing, blank, unusable }.All(p => { var a = plan.Actions.Single(x => x.Source == p); return !a.HasWork && a.Target == p && a.Reason.Contains("Title tag"); }),
            "missing, blank and unusable Title tags keep the filename with an explanation instead of using the scanner's filename fallback");
        Assert(originals.Count == TreeHashes(root).Count && originals.All(x => CoverEditor.Hash(root + x.Key) == x.Value), "recursive rename preview leaves the entire original tree untouched");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.Count == 5 && journal.Entries.All(e => CoverEditor.Hash(e.Target) == e.OriginalHash) && CoverEditor.Hash(existing) == originals[existing.Substring(root.Length)],
            "recursive conflicting filenames are renamed without overwriting any existing music");
        Assert(LibraryPlanner.Build(options, CancellationToken.None).Actions.All(a => !a.HasWork), "repeating cleanup does not keep adding suffixes or renaming already-clean files");
        Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == 5 && originals.Count == TreeHashes(root).Count && originals.All(x => CoverEditor.Hash(root + x.Key) == x.Value),
            "Undo restores every colliding/sanitized filename and preserves the original files");
    }

    private static void FilenameCase(bool square)
    {
        string root = NewFolder("filename-case-" + square), backups = NewFolder("filename-case-backups-" + square), source = CopyTrack(root, "world eater.mp3");
        SetFilenameTitle(source, "World Eater"); if (square) AddRectangleArt(source); string original = CoverEditor.Hash(source);
        var options = Options(root, backups); options.RenameFromTitle = true; options.Square = square;
        var journal = LibraryRunner.Run(LibraryPlanner.Build(options, CancellationToken.None), CancellationToken.None);
        Assert(journal.Issues.Count == 0 && Path.GetFileName(Directory.GetFiles(root, "*.mp3").Single()) == "World Eater.mp3" && (square || CoverEditor.Hash(Path.Combine(root, "World Eater.mp3")) == original),
            "case-only Title cleanup updates the real filename, including when combined with artwork edits: " + square);
        Assert(LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None) == 1 && Path.GetFileName(Directory.GetFiles(root, "*.mp3").Single()) == "world eater.mp3" && CoverEditor.Hash(source) == original,
            "Undo restores original capitalization and bytes after case-only cleanup: " + square);
    }

    private static void FilenameGuards()
    {
        string root = NewFolder("filename-guards"), backups = NewFolder("filename-guard-backups"), source = CopyTrack(root, "01 - old.mp3"); SetFilenameTitle(source, "New title");
        var options = Options(root, backups); options.RenameFromTitle = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None); string target = plan.Actions.Single().Target, original = CoverEditor.Hash(source);
        File.WriteAllText(target, "Created after preview");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 1 && CoverEditor.Hash(source) == original && File.ReadAllText(target) == "Created after preview", "a target created after preview is never overwritten by a rename");
        File.Delete(target); plan = LibraryPlanner.Build(options, CancellationToken.None);
        SetFilenameTitle(source, "A later metadata edit"); File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(10)); string edited = CoverEditor.Hash(source);
        journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 1 && File.Exists(source) && CoverEditor.Hash(source) == edited && !File.Exists(target), "tracks changed since preview are kept under their old names");
        string other = CopyTrack(root, "02 - old.mp3"); SetFilenameTitle(other, "Another title");
        plan = LibraryPlanner.Build(options, CancellationToken.None); plan.Actions.Single(a => a.Source == other).Selected = false;
        journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.Count == 1 && File.Exists(other) && !File.Exists(Path.Combine(root, "Another title.mp3")), "unchecked filename changes remain untouched while selected renames run");
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 1 && File.Exists(source) && CoverEditor.Hash(source) == edited, "Undo of selected renames preserves later metadata edits that preceded the new preview");
    }
}
