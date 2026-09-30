using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace AlbumArtTool.Core
{
    public sealed class CoverEditor
    {
        public EditResult Apply(Album album, byte[] jpeg, bool onlyMissing, bool saveFolderCover, IProgress<ProgressInfo> progress = null)
        {
            // Decode before touching any audio file.
            using (var validation = Artwork.Decode(jpeg)) { }
            var result = new EditResult();
            var targets = album.Tracks.Where(t => !onlyMissing || !t.HasCover).ToList();
            string backupDirectory = Path.Combine(album.Folder, ".album-art-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            int done = 0;
            foreach (var track in targets)
            {
                string temp = TemporaryPath(track.Path);
                try
                {
                    CheckWritable(track.Path);
                    if (!track.Stamp.Matches(track.Path)) throw new IOException("File changed since the scan. Rescan before editing it.");
                    System.IO.File.Copy(track.Path, temp, false);
                    using (var audio = TagLib.File.Create(temp, TagLib.ReadStyle.None))
                    {
                        if (onlyMissing && Artwork.GetCover(audio.Tag) != null) continue;
                        var picture = new TagLib.Picture(new TagLib.ByteVector(jpeg))
                        { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Front cover" };
                        // Preserve back covers, booklets, disc images and other explicitly typed artwork.
                        audio.Tag.Pictures = new TagLib.IPicture[] { picture }.Concat(audio.Tag.Pictures.Where(p => !Artwork.IsCover(p))).ToArray();
                        audio.Save();
                    }
                    using (var check = TagLib.File.Create(temp, TagLib.ReadStyle.None))
                    {
                        byte[] stored = Artwork.GetCover(check.Tag);
                        if (stored == null || !stored.SequenceEqual(jpeg)) throw new IOException("The format did not retain the new cover. Original file kept.");
                    }
                    if (!track.Stamp.Matches(track.Path)) throw new IOException("File changed during the edit. Original file kept.");
                    string hash = Hash(temp);
                    Directory.CreateDirectory(backupDirectory);
                    string backup = Path.Combine(backupDirectory, Path.GetFileName(track.Path));
                    System.IO.File.Replace(temp, track.Path, backup);
                    result.Entries.Add(new BackupEntry { Original = track.Path, Backup = backup, WrittenHash = hash });
                    result.TracksWritten++;
                }
                catch (Exception e) when (Expected(e)) { result.Issues.Add(track.Path + ": " + e.Message); }
                finally { TryDelete(temp); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = targets.Count, Message = "Updating track " + done + " of " + targets.Count });
            }
            // Don't report a folder cover as success when all requested track writes failed.
            if (saveFolderCover && (targets.Count == 0 || result.TracksWritten > 0))
            {
                var paths = album.FolderCovers.Count > 0 ? album.FolderCovers.ToArray() : new[] { Path.Combine(album.Folder, "cover.jpg") };
                foreach (string path in paths)
                {
                    string temp = TemporaryPath(path);
                    try
                    {
                        bool exists = System.IO.File.Exists(path);
                        FileStamp stamp = exists ? FileStamp.Read(path) : null;
                        if (exists) CheckWritable(path);
                        System.IO.File.WriteAllBytes(temp, Artwork.Normalize(jpeg, 1600, Artwork.FormatForPath(path)));
                        string hash = Hash(temp);
                        Directory.CreateDirectory(backupDirectory);
                        string backup = Path.Combine(backupDirectory, Path.GetFileName(path));
                        if (exists)
                        {
                            if (!stamp.Matches(path)) throw new IOException("Folder cover changed during the edit. Original kept.");
                            System.IO.File.Replace(temp, path, backup);
                        }
                        else System.IO.File.Move(temp, path);
                        result.Entries.Add(new BackupEntry { Original = path, Backup = exists ? backup : null, WrittenHash = hash, Created = !exists });
                    }
                    catch (Exception e) when (Expected(e)) { result.Issues.Add(path + ": " + e.Message); }
                    finally { TryDelete(temp); }
                }
            }
            return result;
        }

        public EditResult Undo(EditResult edit, IProgress<ProgressInfo> progress = null)
        {
            var result = new EditResult();
            int done = 0;
            foreach (var entry in edit.Entries.ToArray().Reverse())
            {
                string temp = TemporaryPath(entry.Original);
                try
                {
                    CheckWritable(entry.Original);
                    if (Hash(entry.Original) != entry.WrittenHash)
                        throw new IOException("File changed after this edit. It was not overwritten. Your backup is still available.");
                    if (entry.Created) System.IO.File.Delete(entry.Original);
                    else
                    {
                        System.IO.File.Copy(entry.Backup, temp, false);
                        System.IO.File.Replace(temp, entry.Original, null);
                    }
                    edit.Entries.Remove(entry);
                    result.Restored++;
                }
                catch (Exception e) when (Expected(e)) { result.Issues.Add(entry.Original + ": " + e.Message); }
                finally { TryDelete(temp); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = done + edit.Entries.Count, Message = "Restoring " + Path.GetFileName(entry.Original) });
            }
            return result;
        }

        public static string Hash(string path)
        {
            using (var stream = System.IO.File.OpenRead(path))
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream));
        }
        private static string TemporaryPath(string path) => Path.Combine(Path.GetDirectoryName(path), ".aat-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        private static void CheckWritable(string path)
        {
            var attributes = System.IO.File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Read-only files and linked files are skipped.");
        }
        private static bool Expected(Exception e) => Scanner.IsFileError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException || Artwork.IsImageError(e);
        private static void TryDelete(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception e) when (Scanner.IsFileError(e)) { }
        }
    }
}
