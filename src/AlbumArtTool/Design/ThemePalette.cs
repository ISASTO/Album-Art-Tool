using System;
using System.Drawing;
using System.IO;
using System.Text;
using AlbumArtTool.Core;

namespace AlbumArtTool.Design
{
    internal sealed class ThemePalette
    {
        internal readonly bool Dark;
        internal readonly Color Canvas, Surface, Input, Ink, Muted, Accent, Selection, Border, Positive, Warning;
        internal ThemePalette(bool dark)
        {
            Dark = dark;
            Canvas = dark ? Color.FromArgb(19, 23, 31) : Color.FromArgb(241, 244, 250);
            Surface = dark ? Color.FromArgb(32, 38, 51) : Color.FromArgb(253, 254, 255);
            Input = dark ? Color.FromArgb(25, 31, 41) : Color.FromArgb(246, 248, 252);
            Ink = dark ? Color.FromArgb(237, 242, 250) : Color.FromArgb(30, 36, 51);
            Muted = dark ? Color.FromArgb(172, 184, 205) : Color.FromArgb(101, 112, 133);
            Accent = dark ? Color.FromArgb(150, 185, 255) : Color.FromArgb(50, 103, 235);
            Selection = dark ? Color.FromArgb(41, 60, 93) : Color.FromArgb(231, 238, 255);
            Border = dark ? Color.FromArgb(59, 72, 94) : Color.FromArgb(220, 226, 237);
            Positive = dark ? Color.FromArgb(138, 217, 184) : Color.FromArgb(40, 126, 104);
            Warning = dark ? Color.FromArgb(239, 200, 143) : Color.FromArgb(155, 102, 36);
        }
        internal Color[] Colors => new[] { Canvas, Surface, Input, Ink, Muted, Accent, Selection, Border, Positive, Warning };
    }
    internal static class ThemeSettings
    {
        private static string PathName => Path.Combine(BackupSettings.AppData, "appearance.txt");
        internal static bool ReadDark(string path = null)
        {
            try { return !File.ReadAllText(path ?? PathName).Trim().Equals("light", StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return true; } catch (UnauthorizedAccessException) { return true; } catch (System.Security.SecurityException) { return true; }
        }
        internal static bool SaveDark(bool dark, string path = null)
        {
            path = path ?? PathName; string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, dark ? "dark" : "light", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                return true;
            }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (System.Security.SecurityException) { return false; }
            finally { try { File.Delete(temporary); } catch { } }
        }
    }
}
