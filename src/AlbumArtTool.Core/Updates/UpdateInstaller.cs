using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AlbumArtTool.Updates
{
    // Updates are installed by the verified setup program. It waits for this app to exit,
    // replaces the installed files transactionally (rolling back on failure) and reopens the
    // app in the same music folder.
    internal static class UpdateInstaller
    {
        internal const string SetupFile = "Setup.exe";
        internal static string WorkspaceRoot => Path.Combine(Core.BackupSettings.AppData, "Updates");

        internal static string CreateWorkspace()
        {
            string path = Path.Combine(WorkspaceRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }

        internal static string SetupArguments(string musicFolder)
        {
            // The installer parses its own command line: quotes group text and backslashes are literal.
            var arguments = new StringBuilder("/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART");
            if (!string.IsNullOrEmpty(musicFolder) && musicFolder.IndexOf('"') < 0) arguments.Append(" \"/MUSICFOLDER=").Append(musicFolder).Append('"');
            else arguments.Append(" /LAUNCH=1");
            return arguments.ToString();
        }

        internal static Process Launch(string directory, string musicFolder) =>
            Process.Start(new ProcessStartInfo(Path.Combine(directory, SetupFile), SetupArguments(musicFolder)) { UseShellExecute = false, WorkingDirectory = directory });

        internal static void CleanWorkspace(string directory)
        {
            // Only remove files this updater creates, never an arbitrary directory tree.
            foreach (string name in new[] { SetupFile, "Download.exe", "Updater.exe", "job.json", "error.txt" }) UpdateService.TryDelete(Path.Combine(directory, name));
            try { Directory.Delete(directory, false); } catch { }
        }

        internal static void CleanOldWorkspaces()
        {
            try
            {
                if (!Directory.Exists(WorkspaceRoot)) return;
                foreach (string directory in Directory.GetDirectories(WorkspaceRoot))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                        CleanWorkspace(directory);
            }
            catch { } // Cleanup must never prevent normal startup.
        }

        internal static string QuoteArgument(string value)
        {
            // Windows CommandLineToArgvW quoting, including a drive root's trailing backslash.
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
                result.Append(c);
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
    }
}
