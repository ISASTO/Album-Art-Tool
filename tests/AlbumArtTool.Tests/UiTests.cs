using System;
using System.IO;
using System.Linq;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Ui;

internal static partial class Tests
{
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

        Assert(Kit.MiddleEllipsis(@"C:\Users\isaac\Music", 60) == @"C:\Users\isaac\Music", "short paths are shown in full");
        string shortened = Kit.MiddleEllipsis(@"C:\Users\isaac\OneDrive - Personal\Music\Library\Albums\Jazz", 40);
        Assert(shortened.StartsWith(@"C:\…\") && shortened.EndsWith(@"Albums\Jazz") && shortened.Length <= 40, "long paths keep the drive and last folders: " + shortened);
        Assert(Kit.MiddleEllipsis("/home/user/" + new string('m', 80), 30).Length <= 30, "a single very long folder name is still shortened");
    }
}
