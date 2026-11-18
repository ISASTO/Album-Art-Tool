using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;
using AlbumArtTool.Updates;

namespace AlbumArtTool.Management
{
    internal sealed class EncoderSetup : IDisposable
    {
        internal const string Api = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
        internal const string Repository = "https://github.com/BtbN/FFmpeg-Builds";
        private readonly HttpClient client;

        internal EncoderSetup(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("Muzikette/" + UpdateService.DisplayVersion);
        }

        internal async Task<string> InstallAsync(IProgress<int> progress, CancellationToken token)
        {
            if (!Supported)
                throw new IOException("Automatic FFmpeg setup needs 64-bit Windows or Linux on x86-64. Install FFmpeg yourself and choose its ffmpeg program instead.");
            string stage = Path.Combine(Path.GetTempPath(), "Muzikette-encoder-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                string json;
                using (var output = new MemoryStream()) { await Fetch(Api, output, 2 * 1024 * 1024, null, token).ConfigureAwait(false); json = Encoding.UTF8.GetString(output.ToArray()); }
                string url, digest; long size;
                string source = Repository;
                using (var document = Json.Parse(json))
                {
                    // GitHub may have moved the builds to a new address: the release names its repository, and downloads come from there.
                    source = RepositoryOf(Json.String(document.RootElement, "html_url")) ?? Repository;
                    JsonElement? asset = Json.Array(document.RootElement, "assets")
                        .Where(a => Json.String(a, "name").StartsWith("ffmpeg-", StringComparison.Ordinal) && Json.String(a, "name").EndsWith(BuildSuffix, StringComparison.Ordinal))
                        .OrderBy(a => Json.String(a, "name") == "ffmpeg-master-latest" + BuildSuffix ? 0 : 1).Select(a => (JsonElement?)a).FirstOrDefault();
                    if (asset == null) throw new IOException("The latest encoder release does not contain the static LGPL build for this system yet. Try again shortly or choose a local FFmpeg.");
                    url = Json.String(asset, "browser_download_url"); digest = Json.String(asset, "digest");
                    long.TryParse(Json.String(asset, "size"), out size);
                }
                if (!url.StartsWith(source + "/releases/download/", StringComparison.OrdinalIgnoreCase) || size <= 0 || size > 256L * 1024 * 1024 ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateService.ValidHash(digest.Substring(7)))
                    throw new IOException("The FFmpeg download does not have valid release metadata/checksum (" + Address(url) + ", " + (digest.Length == 0 ? "no checksum" : "checksum " + digest.Split(':')[0]) + ").");
                downloads = new Uri(source).AbsolutePath + "/releases/download/";
                string zip = Path.Combine(stage, OperatingSystem.IsWindows() ? "encoder.zip" : "encoder.tar.xz");
                using (var output = File.Create(zip)) { await Fetch(url, output, size, progress, token).ConfigureAwait(false); if (output.Length != size) throw new IOException("The FFmpeg download was incomplete."); }
                if (!UpdateService.Hash(zip).Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new IOException("FFmpeg's SHA-256 checksum did not match.");
                token.ThrowIfCancellationRequested();
                string extracted = Path.Combine(stage, "tools"); Directory.CreateDirectory(extracted);
                if (OperatingSystem.IsWindows()) Extract(zip, extracted); else ExtractTarXz(zip, extracted, token);
                string exe = Path.Combine(extracted, Encoder.ExecutableName); Encoder.Validate(exe, token);
                Directory.CreateDirectory(Encoder.Cache);
                foreach (string file in Directory.GetFiles(extracted))
                {
                    string destination = Path.Combine(Encoder.Cache, Path.GetFileName(file));
                    string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(file, temp);
                    if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
                    if (!destination.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(destination).Contains("LICENSE") && !Path.GetFileName(destination).Contains("COPYING")) Platform.MakeExecutable(destination);
                }
                File.WriteAllText(Path.Combine(Encoder.Cache, "SOURCE.txt"), source + "\r\n" + url + "\r\n" + digest + "\r\nFFmpeg is a separate command-line program. Its license notices are supplied here.\r\n");
                return Path.Combine(Encoder.Cache, Encoder.ExecutableName);
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }

        /// <summary>64-bit Windows, or Linux on x86-64: the systems BtbN builds static LGPL FFmpeg for.</summary>
        internal static bool Supported => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem || OperatingSystem.IsLinux() && Platform.X64;
        private static string BuildSuffix => OperatingSystem.IsWindows() ? "-win64-lgpl.zip" : "-linux64-lgpl.tar.xz";

        // Linux builds come as .tar.xz, which .NET cannot read itself: the system's tar unpacks it into a scratch folder,
        // then ffmpeg, ffprobe and the license notices are picked out exactly as from the Windows zip.
        internal static void ExtractTarXz(string archive, string output, CancellationToken token)
        {
            string scratch = Path.Combine(Path.GetDirectoryName(archive), "unpacked"); Directory.CreateDirectory(scratch);
            var info = new System.Diagnostics.ProcessStartInfo("tar") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-xJf", archive, "-C", scratch, "--no-same-owner" }) info.ArgumentList.Add(argument);
            System.Diagnostics.Process started;
            try { started = System.Diagnostics.Process.Start(info); }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new IOException("Unpacking FFmpeg needs the tar program, which this system does not have. Install FFmpeg with your package manager instead; Muzikette uses the one on your PATH.");
            }
            using (var tar = started)
            {
                string error = tar.StandardError.ReadToEnd();
                if (!tar.WaitForExit(300000)) { try { tar.Kill(); } catch { } throw new IOException("Unpacking FFmpeg took too long."); }
                if (tar.ExitCode != 0) throw new IOException("Could not unpack FFmpeg: " + error.Trim());
            }
            token.ThrowIfCancellationRequested();
            var files = Directory.GetFiles(scratch, "*", SearchOption.AllDirectories);
            string Find(string name) => files.SingleOrDefault(f => Path.GetFileName(f) == name && Path.GetFileName(Path.GetDirectoryName(f)) == "bin");
            string ffmpeg = Find("ffmpeg") ?? throw new IOException("The FFmpeg archive did not contain bin/ffmpeg.");
            if (new FileInfo(ffmpeg).Length > 350L * 1024 * 1024) throw new IOException("The FFmpeg executable is too large.");
            File.Copy(ffmpeg, Path.Combine(output, "ffmpeg"));
            string probe = Find("ffprobe");
            if (probe != null && new FileInfo(probe).Length <= 350L * 1024 * 1024) File.Copy(probe, Path.Combine(output, "ffprobe"));
            int count = 0;
            foreach (string notice in files.Where(f => { var n = Path.GetFileName(f); long length = new FileInfo(f).Length; return length > 0 && length < 2 * 1024 * 1024 &&
                (n.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)); }))
                File.Copy(notice, Path.Combine(output, (++count) + "-" + Path.GetFileName(notice)));
            if (count == 0) throw new IOException("The encoder archive did not include license notices.");
            foreach (string program in new[] { "ffmpeg", "ffprobe" }) if (File.Exists(Path.Combine(output, program))) Platform.MakeExecutable(Path.Combine(output, program));
        }

