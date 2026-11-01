using System;
using System.IO;
using System.Linq;

namespace AlbumArtTool.Core
{
    public sealed partial class CoverEditor
    {
        public EditResult ApplyArtist(Album album, string artist, IProgress<ProgressInfo> progress = null)
        {
            artist = (artist ?? "").Trim();
            if (!ArtistRules.Specific(artist) || artist.Length > 300 || artist.Any(char.IsControl))
                throw new ArgumentException("Enter the main artist's name before applying the change.");
            var result = new EditResult();
            var targets = album.Tracks.Where(t => ArtistRules.NeedsChange(t, artist)).ToList();
            string backups = Path.Combine(album.Folder, ".album-art-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            int done = 0;
            foreach (var track in targets)
            {
                string temp = TemporaryPath(track.Path);
                try
                {
                    CheckWritable(track.Path);
                    if (!track.Stamp.Matches(track.Path)) throw new IOException("File changed since the scan. Rescan before editing it.");
                    System.IO.File.Copy(track.Path, temp, false);
                    using (var file = TagLib.File.Create(temp, TagLib.ReadStyle.None))
                    {
                        file.Tag.Performers = new[] { artist };
                        file.Tag.AlbumArtists = new[] { artist };
                        // Replace existing artist sort fields too, so a player's sort order cannot retain a guest artist.
                        if (file.Tag.PerformersSort.Length > 0) file.Tag.PerformersSort = new[] { artist };
                        if (file.Tag.AlbumArtistsSort.Length > 0) file.Tag.AlbumArtistsSort = new[] { artist };
                        file.Save();
                    }
                    using (var check = TagLib.File.Create(temp, TagLib.ReadStyle.None))
                        if (!ArtistRules.Single(check.Tag.Performers, artist) || !ArtistRules.Single(check.Tag.AlbumArtists, artist))
                            throw new IOException("This format did not retain the artist fields. Original file kept.");
                    if (!track.Stamp.Matches(track.Path)) throw new IOException("File changed during the edit. Original file kept.");
                    string hash = Hash(temp);
                    Directory.CreateDirectory(backups);
                    string backup = Path.Combine(backups, Path.GetFileName(track.Path));
                    System.IO.File.Replace(temp, track.Path, backup);
                    result.Entries.Add(new BackupEntry { Original = track.Path, Backup = backup, WrittenHash = hash });
                    result.TracksWritten++;
                }
                catch (Exception e) when (Expected(e)) { result.Issues.Add(track.Path + ": " + e.Message); }
                finally { TryDelete(temp); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = targets.Count, Message = "Updating artist " + done + " of " + targets.Count });
            }
            return result;
        }
    }
}
