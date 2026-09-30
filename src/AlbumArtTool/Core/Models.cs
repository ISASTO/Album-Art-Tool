using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        public bool Matches(string path)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == Length && info.LastWriteTimeUtc == ModifiedUtc;
        }
    }

    public sealed class Track
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public string Performer { get; set; }
        public bool HasCover { get; set; }
        public FileStamp Stamp { get; set; }
    }

    public sealed class Album
    {
        public string Key { get; set; }
        public string Folder { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public List<Track> Tracks { get; } = new List<Track>();
        public byte[] Thumbnail { get; set; }
        public string PreviewTrack { get; set; }
        public List<string> FolderCovers { get; } = new List<string>();
        public int MissingCount => Tracks.Count(t => !t.HasCover);
        public bool HasAnyCover => Tracks.Any(t => t.HasCover) || FolderCovers.Count > 0;
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
    }

    public sealed class ProgressInfo
    {
        public string Message { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
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