        internal static void Extract(string zip, string output)
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                var exe = archive.Entries.Single(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (exe.Length > 350L * 1024 * 1024) throw new IOException("The FFmpeg executable is too large.");
                exe.ExtractToFile(Path.Combine(output, "ffmpeg.exe"));
                // ffprobe measures real audio bitrates for the library check; FFmpeg alone can do it too, so it is optional.
                var probe = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase));
                if (probe != null && probe.Length <= 350L * 1024 * 1024) probe.ExtractToFile(Path.Combine(output, "ffprobe.exe"));
                int count = 0;
                foreach (var entry in archive.Entries.Where(e => e.Length > 0 && e.Length < 2 * 1024 * 1024 &&
                    (Path.GetFileName(e.FullName).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(e.FullName).StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))))
                    entry.ExtractToFile(Path.Combine(output, (++count) + "-" + Path.GetFileName(entry.FullName)));
                if (count == 0) throw new IOException("The encoder archive did not include license notices.");
            }
        }

        // Where a refused redirect pointed, without its query (which can hold a download token).
        private static string Address(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Scheme + "://" + u.Host + u.AbsolutePath : "an invalid address";

        // Release downloads are accepted from the builds' repository (its current address, once the release names it).
        private string downloads = "/BtbN/FFmpeg-Builds/releases/download/";

        internal static bool Allowed(string url, string downloads = "/BtbN/FFmpeg-Builds/releases/download/")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Port != 443 || u.UserInfo.Length != 0) return false;
            // GitHub answers a renamed or moved repository's address with a redirect to its numeric id.
            return u.AbsoluteUri == Api || u.Host == "api.github.com" && u.Query.Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(u.AbsolutePath, "^/repositories/[0-9]{1,12}/releases/latest$") ||
                u.Host == "github.com" && u.AbsolutePath.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) ||
                u.Host == "release-assets.githubusercontent.com" || u.Host == "objects.githubusercontent.com";
        }

        /// <summary>"https://github.com/owner/name" from a release page address on github.com, or null.</summary>
        internal static string RepositoryOf(string releasePage)
        {
            if (!Uri.TryCreate(releasePage, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") return null;
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[2] != "releases" || parts.Take(2).Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Za-z0-9_.-]{1,100}$"))) return null;
            return "https://github.com/" + parts[0] + "/" + parts[1];
        }

        private async Task Fetch(string url, Stream output, long limit, IProgress<int> progress, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                for (int redirects = 0; redirects < 6; redirects++)
                {
                    if (!Allowed(url, downloads)) throw new IOException("The encoder download redirected outside GitHub's release servers (" + Address(url) + ").");
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        { if (response.Headers.Location == null) throw new IOException("Invalid encoder redirect."); url = new Uri(new Uri(url), response.Headers.Location).AbsoluteUri; continue; }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > limit) throw new IOException("The encoder download is larger than expected.");
                        using (timeout.Token.Register(response.Dispose))
                        using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                        {
                            byte[] buffer = new byte[65536]; long total = 0; int last = -1;
                            while (true)
                            {
                                int n = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false); if (n == 0) break;
                                total += n; if (total > limit) throw new IOException("The encoder download is too large.");
                                await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                                int percent = (int)(100 * total / limit); if (percent != last) { progress?.Report(percent); last = percent; }
                            }
                        }
                        return;
                    }
                }
                throw new IOException("Too many encoder redirects.");
            }
        }
        public void Dispose() => client.Dispose();
    }
}
