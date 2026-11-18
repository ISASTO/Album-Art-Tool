using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AlbumArtTool.Management;

namespace AlbumArtTool.Core
{
    // Muzikette keeps its settings, caches and backups in %LocalAppData%\Muzikette. Before 4.0 the app (Music Library
    // Manager) kept them in %LocalAppData%\AlbumArtTool. The first launch moves that folder and rewrites the paths saved
    // inside it (settings, the New music folder, Undo journals) so nothing is lost and Undo keeps working.
    internal static class DataMigration
    {
        internal const string Folder = "Muzikette", LegacyFolder = "AlbumArtTool";

        /// <summary>The data folder to use under <paramref name="localAppData"/>, moving the pre-4.0 folder there first.
        /// If the old folder cannot be moved yet (a file in it is open), it is used as it is and moved on a later launch.</summary>
        internal static string Resolve(string localAppData)
        {
            string current = Path.Combine(localAppData, Folder), legacy = Path.Combine(localAppData, LegacyFolder);
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
            try { Directory.Move(legacy, current); }
            catch (Exception e) when (Scanner.IsFileError(e)) { return legacy; }
            Rewrite(current, legacy, current);
            return current;
        }

        /// <summary>Points every path saved under <paramref name="folder"/> that was inside <paramref name="from"/> to the same place inside <paramref name="to"/>.</summary>
        internal static void Rewrite(string folder, string from, string to)
        {
            string Map(string path)
            {
                if (string.IsNullOrEmpty(path)) return path;
                try { return BackupSettings.Within(path, from) ? to + path.Substring(PathUtil.Clean(from).Length) : path; }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return path; }
            }
            // Settings: one path per line, or tab-separated fields (the last Add to library run).
            foreach (string file in Safe(() => Directory.GetFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)))
                Edit(file, text => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => string.Join("\t", line.Split('\t').Select(field =>
                {
                    string trimmed = field.Trim();
                    string mapped = Map(trimmed);
                    return ReferenceEquals(mapped, trimmed) || mapped == trimmed ? field : mapped;
                })))));
            // Undo journals: in the data folder, and in a backup folder kept elsewhere (its runs may name the New music folder).
            var roots = new List<string> { folder };
            string custom = AppSettings.Read(Path.Combine(folder, "backup-folder.txt"));
            if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom) && !BackupSettings.Within(custom, folder)) roots.Add(custom);
            foreach (string root in roots)
            {
                foreach (string file in Safe(() => Directory.GetFiles(root, "journal.json", SearchOption.AllDirectories)))
                    Edit(file, text =>
                    {
                        var journal = JsonSerializer.Deserialize(text, JournalJson.Default.LibraryJournal);
                        if (journal == null) return text;
                        journal.Root = Map(journal.Root); journal.Directory = Map(journal.Directory);
                        foreach (var entry in journal.Entries) MapEntry(entry, Map);
                        return JsonSerializer.Serialize(journal, JournalJson.Default.LibraryJournal);
                    });
                foreach (string file in Safe(() => Directory.GetFiles(root, "events.jsonl", SearchOption.AllDirectories)))
                    Edit(file, text => string.Join("\n", text.Split('\n').Select(line =>
                    {
                        if (line.Trim().Length == 0) return line;
                        var entry = JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry);
                        if (entry == null) return line;
                        MapEntry(entry, Map);
                        return JsonSerializer.Serialize(entry, JournalJson.Default.JournalEntry);
                    })));
            }
        }

        private static void MapEntry(JournalEntry entry, Func<string, string> map)
        {
            entry.Source = map(entry.Source); entry.Target = map(entry.Target); entry.Backup = map(entry.Backup);
        }

        private static string[] Safe(Func<string[]> list)
        {
            try { return list(); }
            catch (Exception e) when (Scanner.IsFileError(e)) { return Array.Empty<string>(); }
        }

        // Each file is rewritten on its own: one unreadable or damaged file never stops the others.
        private static void Edit(string file, Func<string, string> change)
        {
            try
            {
                string text = File.ReadAllText(file);
                string changed = change(text);
                if (changed == text) return;
                string temporary = file + ".tmp";
                File.WriteAllText(temporary, changed, new UTF8Encoding(false));
                File.Replace(temporary, file, null);
            }
            catch (Exception e) when (Scanner.IsFileError(e) || e is JsonException) { }
        }
    }
}
