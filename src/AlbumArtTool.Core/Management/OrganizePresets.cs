using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    /// <summary>A named set of Organize options. Built-in presets ship with the app; custom ones are saved per PC.</summary>
    internal sealed class OrganizePreset
    {
        internal string Name;
        internal bool BuiltIn;
        internal string Description;
        internal HashSet<string> Options = new HashSet<string>(StringComparer.Ordinal);

        internal bool Matches(IEnumerable<string> selected) => Options.SetEquals(selected);
    }

    // Custom presets are stored as plain text, one per line: "name<TAB>option,option".
    // No reflection-based serializer, so it is safe for the NativeAOT build.
    internal static class OrganizePresets
    {
        internal const string Convert = "convert", Compatibility = "compatibility", Square = "square", Shrink = "shrink", SingleCover = "singleCover",
            TitleFromName = "titleFromName", CleanTitles = "cleanTitles", Id3v23 = "id3v23", AlbumArtistFromArtist = "albumArtistFromArtist",
            Organize = "organize", Rename = "rename", Combine = "combine", Duplicates = "duplicates", Archive = "archive";

        internal static readonly string[] Keys = { Convert, Compatibility, Square, SingleCover, Shrink, TitleFromName, AlbumArtistFromArtist, CleanTitles, Id3v23, Organize, Rename, Combine, Duplicates, Archive };
        internal const int MaxName = 40, MaxCustom = 24;

        internal static string FilePath => Path.Combine(BackupSettings.AppData, "organize-presets.txt");

        internal static IReadOnlyList<OrganizePreset> BuiltIn { get; } = new[]
        {
            Make("Tidy tags", "Fill missing titles from filenames and missing album artists from the artist, remove “feat.” credits from titles and standardize MP3 tags to ID3v2.3.", TitleFromName, AlbumArtistFromArtist, CleanTitles, Id3v23),
            Make("Standardize artwork", "Crop embedded artwork to squares, keep one front cover per song and shrink covers over " + Artwork.EmbedSide + " px to a " + Artwork.EmbedSide + " px JPEG.", Square, SingleCover, Shrink),
            Make("Full clean-up", "Convert lossless audio to MP3 320, standardize artwork and tags, remove confirmed duplicates, sort into Artist / Album folders and move old backups off the player.",
                Convert, Square, SingleCover, Shrink, TitleFromName, AlbumArtistFromArtist, Id3v23, Organize, Duplicates, Archive),
        };

        private static OrganizePreset Make(string name, string description, params string[] options) =>
            new OrganizePreset { Name = name, BuiltIn = true, Description = description, Options = new HashSet<string>(options, StringComparer.Ordinal) };

        /// <summary>Trims a preset name to one line of printable text; null when nothing usable is left.</summary>
        internal static string CleanName(string name)
        {
            if (name == null) return null;
            var text = new StringBuilder(name.Length);
            foreach (char c in name) text.Append(char.IsControl(c) ? ' ' : c);
            string clean = string.Join(" ", text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (clean.Length > MaxName) clean = clean.Substring(0, MaxName).TrimEnd();
            return clean.Length == 0 ? null : clean;
        }

        internal static bool IsReserved(string name) => BuiltIn.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

        internal static List<OrganizePreset> LoadCustom(string path = null)
        {
            var presets = new List<OrganizePreset>();
            string text = AppSettings.Read(path ?? FilePath);
            if (text == null) return presets;
            // Before version 2, "shrink" also meant one front cover per song.
            bool v2 = text.StartsWith("#v2", StringComparison.Ordinal);
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string name = CleanName(line.Substring(0, tab));
                if (name == null || IsReserved(name) || presets.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) continue;
                var options = line.Substring(tab + 1).Trim().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(o => o.Trim()).Where(o => Keys.Contains(o));
                var set = new HashSet<string>(options, StringComparer.Ordinal);
                if (!v2 && set.Contains(Shrink)) set.Add(SingleCover);
                presets.Add(new OrganizePreset { Name = name, Options = set });
                if (presets.Count == MaxCustom) break;
            }
            return presets;
        }

        internal static bool SaveCustom(IEnumerable<OrganizePreset> presets, string path = null)
        {
            var lines = presets.Where(p => !p.BuiltIn).Take(MaxCustom).Select(p => CleanName(p.Name) + "\t" + string.Join(",", Keys.Where(p.Options.Contains)));
            return AppSettings.Write(path ?? FilePath, "#v2\n" + string.Join("\n", lines));
        }

        /// <summary>Adds or replaces (same name, ignoring case) a custom preset and saves the list.</summary>
        internal static OrganizePreset Upsert(List<OrganizePreset> custom, string name, IEnumerable<string> options, string path = null)
        {
            name = CleanName(name);
            if (name == null || IsReserved(name)) return null;
            custom.RemoveAll(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (custom.Count >= MaxCustom) return null;
            var preset = new OrganizePreset { Name = name, Options = new HashSet<string>(options.Where(o => Keys.Contains(o)), StringComparer.Ordinal) };
            custom.Add(preset);
            return SaveCustom(custom, path) ? preset : null;
        }
    }
}
