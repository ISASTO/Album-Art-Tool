using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AlbumArtTool.Core;

namespace AlbumArtTool.Management
{
    internal sealed class LibraryOptions
    {
        public string Root { get; set; }
        public string BackupRoot { get; set; }
        public string EncoderPath { get; set; }
        public bool Convert { get; set; }
        public bool ConvertOtherLossy { get; set; }
        public bool Square { get; set; }
        public bool Organize { get; set; }
        public bool RenameFromTitle { get; set; }
        public bool CombineDiscs { get; set; }
        public bool RemoveFeaturedTitles { get; set; }
        public bool Duplicates { get; set; }
        public bool ArchiveOldBackups { get; set; }
    }

    internal sealed class LibraryAction
    {
        internal Track Track;
        public string Source { get; set; }
        public string Target { get; set; }
        public string Keeper { get; set; }
        public string KeeperHash { get; set; }
        public string ExpectedHash { get; set; }
        public string Reason { get; set; }
        public bool Convert { get; set; }
        public bool Square { get; set; }
        public bool Archive { get; set; }
        public bool Selected { get; set; } = true;
        public string NewTitle { get; set; }
        public bool MergeDiscs { get; set; }
        public string MergeGroup { get; set; }
        public string MergeSourceRoot { get; set; }
        public string MergedAlbum { get; set; }
        public string MergedArtist { get; set; }
        public uint MergedTrack { get; set; }
        public uint MergedTrackCount { get; set; }
        internal bool ChangesTags => MergeDiscs || NewTitle != null;
        internal FileStamp Stamp;
        internal bool Move => !string.IsNullOrEmpty(Target) && !Source.Equals(Target, StringComparison.OrdinalIgnoreCase);
        internal bool Rename => !string.IsNullOrEmpty(Target) && !Path.GetFileNameWithoutExtension(Source).Equals(Path.GetFileNameWithoutExtension(Target), StringComparison.Ordinal);
        internal bool HasWork => Archive || Convert || Square || Move || Rename || ChangesTags;
        internal string Description => Archive ? Keeper != null ? "Remove duplicate (backed up)" : "Archive old backup" :
            string.Join(" + ", new[] { Convert ? "MP3 320" : null, Square ? "Square art" : null, MergeDiscs ? "Combine discs · track " + MergedTrack : null, NewTitle != null ? "Clean title" : null, Rename ? "Rename" : null,
                Move && !Path.GetDirectoryName(Source).Equals(Path.GetDirectoryName(Target), StringComparison.OrdinalIgnoreCase) ? "Move" : null }.Where(s => s != null));
    }

    internal sealed class LibraryPlan
    {
        internal LibraryOptions Options;
        internal List<LibraryAction> Actions = new List<LibraryAction>();
        internal List<string> Issues = new List<string>();
    }

    internal static class LibraryPaths
    {
        private static readonly Regex Reserved = new Regex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase);
        private static readonly Regex Spaces = new Regex(@"\s+");
        internal static string Component(string name)
        {
            string value = Spaces.Replace(name ?? "", " ").Trim();
            foreach (char c in "<>:\"/\\|?*") value = value.Replace(c, c == ':' ? '-' : '_');
            value = new string(value.Where(c => !char.IsControl(c)).ToArray()).TrimEnd(' ', '.');
            if (Reserved.IsMatch(value)) value = "_" + value;
            if (value.Length > 120)
            {
                int length = char.IsHighSurrogate(value[119]) ? 119 : 120;
                value = value.Substring(0, length).TrimEnd(' ', '.');
            }
            return value;
        }
        internal static void Safe(string path, string root)
        {
            if (!BackupSettings.Within(path, root)) throw new IOException("A planned path escapes the selected folder.");
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (folder != null && BackupSettings.Within(folder, root))
            {
                if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folders are not changed.");
                folder = Path.GetDirectoryName(folder);
            }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files are not changed.");
        }
        internal static string Available(string desired, string source, HashSet<string> reserved)
        {
            string path = desired; int n = 2;
            while (reserved.Contains(path) || (File.Exists(path) || Directory.Exists(path)) && !path.Equals(source, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Path.GetDirectoryName(desired), Path.GetFileNameWithoutExtension(desired) + " (" + n++ + ")" + Path.GetExtension(desired));
            reserved.Add(path); return path;
        }
        internal static bool ShouldConvert(Track track, LibraryOptions options)
        {
            if (!options.Convert || track.HasVideo) return false;
            bool mp3 = Path.GetExtension(track.Path).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
            return mp3 ? track.Bitrate > 320 : track.Lossless || track.Bitrate > 320 || options.ConvertOtherLossy;
        }
    }

