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
        public int Index { get; set; }
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
        internal void Save(JournalEntry changed = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            if (changed != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(changed) + "\n");
                using (var stream = new FileStream(Path.Combine(Directory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                return;
            }
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, serializer.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
            try { File.Delete(Path.Combine(Directory, "events.jsonl")); } catch { }
            try
            {
                string Csv(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
                var lines = new List<string> { "Status,Source,Destination,Original backup,Error" };
                lines.AddRange(Entries.Select(e => string.Join(",", new[] { e.State, e.Source, e.Target, e.Backup, e.Error }.Select(Csv))));
                File.WriteAllLines(Path.Combine(Directory, "report.csv"), lines, new UTF8Encoding(true));
            }
            catch (IOException) { } // An open report must not prevent durable journal updates.
        }
        internal static LibraryJournal Last()
        {
            if (!File.Exists(LastFile)) return null;
            string path = File.ReadAllText(LastFile); if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return null;
            var journal = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<LibraryJournal>(File.ReadAllText(path));
            if (!Path.GetFullPath(path).Equals(journal.FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The backup journal path does not match its contents.");
            string events = Path.Combine(journal.Directory, "events.jsonl");
            if (File.Exists(events))
            {
                var lines = File.ReadAllLines(events);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    JournalEntry entry;
                    try { entry = new JavaScriptSerializer().Deserialize<JournalEntry>(lines[i]); }
                    catch
                    {
                        if (i == lines.Length - 1)
                        {
                            string repaired = events + ".repair";
                            File.WriteAllLines(repaired, lines.Take(i), new UTF8Encoding(false)); File.Replace(repaired, events, null); break;
                        }
                        throw new IOException("The saved operation log is damaged. Review original backups before restoring manually.");
                    }
                    if (entry.Index < 0 || entry.Index > journal.Entries.Count) throw new IOException("The saved operation log has an invalid entry number.");
                    if (entry.Index == journal.Entries.Count) journal.Entries.Add(entry); else journal.Entries[entry.Index] = entry;
                }
            }
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
                var entry = new JournalEntry { Index = journal.Entries.Count, Source = row.Source, Target = row.Archive ? null : row.Target, Archive = row.Archive,
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
                        entry.WrittenHash = null; entry.State = "Prepared"; journal.Save(entry);
                        CheckOriginal(entry); if (row.Keeper != null) VerifyKeeper(row, completed);
                        File.Delete(row.Source); changed = true;
                    }
                    else if (entry.MoveOnly)
                    {
                        entry.WrittenHash = entry.OriginalHash; entry.State = "Prepared"; journal.Save(entry);
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
                        entry.WrittenHash = CoverEditor.Hash(temp); entry.State = "Prepared"; journal.Save(entry);
                        token.ThrowIfCancellationRequested(); CheckOriginal(entry);
                        if (row.Source.Equals(row.Target, StringComparison.OrdinalIgnoreCase)) { File.Replace(temp, row.Source, null); changed = true; }
                        else
                        {
                            File.Move(temp, row.Target); changed = true; entry.State = "TargetCreated"; journal.Save(entry);
                            try { CheckOriginal(entry); File.Delete(row.Source); }
                            catch
                            {
                                if (File.Exists(row.Source) && CoverEditor.Hash(row.Source) == entry.OriginalHash && CoverEditor.Hash(row.Target) == entry.WrittenHash)
                                { File.Delete(row.Target); changed = false; }
                                throw;
                            }
                        }
                    }
                    entry.State = "Committed"; journal.Save(entry); completed[row.Source] = entry;
                }
                catch (OperationCanceledException)
                {
                    entry.State = changed ? "TargetCreated" : "Cancelled"; journal.Stopped = true; journal.Save(entry); break;
                }
                catch (Exception e)
                {
                    entry.Error = e.Message; entry.State = changed ? "TargetCreated" : "Failed";
                    journal.Issues.Add(row.Source + ": " + e.Message); journal.Save(entry);
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
            bool created = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            }
            catch { if (created) try { File.Delete(target); } catch { } throw; }
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
                    entry.State = "Restored"; entry.Error = null; journal.Save(entry); restored++;
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { entry.Error = e.Message; journal.Issues.Add("Undo " + entry.Source + ": " + e.Message); journal.Save(entry); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = entries.Length, Message = done + "/" + entries.Length + " changes checked for Undo" });
            }
            RemoveEmptySourceFolders(journal.Root, entries.Where(e => e.State == "Restored" && e.Target != null).Select(e => e.Target));
            journal.Save(); return restored;
        }
        private static void RestoreBackup(JournalEntry entry, bool replace, CancellationToken token)
        {
            if (entry.Backup == null || CoverEditor.Hash(entry.Backup) != entry.OriginalHash) throw new IOException("The verified original backup is missing or changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Source));
            string temp = Path.Combine(Path.GetDirectoryName(entry.Source), ".aat-restore-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Source));
            try
            {
                Copy(entry.Backup, temp, token);
                if (replace)
                {
                    string hash = CoverEditor.Hash(entry.Source);
                    if (hash == entry.OriginalHash) return;
                    if (hash != entry.WrittenHash) throw new IOException("The file changed while Undo was preparing its restore. It was kept.");
                    File.Replace(temp, entry.Source, null);
                }
                else File.Move(temp, entry.Source);
            }
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
