using System;
using System.IO;

namespace AlbumArtTool.Core
{
    internal static class BackupSettings
    {
        internal static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumArtTool");
        private static string setting => Path.Combine(AppData, "backup-folder.txt");
        internal static string Root
        {
            get { try { if (File.Exists(setting)) return Path.GetFullPath(File.ReadAllText(setting).Trim()); } catch { } return Path.Combine(AppData, "LibraryBackups"); }
            set { Directory.CreateDirectory(AppData); File.WriteAllText(setting, Path.GetFullPath(value)); }
        }
        internal static string NewFolder(string sourceFolder)
        {
            string root = Root;
            if (Within(root, sourceFolder)) throw new IOException("Choose a backup folder outside your music folder in Manage library.");
            return Path.Combine(root, "edit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        }
        internal static bool Within(string path, string parent)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
