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
        { ".mp3", ".flac", ".m4a", ".m4b", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aif", ".aiff", ".ape", ".wv", ".aac", ".mp4", ".asf", ".aifc", ".dsf", ".tta", ".mpc" };
        private static readonly HashSet<string> CoverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cover", "folder", "front", "album", "albumart" };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

        public ScanResult Scan(string root, CancellationToken cancellation, IProgress<ProgressInfo> progress = null, bool recursive = true)
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("That music folder no longer exists.");
            var result = new ScanResult();
            // Directory enumeration is cheap compared with opening tags, especially on an SD card.
            // Use one snapshot for both the total and the actual scan so progress cannot drift.
            var folders = Discover(root, recursive, cancellation, result, progress);
            if (result.Cancelled) return result;
            int total = folders.Sum(f => f.Tracks.Length), checkedFiles = 0;
            var clock = Stopwatch.StartNew();
            void Report(string folder) => progress?.Report(new ProgressInfo {
                Message = checkedFiles + "/" + total + " tracks scanned · " + folder,
                Completed = checkedFiles, Total = total });
            Report(root);
            foreach (var entry in folders)
            {
                if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
                string folder = entry.Path;
                var albums = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in entry.Tracks)
                {
                    if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
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
                                album = new Album { Key = folder + "\u001f" + key, Folder = folder, Title = title, Artist = albumArtist, TaggedArtist = albumArtist };
                                albums.Add(key, album);
                            }
                            byte[] cover = Artwork.GetCover(file.Tag);
                            album.Tracks.Add(new Track { Path = path, Title = file.Tag.Title ?? Path.GetFileNameWithoutExtension(path),
                                Performer = file.Tag.FirstPerformer ?? "", Performers = file.Tag.Performers.ToArray(), AlbumArtists = file.Tag.AlbumArtists.ToArray(),
                                AlbumTitle = file.Tag.Album ?? "", Disc = file.Tag.Disc, DiscCount = file.Tag.DiscCount, Number = file.Tag.Track,
                                Bitrate = file.Properties.AudioBitrate, HasVideo = (file.Properties.MediaTypes & TagLib.MediaTypes.Video) != 0,
                                Codec = string.Join("; ", file.Properties.Codecs.Where(c => c != null).Select(c => c.Description)),
                                Lossless = Lossless(path, string.Join(" ", file.Properties.Codecs.Where(c => c != null).Select(c => c.Description)), file.InvariantStartPosition),
                                Duration = file.Properties.Duration, HasTitleTag = !string.IsNullOrWhiteSpace(file.Tag.Title),
                                AudioStart = file.InvariantStartPosition, AudioEnd = file.InvariantEndPosition,
                                SampleRate = file.Properties.AudioSampleRate, Channels = file.Properties.AudioChannels,
                                HasCover = cover != null, Stamp = stamp });
                            if (album.Thumbnail == null && cover != null)
                            { album.Thumbnail = Artwork.Thumbnail(cover); album.PreviewTrack = path; }
                        }
                        result.FilesRead++;
                    }
                    catch (Exception e) when (IsFileError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException || Artwork.IsImageError(e))
                    { result.Issues.Add(path + ": " + e.Message); }
                    finally
                    {
                        // Failed/corrupt files have still been checked and count toward completion.
                        checkedFiles++;
                        if (checkedFiles == 1 || checkedFiles == total || clock.ElapsedMilliseconds >= 120)
                        { Report(folder); clock.Restart(); }
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
                foreach (string path in entry.Covers)
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
                    album.MainArtist = ArtistRules.Suggest(album);
                    if (string.IsNullOrEmpty(album.TaggedArtist) && album.MainArtist.Length > 0) album.Artist = album.MainArtist;
                    result.Albums.Add(album);
                }
            }
            result.Albums.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Artist + "\u001f" + a.Title + a.Folder, b.Artist + "\u001f" + b.Title + b.Folder));
            return result;
        }

        private sealed class ScanFolder
        {
            public string Path;
            public string[] Tracks, Covers;
        }

        private static List<ScanFolder> Discover(string root, bool recursive, CancellationToken token, ScanResult result, IProgress<ProgressInfo> progress)
        {
            var found = new List<ScanFolder>();
            var pending = new Stack<string>(); pending.Push(root);
            var clock = Stopwatch.StartNew();
            int total = 0, visited = 0;
            progress?.Report(new ProgressInfo { Message = "Counting music files…" });
            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                string folder = pending.Pop();
                var tracks = new List<string>(); var covers = new List<string>();
                try
                {
                    foreach (var info in new DirectoryInfo(folder).EnumerateFiles())
                    {
                        if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                        string path = info.FullName;
                        string extension = Path.GetExtension(path);
                        bool music = Extensions.Contains(extension) && !TemporaryOrBackup(Path.GetFileName(path));
                        bool cover = ImageExtensions.Contains(extension) && CoverNames.Contains(Path.GetFileNameWithoutExtension(path));
                        if (!music && !cover) continue;
                        try
                        {
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (music) tracks.Add(path); else covers.Add(path);
                        }
                        catch (Exception e) when (IsFileError(e)) { result.Issues.Add(path + ": " + e.Message); }
                    }
                }
                catch (Exception e) when (IsFileError(e))
                {
                    // An interrupted directory listing must not become a partial editable album.
                    tracks.Clear(); covers.Clear(); result.Issues.Add(folder + ": " + e.Message);
                }
                if (result.Cancelled) break;
                tracks.Sort(StringComparer.OrdinalIgnoreCase); covers.Sort(StringComparer.OrdinalIgnoreCase);
                if (tracks.Count > 0) found.Add(new ScanFolder { Path = folder, Tracks = tracks.ToArray(), Covers = covers.ToArray() });
                total += tracks.Count; visited++;
                if (clock.ElapsedMilliseconds >= 120)
                {
                    progress?.Report(new ProgressInfo { Message = "Counting music files… " + total + " found in " + visited + " folders" });
                    clock.Restart();
                }
                if (!recursive) continue;
                try
                {
                    foreach (string child in Directory.GetDirectories(folder).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (Path.GetFileName(child).Equals(".album-art-backups", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(child).Equals(".music-manager-backups", StringComparison.OrdinalIgnoreCase) || BackupSettings.Within(child, BackupSettings.Root)) continue;
                        try
                        {
                            if ((System.IO.File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0) pending.Push(child);
                        }
                        catch (Exception e) when (IsFileError(e)) { result.Issues.Add(child + ": " + e.Message); }
                    }
                }
                catch (Exception e) when (IsFileError(e)) { result.Issues.Add(folder + ": " + e.Message); }
            }
            if (token.IsCancellationRequested) result.Cancelled = true;
            return found;
        }

        internal static bool TemporaryOrBackup(string name) => name.StartsWith(".aat-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".__mp3convert_", StringComparison.OrdinalIgnoreCase) || name.Contains(".before-square-crop") || name.StartsWith(".covercrop-tmp-", StringComparison.OrdinalIgnoreCase);
        private static bool Lossless(string path, string codec, long start)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant(); codec = codec.ToLowerInvariant();
            if (ext == ".wv")
            {
                using (var stream = System.IO.File.OpenRead(path))
                {
                    stream.Position = Math.Max(0, start); var header = new byte[32];
                    if (stream.Read(header, 0, header.Length) == header.Length && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "wvpk")
                        return (BitConverter.ToUInt32(header, 24) & 8) == 0; // WavPack HYBRID_FLAG
                }
                return false;
            }
            return new[] { ".flac", ".ape", ".tta", ".dsf" }.Contains(ext) ||
                codec.Contains("lossless") || codec.Contains("pcm");
        }

        public static bool IsFileError(Exception e) => e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is NotSupportedException;
    }
}
