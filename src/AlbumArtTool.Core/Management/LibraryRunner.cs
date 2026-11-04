using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    internal sealed class JournalEntry
    {
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
            if (changed != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(changed, JournalJson.Default.JournalEntry) + "\n");
                using (var stream = new FileStream(Path.Combine(Directory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                return;
            }
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JournalJson.Default.LibraryJournal), new UTF8Encoding(false));
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
            LibraryJournal journal;
            try { journal = JsonSerializer.Deserialize(File.ReadAllText(path), JournalJson.Default.LibraryJournal); }
            catch (JsonException e) { throw new IOException("The backup journal is damaged. Review original backups before restoring manually.", e); }
            if (journal == null || !Path.GetFullPath(path).Equals(journal.FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The backup journal path does not match its contents.");
            string events = Path.Combine(journal.Directory, "events.jsonl");
            if (File.Exists(events))
            {
                var lines = File.ReadAllLines(events);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    JournalEntry entry;
                    try { entry = JsonSerializer.Deserialize(lines[i], JournalJson.Default.JournalEntry); if (entry == null) throw new JsonException(); }
                    catch (JsonException)
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

    // Reflection-free serialization (NativeAOT). Property names match journals written by earlier versions.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(LibraryJournal))]
    [JsonSerializable(typeof(JournalEntry))]
    internal sealed partial class JournalJson : JsonSerializerContext { }

    internal static class LibraryRunner
    {
        private static readonly string[] Recoverable = { "Committed", "Prepared", "TargetCreated" };

        internal static LibraryJournal Run(LibraryPlan plan, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            var options = plan.Options;
            if (BackupSettings.Within(options.BackupRoot, options.Root)) throw new IOException("Backups must be outside the active music tree.");
            foreach (var group in plan.Actions.Where(a => !string.IsNullOrEmpty(a.MergeGroup)).GroupBy(a => a.MergeGroup))
                if (group.Any(a => a.Selected) && group.Any(a => !a.Selected)) throw new IOException("Select or clear the complete multi-disc album before applying its merge.");
            long backupBytes = plan.Actions.Where(a => a.Selected && a.HasWork && (a.Archive || a.Convert || a.Square || a.ChangesTags || a.ReplacementUrl != null)).Sum(a => a.Stamp.Length);
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(options.BackupRoot));
                if (drive.IsReady && backupBytes > drive.AvailableFreeSpace - 16L * 1024 * 1024)
                    throw new IOException("Not enough free space for original backups. Choose a backup folder with at least " + (backupBytes / 1048576.0).ToString("N0") + " MB free.");
            }
            catch (ArgumentException) { } // Some network-share roots do not expose DriveInfo.
            var journal = new LibraryJournal { Root = options.Root, Directory = Path.Combine(options.BackupRoot, "library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 10)) };
            journal.Save(); Directory.CreateDirectory(BackupSettings.AppData); File.WriteAllText(LibraryJournal.LastFile, journal.FilePath);
            var selected = plan.Actions.Where(a => a.Selected && a.HasWork).OrderBy(a => a.Archive ? a.Keeper == null ? 0 : 1 : 2).ToArray();
            var completed = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            foreach (var row in selected)
            {
                if (token.IsCancellationRequested) { journal.Stopped = true; break; }
                var entry = new JournalEntry { Index = journal.Entries.Count, Source = row.Source, Target = row.Archive ? null : row.Target, Archive = row.Archive,
                    MoveOnly = !row.Archive && !row.Convert && !row.Square && !row.ChangesTags && row.ReplacementUrl == null, State = "Preparing" };
                journal.Entries.Add(entry);
                string temp = null; bool changed = false;
                try
                {
                    LibraryPaths.Safe(row.Source, options.Root);
                    if (entry.Target != null) LibraryPaths.Safe(entry.Target, options.Root);
                    if ((File.GetAttributes(row.Source) & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only file skipped.");
                    if (!row.Stamp.Matches(row.Source)) throw new IOException("File changed after preview. Preview again before changing it.");
                    entry.OriginalHash = row.ExpectedHash != null || entry.MoveOnly ? CoverEditor.Hash(row.Source) : null;
                    if (row.ExpectedHash != null && entry.OriginalHash != row.ExpectedHash) throw new IOException("The duplicate changed after preview. It was kept.");
                    if (row.Keeper != null) VerifyKeeper(row, completed);
                    if (!entry.MoveOnly)
                    {
                        entry.Backup = Path.Combine(journal.Directory, "originals", (journal.Entries.Count - 1).ToString("D6") + "-" + LibraryPaths.Component(Path.GetFileName(row.Source)));
                        string copiedHash = CopyAndHash(row.Source, entry.Backup, token);
                        if (entry.OriginalHash != null && copiedHash != entry.OriginalHash) throw new IOException("The original changed during backup. Original kept.");
                        entry.OriginalHash = copiedHash;
                        if (!row.Stamp.Matches(row.Source) || CoverEditor.Hash(entry.Backup) != entry.OriginalHash) throw new IOException("Backup verification failed. Original kept.");
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
                        if (row.ReplacementUrl != null) Replace(options, row, temp, token, (step, fraction) => progress?.Report(new ProgressInfo { Completed = done, Total = selected.Length, Fraction = fraction,
                            Message = step + " · " + (done + 1) + "/" + selected.Length + " · " + Path.GetFileName(row.Source) }));
                        else if (row.Convert)
                            Encoder.Convert(options.EncoderPath, row.Track, temp, token, fraction => progress?.Report(new ProgressInfo { Completed = done, Total = selected.Length, Fraction = fraction,
                                Message = "MP3 320 · " + (done + 1) + "/" + selected.Length + " · " + Path.GetFileName(row.Source) }));
                        else Copy(row.Source, temp, token);
                        if (row.Square || row.ChangesTags) EditTags(temp, row.Square, row);
                        entry.WrittenHash = CoverEditor.Hash(temp); entry.State = "Prepared"; journal.Save(entry);
                        token.ThrowIfCancellationRequested(); CheckOriginal(entry);
                        if (row.Source.Equals(row.Target, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Replace(temp, row.Source, null); changed = true;
                            if (!Path.GetFileName(row.Source).Equals(Path.GetFileName(row.Target), StringComparison.Ordinal)) File.Move(row.Source, row.Target);
                        }
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

        // Download outside the music tree (beside the backups), encode MP3 320 with the original's tags, then the usual verified swap.
        private static void Replace(LibraryOptions options, LibraryAction row, string temp, CancellationToken token, Action<string, double> progress)
        {
            string stage = Path.Combine(options.BackupRoot, ".aat-downloads", Guid.NewGuid().ToString("N"));
            try
            {
                var download = options.Download ?? ((url, folder, t, p) => YtDlp.Download(options.DownloaderPath, options.EncoderPath, url, folder, t, p));
                string audio = download(row.ReplacementUrl, stage, token, fraction => progress("Downloading", fraction * 0.6));
                token.ThrowIfCancellationRequested();
                Encoder.EncodeReplacement(options.EncoderPath, audio, row.Track, temp, row.ReplacementSeconds, token, fraction => progress("MP3 320", 0.6 + fraction * 0.4));
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }

        private static void VerifyKeeper(LibraryAction row, Dictionary<string, JournalEntry> completed)
        {
            string path = row.Keeper, hash = row.KeeperHash;
            if (completed.TryGetValue(path, out var keeper) && !keeper.Archive) { path = keeper.Target; hash = keeper.WrittenHash; }
            if (!File.Exists(path) || CoverEditor.Hash(path) != hash) throw new IOException("The copy being kept is missing or changed. Duplicate not removed.");
        }
        private static void CheckOriginal(JournalEntry entry)
        { if (CoverEditor.Hash(entry.Source) != entry.OriginalHash) throw new IOException("The original changed during processing. It was not removed or overwritten."); }
        internal static void SquarePictures(string path) => EditTags(path, true, null);

        private static void EditTags(string path, bool square, LibraryAction row)
        {
            byte[][] expected = null;
            List<string> id3Snapshot = null;
            bool shrink = row?.Shrink == true, single = row?.SinglePicture == true, id3 = row?.Id3v23 == true, pictureEdit = square || shrink || single, compact = shrink || single;
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                if (pictureEdit)
                {
                    var pictures = file.Tag.Pictures;
                    if (single && pictures.Length > 0)
                    {
                        // One embedded picture, in the front-cover slot; every other slot is cleared.
                        var keep = pictures[Artwork.BestPictureIndex(pictures)];
                        keep.Type = TagLib.PictureType.FrontCover;
                        pictures = new[] { keep };
                    }
                    foreach (var picture in pictures)
                    {
                        byte[] bytes = picture.Data.Data, edited = square ? Artwork.Square(bytes) : bytes;
                        // Crop first at native resolution, then bring the result to the embedded standard (JPEG, 600 px, quality 88).
                        if (shrink) edited = Artwork.Standardize(edited);
                        if (ReferenceEquals(bytes, edited)) continue;
                        picture.Data = new TagLib.ByteVector(edited);
                        picture.MimeType = Artwork.MimeType(edited);
                    }
                    expected = pictures.Select(p => p.Data.Data).ToArray(); file.Tag.Pictures = pictures;
                    if (compact) TagCompaction.RebuildId3v2(file);
                }
                if (row?.NewTitle != null)
                {
                    file.Tag.Title = row.NewTitle;
                    // A file with no tag block at all ignores the write; create the format's native tag.
                    if (file.Tag.Title != row.NewTitle)
                        foreach (var type in new[] { TagLib.TagTypes.Id3v2, TagLib.TagTypes.Xiph, TagLib.TagTypes.Apple, TagLib.TagTypes.Asf, TagLib.TagTypes.Ape, TagLib.TagTypes.RiffInfo })
                        {
                            TagLib.Tag created;
                            try { created = file.GetTag(type, true); } catch (NotSupportedException) { continue; } catch (NotImplementedException) { continue; }
                            if (created == null) continue;
                            created.Title = row.NewTitle;
                            if (file.Tag.Title == row.NewTitle) break;
                        }
                }
                if (row?.MergeDiscs == true)
                {
                    file.Tag.Album = row.MergedAlbum; file.Tag.Track = row.MergedTrack; file.Tag.TrackCount = row.MergedTrackCount;
                    file.Tag.Disc = 1; file.Tag.DiscCount = 1;
                    var artists = file.Tag.AlbumArtists;
                    if (artists.Length == 0) artists = new[] { row.MergedArtist }; else artists[0] = row.MergedArtist;
                    file.Tag.AlbumArtists = artists;
                }
                // Last, so frames added by the edits above are converted too.
                if (id3) id3Snapshot = Id3Standard.Apply(file);
                file.Save();
            }
            if (compact) TagCompaction.Compact(path);
            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                if (pictureEdit)
                {
                    var pictures = file.Tag.Pictures;
                    if (pictures.Length != expected.Length || pictures.Where((p, i) => !p.Data.Data.AsSpan().SequenceEqual(expected[i])).Any()) throw new IOException("The format did not retain all edited pictures. Original kept.");
                    if (single && pictures.Length == 1 && pictures[0].Type != TagLib.PictureType.FrontCover && !(file is TagLib.Mpeg4.File)) throw new IOException("The format did not keep the picture in the front-cover slot. Original kept.");
                }
                if (id3 && Id3Standard.Problem(file, id3Snapshot) is string problem) throw new IOException("The tag could not be saved as ID3v2.3 (" + problem + "). Original kept.");
                if (row?.NewTitle != null && file.Tag.Title != row.NewTitle) throw new IOException("The format did not retain the new song title. Original kept.");
                if (row?.MergeDiscs == true && (file.Tag.Album != row.MergedAlbum || file.Tag.Track != row.MergedTrack || file.Tag.TrackCount != row.MergedTrackCount ||
                    file.Tag.Disc != 1 || file.Tag.DiscCount != 1 || file.Tag.FirstAlbumArtist != row.MergedArtist)) throw new IOException("The format did not retain the combined album and track numbering. Original kept.");
            }
        }

        internal static void Copy(string source, string target, CancellationToken token) => FileTransfer.Copy(source, target, token);
        internal static string CopyAndHash(string source, string target, CancellationToken token) => FileTransfer.CopyAndHash(source, target, token);

        internal static int Undo(LibraryJournal journal, CancellationToken token, IProgress<ProgressInfo> progress = null, ISet<JournalEntry> selected = null)
        {
            var entries = journal.Entries.Where(e => (selected == null || selected.Contains(e)) && Recoverable.Contains(e.State)).Reverse().ToArray(); int restored = 0, done = 0;
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
                        // A capitalization-only rename: on a case-sensitive filesystem only the new name exists.
                        string existing = File.Exists(entry.Source) ? entry.Source : entry.Target;
                        string current = CoverEditor.Hash(existing);
                        if (current != entry.OriginalHash && current != entry.WrittenHash) throw new IOException("File changed after this run. Backup kept.");
                        if (!ReferenceEquals(existing, entry.Source)) File.Move(entry.Target, entry.Source);
                        if (current != entry.OriginalHash) RestoreBackup(entry, true, token);
                        if (ReferenceEquals(existing, entry.Source) && !Path.GetFileName(entry.Source).Equals(Path.GetFileName(entry.Target), StringComparison.Ordinal)) File.Move(entry.Target, entry.Source);
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
