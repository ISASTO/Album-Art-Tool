using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AlbumArtTool.Core
{
    // Where the app runs and how it was installed: the Windows installer, an AppImage or a Flatpak on Linux, or a plain
    // folder. The words it uses for the system's own tools follow the platform.
    internal static class Platform
    {
        internal static bool Windows => OperatingSystem.IsWindows();
        internal static bool Linux => OperatingSystem.IsLinux();
        internal static bool X64 => RuntimeInformation.OSArchitecture == Architecture.X64;

        /// <summary>The AppImage file this copy runs from (its runtime sets APPIMAGE), or null.</summary>
        internal static string AppImage => Linux && Environment.GetEnvironmentVariable("APPIMAGE") is string path && path.Length > 0 && File.Exists(path) ? path : null;

        /// <summary>True inside a Flatpak sandbox.</summary>
        internal static bool Flatpak => Linux && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) || File.Exists("/.flatpak-info"));

        /// <summary>"Windows" or "your system", for "Match …" and similar labels.</summary>
        internal static string SystemName => Windows ? "Windows" : "your system";
        /// <summary>"File Explorer" or "your file manager".</summary>
        internal static string FileManager => Windows ? "File Explorer" : "your file manager";

        /// <summary>Marks a downloaded program as executable (Linux); nothing to do on Windows.</summary>
        internal static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
