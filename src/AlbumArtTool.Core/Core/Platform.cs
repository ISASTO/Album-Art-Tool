using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AlbumArtTool.Core
{
    // Where the app runs and how it was installed: the Windows installer, an AppImage or a Flatpak on Linux, the Mac app,
    // or a plain folder. The words it uses for the system's own tools follow the platform.
    internal static class Platform
    {
        internal static bool Windows => OperatingSystem.IsWindows();
        internal static bool Linux => OperatingSystem.IsLinux();
        internal static bool Mac => OperatingSystem.IsMacOS();
        internal static bool X64 => RuntimeInformation.OSArchitecture == Architecture.X64;
        internal static bool Arm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

        /// <summary>The AppImage file this copy runs from (its runtime sets APPIMAGE), or null.</summary>
        internal static string AppImage => Linux && Environment.GetEnvironmentVariable("APPIMAGE") is string path && path.Length > 0 && File.Exists(path) ? path : null;

        /// <summary>True inside a Flatpak sandbox.</summary>
        internal static bool Flatpak => Linux && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) || File.Exists("/.flatpak-info"));

        /// <summary>"Windows", "macOS" or "your system", for "Match …" and similar labels.</summary>
        internal static string SystemName => Windows ? "Windows" : Mac ? "macOS" : "your system";
        /// <summary>"File Explorer", "Finder" or "your file manager".</summary>
        internal static string FileManager => Windows ? "File Explorer" : Mac ? "Finder" : "your file manager";
        /// <summary>"PC", or "Mac" on a Mac: "kept on this PC".</summary>
        internal static string Computer => Mac ? "Mac" : "PC";
        /// <summary>"Recycle Bin", "Trash" (Mac) or "trash".</summary>
        internal static string Trash => Windows ? "Recycle Bin" : Mac ? "Trash" : "trash";

        /// <summary>
        /// Folders searched for helper programs (ffmpeg, yt-dlp, deno): PATH, and on a Mac also Homebrew's and MacPorts'
        /// folders, which apps opened from the Finder or the Dock do not get on their PATH.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<string> ProgramFolders()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(folder)) yield return folder.Trim('"');
            if (Mac) { yield return "/opt/homebrew/bin"; yield return "/usr/local/bin"; yield return "/opt/local/bin"; }
        }

        /// <summary>Marks a downloaded program as executable (Linux); nothing to do on Windows.</summary>
        internal static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
