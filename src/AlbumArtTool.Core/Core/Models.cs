using System;
using System.Collections.Generic;
using System.IO;

namespace AlbumArtTool.Core
{
    public sealed class FileStamp
    {
        public long Length { get; private set; }
        public DateTime ModifiedUtc { get; private set; }

        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file no longer exists.", path);
            return new FileStamp { Length = info.Length, ModifiedUtc = info.LastWriteTimeUtc };
        }
        internal static FileStamp FromInfo(FileInfo info) => new FileStamp { Length = info.Length, ModifiedUtc = info.LastWriteTimeUtc };
        internal static FileStamp Create(long length, DateTime modifiedUtc) => new FileStamp { Length = length, ModifiedUtc = modifiedUtc };
        internal bool Same(FileStamp other) => other != null && Length == other.Length && ModifiedUtc == other.ModifiedUtc;
        public bool Matches(string path)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == Length && info.LastWriteTimeUtc == ModifiedUtc;
        }
    }

    public sealed class Track
    {
        private static readonly string[] none = Array.Empty<string>();
        private string normalizedSource, normalized;

        public string Path { get; set; }
        public string Title { get; set; }
        public string Performer { get; set; }
        public string[] Performers { get; set; } = none;
        public string[] AlbumArtists { get; set; } = none;
        public string AlbumTitle { get; set; }
        public uint Disc { get; set; }
        public uint DiscCount { get; set; }
        public uint Number { get; set; }
        public int Bitrate { get; set; }
        public string Codec { get; set; }
        public bool Lossless { get; set; }
        public bool HasVideo { get; set; }
        public TimeSpan Duration { get; set; }
        public bool HasTitleTag { get; set; }
        public long AudioStart { get; set; } = -1;
        public long AudioEnd { get; set; } = -1;
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string ArtistText => string.Join(", ", Performers);
        public bool HasCover { get; set; }
        /// <summary>Embedded pictures of any type, and how many are front/unlabeled covers.</summary>
        public int PictureCount { get; set; }
        public int CoverPictures { get; set; }
        /// <summary>The longest side, in pixels, of the largest embedded picture (0 when there is none or it cannot be read).</summary>
        public int LargestPicture { get; set; }
        public string PictureSummary { get; set; } = "";
        public FileStamp Stamp { get; set; }

        // Normalizing the single track credit is the hot path of artist cleanup counts,
        // filtering and sorting. Cache it against the exact string instance it came from.
        internal string NormalizedSinglePerformer
        {
            get
            {
                string source = Performers != null && Performers.Length == 1 ? Performers[0] : null;
                if (source == null) return null;
                if (!ReferenceEquals(source, normalizedSource)) { normalized = ArtistRules.Normalize(source); normalizedSource = source; }
                return normalized;
            }
        }
    }

    public sealed class Album
    {
        private int missingTracks = -1, missingValue, changeTracks = -1, changeValue;
        private string changeArtist;

        public string Key { get; set; }
        public string Folder { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string TaggedArtist { get; set; }
        public string MainArtist { get; set; }
        public List<Track> Tracks { get; } = new List<Track>();
        public byte[] Thumbnail { get; set; }
        public string PreviewTrack { get; set; }
        public List<string> FolderCovers { get; } = new List<string>();

        // Counts are read for every visible row, tab label, filter and sort comparison.
        // They are computed once per album state rather than re-scanning tracks each time.
        public int ArtistChangeCount
        {
            get
            {
                if (string.IsNullOrEmpty(MainArtist)) return 0;
                if (changeTracks != Tracks.Count || !ReferenceEquals(changeArtist, MainArtist))
                {
                    string target = ArtistRules.Normalize(MainArtist); int count = 0;
                    foreach (var track in Tracks) if (ArtistRules.NeedsChangeNormalized(track, target)) count++;
                    changeValue = count; changeTracks = Tracks.Count; changeArtist = MainArtist;
                }
                return changeValue;
            }
        }
        public int MissingCount
        {
            get
            {
                if (missingTracks != Tracks.Count)
                {
                    int count = 0;
                    foreach (var track in Tracks) if (!track.HasCover) count++;
                    missingValue = count; missingTracks = Tracks.Count;
                }
                return missingValue;
            }
        }
        public bool HasAnyCover => MissingCount < Tracks.Count || FolderCovers.Count > 0;
        public string Coverage => MissingCount == 0 ? "All tracks have art" :
            MissingCount == Tracks.Count ? (FolderCovers.Count > 0 ? "Folder art only" : "No embedded art") :
            MissingCount + " of " + Tracks.Count + " tracks missing art";
    }

    public sealed class ScanResult
    {
        public List<Album> Albums { get; } = new List<Album>();
        public List<string> Issues { get; } = new List<string>();
        public int FilesRead { get; set; }
        public bool Cancelled { get; set; }
        internal int CachedFiles { get; set; }
        internal int FilesOpened { get; set; }
        internal int ArtworkImagesDecoded { get; set; }
    }

    public sealed class ProgressInfo
    {
        public string Message { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public double Fraction { get; set; }
    }

    public sealed class BackupEntry
    {
        public string Original { get; set; }
        public string Backup { get; set; }
        public string WrittenHash { get; set; }
        public bool Created { get; set; }
    }

    public sealed class EditResult
    {
        public List<BackupEntry> Entries { get; } = new List<BackupEntry>();
        public List<string> Issues { get; } = new List<string>();
        public int TracksWritten { get; set; }
        public int Restored { get; set; }
    }
}
