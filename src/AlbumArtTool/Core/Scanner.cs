using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AlbumArtTool.Core
{
    public sealed class Scanner
    {
        public static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".m4b", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aif", ".aiff", ".ape", ".wv" };
        private static readonly HashSet<string> CoverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cover", "folder", "front", "album", "albumart" };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

        public ScanResult Scan(string root, CancellationToken cancellation, IProgress<ProgressInfo> progress = null, bool recursive = true)
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("That music folder no longer exists.");
            var result = new ScanResult();
            var folders = new Stack<string>();
            var clock = Stopwatch.StartNew();
            folders.Push(root);
            while (folders.Count > 0)
            {
                if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
                string folder = folders.Pop();
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder);
                    if (recursive)
                        foreach (string child in Directory.GetDirectories(folder).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                            if (!Path.GetFileName(child).Equals(".album-art-backups", StringComparison.OrdinalIgnoreCase) &&
                                (System.IO.File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                                folders.Push(child);
                }
                catch (Exception e) when (IsFileError(e)) { result.Issues.Add(folder + ": " + e.Message); continue; }

                var albums = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
                    if (!Extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).StartsWith(".aat-", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        var stamp = FileStamp.Read(path);
                        using (var file = TagLib.File.Create(path, TagLib.ReadStyle.Average))
                        {
                            if (file.Properties == null || (file.Properties.MediaTypes & TagLib.MediaTypes.Audio) == 0)
                                throw new InvalidDataException("No readable audio stream was found.");
                            string title = string.IsNullOrWhiteSpace(file.Tag.Album) ? new DirectoryInfo(folder).Name : file.Tag.Album.Trim();
                            // Album artist, never track artist, keeps compilations together.
                            string albumArtist = (file.Tag.FirstAlbumArtist ?? "").Trim();
                            string key = title + "\u001f" + albumArtist;
                            if (!albums.TryGetValue(key, out Album album))
                            {
                                album = new Album { Key = folder + "\u001f" + key, Folder = folder, Title = title, Artist = albumArtist };
                                albums.Add(key, album);
                            }
                            byte[] cover = Artwork.GetCover(file.Tag);
                            album.Tracks.Add(new Track { Path = path, Title = file.Tag.Title ?? Path.GetFileNameWithoutExtension(path),
                                Performer = file.Tag.FirstPerformer ?? "", HasCover = cover != null, Stamp = stamp });
                            if (album.Thumbnail == null && cover != null)
                            { album.Thumbnail = Artwork.Thumbnail(cover); album.PreviewTrack = path; }
                        }
                        result.FilesRead++;
                    }
                    catch (Exception e) when (IsFileError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException || Artwork.IsImageError(e))
                    { result.Issues.Add(path + ": " + e.Message); }
                    if (clock.ElapsedMilliseconds >= 120)
                    {
                        progress?.Report(new ProgressInfo { Message = "Reading " + result.FilesRead + " tracks · " + folder, Completed = result.FilesRead });
                        clock.Restart();
                    }
                }
                if (result.Cancelled)
                {
                    // Never offer a partly scanned album for a whole-album edit.
                    result.FilesRead -= albums.Values.Sum(a => a.Tracks.Count);
                    break;
                }
                var covers = new List<string>();
                byte[] folderThumbnail = null;
                foreach (string path in files.Where(p => CoverNames.Contains(Path.GetFileNameWithoutExtension(p)) && ImageExtensions.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        byte[] thumbnail = Artwork.Thumbnail(Artwork.LoadFile(path));
                        covers.Add(path);
                        if (folderThumbnail == null) folderThumbnail = thumbnail;
                    }
                    catch (Exception e) when (IsFileError(e) || Artwork.IsImageError(e)) { result.Issues.Add(path + ": " + e.Message); }
                }
                foreach (var album in albums.Values)
                {
                    album.FolderCovers.AddRange(covers);
                    if (album.Thumbnail == null) album.Thumbnail = folderThumbnail;
                    if (string.IsNullOrEmpty(album.Artist))
                    {
                        string[] performers = album.Tracks.Select(t => t.Performer).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                        album.Artist = performers.Length == 1 ? performers[0] : performers.Length > 1 ? "Various artists" : "Unknown artist";
                    }
                    result.Albums.Add(album);
                }
            }
            result.Albums.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Artist + "\u001f" + a.Title + a.Folder, b.Artist + "\u001f" + b.Title + b.Folder));
            return result;
        }

        public static bool IsFileError(Exception e) => e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is NotSupportedException;
    }
}
