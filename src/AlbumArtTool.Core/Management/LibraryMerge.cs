using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    /// <summary>One file from New music and where it goes in the library.</summary>
    internal sealed class MergeMove
    {
        internal string Source, Target;
        /// <summary>The library already has this exact file: it is not added again.</summary>
        internal bool AlreadyInLibrary;
    }

    // Moves the New music folder into the library, keeping its Artist \ Album folders. Nothing is overwritten:
    // a different file with the same name is added with " (2)", an identical one is left out. The last merge can be undone.
    internal static class LibraryMerge
    {
        internal static string JournalFile => Path.Combine(BackupSettings.AppData, "last-add-to-library.txt");

        internal static List<MergeMove> Plan(string folder, string root, CancellationToken token = default)
        {
            Check(folder, root);
            var moves = new List<MergeMove>();
            if (!Directory.Exists(folder)) return moves;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                if (name.StartsWith(".aat-", StringComparison.Ordinal) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = Path.GetRelativePath(folder, file);
                string target = Path.Combine(root, relative);
                var move = new MergeMove { Source = file, Target = target };
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length && CoverEditor.Hash(target) == CoverEditor.Hash(file)) move.AlreadyInLibrary = true;
                else
                {
                    string stem = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target)), extension = Path.GetExtension(target);
                    for (int n = 2; File.Exists(move.Target) || taken.Contains(move.Target); n++) move.Target = stem + " (" + n + ")" + extension;
                    taken.Add(move.Target);
                }
                moves.Add(move);
            }
            return moves;
        }

        private static void Check(string folder, string root)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(root)) throw new IOException("Choose a music folder first.");
            if (BackupSettings.Within(folder, root) || BackupSettings.Within(root, folder)) throw new IOException("The New music folder must be outside the music folder (and the other way round).");
        }

        /// <summary>Moves the planned files and records them for Undo. Returns how many files were moved.</summary>
        internal static int Run(IReadOnlyList<MergeMove> moves, string folder, string root, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            Check(folder, root);
            var journal = new StringBuilder();
            int moved = 0, done = 0;
            var work = moves.Where(m => !m.AlreadyInLibrary).ToList();
            try
            {
                foreach (var move in work)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(move.Source) || File.Exists(move.Target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(move.Target));
                    long length = new FileInfo(move.Source).Length;
                    File.Move(move.Source, move.Target);
                    journal.Append(move.Target).Append('\t').Append(move.Source).Append('\t').Append(length).Append('\n');
                    moved++;
                    progress?.Report(new ProgressInfo { Completed = ++done, Total = work.Count, Message = "Adding " + Path.GetFileName(move.Target) + " · " + done + "/" + work.Count });
                }
            }
            finally
            {
                if (moved > 0) AppSettings.Write(JournalFile, journal.ToString());
                RemoveEmptyFolders(folder);
            }
            return moved;
        }

        internal static bool CanUndo => AppSettings.Read(JournalFile) is string text && text.Length > 0;

        /// <summary>Moves the last merged files back to New music, unless they were changed since. Returns how many moved back.</summary>
        internal static int Undo()
        {
            string text = AppSettings.Read(JournalFile);
            if (string.IsNullOrEmpty(text)) return 0;
            int restored = 0;
            foreach (string line in text.Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length != 3 || !long.TryParse(parts[2], out long length)) continue;
                string target = parts[0], source = parts[1];
                try
                {
                    if (!File.Exists(target) || File.Exists(source) || new FileInfo(target).Length != length) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(source));
                    File.Move(target, source);
                    restored++;
                    RemoveEmptyUpTo(Path.GetDirectoryName(target), 2);
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { }
            }
            try { File.Delete(JournalFile); } catch { }
            return restored;
        }

        internal static void RemoveEmptyFolders(string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); } catch (Exception e) when (Scanner.IsFileError(e)) { }
        }

        // An album folder emptied by Undo goes, and its artist folder too when nothing else is in it.
        private static void RemoveEmptyUpTo(string directory, int levels)
        {
            for (int i = 0; i < levels && directory != null; i++, directory = Path.GetDirectoryName(directory))
                try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); else return; } catch { return; }
        }
    }
}
