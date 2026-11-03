using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        // Folders are read concurrently. A few parallel readers hide per-file latency on SSDs,
        // USB players and SD cards without flooding a single slow device.
        internal static int Parallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 6);

        public ScanResult Scan(string root, CancellationToken cancellation, IProgress<ProgressInfo> progress = null, bool recursive = true, bool useCache = true)
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("That music folder no longer exists.");
            var result = new ScanResult();
            // Directory enumeration is cheap compared with opening tags, especially on an SD card.
            // Use one snapshot for both the total and the actual scan so progress cannot drift.
            var folders = Discover(root, recursive, cancellation, result, progress);
            if (result.Cancelled) return result;
            var counter = new Counter(progress, folders.Sum(f => f.Tracks.Length));
            counter.Start(root);
            var artwork = new ArtworkValidationCache();
            var outcomes = new FolderOutcome[folders.Count];
            var pending = new List<int>();
            for (int i = 0; i < folders.Count; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                if (useCache && ScanCache.TryRead(folders[i].Path, folders[i].Stamps, out var cached))
                {
                    outcomes[i] = new FolderOutcome { Albums = cached.ToList(), Completed = true, FilesRead = folders[i].Tracks.Length, Cached = true };
                    counter.Advance(folders[i].Tracks.Length, folders[i].Path);
                }
                else pending.Add(i);
            }
            if (!cancellation.IsCancellationRequested && pending.Count > 0)
            {
                if (pending.Count == 1 || Parallelism <= 1) foreach (int i in pending) outcomes[i] = ReadFolder(folders[i], cancellation, counter, artwork);
                else Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
                    i => { if (!cancellation.IsCancellationRequested) outcomes[i] = ReadFolder(folders[i], cancellation, counter, artwork); });
            }
            foreach (var outcome in outcomes)
            {
                if (outcome == null) { result.Cancelled = true; continue; }
                result.Issues.AddRange(outcome.Issues);
                result.FilesOpened += outcome.FilesOpened;
                // Never offer a partly scanned album for a whole-album edit.
                if (!outcome.Completed) { result.Cancelled = true; continue; }
                result.Albums.AddRange(outcome.Albums); result.FilesRead += outcome.FilesRead;
                if (outcome.Cached) result.CachedFiles += outcome.FilesRead;
            }
            if (cancellation.IsCancellationRequested) result.Cancelled = true;
            result.ArtworkImagesDecoded = artwork.Decoded;
            var comparer = StringComparer.CurrentCultureIgnoreCase;
            var keys = new Dictionary<Album, string>(result.Albums.Count);
            foreach (var album in result.Albums) keys[album] = album.Artist + "\u001f" + album.Title + album.Folder;
            result.Albums.Sort((a, b) => comparer.Compare(keys[a], keys[b]));
            ScanCache.ScheduleSave();
            return result;
        }

        private sealed class ScanFolder
        {
            public string Path;
            public string[] Tracks, Covers;
            public Dictionary<string, FileStamp> Stamps;
        }

        private sealed class FolderOutcome
        {
            public List<Album> Albums = new List<Album>();
            public List<string> Issues = new List<string>();
            public int FilesRead, FilesOpened;
            public bool Completed, Cached;
        }

        // Progress is counted and reported under one lock so the visible counter can never
        // move backwards while several folders are read at once.
        private sealed class Counter
        {
            private readonly IProgress<ProgressInfo> progress;
            private readonly int total;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private int done;
            internal Counter(IProgress<ProgressInfo> progress, int total) { this.progress = progress; this.total = total; }
            internal void Start(string folder) { lock (this) Report(folder); }
            internal void Advance(int count, string folder)
            {
                lock (this)
                    for (int i = 0; i < count; i++)
                    {
                        done++;
                        if (done == 1 || done == total || clock.ElapsedMilliseconds >= 120) { Report(folder); clock.Restart(); }
                    }
            }
            private void Report(string folder) => progress?.Report(new ProgressInfo
            { Message = done + "/" + total + " tracks scanned · " + folder, Completed = done, Total = total });
        }

        private static FolderOutcome ReadFolder(ScanFolder entry, CancellationToken cancellation, Counter counter, ArtworkValidationCache artwork)
        {
            var outcome = new FolderOutcome();
            string folder = entry.Path, folderName = new DirectoryInfo(folder).Name;
            var albums = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in entry.Tracks)
            {
                if (cancellation.IsCancellationRequested) return outcome;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) throw new FileNotFoundException("The file no longer exists.", path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var stamp = FileStamp.FromInfo(info);
                    outcome.FilesOpened++;
                    using (var file = TagLib.File.Create(path, TagLib.ReadStyle.Average))
                    {
                        if (file.Properties == null || (file.Properties.MediaTypes & TagLib.MediaTypes.Audio) == 0)
                            throw new InvalidDataException("No readable audio stream was found.");
                        var tag = file.Tag;
                        string album = tag.Album;
                        string title = string.IsNullOrWhiteSpace(album) ? folderName : album.Trim();
                        // Album artist, never track artist, keeps compilations together.
                        string albumArtist = (tag.FirstAlbumArtist ?? "").Trim();
                        string key = title + "\u001f" + albumArtist;
                        if (!albums.TryGetValue(key, out Album target))
                        {
                            target = new Album { Key = folder + "\u001f" + key, Folder = folder, Title = title, Artist = albumArtist, TaggedArtist = albumArtist };
                            albums.Add(key, target);
                        }
                        var pictures = tag.Pictures ?? Array.Empty<TagLib.IPicture>();
                        byte[] cover = Artwork.GetCover(pictures, artwork);
                        var codecs = file.Properties.Codecs.Where(c => c != null).Select(c => c.Description).ToArray();
                        string trackTitle = tag.Title;
                        target.Tracks.Add(new Track
                        {
                            Path = path, Title = trackTitle ?? Path.GetFileNameWithoutExtension(path),
                            Performer = tag.FirstPerformer ?? "", Performers = tag.Performers ?? Array.Empty<string>(), AlbumArtists = tag.AlbumArtists ?? Array.Empty<string>(),
                            AlbumTitle = album ?? "", Disc = tag.Disc, DiscCount = tag.DiscCount, Number = tag.Track,
                            Bitrate = file.Properties.AudioBitrate, HasVideo = (file.Properties.MediaTypes & TagLib.MediaTypes.Video) != 0,
                            Codec = string.Join("; ", codecs), Lossless = Lossless(path, string.Join(" ", codecs), file.InvariantStartPosition),
                            Duration = file.Properties.Duration, HasTitleTag = !string.IsNullOrWhiteSpace(trackTitle),
                            AudioStart = file.InvariantStartPosition, AudioEnd = file.InvariantEndPosition,
                            SampleRate = file.Properties.AudioSampleRate, Channels = file.Properties.AudioChannels,
                            HasCover = cover != null, Stamp = stamp, PictureCount = pictures.Length,
                            CoverPictures = pictures.Count(Artwork.IsCover), PictureSummary = Artwork.Describe(pictures)
                        });
                        if (target.Thumbnail == null && cover != null)
                        { target.Thumbnail = Artwork.Thumbnail(cover); target.PreviewTrack = path; }
                    }
                    outcome.FilesRead++;
                }
                catch (Exception e) when (!(e is OperationCanceledException) && !(e is OutOfMemoryException))
                { outcome.Issues.Add(path + ": " + e.Message); }
                finally
                {
                    // Failed/corrupt files have still been checked and count toward completion.
                    counter.Advance(1, folder);
                }
            }
            var covers = new List<string>();
            byte[] folderThumbnail = null;
            foreach (string path in entry.Covers)
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    byte[] thumbnail = Artwork.Thumbnail(Artwork.LoadFile(path));
                    covers.Add(path);
                    if (folderThumbnail == null) folderThumbnail = thumbnail;
                }
                catch (Exception e) when (IsFileError(e) || Artwork.IsImageError(e)) { outcome.Issues.Add(path + ": " + e.Message); }
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
                outcome.Albums.Add(album);
            }
            outcome.Completed = true;
            if (outcome.Issues.Count == 0) ScanCache.Store(folder, entry.Stamps, albums.Values);
            return outcome;
        }

        private static List<ScanFolder> Discover(string root, bool recursive, CancellationToken token, ScanResult result, IProgress<ProgressInfo> progress)
        {
            var found = new List<ScanFolder>();
            var pending = new Stack<string>(); pending.Push(root);
            var clock = Stopwatch.StartNew();
            int total = 0, visited = 0;
            // Read the setting once per scan instead of once per directory.
            string backupRoot = PathUtil.Clean(BackupSettings.Root);
            progress?.Report(new ProgressInfo { Message = "Counting music files…" });
            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                string folder = pending.Pop();
                var tracks = new List<string>(); var covers = new List<string>(); var children = new List<string>();
                var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // One directory listing yields names, sizes, times and attributes for files and folders.
                    foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    {
                        if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (item is DirectoryInfo directory)
                        {
                            if (!recursive) continue;
                            string name = directory.Name, path = directory.FullName;
                            if (name.Equals(".album-art-backups", StringComparison.OrdinalIgnoreCase) || name.Equals(".music-manager-backups", StringComparison.OrdinalIgnoreCase) ||
                                PathUtil.WithinFull(PathUtil.Clean(path), backupRoot)) continue;
                            try { if ((directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0) children.Add(path); }
                            catch (Exception e) when (IsFileError(e)) { result.Issues.Add(path + ": " + e.Message); }
                            continue;
                        }
                        var info = (FileInfo)item;
                        string file = info.FullName, extension = Path.GetExtension(file);
                        bool music = Extensions.Contains(extension) && !TemporaryOrBackup(info.Name);
                        bool cover = !music && ImageExtensions.Contains(extension) && CoverNames.Contains(Path.GetFileNameWithoutExtension(file));
                        if (!music && !cover) continue;
                        try
                        {
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var stamp = FileStamp.FromInfo(info);
                            if (music) tracks.Add(file); else covers.Add(file);
                            stamps[file] = stamp;
                        }
                        catch (Exception e) when (IsFileError(e)) { result.Issues.Add(file + ": " + e.Message); }
                    }
                }
                catch (Exception e) when (IsFileError(e))
                {
                    // An interrupted directory listing must not become a partial editable album.
                    tracks.Clear(); covers.Clear(); children.Clear(); result.Issues.Add(folder + ": " + e.Message);
                }
                if (result.Cancelled) break;
                tracks.Sort(StringComparer.OrdinalIgnoreCase); covers.Sort(StringComparer.OrdinalIgnoreCase);
                if (tracks.Count > 0) found.Add(new ScanFolder { Path = folder, Tracks = tracks.ToArray(), Covers = covers.ToArray(), Stamps = stamps });
                total += tracks.Count; visited++;
                if (clock.ElapsedMilliseconds >= 120)
                {
                    progress?.Report(new ProgressInfo { Message = "Counting music files… " + total + " found in " + visited + " folders" });
                    clock.Restart();
                }
                // Push in reverse so folders are visited alphabetically.
                children.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
            if (token.IsCancellationRequested) result.Cancelled = true;
            return found;
        }

        internal static bool TemporaryOrBackup(string name) => name.StartsWith(".aat-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".__mp3convert_", StringComparison.OrdinalIgnoreCase) || name.Contains(".before-square-crop") || name.StartsWith(".covercrop-tmp-", StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> LosslessExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".flac", ".ape", ".tta", ".dsf" };
        private static bool Lossless(string path, string codec, long start)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(".wv", StringComparison.OrdinalIgnoreCase))
            {
                using (var stream = File.OpenRead(path))
                {
                    stream.Position = Math.Max(0, start); var header = new byte[32];
                    if (stream.Read(header, 0, header.Length) == header.Length && header[0] == 'w' && header[1] == 'v' && header[2] == 'p' && header[3] == 'k')
                        return (BitConverter.ToUInt32(header, 24) & 8) == 0; // WavPack HYBRID_FLAG
                }
                return false;
            }
            return LosslessExtensions.Contains(ext) || codec.Contains("lossless", StringComparison.OrdinalIgnoreCase) || codec.Contains("pcm", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFileError(Exception e) => e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is NotSupportedException;
    }
}
