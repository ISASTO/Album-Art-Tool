using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    internal sealed class JournalEntry
    {
        public JournalEntry() { }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Backup { get; set; }
        public string OriginalHash { get; set; }
        public string WrittenHash { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
        public bool Archive { get; set; }
        public bool MoveOnly { get; set; }
    }
    internal sealed class LibraryJournal
    {
        public LibraryJournal() { }
        public string Root { get; set; }
        public string Directory { get; set; }
        public List<JournalEntry> Entries { get; set; } = new List<JournalEntry>();
        public List<string> Issues { get; set; } = new List<string>();
        public bool Stopped { get; set; }
        internal string FilePath => Path.Combine(Directory, "journal.json");
        internal static string LastFile => Path.Combine(BackupSettings.AppData, "last-library-run.txt");
        internal void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
        }
        internal static LibraryJournal Last()
        {
            if (!File.Exists(LastFile)) return null;
            string path = File.ReadAllText(LastFile); if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return null;
            var journal = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<LibraryJournal>(File.ReadAllText(path));
            if (!Path.GetFullPath(path).Equals(journal.FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The backup journal path does not match its contents.");
            return journal;
        }
    }
    internal static class LibraryRunner
    {
        internal static LibraryJournal Run(LibraryPlan plan, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            var options = plan.Options;
            if (BackupSettings.Within(options.BackupRoot, options.Root)) throw new IOException("Backups must be outside the active music tree.");
            var journal = new LibraryJournal { Root = options.Root, Directory = Path.Combine(options.BackupRoot, "library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 10)) };
            journal.Save(); Directory.CreateDirectory(BackupSettings.AppData); File.WriteAllText(LibraryJournal.LastFile, journal.FilePath);
            var selected = plan.Actions.Where(a => a.Selected && a.HasWork).ToArray();
            var completed = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            foreach (var row in selected)
            {
                if (token.IsCancellationRequested) { journal.Stopped = true; break; }
                var entry = new JournalEntry { Source = row.Source, Target = row.Archive ? null : row.Target, Archive = row.Archive,
                    MoveOnly = !row.Archive && !row.Convert && !row.Square, State = "Preparing" };
                journal.Entries.Add(entry);
                string temp = null; bool changed = false;
                try
                {
                    LibraryPaths.Safe(row.Source, options.Root);
                    if (entry.Target != null) LibraryPaths.Safe(entry.Target, options.Root);
                    if ((File.GetAttributes(row.Source) & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only file skipped.");
                    if (!row.Stamp.Matches(row.Source)) throw new IOException("File changed after preview. Preview again before changing it.");
                    entry.OriginalHash = CoverEditor.Hash(row.Source);
                    if (row.ExpectedHash != null && entry.OriginalHash != row.ExpectedHash) throw new IOException("The duplicate changed after preview. It was kept.");
                    if (row.Keeper != null) VerifyKeeper(row, completed);
                    if (!entry.MoveOnly)
                    {
                        entry.Backup = Path.Combine(journal.Directory, "originals", (journal.Entries.Count - 1).ToString("D6") + "-" + LibraryPaths.Component(Path.GetFileName(row.Source)));
                        Copy(row.Source, entry.Backup, token);
                        if (CoverEditor.Hash(entry.Backup) != entry.OriginalHash) throw new IOException("Backup verification failed. Original kept.");
                    }
                    if (row.Archive)
                    {
                        entry.WrittenHash = null; entry.State = "Prepared"; journal.Save();
                        CheckOriginal(entry); if (row.Keeper != null) VerifyKeeper(row, completed);
                        File.Delete(row.Source); changed = true;
                    }
                    else if (entry.MoveOnly)
                    {
                        entry.WrittenHash = entry.OriginalHash; entry.State = "Prepared"; journal.Save();
                        Directory.CreateDirectory(Path.GetDirectoryName(row.Target)); LibraryPaths.Safe(row.Target, options.Root);
                        CheckOriginal(entry); File.Move(row.Source, row.Target); changed = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(row.Target)); LibraryPaths.Safe(row.Target, options.Root);
                        temp = Path.Combine(Path.GetDirectoryName(row.Target), ".aat-manage-" + Guid.NewGuid().ToString("N") + Path.GetExtension(row.Target));
                        if (row.Convert)
                            Encoder.Convert(options.EncoderPath, row.Track, temp, token, fraction => progress?.Report(new ProgressInfo { Completed = done, Total = selected.Length, Fraction = fraction,
                                Message = "MP3 320 · " + (done + 1) + "/" + selected.Length + " · " + Path.GetFileName(row.Source) }));
                        else Copy(row.Source, temp, token);
                        if (row.Square) SquarePictures(temp);
                        entry.WrittenHash = CoverEditor.Hash(temp); entry.State = "Prepared"; journal.Save();
                        token.ThrowIfCancellationRequested(); CheckOriginal(entry);
                        if (row.Source.Equals(row.Target, StringComparison.OrdinalIgnoreCase)) { File.Replace(temp, row.Source, null); changed = true; }
                        else
                        {
                            File.Move(temp, row.Target); changed = true; entry.State = "TargetCreated"; journal.Save();
                            try { CheckOriginal(entry); File.Delete(row.Source); }
                            catch
                            {
                                if (File.Exists(row.Source) && CoverEditor.Hash(row.Source) == entry.OriginalHash && CoverEditor.Hash(row.Target) == entry.WrittenHash)
                                { File.Delete(row.Target); changed = false; }
                                throw;
                            }
                        }
                    }
                    entry.State = "Committed"; journal.Save(); completed[row.Source] = entry;
                }
                catch (OperationCanceledException)
                {
                    entry.State = changed ? "TargetCreated" : "Cancelled"; journal.Stopped = true; journal.Save(); break;
                }
                catch (Exception e)
                {
                    entry.Error = e.Message; entry.State = changed ? "TargetCreated" : "Failed";
                    journal.Issues.Add(row.Source + ": " + e.Message); journal.Save();
                }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = selected.Length, Message = done + "/" + selected.Length + " changes processed" });
            }
            RemoveEmptySourceFolders(plan.Options.Root, journal.Entries.Where(e => e.State == "Committed").Select(e => e.Source));
            journal.Save(); return journal;
        }
        private static void VerifyKeeper(LibraryAction row, Dictionary<string, JournalEntry> completed)
        {
            string path = row.Keeper, hash = row.KeeperHash;
            if (completed.TryGetValue(path, out var keeper) && !keeper.Archive) { path = keeper.Target; hash = keeper.WrittenHash; }
            if (!File.Exists(path) || CoverEditor.Hash(path) != hash) throw new IOException("The copy being kept is missing or changed. Duplicate not removed.");
        }
        private static void CheckOriginal(JournalEntry entry)
        { if (CoverEditor.Hash(entry.Source) != entry.OriginalHash) throw new IOException("The original changed during processing. It was not removed or overwritten."); }
        internal static void SquarePictures(string path)
        {
            byte[][] expected;
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                var pictures = file.Tag.Pictures;
                foreach (var picture in pictures)
                {
                    byte[] bytes = picture.Data.Data; byte[] square = Artwork.Square(bytes);
                    if (ReferenceEquals(bytes, square)) continue;
                    picture.Data = new TagLib.ByteVector(square);
                    picture.MimeType = square[0] == 0xff && square[1] == 0xd8 ? "image/jpeg" : "image/png";
                }
                expected = pictures.Select(p => p.Data.Data).ToArray(); file.Tag.Pictures = pictures; file.Save();
            }
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                var pictures = file.Tag.Pictures;
                if (pictures.Length != expected.Length || pictures.Where((p, i) => !p.Data.Data.SequenceEqual(expected[i])).Any()) throw new IOException("The format did not retain all cropped pictures. Original kept.");
            }
        }
        internal static void Copy(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            }
            catch { try { File.Delete(target); } catch { } throw; }
        }
        internal static int Undo(LibraryJournal journal, CancellationToken token, IProgress<ProgressInfo> progress = null, ISet<JournalEntry> selected = null)
        {
            var entries = journal.Entries.Where(e => (selected == null || selected.Contains(e)) && new[] { "Committed", "Prepared", "TargetCreated" }.Contains(e.State)).Reverse().ToArray(); int restored = 0, done = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    LibraryPaths.Safe(entry.Source, journal.Root); if (entry.Target != null) LibraryPaths.Safe(entry.Target, journal.Root);
                    bool same = string.Equals(entry.Source, entry.Target, StringComparison.OrdinalIgnoreCase);
                    bool originalExists = File.Exists(entry.Source);
                    if (entry.Archive)
                    {
                        if (originalExists && CoverEditor.Hash(entry.Source) != entry.OriginalHash) throw new IOException("The original path is occupied by a changed file.");
                        if (!originalExists) RestoreBackup(entry, false, token);
                    }
                    else if (same)
                    {
                        string current = CoverEditor.Hash(entry.Source);
                        if (current != entry.OriginalHash)
                        { if (current != entry.WrittenHash) throw new IOException("File changed after this run. Backup kept."); RestoreBackup(entry, true, token); }
                    }
                    else
                    {
                        bool targetExists = File.Exists(entry.Target);
                        if (originalExists && CoverEditor.Hash(entry.Source) != entry.OriginalHash) throw new IOException("The original path is occupied by another file.");
                        if (targetExists && CoverEditor.Hash(entry.Target) != entry.WrittenHash) throw new IOException("The moved/converted file changed after this run. It was not removed.");
                        if (entry.State == "Prepared" && originalExists) continue; // uncertain crash boundary: do not remove an unowned target
                        if (entry.MoveOnly)
                        {
                            if (originalExists) { if (targetExists) throw new IOException("Both locations exist. Review them before Undo."); }
                            else { if (!targetExists) throw new IOException("Moved file is missing."); Directory.CreateDirectory(Path.GetDirectoryName(entry.Source)); File.Move(entry.Target, entry.Source); }
                        }
                        else { if (!originalExists) RestoreBackup(entry, false, token); if (targetExists) File.Delete(entry.Target); }
                    }
                    entry.State = "Restored"; entry.Error = null; journal.Save(); restored++;
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { entry.Error = e.Message; journal.Issues.Add("Undo " + entry.Source + ": " + e.Message); journal.Save(); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = entries.Length, Message = done + "/" + entries.Length + " changes checked for Undo" });
            }
            RemoveEmptySourceFolders(journal.Root, entries.Where(e => e.State == "Restored" && e.Target != null).Select(e => e.Target));
            return restored;
        }
        private static void RestoreBackup(JournalEntry entry, bool replace, CancellationToken token)
        {
            if (entry.Backup == null || CoverEditor.Hash(entry.Backup) != entry.OriginalHash) throw new IOException("The verified original backup is missing or changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Source));
            string temp = Path.Combine(Path.GetDirectoryName(entry.Source), ".aat-restore-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Source));
            try { Copy(entry.Backup, temp, token); if (replace) File.Replace(temp, entry.Source, null); else File.Move(temp, entry.Source); }
            finally { try { File.Delete(temp); } catch { } }
        }
        private static void RemoveEmptySourceFolders(string root, IEnumerable<string> files)
        {
            foreach (string path in files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length))
            {
                string folder = path;
                while (folder != null && BackupSettings.Within(folder, root) && !folder.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    try { if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(folder).Any()) break; Directory.Delete(folder, false); }
                    catch { break; }
                    folder = Path.GetDirectoryName(folder);
                }
            }
        }
    }
}
