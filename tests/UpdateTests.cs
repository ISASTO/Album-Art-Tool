using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Updates;
using Microsoft.CSharp;

internal static partial class Tests
{
    private static string ReleaseJson(string tag, byte[] bytes, string digest = null, bool prerelease = false, bool draft = false, string url = null)
    {
        string hash;
        using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new JavaScriptSerializer().Serialize(new { tag_name = tag, draft, prerelease, assets = new[] { new {
            name = "AlbumArtTool.exe", size = bytes.Length, digest = digest ?? "sha256:" + hash,
            browser_download_url = url ?? UpdateService.RepositoryUrl + "/releases/download/" + tag + "/AlbumArtTool.exe" } } });
    }

    private static async Task UpdateTests()
    {
        byte[] appBytes = File.ReadAllBytes(typeof(MainForm).Assembly.Location);
        string version = UpdateService.CurrentVersion.ToString(3);
        string json = ReleaseJson("v" + version, appBytes);
        var old = new Version(1, 2, 1);
        var release = UpdateService.ParseRelease(json, old);
        Assert(release != null && release.Version == UpdateService.CurrentVersion, "new stable release is available");
        Assert(UpdateService.ParseRelease(json, UpdateService.CurrentVersion) == null, "equal version does not reinstall");
        Assert(UpdateService.ParseRelease(json, new Version(9, 0)) == null, "older release never downgrades the app");
        Assert(UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, prerelease: true), old) == null &&
            UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, draft: true), old) == null, "draft and prerelease builds are not installed");
        Assert(UpdateService.ParseRelease(ReleaseJson("v1.10.0", appBytes), new Version(1, 9)).Version == new Version(1, 10, 0, 0), "versions compare numerically, including two-digit components");
        Assert(Throws(() => UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, digest: ""), old)), "updates without a checksum are rejected");
        Assert(Throws(() => UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, url: "https://github.com/another/repo/releases/download/v2.0.0/AlbumArtTool.exe"), old)), "another repository's download is rejected");
        foreach (string address in new[] { "http://github.com/ISASTO/Album-Art-Tool/releases/download/v2/a.exe", "https://github.com.evil.test/ISASTO/Album-Art-Tool/releases/download/a", "https://github.com@evil.test/a", "file:///C:/a.exe", "https://localhost/a", "https://release-assets.githubusercontent.com:8443/a", "https://example.com/a", "https://api.github.com/user" })
            Assert(!UpdateService.AllowedUrl(address), "unsafe update URL rejected: " + address);
        Assert(UpdateService.AllowedUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?token=abc"), "GitHub's release asset CDN is allowed");

        string download = Path.Combine(NewFolder("update-download"), "Download.exe");
        int lastProgress = -1;
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
        {
            await service.DownloadAsync(release, download, new DirectProgress<int>(p => lastProgress = p), CancellationToken.None);
            Assert(lastProgress == 100 && UpdateService.Hash(download) == release.Sha256, "verified complete download reports 100 percent");
            File.Delete(download);
        }
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes.Take(appBytes.Length - 10).ToArray()) })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && !File.Exists(download), "truncated download is removed");
        var corrupt = (byte[])appBytes.Clone(); corrupt[100] ^= 1;
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && !File.Exists(download), "checksum mismatch is removed before execution");
        var wrongVersion = UpdateService.ParseRelease(ReleaseJson("v9.0.0", appBytes), old);
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
            Assert(await Rejects(() => service.DownloadAsync(wrongVersion, download, null, CancellationToken.None)), "EXE assembly version must match the release tag");
        int requests = 0;
        using (var service = new UpdateService(new FakeHandler(_ => { requests++; var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://evil.test/update.exe"); return r; })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && requests == 1, "unsafe redirects are rejected before a second request");
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)429))))
            Assert(await Rejects(() => service.CheckAsync(CancellationToken.None)), "rate limit becomes a recoverable update error");
        using (var cancel = new CancellationTokenSource())
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
        {
            cancel.Cancel();
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, cancel.Token)) && !File.Exists(download), "cancelled update leaves no partial EXE");
        }
        Assert(UpdateInstaller.QuoteArgument(@"G:\") == "\"G:\\\\\"", "drive root trailing slash survives Windows argument quoting");
        Assert(UpdateInstaller.QuoteArgument("a\"b") == "\"a\\\"b\"", "embedded quotes are escaped without using a shell");
        UpdateRollbackTests(appBytes, release);
        await UpdateRestartTest(appBytes, release);
    }

    private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    private static async Task<bool> Rejects(Func<Task> action) { try { await action(); return false; } catch { return true; } }

    private static void BuildPreviousApp(string target)
    {
        using (var compiler = new CSharpCodeProvider())
        {
            var options = new CompilerParameters(new[] { "System.dll" }, target, false) { GenerateExecutable = true, CompilerOptions = "/target:winexe" };
            var result = compiler.CompileAssemblyFromSource(options,
                "[assembly:System.Reflection.AssemblyVersion(\"1.2.1.0\")] class Entry { static void Main() { System.Threading.Thread.Sleep(120000); } }");
            if (result.Errors.HasErrors) throw new Exception(string.Join("; ", result.Errors.Cast<CompilerError>().Select(e => e.ToString())));
        }
    }

    private static void UpdateRollbackTests(byte[] appBytes, AppRelease release)
    {
        string installed = Path.Combine(NewFolder("update-rollback-app"), "AlbumArtTool.exe"); BuildPreviousApp(installed);
        string directory = NewFolder("update-rollback-package"); File.WriteAllBytes(Path.Combine(directory, "Download.exe"), appBytes);
        var job = new UpdateJob { Target = installed, MusicFolder = workspace, OriginalHash = UpdateService.Hash(installed),
            DownloadHash = release.Sha256, Version = release.Version.ToString(), Id = Guid.NewGuid().ToString("N") };
        Assert(Throws(() => UpdateInstaller.Install(directory, job, () => false)) && UpdateService.Hash(installed) == job.OriginalHash,
            "startup failure restores the exact previous EXE");
        Assert(!File.Exists(job.Candidate) && !File.Exists(job.Backup), "rollback removes temporary swap files");
        using (var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert(Throws(() => UpdateInstaller.Install(directory, job, () => true)) && UpdateService.Hash(installed) == job.OriginalHash,
                "a locked EXE fails safely without replacing it");
        var modified = (byte[])appBytes.Clone(); modified[100] ^= 1;
        File.WriteAllBytes(Path.Combine(directory, "Download.exe"), modified);
        Assert(Throws(() => UpdateInstaller.Install(directory, job, () => true)) && UpdateService.Hash(installed) == job.OriginalHash,
            "installer rechecks package integrity immediately before replacing the EXE");
    }

    private static async Task UpdateRestartTest(byte[] appBytes, AppRelease release)
    {
        string installed = Path.Combine(NewFolder("update app & 100% élite"), "AlbumArtTool.exe"); BuildPreviousApp(installed);
        string music = NewFolder("music & 100% élite") + Path.DirectorySeparatorChar;
        string directory = NewFolder("update-restart-package"); File.WriteAllBytes(Path.Combine(directory, "Download.exe"), appBytes);
        Process helper = null, reopened = null;
        using (var parent = UpdateInstaller.StartApp(installed))
        {
            try
            {
                var job = UpdateInstaller.Prepare(directory, installed, music, release, parent);
                helper = Process.GetProcessById(await UpdateInstaller.StartHelperAsync(directory, job, CancellationToken.None));
                Assert(UpdateService.Hash(installed) == job.OriginalHash && !helper.HasExited, "helper waits until the old process has exited");
                parent.Kill(); parent.WaitForExit();
                bool finished = helper.WaitForExit(60000);
                string error = Path.Combine(directory, "error.txt");
                Assert(finished && helper.ExitCode == 0, "actual helper replaces and restarts Windows app: " + (File.Exists(error) ? File.ReadAllText(error) : "no error file"));
                Assert(UpdateService.Hash(installed) == release.Sha256 && !File.Exists(job.Backup), "verified new EXE replaces old one, then removes rollback backup after startup");
                reopened = Process.GetProcessesByName("AlbumArtTool").Single(p => { try { return string.Equals(p.MainModule.FileName, installed, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                reopened.Refresh();
                Assert(reopened.MainWindowHandle != IntPtr.Zero && reopened.Responding, "updated app opens a responsive window");
                bool foundFolder = false;
                EnumChildWindows(reopened.MainWindowHandle, (window, _) =>
                {
                    var text = new StringBuilder(4096); SendMessage(window, 0x000D, text.Capacity, text);
                    if (text.ToString() == music) foundFolder = true; return true;
                }, IntPtr.Zero);
                Assert(foundFolder, "restart preserves music folder including spaces, symbols, Unicode and trailing slash");
                reopened.CloseMainWindow();
                Assert(reopened.WaitForExit(10000), "updated app closes normally");
            }
            finally
            {
                if (!parent.HasExited) parent.Kill();
                if (helper != null) { if (!helper.HasExited) helper.Kill(); helper.Dispose(); }
                if (reopened != null) { if (!reopened.HasExited) reopened.Kill(); reopened.Dispose(); }
            }
        }
    }

    private delegate bool ChildWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, ChildWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, int length, StringBuilder text);

    private static async Task UpdateGuiTest(MainForm form, string output)
    {
        var button = Descendants(form).OfType<Button>().Single(b => b.Text == "Get updates");
        Assert(button.Visible && button.Height >= button.Font.Height + 8 && button.Bottom <= button.Parent.ClientSize.Height,
            "Get updates fits fully in the header");
        form.CreateUpdateService = () => new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ReleaseJson("v1.0.0", new byte[32])) }));
        await form.GetUpdatesAsync();
        var dialog = form.OwnedForms.Single(f => f.Text == "Get updates");
        Assert(Descendants(dialog).OfType<Label>().Any(l => l.Text.StartsWith("You are up to date.")), "update button displays current-version feedback");
        Directory.CreateDirectory(output);
        using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
        { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "app-updates.png"), ImageFormat.Png); }
        dialog.Close();
        Assert(form.CanInstallUpdate, "idle app can hand off an update");
    }
}
