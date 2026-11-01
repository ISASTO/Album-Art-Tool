using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbumArtTool.Core
{
    // Session cache: directory discovery still checks every file's current size and
    // write time. Only unchanged, completely successful folders reuse parsed tags.
    // Copies keep mutable UI albums separate from cached scan snapshots.
    internal static class ScanCache
    {
        private sealed class Entry
        {
            internal Dictionary<string, FileStamp> Stamps;
            internal Album[] Albums;
            internal long Age, Bytes;
        }
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Entry> folders = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static long age, bytes;
        private const long Limit = 48L * 1024 * 1024;
        internal static void Clear() { lock (gate) { folders.Clear(); bytes = 0; } }
        internal static bool TryRead(string path, Dictionary<string, FileStamp> current, out Album[] albums)
        {
            lock (gate)
            {
                if (folders.TryGetValue(path, out var entry) && entry.Stamps.Count == current.Count &&
                    entry.Stamps.All(p => current.TryGetValue(p.Key, out var stamp) && p.Value.Same(stamp)))
                {
                    entry.Age = ++age; albums = entry.Albums.Select(Clone).ToArray(); return true;
                }
            }
            albums = null; return false;
        }
        internal static void Store(string path, Dictionary<string, FileStamp> stamps, IEnumerable<Album> values)
        {
            var albums = values.Select(Clone).ToArray();
            // Do not cache a scan whose files changed while their tags were read.
            try
            {
                if (albums.SelectMany(a => a.Tracks).Any(t => !stamps.TryGetValue(t.Path, out var original) || !original.Same(t.Stamp)) ||
                    stamps.Any(p => !p.Value.Matches(p.Key))) return;
            }
            catch (Exception e) when (Scanner.IsFileError(e)) { return; }
            long size = albums.Sum(a => (long)(a.Thumbnail?.Length ?? 0) + a.Tracks.Count * 1536L);
            if (size > Limit) return;
            lock (gate)
            {
                if (folders.TryGetValue(path, out var previous)) bytes -= previous.Bytes;
                folders[path] = new Entry { Albums = albums, Stamps = new Dictionary<string, FileStamp>(stamps, StringComparer.OrdinalIgnoreCase), Age = ++age, Bytes = size };
                bytes += size;
                while (bytes > Limit || folders.Count > 4096)
                {
                    var oldest = folders.OrderBy(p => p.Value.Age).First();
                    bytes -= oldest.Value.Bytes; folders.Remove(oldest.Key);
                }
            }
        }
        private static Album Clone(Album source)
        {
            var album = new Album { Key = source.Key, Folder = source.Folder, Title = source.Title, Artist = source.Artist,
                TaggedArtist = source.TaggedArtist, MainArtist = source.MainArtist, PreviewTrack = source.PreviewTrack,
                Thumbnail = source.Thumbnail == null ? null : (byte[])source.Thumbnail.Clone() };
            album.FolderCovers.AddRange(source.FolderCovers);
            foreach (var t in source.Tracks)
                album.Tracks.Add(new Track { Path = t.Path, Title = t.Title, Performer = t.Performer,
                    Performers = (string[])t.Performers.Clone(), AlbumArtists = (string[])t.AlbumArtists.Clone(),
                    AlbumTitle = t.AlbumTitle, Disc = t.Disc, DiscCount = t.DiscCount, Number = t.Number, Bitrate = t.Bitrate,
                    Codec = t.Codec, Lossless = t.Lossless, HasVideo = t.HasVideo, Duration = t.Duration, HasTitleTag = t.HasTitleTag,
                    AudioStart = t.AudioStart, AudioEnd = t.AudioEnd, SampleRate = t.SampleRate, Channels = t.Channels, HasCover = t.HasCover, Stamp = t.Stamp });
            return album;
        }
    }
    internal sealed class ArtworkValidationCache
    {
        private readonly Dictionary<string, bool> images = new Dictionary<string, bool>();
        private readonly Queue<string> order = new Queue<string>();
        internal int Decoded { get; private set; }
        internal bool Valid(byte[] data)
        {
            string key;
            using (var hash = System.Security.Cryptography.SHA256.Create()) key = Convert.ToBase64String(hash.ComputeHash(data));
            if (images.TryGetValue(key, out bool valid)) return valid;
            try { Decoded++; using (var image = Artwork.Decode(data)) { } valid = true; }
            catch (Exception e) when (Artwork.IsImageError(e)) { valid = false; }
            if (images.Count >= 256) images.Remove(order.Dequeue());
            images.Add(key, valid); order.Enqueue(key); return valid;
        }
    }
}
