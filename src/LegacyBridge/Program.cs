using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AlbumArtTool.Bridge
{
    // Replaces a v2.x portable AlbumArtTool.exe through that version's own "Get updates".
    // 1. Confirms startup to the old updater (so it keeps this file and removes its rollback copy).
    // 2. Installs the new version from the embedded, release-verified setup program.
    // 3. Later launches simply forward to the installed app.
    internal static class Program
    {
        private const string AppId = "{6C4C3B4E-0F2B-4B5E-9E61-5C2B8D7A3A11}_is1";

        [STAThread]
        private static int Main(string[] args)
        {
            string folder = args.Length > 0 && Directory.Exists(args[0]) ? args[0] : null;
            SignalStarted(args);
            try
            {
                string installed = InstalledApp();
                if (installed != null && InstalledVersion(installed) >= typeof(Program).Assembly.GetName().Version)
                {
                    Launch(installed, folder);
                    return 0;
                }
                return Install(folder);
            }
            catch (Exception e)
            {
                MessageBox.Show("Music Library Manager could not be installed.\n\n" + e.Message +
                    "\n\nDownload the installer from https://github.com/ISASTO/Album-Art-Tool/releases/latest",
                    "Music Library Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
        }

        // v2.x updaters start the new EXE as: <music folder> --update-started <event name>.
        private static void SignalStarted(string[] args)
        {
            if (args.Length != 3 || args[1] != "--update-started" || !args[2].StartsWith("Local\\AlbumArtTool-update-started-", StringComparison.Ordinal)) return;
            try { using (var started = EventWaitHandle.OpenExisting(args[2])) started.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static int Install(string folder)
        {
            string directory = Path.Combine(Path.GetTempPath(), "AlbumArtTool-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string setup = Path.Combine(directory, "AlbumArtTool-Setup.exe");
            try
            {
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlbumArtTool.Setup.exe"))
                using (var output = File.Create(setup))
                {
                    if (input == null) throw new InvalidDataException("The installer is missing from this download.");
                    input.CopyTo(output);
                }
                var arguments = new StringBuilder("/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART");
                if (folder != null && folder.IndexOf('"') < 0) arguments.Append(" \"/MUSICFOLDER=").Append(folder).Append('"');
                else arguments.Append(" /LAUNCH=1");
                using (var process = Process.Start(new ProcessStartInfo(setup, arguments.ToString()) { UseShellExecute = false, WorkingDirectory = directory }))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new IOException("Setup ended with code " + process.ExitCode + ".");
                }
                return 0;
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        private static string InstalledApp()
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view))
                        using (var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId))
                        {
                            string location = key?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrEmpty(location)) continue;
                            string exe = Path.Combine(location, "AlbumArtTool.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException) { }
            return null;
        }

        private static Version InstalledVersion(string exe)
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return new Version(Math.Max(0, info.FileMajorPart), Math.Max(0, info.FileMinorPart), Math.Max(0, info.FileBuildPart), Math.Max(0, info.FilePrivatePart));
        }

        private static void Launch(string exe, string folder)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            if (folder != null) info.Arguments = Quote(folder);
            using (Process.Start(info)) { }
        }

        private static string Quote(string value)
        {
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
