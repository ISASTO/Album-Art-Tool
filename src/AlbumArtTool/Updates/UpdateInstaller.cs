using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AlbumArtTool.Updates
{
    internal sealed class UpdateJob
    {
        public UpdateJob() { }
        public string Target { get; set; }
        public string MusicFolder { get; set; }
        public string OriginalHash { get; set; }
        public string DownloadHash { get; set; }
        public string Version { get; set; }
        public string Id { get; set; }
        public int ParentId { get; set; }
        public long ParentStarted { get; set; }
        internal string ReadyEvent => "Local\\AlbumArtTool-update-ready-" + Id;
        internal string StartedEvent => "Local\\AlbumArtTool-update-started-" + Id;
        internal string Candidate => Path.Combine(Path.GetDirectoryName(Target), ".aat-update-" + Id + ".tmp");
        internal string Backup => Path.Combine(Path.GetDirectoryName(Target), ".aat-update-" + Id + ".bak");
    }

    internal static class UpdateInstaller
    {
        internal static string WorkspaceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumArtTool", "Updates");
        internal static string CreateWorkspace()
        {
            string path = Path.Combine(WorkspaceRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }

        internal static UpdateJob Prepare(string directory, string target, string musicFolder, AppRelease release, Process parent)
        {
            var job = new UpdateJob { Target = Path.GetFullPath(target), MusicFolder = Path.GetFullPath(musicFolder),
                OriginalHash = UpdateService.Hash(target), DownloadHash = release.Sha256, Version = release.Version.ToString(),
                Id = Guid.NewGuid().ToString("N"), ParentId = parent.Id, ParentStarted = parent.StartTime.ToUniversalTime().Ticks };
            ValidateFiles(directory, job);
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("The app EXE is read-only. Make it writable or move it to a folder you can update.");
            // Check directory write access before closing the working application.
            using (var probe = new FileStream(job.Candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(job.Candidate);
            File.Copy(typeof(UpdateInstaller).Assembly.Location, Path.Combine(directory, "Updater.exe"));
            File.WriteAllText(Path.Combine(directory, "job.json"), new JavaScriptSerializer().Serialize(job), Encoding.UTF8);
            return job;
        }

        internal static async Task<int> StartHelperAsync(string directory, UpdateJob job, CancellationToken token)
        {
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, job.ReadyEvent))
            using (var helper = Process.Start(new ProcessStartInfo(Path.Combine(directory, "Updater.exe"), "--apply-update")
                { UseShellExecute = false, WorkingDirectory = directory }))
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (!ready.WaitOne(0))
                    {
                        token.ThrowIfCancellationRequested();
                        if (helper.HasExited || timer.Elapsed > TimeSpan.FromSeconds(15))
                        {
                            string error = Path.Combine(directory, "error.txt");
                            throw new IOException(File.Exists(error) ? File.ReadAllText(error) : "The updater could not start. Your current app is still running.");
                        }
                        await Task.Delay(50, token);
                    }
                    token.ThrowIfCancellationRequested();
                    return helper.Id;
                }
                catch
                {
                    // Before handoff the helper only waits; cancelling cannot interrupt a replacement.
                    try { if (!helper.HasExited) helper.Kill(); } catch { }
                    throw;
                }
            }
        }

        // Runs from a temporary copy of the *current* app, never from unverified download bytes.
        internal static int RunHelper()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            UpdateJob job = null;
            bool parentExited = false, readySent = false;
            try
            {
                string manifest = Path.Combine(directory, "job.json");
                if (new FileInfo(manifest).Length > 32768) throw new InvalidDataException("Invalid update instructions.");
                job = new JavaScriptSerializer().Deserialize<UpdateJob>(File.ReadAllText(manifest));
                ValidateFiles(directory, job);
                using (var parent = Process.GetProcessById(job.ParentId))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStarted ||
                        !string.Equals(Path.GetFullPath(parent.MainModule.FileName), job.Target, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The running app no longer matches this update.");
                    using (var ready = EventWaitHandle.OpenExisting(job.ReadyEvent)) { ready.Set(); readySent = true; }
                    if (!parent.WaitForExit(120000)) throw new IOException("The app did not close. The update was cancelled without changing it.");
                    parentExited = true;
                }
                Install(directory, job, () => LaunchAndConfirm(job));
                return 0;
            }
            catch (Exception e)
            {
                try { File.WriteAllText(Path.Combine(directory, "error.txt"), e.Message); } catch { }
                if (parentExited && job != null)
                {
                    try { if (UpdateService.Hash(job.Target) == job.OriginalHash) StartApp(job.Target, job.MusicFolder); } catch { }
                }
                if (readySent) MessageBox.Show(e.Message + "\n\nYou can also download the latest EXE from " + UpdateService.RepositoryUrl + "/releases/latest",
                    "Album Art Tool update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
        }

        internal static void Install(string directory, UpdateJob job, Func<bool> startAndConfirm)
        {
            ValidateFiles(directory, job);
            bool replaced = false;
            try
            {
                File.Copy(Path.Combine(directory, "Download.exe"), job.Candidate, false);
                UpdateService.VerifyExecutable(job.Candidate, job.DownloadHash, System.Version.Parse(job.Version));
                // Recheck immediately before the swap, including edits by another running copy.
                if (UpdateService.Hash(job.Target) != job.OriginalHash) throw new IOException("The app changed while the update was downloading. Check for updates again.");
                File.Replace(job.Candidate, job.Target, job.Backup);
                replaced = true;
                if (!startAndConfirm()) throw new IOException("The new version closed before it finished starting.");
                UpdateService.TryDelete(job.Backup);
            }
            catch (StartupUnconfirmedException) { throw; } // Do not replace an EXE which may still be running.
            catch (Exception failure)
            {
                if (replaced)
                {
                    try
                    {
                        if (UpdateService.Hash(job.Target) != job.DownloadHash || UpdateService.Hash(job.Backup) != job.OriginalHash)
                            throw new IOException("An app file changed after replacement.");
                        File.Replace(job.Backup, job.Target, null);
                    }
                    catch (Exception restore)
                    { throw new IOException("The update could not finish. The previous EXE is saved at " + job.Backup + ". " + restore.Message, failure); }
                    throw new IOException("The update could not start. Your previous version has been restored. " + failure.Message, failure);
                }
                throw new IOException("The update could not be installed. Your current EXE has not changed. " + failure.Message, failure);
            }
            finally { UpdateService.TryDelete(job.Candidate); }
        }

        private static bool LaunchAndConfirm(UpdateJob job)
        {
            using (var started = new EventWaitHandle(false, EventResetMode.ManualReset, job.StartedEvent))
            using (var app = StartApp(job.Target, job.MusicFolder, "--update-started", job.StartedEvent))
            {
                var timer = Stopwatch.StartNew();
                while (!started.WaitOne(100))
                {
                    if (app.HasExited) return false;
                    if (timer.Elapsed > TimeSpan.FromSeconds(45))
                        throw new StartupUnconfirmedException("The updated app has not confirmed startup yet. It has been left running; the previous EXE is saved at " + job.Backup + ".");
                }
                return true;
            }
        }

        internal static void SignalStarted(string[] args)
        {
            if (args.Length != 3 || args[1] != "--update-started" || !args[2].StartsWith("Local\\AlbumArtTool-update-started-", StringComparison.Ordinal)) return;
            try { using (var started = EventWaitHandle.OpenExisting(args[2])) started.Set(); } catch (WaitHandleCannotBeOpenedException) { }
        }

        internal static Process StartApp(string exe, params string[] args)
        {
            var arguments = new StringBuilder();
            foreach (string arg in args) { if (arguments.Length > 0) arguments.Append(' '); arguments.Append(QuoteArgument(arg)); }
            return Process.Start(new ProcessStartInfo(exe, arguments.ToString()) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
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

        private static void ValidateFiles(string directory, UpdateJob job)
        {
            if (job == null || !Guid.TryParseExact(job.Id, "N", out _) || !UpdateService.ValidHash(job.OriginalHash) ||
                !UpdateService.ValidHash(job.DownloadHash) || !System.Version.TryParse(job.Version, out var version) ||
                string.IsNullOrEmpty(job.Target) || !Path.IsPathRooted(job.Target) || !Path.IsPathRooted(job.MusicFolder) ||
                !job.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update instructions.");
            string package = Path.Combine(directory, "Download.exe");
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(package)).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(job.Target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update must be staged outside the application folder.");
            if (AssemblyName.GetAssemblyName(job.Target).Name != "AlbumArtTool" || UpdateService.Hash(job.Target) != job.OriginalHash)
                throw new IOException("The installed app changed. Check for updates again.");
            UpdateService.VerifyExecutable(package, job.DownloadHash, version);
        }

        internal static void CleanWorkspace(string directory)
        {
            // Only remove files this updater creates, never an arbitrary directory tree.
            foreach (string name in new[] { "Download.exe", "Updater.exe", "job.json", "error.txt" }) UpdateService.TryDelete(Path.Combine(directory, name));
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

        private sealed class StartupUnconfirmedException : IOException
        { internal StartupUnconfirmedException(string message) : base(message) { } }
    }
}
