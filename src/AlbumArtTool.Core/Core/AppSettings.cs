using System;
using System.IO;
using System.Text;

namespace AlbumArtTool.Core
{
    // Small per-PC preferences, each stored as one text file beside the other app data.
    internal static class AppSettings
    {
        internal static string AppearanceFile => Path.Combine(BackupSettings.AppData, "appearance.txt");
        internal static string LastFolderFile => Path.Combine(BackupSettings.AppData, "last-folder.txt");

        internal static bool ReadDark(string path = null)
        {
            string value = Read(path ?? AppearanceFile);
            return value == null || !value.Equals("light", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool SaveDark(bool dark, string path = null) => Write(path ?? AppearanceFile, dark ? "dark" : "light");

        internal static string LastFolder
        {
            get { string value = Read(LastFolderFile); return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null; }
            set { if (!string.IsNullOrWhiteSpace(value)) Write(LastFolderFile, value); }
        }

        private static string Read(string path)
        {
            try { return File.ReadAllText(path).Trim(); }
            catch (Exception e) when (Scanner.IsFileError(e)) { return null; }
        }

        private static bool Write(string path, string value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, value, new UTF8Encoding(false));
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception e) when (Scanner.IsFileError(e)) { return false; }
            finally { try { File.Delete(temporary); } catch { } }
        }
    }
}
