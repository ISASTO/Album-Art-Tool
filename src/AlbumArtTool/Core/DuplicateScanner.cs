using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AlbumArtTool.Core
{
    public sealed class DuplicateGroup
    {
        public string Basis { get; set; }
        public bool Confirmed { get; set; }
        public List<Track> Tracks { get; } = new List<Track>();
        public List<Album> Albums { get; } = new List<Album>();
        public string Name => Albums.Count > 0 ? Albums[0].Artist + " · " + Albums[0].Title : Tracks[0].ArtistText + " · " + Tracks[0].Title;
    }
    public sealed class DuplicateResult
    {
        public List<DuplicateGroup> Songs { get; } = new List<DuplicateGroup>();
        public List<DuplicateGroup> Albums { get; } = new List<DuplicateGroup>();
        public List<string> Issues { get; } = new List<string>();
    }

    public sealed class DuplicateScanner
    {
        private sealed class Item
        {
            internal Track Track;
            internal Album Album;
            internal long Start, End;
            internal string Signature, Prefix, FileHash, AudioHash;
            internal string Identity => AudioHash != null ? "audio:" + Signature + ":" + AudioHash : FileHash != null ? "file:" + FileHash : null;
        }

        public DuplicateResult Scan(IEnumerable<Album> albums, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            var result = new DuplicateResult();
            var input = albums.ToList();
            var items = input.SelectMany(a => a.Tracks.Select(t => new Item { Track = t, Album = a })).GroupBy(i => i.Track.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var valid = new List<Item>(); var clock = Stopwatch.StartNew();
            void Report(string phase, int done, int total, bool force = false)
            {
                if (force || clock.ElapsedMilliseconds >= 100)
                { progress?.Report(new ProgressInfo { Message = phase + " · " + done + "/" + total + " tracks", Completed = done, Total = total }); clock.Restart(); }
            }
            int checkedCount = 0; Report("Finding duplicate candidates", 0, items.Count, true);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    CheckStamp(item.Track);
                    using (var stream = Open(item.Track.Path))
                    {
                        if (AudioRange(item, stream))
                            item.Prefix = item.Signature + ":" + (item.End - item.Start) + ":" + HashRange(stream, item.Start, Math.Min(65536, item.End - item.Start), token);
                    }
                    CheckStamp(item.Track); valid.Add(item);
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { result.Issues.Add(item.Track.Path + ": " + e.Message); }
                Report("Finding duplicate candidates", ++checkedCount, items.Count, checkedCount == items.Count);
            }
            var sizes = new HashSet<long>(valid.GroupBy(i => i.Track.Stamp.Length).Where(g => g.Count() > 1).Select(g => g.Key));
            var prefixes = new HashSet<string>(valid.Where(i => i.Prefix != null).GroupBy(i => i.Prefix).Where(g => g.Count() > 1).Select(g => g.Key));
            var candidates = valid.Where(i => sizes.Contains(i.Track.Stamp.Length) || i.Prefix != null && prefixes.Contains(i.Prefix)).ToList();
            checkedCount = 0; Report("Comparing complete files and audio", 0, candidates.Count, true);
            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    CheckStamp(item.Track); HashFileAndAudio(item, token); CheckStamp(item.Track);
                }
                catch (Exception e) when (Scanner.IsFileError(e))
                { item.FileHash = item.AudioHash = null; valid.Remove(item); result.Issues.Add(item.Track.Path + ": " + e.Message); }
                Report("Comparing complete files and audio", ++checkedCount, candidates.Count, checkedCount == candidates.Count);
            }
            token.ThrowIfCancellationRequested();
            var claimed = new HashSet<Track>();
            foreach (var group in valid.Where(i => i.AudioHash != null).GroupBy(i => i.Signature + ":" + i.AudioHash).Where(g => g.Count() > 1))
            {
                var match = new DuplicateGroup { Confirmed = true, Basis = group.Select(i => i.FileHash).Distinct().Count() == 1 ? "Identical files" : "Identical audio bytes; tags or artwork may differ" };
                match.Tracks.AddRange(group.Select(i => i.Track)); result.Songs.Add(match);
                foreach (var item in group) claimed.Add(item.Track);
            }
            foreach (var group in valid.Where(i => i.FileHash != null).GroupBy(i => i.FileHash).Where(g => g.Count() > 1 && g.Any(i => !claimed.Contains(i.Track))))
            {
                var match = new DuplicateGroup { Confirmed = true, Basis = "Identical files" };
                match.Tracks.AddRange(group.Select(i => i.Track)); result.Songs.Add(match);
            }
            foreach (var group in valid.Where(HasMetadata).GroupBy(MetadataKey))
            {
                var remaining = group.OrderBy(i => i.Track.Duration).ToList();
                while (remaining.Count > 0)
                {
                    var first = remaining[0];
                    var similar = remaining.TakeWhile(i => (i.Track.Duration - first.Track.Duration).TotalSeconds <= 2).ToList();
                    remaining.RemoveRange(0, similar.Count);
                    if (similar.Count < 2 || similar.All(i => i.Identity != null && i.Identity == first.Identity)) continue;
                    var match = new DuplicateGroup { Confirmed = false, Basis = "Possible match: title, artist and duration within 2 seconds" };
                    match.Tracks.AddRange(similar.Select(i => i.Track)); result.Songs.Add(match);
                }
            }
            AddAlbums(input, valid, result, token);
            result.Songs.Sort((a, b) => a.Confirmed != b.Confirmed ? (a.Confirmed ? -1 : 1) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            progress?.Report(new ProgressInfo { Message = "Duplicate scan finished", Completed = items.Count, Total = items.Count });
            return result;
        }

        private static void AddAlbums(List<Album> albums, List<Item> valid, DuplicateResult result, CancellationToken token)
        {
            var data = valid.ToDictionary(i => i.Track);
            string Identity(Album a) => a.Tracks.All(t => data.ContainsKey(t) && data[t].Identity != null)
                ? string.Join("\n", a.Tracks.Select(t => data[t].Identity).OrderBy(s => s, StringComparer.Ordinal)) : null;
            var complete = albums.Where(a => a.Tracks.Count > 0 && a.Tracks.All(data.ContainsKey)).ToList();
            var confirmed = new Dictionary<Album, string>();
            foreach (var group in complete.Select(a => new { Album = a, Key = Identity(a) }).Where(x => x.Key != null).GroupBy(x => x.Key).Where(g => g.Select(x => x.Album.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                token.ThrowIfCancellationRequested();
                var match = new DuplicateGroup { Confirmed = true, Basis = "Same complete set of file/audio hashes" };
                match.Albums.AddRange(group.Select(x => x.Album)); result.Albums.Add(match);
                foreach (var entry in group) confirmed[entry.Album] = group.Key;
            }
            foreach (var group in complete.Where(a => a.Tracks.All(t => HasMetadata(data[t])))
                .GroupBy(a => ArtistRules.Normalize(a.Title) + "\u001f" + ArtistRules.Normalize(a.Artist) + "\u001f" + a.Tracks.Count))
            {
                var left = group.ToList();
                while (left.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var first = left[0];
                    var list = left.Where(a => SameTrackList(first, a, data)).ToList();
                    foreach (var a in list) left.Remove(a);
                    if (list.Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2 ||
                        confirmed.TryGetValue(first, out var key) && list.All(a => confirmed.TryGetValue(a, out var value) && value == key)) continue;
                    var match = new DuplicateGroup { Basis = "Possible album copy: matching album, artists, track titles and durations", Confirmed = false };
                    match.Albums.AddRange(list); result.Albums.Add(match);
                }
            }
        }
        private static bool SameTrackList(Album a, Album b, Dictionary<Track, Item> data)
        {
            var first = a.Tracks.OrderBy(t => MetadataKey(data[t]), StringComparer.Ordinal).ThenBy(t => t.Duration).ToArray();
            var second = b.Tracks.OrderBy(t => MetadataKey(data[t]), StringComparer.Ordinal).ThenBy(t => t.Duration).ToArray();
            return first.Length == second.Length && first.Zip(second, (x, y) => MetadataKey(data[x]) == MetadataKey(data[y]) && Math.Abs((x.Duration - y.Duration).TotalSeconds) <= 2).All(x => x);
        }
        private static bool HasMetadata(Item item) => item.Track.HasTitleTag && item.Track.Duration > TimeSpan.Zero && ArtistRules.Specific(item.Track.Performer);
        private static string MetadataKey(Item item) => ArtistRules.Normalize(item.Track.Title) + "\u001f" + ArtistRules.Normalize(item.Track.Performer);
        private static void CheckStamp(Track track)
        {
            if ((System.IO.File.GetAttributes(track.Path) & FileAttributes.ReparsePoint) != 0 || !track.Stamp.Matches(track.Path))
                throw new IOException("File changed since the library scan or is a link. Rescan the library and retry.");
        }
        private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);

        private static bool AudioRange(Item item, Stream stream)
        {
            var t = item.Track; string ext = Path.GetExtension(t.Path).ToLowerInvariant();
            if (t.AudioStart < 0 || t.AudioEnd <= t.AudioStart || t.AudioEnd > stream.Length || ext != ".mp3" && ext != ".flac") return false;
            item.Start = t.AudioStart; item.End = t.AudioEnd;
            item.Signature = ext + ":" + t.SampleRate + ":" + t.Channels;
            if (ext == ".mp3") return true;
            stream.Position = item.Start;
            byte[] marker = new byte[4]; ReadExact(stream, marker);
            if (Encoding.ASCII.GetString(marker) != "fLaC") return false;
            byte[] info = null;
            for (int blocks = 0; blocks < 4096; blocks++)
            {
                byte[] header = new byte[4]; ReadExact(stream, header);
                int length = header[1] << 16 | header[2] << 8 | header[3];
                if (stream.Position + length > item.End) throw new InvalidDataException("FLAC metadata extends beyond its audio data.");
                if ((header[0] & 127) == 0 && length == 34) { info = new byte[34]; ReadExact(stream, info); }
                else stream.Seek(length, SeekOrigin.Current);
                if ((header[0] & 128) != 0)
                {
                    item.Start = stream.Position;
                    if (info == null || item.Start >= item.End) return false;
                    item.Signature += ":" + Convert.ToBase64String(info); return true;
                }
            }
            throw new InvalidDataException("Too many FLAC metadata blocks.");
        }
        private static void ReadExact(Stream stream, byte[] bytes)
        { int done = 0; while (done < bytes.Length) { int count = stream.Read(bytes, done, bytes.Length - done); if (count == 0) throw new EndOfStreamException(); done += count; } }
        private static string HashRange(Stream stream, long start, long length, CancellationToken token)
        {
            stream.Position = start;
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                while (length > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                    if (count == 0) throw new EndOfStreamException();
                    hash.TransformBlock(buffer, 0, count, buffer, 0); length -= count;
                }
                hash.TransformFinalBlock(new byte[0], 0, 0); return Convert.ToBase64String(hash.Hash);
            }
        }
        private static void HashFileAndAudio(Item item, CancellationToken token)
        {
            using (var stream = Open(item.Track.Path))
            using (var fileHash = SHA256.Create())
            using (var audioHash = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; long position = 0; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    fileHash.TransformBlock(buffer, 0, count, buffer, 0);
                    if (item.Prefix != null)
                    {
                        long from = Math.Max(item.Start, position), until = Math.Min(item.End, position + count);
                        if (until > from) audioHash.TransformBlock(buffer, (int)(from - position), (int)(until - from), buffer, (int)(from - position));
                    }
                    position += count;
                }
                fileHash.TransformFinalBlock(new byte[0], 0, 0); item.FileHash = Convert.ToBase64String(fileHash.Hash);
                if (item.Prefix != null) { audioHash.TransformFinalBlock(new byte[0], 0, 0); item.AudioHash = Convert.ToBase64String(audioHash.Hash); }
            }
        }
    }
}
