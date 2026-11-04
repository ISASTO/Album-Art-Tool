using System;
using System.IO;

namespace AlbumArtTool.Core
{
    internal static class BackupSettings
    {
        // ALBUMART_DATA relocates all per-PC settings, caches and backups (used by the tests).
        internal static string AppData => Environment.GetEnvironmentVariable("ALBUMART_DATA") is string custom && custom.Length > 0
            ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumArtTool");
        private static string setting => Path.Combine(AppData, "backup-folder.txt");
        internal static string Root
        {
            get { try { if (File.Exists(setting)) return Path.GetFullPath(File.ReadAllText(setting).Trim()); } catch { } return Path.Combine(AppData, "LibraryBackups"); }
            set { Directory.CreateDirectory(AppData); File.WriteAllText(setting, Path.GetFullPath(value)); }
        }
        internal static string NewFolder(string sourceFolder)
        {
            string root = Root;
            if (Within(root, sourceFolder)) throw new IOException("Choose a backup folder outside your music folder in Settings.");
            return Path.Combine(root, "edit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        }
        internal static bool Within(string path, string parent) =>
            PathUtil.WithinFull(PathUtil.Clean(Path.GetFullPath(path)), PathUtil.Clean(Path.GetFullPath(parent)));
    }

    internal static class PathUtil
    {
        // For paths that are already absolute and normalized (scanner output, planned targets).
        // Avoids Path.GetFullPath's allocations in loops over whole libraries.
        internal static string Clean(string path) => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
        internal static bool WithinFull(string path, string parent)
        {
            if (path.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
            return path.Length > parent.Length && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                (path[parent.Length] == Path.DirectorySeparatorChar || parent.Length > 0 && parent[parent.Length - 1] == Path.DirectorySeparatorChar);
        }
    }
}