    // Sorted track paths: "every track inside this folder" becomes a binary search plus a
    // short range scan instead of a pass over the whole library for every album.
    internal sealed class FolderIndex
    {
        private readonly Track[] tracks;
        internal FolderIndex(IEnumerable<Track> all) { tracks = all.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToArray(); }
        internal IEnumerable<Track> Within(string folder)
        {
            string prefix = PathUtil.Clean(folder);
            if (prefix.Length == 0 || prefix[prefix.Length - 1] != Path.DirectorySeparatorChar) prefix += Path.DirectorySeparatorChar;
            int low = 0, high = tracks.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (string.Compare(tracks[middle].Path, prefix, StringComparison.OrdinalIgnoreCase) < 0) low = middle + 1; else high = middle;
            }
            for (int i = low; i < tracks.Length && tracks[i].Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++) yield return tracks[i];
        }
    }

    // Embedded pictures repeat across an album's tracks; inspect each distinct image once.
    internal sealed class PictureShapes
    {
        private readonly Dictionary<string, string> invalid = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> square = new Dictionary<string, bool>();
        internal bool IsSquare(byte[] bytes)
        {
            string key = bytes.Length + ":" + System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes));
            if (invalid.TryGetValue(key, out string error)) throw new InvalidDataException(error);
            if (square.TryGetValue(key, out bool known)) return known;
            try
            {
                var size = Artwork.ReadInfo(bytes);
                if (!Artwork.IsValid(bytes)) throw new InvalidDataException("The embedded image could not be decoded.");
                square[key] = size.Width == size.Height; return size.Width == size.Height;
            }
            catch (Exception e) when (Artwork.IsImageError(e)) { invalid[key] = e.Message; throw; }
        }
    }

    internal static class LibraryPlanner
    {
        private static readonly Regex DiscFolder = new Regex(@"^(CD|DISC|DISK)\s*[-_. ]*\d+$", RegexOptions.IgnoreCase);
        private static readonly Regex CopySuffix = new Regex(@"(?i)(\(\d+\)|copy|duplicate)$");
        private static readonly Regex CopyName = new Regex(@"(?i)\s*(\(\d+\)|[-_ ]+(copy|duplicate))\s*$");
        private static readonly Regex DiscName = new Regex(@"^Disc \d+$");
        internal static readonly HashSet<string> AssetExtensions = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".pdf", ".txt", ".nfo", ".cue", ".log", ".m3u", ".m3u8", ".pls", ".lrc", ".srt" }, StringComparer.OrdinalIgnoreCase);

        internal static LibraryPlan Build(LibraryOptions options, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            options.Root = Path.GetFullPath(options.Root); options.BackupRoot = Path.GetFullPath(options.BackupRoot);
            if (BackupSettings.Within(options.BackupRoot, options.Root)) throw new IOException("The backup folder must be outside the music folder so your player cannot index the backups.");
            LibraryPaths.Safe(options.Root, options.Root); LibraryPaths.Safe(options.BackupRoot, Path.GetPathRoot(options.BackupRoot));
            var scan = new Scanner().Scan(options.Root, token, progress);
            if (scan.Cancelled) throw new OperationCanceledException(token);
            var plan = new LibraryPlan { Options = options }; plan.Issues.AddRange(scan.Issues);
            var tracks = scan.Albums.SelectMany(a => a.Tracks).ToArray();
            if (tracks.Any(t => LibraryPaths.ShouldConvert(t, options)))
            {
                if (string.IsNullOrEmpty(options.EncoderPath)) throw new IOException("Set up FFmpeg before previewing conversion, or uncheck conversion.");
                Encoder.Validate(options.EncoderPath, token);
            }
            var actions = new Dictionary<string, LibraryAction>(StringComparer.OrdinalIgnoreCase);
            var albumFor = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in scan.Albums) foreach (var track in album.Tracks) albumFor[track.Path] = album;
            var shapes = new PictureShapes();
            int done = 0;
            foreach (var track in tracks)
            {
                token.ThrowIfCancellationRequested();
                var row = new LibraryAction { Source = track.Path, Target = track.Path, Track = track, Stamp = track.Stamp, Convert = LibraryPaths.ShouldConvert(track, options) };
                try
                {
                    if (!track.Stamp.Matches(track.Path)) throw new IOException("The file changed during preview. Preview again.");
                    if (options.Square)
                        using (var file = TagLib.File.Create(track.Path, TagLib.ReadStyle.None))
                            row.Square = file.Tag.Pictures.Any(p => !shapes.IsSquare(p.Data.Data));
                    if (options.Organize)
                    {
                        var album = albumFor[track.Path];
                        string artist = !string.IsNullOrWhiteSpace(album.TaggedArtist) && !album.TaggedArtist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase) ? album.TaggedArtist :
                            !string.IsNullOrEmpty(album.MainArtist) ? album.MainArtist : album.Artist.Equals("Various artists", StringComparison.OrdinalIgnoreCase) ? "Various Artists" : track.Performer;
                        if (!ArtistRules.Specific(artist) && album.Artist.Equals("Various artists", StringComparison.OrdinalIgnoreCase)) artist = "Various Artists";
                        string folder = LibraryPaths.Component(artist), title = LibraryPaths.Component(track.AlbumTitle);
                        if (folder.Length == 0 || title.Length == 0) row.Reason = "Organization skipped: missing usable Artist/Album tags.";
                        else
                        {
                            string dest = Path.Combine(options.Root, folder, title);
                            string parent = Path.GetFileName(Path.GetDirectoryName(track.Path));
                            if (track.Disc > 1 || track.DiscCount > 1 && track.Disc > 0 || DiscFolder.IsMatch(parent))
                                dest = Path.Combine(dest, "Disc " + (track.Disc > 0 ? track.Disc.ToString() : Regex.Match(parent, @"\d+").Value));
                            row.Target = Path.Combine(dest, Path.GetFileName(track.Path));
                        }
                    }
                    if (options.RenameFromTitle)
                    {
                        // Scanner titles fall back to filenames; only real Title metadata can name a file.
                        string title = track.HasTitleTag ? LibraryPaths.Component(track.Title) : "";
                        if (title.Length == 0) AddReason(row, "Filename kept: missing usable Title tag.");
                        else row.Target = Path.Combine(Path.GetDirectoryName(row.Target), title + Path.GetExtension(row.Target));
                    }
                    if (options.RemoveFeaturedTitles)
                    {
                        string title = track.HasTitleTag ? TitleCleanup.Clean(track.Title) : null;
                        if (title != null && !title.Equals(track.Title, StringComparison.Ordinal)) row.NewTitle = title;
                        string previousName = Path.GetFileNameWithoutExtension(row.Target);
                        string name = TitleCleanup.Clean(previousName);
                        if (name != previousName && !string.IsNullOrWhiteSpace(name)) row.Target = Path.Combine(Path.GetDirectoryName(row.Target), LibraryPaths.Component(name) + Path.GetExtension(row.Target));
                        if (row.NewTitle != null) AddReason(row, "Title: " + track.Title + " → " + row.NewTitle);
                    }
                    if (row.Convert) row.Target = Path.ChangeExtension(row.Target, ".mp3");
                    LibraryPaths.Safe(row.Target, options.Root);
                    if (row.Target.Length > 250) throw new IOException("The planned path is longer than 250 characters. Shorten the artist/album/file names first.");
                    if (track.HasVideo && options.Convert) AddReason(row, "This file has video; automatic audio conversion is skipped.");
                    if (!track.HasVideo && !row.Convert && options.Convert && !track.Lossless) AddReason(row, "Existing MP3/lower-bitrate lossy audio is not re-encoded.");
                    actions.Add(track.Path, row);
                }
                catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
                { plan.Issues.Add(track.Path + ": " + e.Message); }
                progress?.Report(new ProgressInfo { Completed = ++done, Total = tracks.Length, Message = "Planning library changes · " + done + "/" + tracks.Length + " tracks" });
            }
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string HashOf(string path)
            {
                if (!hashes.TryGetValue(path, out string hash)) hashes[path] = hash = CoverEditor.Hash(path);
                return hash;
            }
            if (options.Duplicates)
            {
                var matches = new DuplicateScanner().Scan(scan.Albums, token, progress); plan.Issues.AddRange(matches.Issues);
                foreach (var known in matches.FileHashes) hashes[known.Key] = known.Value;
                var confirmed = matches.Songs.Where(g => g.Confirmed).Select(g => g.Tracks.ToArray()).ToList();
                // Other containers can be confirmed by FFmpeg's demuxed packet hash when the encoder is available.
                if (!string.IsNullOrEmpty(options.EncoderPath))
                    foreach (var group in tracks.Where(t => !t.HasVideo && !IsMp3OrFlac(t.Path)).GroupBy(t => SongKey(t, albumFor[t.Path])).Where(g => g.Count() > 1))
                    {
                        token.ThrowIfCancellationRequested();
                        var audio = new List<Tuple<Track, string>>();
                        foreach (var track in group)
                            try { if (!track.Stamp.Matches(track.Path)) continue; string hash = Encoder.AudioHash(options.EncoderPath, track, token); if (track.Stamp.Matches(track.Path)) audio.Add(Tuple.Create(track, hash)); }
                            catch (Exception e) when (Scanner.IsFileError(e)) { plan.Issues.Add(track.Path + ": " + e.Message); }
                        confirmed.AddRange(audio.GroupBy(x => x.Item2).Where(g => g.Count() > 1).Select(g => g.Select(x => x.Item1).ToArray()));
                    }
                foreach (var match in confirmed)
                    foreach (var logical in match.Where(t => actions.ContainsKey(t.Path)).GroupBy(t => SongKey(t, albumFor[t.Path])).Where(g => g.Count() > 1))
                    {
                        if (logical.Where(t => t.Disc > 0).Select(t => t.Disc).Distinct().Take(2).Count() > 1 ||
                            logical.Where(t => t.Number > 0).Select(t => t.Number).Distinct().Take(2).Count() > 1) continue;
                        var keep = logical.OrderByDescending(t => t.HasCover).ThenBy(t => CopySuffix.IsMatch(Path.GetFileNameWithoutExtension(t.Path)) ? 1 : 0)
                            .ThenBy(t => t.Path.Length).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).First();
                        string keepHash = HashOf(keep.Path);
                        foreach (var copy in logical.Where(t => t != keep))
                        {
                            var row = actions[copy.Path];
                            if (row.Archive) continue;
                            row.Archive = true; row.Convert = row.Square = false; row.Target = null; row.Keeper = keep.Path; row.KeeperHash = keepHash;
                            row.ExpectedHash = HashOf(copy.Path); row.Reason = "Confirmed file/audio copy of " + keep.Path;
                        }
                    }
            }
            foreach (var row in actions.Values.Where(a => a.Keeper != null))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (actions.TryGetValue(row.Keeper, out var next) && next.Archive && next.Keeper != null && seen.Add(row.Keeper)) row.Keeper = next.Keeper;
                row.KeeperHash = HashOf(row.Keeper);
            }
            var index = new FolderIndex(tracks);
            if (options.CombineDiscs) MultiDisc.Plan(plan, scan, actions, token);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in actions.Values.Where(a => !a.Archive).OrderBy(a => a.Move ? 1 : 0))
                row.Target = LibraryPaths.Available(row.Target, row.Source, reserved);
            plan.Actions.AddRange(actions.Values.Where(a => !a.Archive));
            if (options.Organize) AddAssets(plan, scan, index, actions, reserved, token);
            if (options.CombineDiscs) MultiDisc.AddAssets(plan, index, actions, reserved, token);
            plan.Actions.AddRange(actions.Values.Where(a => a.Archive));
            if (options.ArchiveOldBackups) AddOldBackups(plan, token);
            return plan;
        }

        private static bool IsMp3OrFlac(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase);
        }
        private static void AddReason(LibraryAction row, string reason) => row.Reason = string.IsNullOrEmpty(row.Reason) ? reason : row.Reason + " " + reason;
        private static string SongKey(Track track, Album album)
        {
            string artist = album.MainArtist;
            if (string.IsNullOrEmpty(artist)) artist = string.IsNullOrWhiteSpace(album.TaggedArtist) ? track.Performer : album.TaggedArtist;
            string title = track.HasTitleTag ? track.Title : CopyName.Replace(Path.GetFileNameWithoutExtension(track.Path), "");
            // Missing metadata cannot establish that tracks in different folders belong to one album.
            string scope = string.IsNullOrWhiteSpace(track.AlbumTitle) || !(ArtistRules.Specific(artist) || string.Equals(artist, "Various Artists", StringComparison.OrdinalIgnoreCase))
                ? "folder:" + ArtistRules.Normalize(Path.GetDirectoryName(track.Path)) + "\u001f" : "";
            // Repeated audio in different albums or different titled/numbered tracks is kept.
            return scope + ArtistRules.Normalize(artist) + "\u001f" + ArtistRules.Normalize(track.AlbumTitle) + "\u001f" + ArtistRules.Normalize(title);
        }

        private static void AddAssets(LibraryPlan plan, ScanResult scan, FolderIndex index, Dictionary<string, LibraryAction> actions, HashSet<string> reserved, CancellationToken token)
        {
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = PathUtil.Clean(plan.Options.Root);
            foreach (var album in scan.Albums)
            {
                token.ThrowIfCancellationRequested();
                string source = album.Folder;
                string albumTitle = ArtistRules.Normalize(album.Tracks[0].AlbumTitle);
                while (source != null && !source.Equals(plan.Options.Root, StringComparison.OrdinalIgnoreCase) && PathUtil.WithinFull(PathUtil.Clean(source), root))
                {
                    string name = Path.GetFileName(source); if (name.StartsWith("Album - ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(8);
                    string normalized = ArtistRules.Normalize(name);
                    if (album.Tracks.All(t => ArtistRules.Normalize(LibraryPaths.Component(t.AlbumTitle)) == normalized)) break;
                    source = Path.GetDirectoryName(source);
                }
                if (source == null || source.Equals(plan.Options.Root, StringComparison.OrdinalIgnoreCase) || !handled.Add(source)) continue;
                var contained = index.Within(source).ToArray();
                if (contained.Any(t => !actions.ContainsKey(t.Path) || ArtistRules.Normalize(t.AlbumTitle) != albumTitle)) continue;
                var live = contained.Select(t => actions[t.Path]).Where(a => !a.Archive).ToArray();
                string destination = live.Length > 0 ? Path.GetDirectoryName(live[0].Target) : null;
                if (destination != null && DiscName.IsMatch(Path.GetFileName(destination))) destination = Path.GetDirectoryName(destination);
                if (destination == null && contained.Length > 0)
                { string keeper = actions[contained[0].Path].Keeper; if (keeper != null && actions.ContainsKey(keeper)) destination = Path.GetDirectoryName(actions[keeper].Target); }
                if (destination != null && DiscName.IsMatch(Path.GetFileName(destination))) destination = Path.GetDirectoryName(destination);
                if (destination == null || live.Any(a => !BackupSettings.Within(a.Target, destination))) continue;
                string mergeGroup = live.FirstOrDefault(a => a.MergeDiscs)?.MergeGroup;
                foreach (string path in WalkFiles(source, token, issues: plan.Issues))
                {
                    if (!AssetExtensions.Contains(Path.GetExtension(path)) || actions.ContainsKey(path)) continue;
                    string relative = path.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    if (mergeGroup != null)
                    {
                        var parts = relative.Split(Path.DirectorySeparatorChar);
                        if (parts.Length > 1 && MultiDisc.IsDiscFolder(parts[0])) relative = Path.Combine(parts.Skip(1).ToArray());
                    }
                    string target = Path.Combine(destination, relative);
                    if (path.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                    var row = new LibraryAction { Source = path, Target = LibraryPaths.Available(target, path, reserved), Stamp = FileStamp.Read(path), MergeGroup = mergeGroup, Reason = "Album artwork/booklet/sidecar kept with its album." };
                    plan.Actions.Add(row);
                }
            }
        }

        internal static IEnumerable<string> WalkFiles(string root, CancellationToken token, bool includeBackups = false, List<string> issues = null)
        {
            string backupRoot = PathUtil.Clean(BackupSettings.Root);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string folder = pending.Pop();
                var files = new List<string>(); var children = new List<string>();
                try
                {
                    foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    {
                        if (item is DirectoryInfo directory)
                        {
                            if ((directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0 &&
                                (includeBackups || directory.Name != ".album-art-backups") && !PathUtil.WithinFull(PathUtil.Clean(directory.FullName), backupRoot)) children.Add(directory.FullName);
                        }
                        else if ((item.Attributes & FileAttributes.ReparsePoint) == 0) files.Add(item.FullName);
                    }
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { issues?.Add(folder + ": " + e.Message); continue; }
                files.Sort(StringComparer.OrdinalIgnoreCase); children.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files) { token.ThrowIfCancellationRequested(); yield return file; }
                foreach (string child in children) pending.Push(child);
            }
        }

        private static void AddOldBackups(LibraryPlan plan, CancellationToken token)
        {
            foreach (string path in WalkFiles(plan.Options.Root, token, true, plan.Issues))
                if (path.Split(Path.DirectorySeparatorChar).Any(s => s.Equals(".album-art-backups", StringComparison.OrdinalIgnoreCase)) ||
                    Path.GetFileName(path).Contains(".before-square-crop") || Path.GetFileName(path).StartsWith(".covercrop-tmp-", StringComparison.OrdinalIgnoreCase))
                    plan.Actions.Add(new LibraryAction { Source = path, Archive = true, Stamp = FileStamp.Read(path), Reason = "Old tool backup moved off the active music tree." });
        }
    }
}
