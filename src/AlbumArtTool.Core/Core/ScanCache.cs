using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AlbumArtTool.Core
{
    // Folder-level tag cache. Directory discovery still checks every file's current size and
    // write time; only unchanged, completely successful folders reuse parsed tags. Copies keep
    // mutable UI albums separate from cached snapshots. The cache is also saved on this PC so an
    // unchanged library opens without re-reading every music file after a restart.
    internal static class ScanCache
    {
        private sealed class Entry
        {
            internal Dictionary<string, FileStamp> Stamps;
            internal Album[] Albums;
            internal long Age, Bytes;
        }
        private const int FormatVersion = 5;
        private const long Limit = 48L * 1024 * 1024;
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Entry> folders = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static long age, bytes;
        private static bool loaded, dirty;
        private static Timer saveTimer;

        internal static bool Persistent { get; set; } = true;
        internal static string FilePath => Path.Combine(BackupSettings.AppData, "scan-cache.bin");

        internal static void Clear()
        {
            lock (gate) { folders.Clear(); bytes = 0; loaded = true; dirty = false; }
            try { File.Delete(FilePath); } catch { }
        }

        // Forgets the in-memory copy so the next scan loads the saved cache, as after a restart.
        internal static void Unload() { lock (gate) { folders.Clear(); bytes = 0; loaded = false; dirty = false; } }

        internal static bool TryRead(string path, Dictionary<string, FileStamp> current, out Album[] albums)
        {
            lock (gate)
            {
                EnsureLoaded();
                if (folders.TryGetValue(path, out var entry) && entry.Stamps.Count == current.Count && entry.Stamps.All(p => current.TryGetValue(p.Key, out var stamp) && p.Value.Same(stamp)))
                {
                    entry.Age = ++age; albums = entry.Albums;
                }
                else albums = null;
            }
            if (albums == null) return false;
            albums = albums.Select(Clone).ToArray();
            return true;
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
            long size = Size(albums);
            if (size > Limit) return;
            lock (gate)
            {
                EnsureLoaded();
                Add(path, new Entry { Albums = albums, Stamps = new Dictionary<string, FileStamp>(stamps, StringComparer.OrdinalIgnoreCase), Age = ++age, Bytes = size });
                dirty = true;
            }
        }

        private static long Size(Album[] albums) => albums.Sum(a => (long)(a.Thumbnail?.Length ?? 0) + a.Tracks.Count * 1536L);

        private static void Add(string path, Entry entry)
        {
            if (folders.TryGetValue(path, out var previous)) bytes -= previous.Bytes;
            folders[path] = entry; bytes += entry.Bytes;
            while (bytes > Limit || folders.Count > 4096)
            {
                var oldest = folders.OrderBy(p => p.Value.Age).First();
                bytes -= oldest.Value.Bytes; folders.Remove(oldest.Key);
            }
        }

        /// <summary>Saves changes shortly after a scan, off the UI thread.</summary>
        internal static void ScheduleSave()
        {
            if (!Persistent) return;
            lock (gate)
            {
                if (!dirty) return;
                saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                saveTimer.Change(1500, Timeout.Infinite);
            }
        }

        internal static void Flush()
        {
            if (!Persistent) return;
            List<KeyValuePair<string, Entry>> snapshot;
            lock (gate) { if (!dirty) return; dirty = false; snapshot = folders.ToList(); }
            string target = FilePath, temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(0x43544141); writer.Write(FormatVersion); writer.Write(LibraryVersion);
                    writer.Write(snapshot.Count);
                    foreach (var item in snapshot)
                    {
                        writer.Write(item.Key);
                        writer.Write(item.Value.Stamps.Count);
                        foreach (var stamp in item.Value.Stamps) { writer.Write(stamp.Key); WriteStamp(writer, stamp.Value); }
                        writer.Write(item.Value.Albums.Length);
                        foreach (var album in item.Value.Albums) WriteAlbum(writer, album);
                    }
                }
                File.Move(temporary, target, true);
            }
            catch (Exception e) when (Scanner.IsFileError(e)) { }
            finally { try { File.Delete(temporary); } catch { } }
        }

        // A tag library upgrade can change what is read from the same file, so the saved
        // cache is only reused by the same app build.
        private static string LibraryVersion => typeof(ScanCache).Assembly.GetName().Version + "|" + typeof(TagLib.File).Assembly.GetName().Version;

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            if (!Persistent) return;
            try
            {
                if (!File.Exists(FilePath)) return;
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != 0x43544141 || reader.ReadInt32() != FormatVersion || reader.ReadString() != LibraryVersion) return;
                    int count = reader.ReadInt32();
                    var restored = new List<KeyValuePair<string, Entry>>(count);
                    for (int i = 0; i < count; i++)
                    {
                        string path = reader.ReadString();
                        int stampCount = reader.ReadInt32();
                        var stamps = new Dictionary<string, FileStamp>(stampCount, StringComparer.OrdinalIgnoreCase);
                        for (int s = 0; s < stampCount; s++) { string file = reader.ReadString(); stamps[file] = ReadStamp(reader); }
                        var albums = new Album[reader.ReadInt32()];
                        for (int a = 0; a < albums.Length; a++) albums[a] = ReadAlbum(reader);
                        restored.Add(new KeyValuePair<string, Entry>(path, new Entry { Stamps = stamps, Albums = albums, Bytes = Size(albums) }));
                    }
                    foreach (var item in restored) { item.Value.Age = ++age; Add(item.Key, item.Value); }
                }
            }
            catch (Exception e) when (Scanner.IsFileError(e) || e is EndOfStreamException || e is FormatException || e is ArgumentException || e is OverflowException)
            {
                // A damaged cache only costs a full scan.
                folders.Clear(); bytes = 0;
            }
        }

        private static void WriteStamp(BinaryWriter writer, FileStamp stamp) { writer.Write(stamp.Length); writer.Write(stamp.ModifiedUtc.Ticks); }
        private static FileStamp ReadStamp(BinaryReader reader) => FileStamp.Create(reader.ReadInt64(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
        private static void Text(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
        private static string Text(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
        private static void Texts(BinaryWriter writer, string[] values) { writer.Write(values?.Length ?? 0); if (values != null) foreach (string value in values) Text(writer, value); }
        private static string[] Texts(BinaryReader reader)
        {
            var values = new string[reader.ReadInt32()];
            for (int i = 0; i < values.Length; i++) values[i] = Text(reader);
            return values;
        }

        private static void WriteAlbum(BinaryWriter writer, Album album)
        {
            Text(writer, album.Key); Text(writer, album.Folder); Text(writer, album.Title); Text(writer, album.Artist);
            Text(writer, album.TaggedArtist); Text(writer, album.MainArtist); Text(writer, album.PreviewTrack);
            writer.Write(album.Thumbnail?.Length ?? -1); if (album.Thumbnail != null) writer.Write(album.Thumbnail);
            Texts(writer, album.FolderCovers.ToArray());
            writer.Write(album.Tracks.Count);
            foreach (var t in album.Tracks)
            {
                Text(writer, t.Path); Text(writer, t.Title); Text(writer, t.Performer); Texts(writer, t.Performers); Texts(writer, t.AlbumArtists);
                Text(writer, t.AlbumTitle); writer.Write(t.Disc); writer.Write(t.DiscCount); writer.Write(t.Number); writer.Write(t.Bitrate);
                Text(writer, t.Codec); writer.Write(t.Lossless); writer.Write(t.HasVideo); writer.Write(t.Duration.Ticks); writer.Write(t.HasTitleTag);
                writer.Write(t.AudioStart); writer.Write(t.AudioEnd); writer.Write(t.SampleRate); writer.Write(t.Channels); writer.Write(t.HasCover);
                writer.Write(t.PictureCount); writer.Write(t.CoverPictures); Text(writer, t.PictureSummary); writer.Write(t.LargestPicture);
                WriteStamp(writer, t.Stamp);
            }
        }

        private static Album ReadAlbum(BinaryReader reader)
        {
            var album = new Album { Key = Text(reader), Folder = Text(reader), Title = Text(reader), Artist = Text(reader), TaggedArtist = Text(reader), MainArtist = Text(reader), PreviewTrack = Text(reader) };
            int thumbnail = reader.ReadInt32();
            if (thumbnail >= 0) album.Thumbnail = reader.ReadBytes(thumbnail);
            album.FolderCovers.AddRange(Texts(reader));
            int tracks = reader.ReadInt32();
            for (int i = 0; i < tracks; i++)
                album.Tracks.Add(new Track
                {
                    Path = Text(reader), Title = Text(reader), Performer = Text(reader), Performers = Texts(reader), AlbumArtists = Texts(reader),
                    AlbumTitle = Text(reader), Disc = reader.ReadUInt32(), DiscCount = reader.ReadUInt32(), Number = reader.ReadUInt32(), Bitrate = reader.ReadInt32(),
                    Codec = Text(reader), Lossless = reader.ReadBoolean(), HasVideo = reader.ReadBoolean(), Duration = new TimeSpan(reader.ReadInt64()), HasTitleTag = reader.ReadBoolean(),
                    AudioStart = reader.ReadInt64(), AudioEnd = reader.ReadInt64(), SampleRate = reader.ReadInt32(), Channels = reader.ReadInt32(), HasCover = reader.ReadBoolean(),
                    PictureCount = reader.ReadInt32(), CoverPictures = reader.ReadInt32(), PictureSummary = Text(reader) ?? "", LargestPicture = reader.ReadInt32(),
                    Stamp = ReadStamp(reader)
                });
            return album;
        }

        private static Album Clone(Album source)
        {
            var album = new Album { Key = source.Key, Folder = source.Folder, Title = source.Title, Artist = source.Artist,
                TaggedArtist = source.TaggedArtist, MainArtist = source.MainArtist, PreviewTrack = source.PreviewTrack,
                Thumbnail = source.Thumbnail }; // thumbnail bytes are never modified in place
            album.FolderCovers.AddRange(source.FolderCovers);
            foreach (var t in source.Tracks)
                album.Tracks.Add(new Track { Path = t.Path, Title = t.Title, Performer = t.Performer,
                    Performers = (string[])t.Performers.Clone(), AlbumArtists = (string[])t.AlbumArtists.Clone(),
                    AlbumTitle = t.AlbumTitle, Disc = t.Disc, DiscCount = t.DiscCount, Number = t.Number, Bitrate = t.Bitrate,
                    Codec = t.Codec, Lossless = t.Lossless, HasVideo = t.HasVideo, Duration = t.Duration, HasTitleTag = t.HasTitleTag,
                    AudioStart = t.AudioStart, AudioEnd = t.AudioEnd, SampleRate = t.SampleRate, Channels = t.Channels, HasCover = t.HasCover, Stamp = t.Stamp,
                    PictureCount = t.PictureCount, CoverPictures = t.CoverPictures, PictureSummary = t.PictureSummary, LargestPicture = t.LargestPicture });
            return album;
        }
    }
}
