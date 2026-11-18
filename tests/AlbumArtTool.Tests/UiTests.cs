using System;
using System.IO;
using System.Linq;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Ui;

internal static partial class Tests
{
    // The rename to Muzikette: the pre-4.0 data folder moves, and every path saved inside it follows.
    private static void DataMigrationTests()
    {
        string local = NewFolder("local-app-data"), legacy = Path.Combine(local, DataMigration.LegacyFolder), current = Path.Combine(local, DataMigration.Folder);
        string run = Path.Combine(legacy, "LibraryBackups", "run-1"), newMusic = Path.Combine(legacy, "New music"), music = NewFolder("migration-music");
        Directory.CreateDirectory(run); Directory.CreateDirectory(newMusic);
        File.WriteAllText(Path.Combine(legacy, "appearance.txt"), "dark");
        File.WriteAllText(Path.Combine(legacy, "new-music-folder.txt"), newMusic);
        File.WriteAllText(Path.Combine(legacy, "last-folder.txt"), music);
        File.WriteAllText(Path.Combine(legacy, "last-library-run.txt"), Path.Combine(run, "journal.json"));
        File.WriteAllText(Path.Combine(legacy, "last-add-to-library.txt"), Path.Combine(music, "a.mp3") + "\t" + Path.Combine(newMusic, "a.mp3") + "\t12\n" + Path.Combine(music, "b.mp3") + "\t" + Path.Combine(newMusic, "b.mp3") + "\t34");
        var journal = new LibraryJournal { Root = newMusic, Directory = run };
        journal.Entries.Add(new JournalEntry { Index = 0, Source = Path.Combine(newMusic, "x.mp3"), Target = Path.Combine(music, "x.mp3"), Backup = Path.Combine(run, "files", "x.mp3"), State = "done" });
        journal.Save();
        File.WriteAllText(Path.Combine(run, "events.jsonl"), System.Text.Json.JsonSerializer.Serialize(journal.Entries[0], JournalJson.Default.JournalEntry) + "\n");
        // A custom backup folder elsewhere, whose run changed songs in New music.
        string elsewhere = Path.Combine(NewFolder("custom-backups"), "run-2");
        File.WriteAllText(Path.Combine(legacy, "backup-folder.txt"), Path.GetDirectoryName(elsewhere));
        var other = new LibraryJournal { Root = newMusic, Directory = elsewhere };
        other.Entries.Add(new JournalEntry { Source = Path.Combine(newMusic, "y.mp3"), Target = Path.Combine(newMusic, "z.mp3"), Backup = Path.Combine(elsewhere, "y.mp3"), State = "done" });
        other.Save();

        Assert(DataMigration.Resolve(local) == current && Directory.Exists(current) && !Directory.Exists(legacy), "the pre-4.0 data folder moves to Muzikette");
        string Read(string name) => File.ReadAllText(Path.Combine(current, name)).Trim();
        string movedRun = Path.Combine(current, "LibraryBackups", "run-1"), movedMusic = Path.Combine(current, "New music");
        Assert(Read("appearance.txt") == "dark" && Read("last-folder.txt") == music, "settings move unchanged; paths outside the data folder stay as they were");
        Assert(Read("new-music-folder.txt") == movedMusic && Read("last-library-run.txt") == Path.Combine(movedRun, "journal.json"), "saved paths inside the data folder follow it");
        Assert(Read("last-add-to-library.txt").Replace("\r", "") == Path.Combine(music, "a.mp3") + "\t" + Path.Combine(movedMusic, "a.mp3") + "\t12\n" + Path.Combine(music, "b.mp3") + "\t" + Path.Combine(movedMusic, "b.mp3") + "\t34",
            "the last Add to library run can still be undone");
        var moved = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(movedRun, "journal.json")), JournalJson.Default.LibraryJournal);
        Assert(moved.Directory == movedRun && moved.Root == movedMusic && moved.Entries[0].Backup == Path.Combine(movedRun, "files", "x.mp3") &&
            moved.Entries[0].Source == Path.Combine(movedMusic, "x.mp3") && moved.Entries[0].Target == Path.Combine(music, "x.mp3") && moved.Entries[0].State == "done", "Organize's Undo journal points to the moved backups");
        var evented = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(movedRun, "events.jsonl")).Trim(), JournalJson.Default.JournalEntry);
        Assert(evented.Backup == Path.Combine(movedRun, "files", "x.mp3"), "an interrupted run's event log follows too");
        var outside = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(elsewhere, "journal.json")), JournalJson.Default.LibraryJournal);
        Assert(outside.Directory == elsewhere && outside.Entries[0].Source == Path.Combine(movedMusic, "y.mp3") && outside.Entries[0].Backup == Path.Combine(elsewhere, "y.mp3"),
            "a run kept in a custom backup folder keeps its backups and finds New music in its new place");
        Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, "appearance.txt"), "light");
        Assert(DataMigration.Resolve(local) == current && Read("appearance.txt") == "dark" && Directory.Exists(legacy), "once moved, a reappearing old folder is left alone");
        string fresh = NewFolder("fresh-local");
        Assert(DataMigration.Resolve(fresh) == Path.Combine(fresh, DataMigration.Folder) && !Directory.Exists(Path.Combine(fresh, DataMigration.LegacyFolder)), "a new install uses the Muzikette folder");
    }

    // Organize presets, theme preference and path display (no window needed).
    private static void PreferenceTests()
    {
        string file = Path.Combine(NewFolder("presets"), "organize-presets.txt");
        Assert(OrganizePresets.LoadCustom(file).Count == 0, "no custom presets before any are saved");
        Assert(OrganizePresets.BuiltIn.Select(p => p.Name).SequenceEqual(new[] { "Tidy tags", "Standardize artwork", "Full clean-up" }) && OrganizePresets.BuiltIn.All(p => p.BuiltIn && p.Options.All(OrganizePresets.Keys.Contains)),
            "the three built-in presets only use known options");
        var custom = OrganizePresets.LoadCustom(file);
        Assert(OrganizePresets.Upsert(custom, "  Before\tsyncing\nmy phone ", new[] { OrganizePresets.Convert, OrganizePresets.Rename, "unknown" }, file)?.Name == "Before syncing my phone", "preset names are cleaned to one line");
        Assert(OrganizePresets.Upsert(custom, "tidy TAGS", new[] { OrganizePresets.Convert }, file) == null && OrganizePresets.Upsert(custom, " \t ", new[] { OrganizePresets.Convert }, file) == null, "built-in and empty names are refused");
        OrganizePresets.Upsert(custom, "Archive only", new[] { OrganizePresets.Archive }, file);
        OrganizePresets.Upsert(custom, "before syncing my phone", new[] { OrganizePresets.Convert, OrganizePresets.Combine }, file);
        var loaded = OrganizePresets.LoadCustom(file);
        Assert(loaded.Count == 2 && loaded.Single(p => p.Name == "before syncing my phone").Options.SetEquals(new[] { OrganizePresets.Convert, OrganizePresets.Combine }) && !loaded.Any(p => p.Options.Contains("unknown")),
            "saving under an existing name replaces that preset and unknown options are dropped");
        Assert(OrganizePresets.CleanName(new string('x', 90)).Length == OrganizePresets.MaxName, "long preset names are shortened");
        File.WriteAllText(file, "Tidy tags\tconvert\nok\tarchive,square\n\tconvert\nbroken line");
        Assert(OrganizePresets.LoadCustom(file).Select(p => p.Name).SequenceEqual(new[] { "ok" }), "a damaged presets file loads what it can and skips reserved names");

        string theme = Path.Combine(NewFolder("theme"), "appearance.txt");
        Assert(AppSettings.ReadTheme(theme) == ThemeMode.System && AppSettings.ReadDark(theme), "with no saved choice the app follows Windows");
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
            Assert(AppSettings.SaveTheme(mode, theme) && AppSettings.ReadTheme(theme) == mode, "the " + mode + " appearance is saved and read back");
        File.WriteAllText(theme, "light"); Assert(AppSettings.ReadTheme(theme) == ThemeMode.Light && !AppSettings.ReadDark(theme), "older saved light/dark choices are still read");

        Assert(AddMusic.Parallel == AddMusic.DefaultParallel && AddMusic.DefaultParallel == 5, "five songs download at once unless changed");
        AddMusic.Parallel = 8; Assert(AddMusic.Parallel == 8 && File.ReadAllText(AddMusic.ParallelFile).Trim() == "8", "the number of downloads at once is saved on this PC");
        AddMusic.Parallel = 25; Assert(AddMusic.Parallel == 10, "at most ten downloads at once");
        File.WriteAllText(AddMusic.ParallelFile, "zero"); Assert(AddMusic.Parallel == 5, "an unreadable setting falls back to five");
        File.Delete(AddMusic.ParallelFile);
        Assert(Kit.MiddleEllipsis(@"C:\Users\isaac\Music", 60) == @"C:\Users\isaac\Music", "short paths are shown in full");
        string shortened = Kit.MiddleEllipsis(@"C:\Users\isaac\OneDrive - Personal\Music\Library\Albums\Jazz", 40);
        Assert(shortened.StartsWith(@"C:\…\") && shortened.EndsWith(@"Albums\Jazz") && shortened.Length <= 40, "long paths keep the drive and last folders: " + shortened);
        Assert(Kit.MiddleEllipsis("/home/user/" + new string('m', 80), 30).Length <= 30, "a single very long folder name is still shortened");
    }
}
