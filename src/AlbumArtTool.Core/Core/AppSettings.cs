using System;
using System.IO;
using System.Text;

namespace AlbumArtTool.Core
{
    /// <summary>Appearance preference: follow the Windows setting, or always light / dark.</summary>
    internal enum ThemeMode { System, Light, Dark }

    // Small per-PC preferences, each stored as one text file beside the other app data.
    internal static class AppSettings
    {
        internal static string AppearanceFile => Path.Combine(BackupSettings.AppData, "appearance.txt");
        internal static string LastFolderFile => Path.Combine(BackupSettings.AppData, "last-folder.txt");

        /// <summary>The saved appearance; no saved choice (or "system") follows Windows.</summary>
        internal static ThemeMode ReadTheme(string path = null)
        {
            string value = Read(path ?? AppearanceFile);
            if (value == null) return ThemeMode.System;
            if (value.Equals("light", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Light;
            if (value.Equals("dark", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Dark;
            return ThemeMode.System;
        }

        internal static bool SaveTheme(ThemeMode mode, string path = null) => Write(path ?? AppearanceFile, mode.ToString().ToLowerInvariant());

        internal static bool ReadDark(string path = null) => ReadTheme(path) != ThemeMode.Light;
        internal static bool SaveDark(bool dark, string path = null) => SaveTheme(dark ? ThemeMode.Dark : ThemeMode.Light, path);

        internal static string LastFolder
        {
            get { string value = Read(LastFolderFile); return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null; }
            set { if (!string.IsNullOrWhiteSpace(value)) Write(LastFolderFile, value); }
        }

        internal static string Read(string path)
        {
            try { return File.ReadAllText(path).Trim(); }
            catch (Exception e) when (Scanner.IsFileError(e)) { return null; }
        }

        internal static bool Write(string path, string value)
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
