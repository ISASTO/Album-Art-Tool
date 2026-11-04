using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    // yt-dlp downloads the audio; Deno is the JavaScript runtime yt-dlp needs for YouTube (it finds deno.exe beside yt-dlp.exe).
    internal static class YtDlp
    {
        internal static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        internal static string DenoName => OperatingSystem.IsWindows() ? "deno.exe" : "deno";
        internal static string Cache => Path.Combine(BackupSettings.AppData, "Downloader");
        internal static string CookiesSetting => Path.Combine(BackupSettings.AppData, "youtube-cookies-path.txt");

        internal static string Find() => Locate(Environment.GetEnvironmentVariable("ALBUMART_YTDLP"), ExecutableName);
        internal static string FindDeno(string ytdlp) => Locate(null, DenoName, ytdlp == null ? null : Path.GetDirectoryName(ytdlp));

        private static string Locate(string explicitPath, string name, string first = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
            var folders = new[] { first, Cache, AppContext.BaseDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (string folder in folders)
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    string file = Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(file)) return Path.GetFullPath(file);
                }
                catch { }
            return null;
        }

        /// <summary>An optional cookies.txt (Netscape format) for when YouTube asks to confirm you are not a bot.</summary>
        internal static string Cookies
        {
            get { try { string path = File.ReadAllText(CookiesSetting).Trim(); return File.Exists(path) ? path : null; } catch { return null; } }
            set
            {
                Directory.CreateDirectory(BackupSettings.AppData);
                if (string.IsNullOrEmpty(value)) { try { File.Delete(CookiesSetting); } catch { } } else File.WriteAllText(CookiesSetting, value);
            }
        }

        /// <summary>The yt-dlp arguments for one track: YouTube Music only, its best audio stream (Opus first), no playlist expansion.</summary>
        internal static List<string> Arguments(string url, string folder, string ffmpeg, string deno, string cookies)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "music.youtube.com" || uri.AbsolutePath != "/watch")
                throw new ArgumentException("Only YouTube Music track links can be downloaded.");
            var args = new List<string> { "--ignore-config", "--no-playlist", "--no-part", "--no-mtime", "--no-color", "--newline", "--progress",
                "-f", "bestaudio[acodec=opus]/bestaudio[ext=m4a]/bestaudio",
                "--progress-template", "download:aat-progress %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
                "--no-simulate", "--print", "after_move:aat-file %(filepath)s",
                "-o", Path.Combine(folder, "%(id)s.%(ext)s") };
            if (ffmpeg != null) { args.Add("--ffmpeg-location"); args.Add(ffmpeg); }
            if (deno != null) { args.Add("--js-runtimes"); args.Add("deno:" + deno); }
            if (cookies != null) { args.Add("--cookies"); args.Add(cookies); }
            args.Add(url);
            return args;
        }

        /// <summary>Downloads one YouTube Music track's best audio stream into the folder and returns the file.</summary>
        internal static string Download(string exe, string ffmpeg, string url, string folder, CancellationToken token, Action<double> progress = null)
        {
            if (exe == null) throw new IOException("yt-dlp is not set up. Click Set up in the YouTube Music window.");
            Directory.CreateDirectory(folder);
            string file = null; var errors = new StringBuilder();
            void Line(string line)
            {
                if (line.StartsWith("aat-file ", StringComparison.Ordinal)) { file = line.Substring(9).Trim(); return; }
                if (line.StartsWith("aat-progress ", StringComparison.Ordinal))
                {
                    var parts = line.Substring(13).Split(' ');
                    double done = Number(parts.ElementAtOrDefault(0)), total = Number(parts.ElementAtOrDefault(1));
                    if (total <= 0) total = Number(parts.ElementAtOrDefault(2));
                    if (total > 0) progress?.Invoke(Math.Min(1, done / total));
                    return;
                }
                if (line.StartsWith("ERROR:", StringComparison.Ordinal) && errors.Length < 4000) errors.AppendLine(line.Substring(6).Trim());
            }
            int exit = Run(exe, Arguments(url, folder, ffmpeg, FindDeno(exe), Cookies), token, Line, 900);
            if (exit != 0 || file == null || !File.Exists(file)) throw new IOException(Explain(errors.ToString()));
            return file;
        }

        internal static string Explain(string error)
        {
            error = error.Trim();
            if (error.IndexOf("confirm you", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0)
                return "YouTube asked to confirm you're not a bot. Try again later, or choose a cookies file from a signed-in browser (Settings in the YouTube Music window).";
            if (error.IndexOf("Video unavailable", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("not available", StringComparison.OrdinalIgnoreCase) >= 0)
                return "This track isn't available from YouTube Music in your region.";
            if (error.IndexOf("JavaScript", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("n challenge", StringComparison.OrdinalIgnoreCase) >= 0)
                return "yt-dlp could not run YouTube's player code. Click Update tools in the YouTube Music window.";
            return error.Length > 0 ? "yt-dlp: " + error.Split('\n')[0].Trim() + " (if this keeps happening, click Update tools)" : "yt-dlp did not produce an audio file.";
        }

        private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

        internal static string Version(string exe, CancellationToken token)
        {
            string version = "";
            Run(exe, new[] { "--version" }, token, line => { if (version.Length == 0 && line.Trim().Length > 0) version = line.Trim(); }, 30);
            return version;
        }

        // Both output streams are read: yt-dlp writes progress and errors to stderr in quiet mode.
        internal static int Run(string exe, IEnumerable<string> args, CancellationToken token, Action<string> line, int timeoutSeconds)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) line?.Invoke(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) line?.Invoke(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                var clock = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    { token.ThrowIfCancellationRequested(); if (clock.Elapsed.TotalSeconds > timeoutSeconds) throw new IOException("yt-dlp timed out."); }
                    process.WaitForExit(); token.ThrowIfCancellationRequested();
                    return process.ExitCode;
                }
                catch { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { } throw; }
            }
        }
    }

    // Installs or updates yt-dlp.exe and deno.exe from their official GitHub releases, verifying GitHub's SHA-256 digests.
    internal sealed class YtDlpSetup : IDisposable
    {
        internal const string YtDlpApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        internal const string DenoApi = "https://api.github.com/repos/denoland/deno/releases/latest";
        internal static string VersionFile => Path.Combine(YtDlp.Cache, "VERSION.txt");
        private readonly HttpClient client;

        internal YtDlpSetup(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbumArtTool/" + UpdateService.DisplayVersion);
        }

        internal static bool Installed => File.Exists(Path.Combine(YtDlp.Cache, YtDlp.ExecutableName)) && File.Exists(Path.Combine(YtDlp.Cache, YtDlp.DenoName));
        internal static string InstalledTag { get { try { return File.ReadAllText(VersionFile).Trim(); } catch { return ""; } } }

        /// <summary>The latest yt-dlp release tag, e.g. "2026.10.26".</summary>
        internal async Task<string> LatestTagAsync(CancellationToken token)
        {
            using (var document = await ReleaseAsync(YtDlpApi, token).ConfigureAwait(false)) return Json.String(document.RootElement, "tag_name");
        }

        /// <summary>Downloads yt-dlp (and Deno when missing or asked for). Progress is 0–100 over both downloads.</summary>
        internal async Task<string> InstallAsync(bool includeDeno, IProgress<int> progress, CancellationToken token)
        {
            if (!Environment.Is64BitOperatingSystem || !OperatingSystem.IsWindows())
                throw new IOException("Automatic setup needs 64-bit Windows. Put yt-dlp and deno on PATH instead.");
            string stage = Path.Combine(Path.GetTempPath(), "AlbumArtTool-downloader-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                string tag;
                string ytdlp = Path.Combine(stage, "yt-dlp.exe");
                using (var release = await ReleaseAsync(YtDlpApi, token).ConfigureAwait(false))
                {
                    tag = Json.String(release.RootElement, "tag_name");
                    await FetchAsset(release.RootElement, "yt-dlp.exe", ytdlp, 64L * 1024 * 1024, new Progress<int>(p => progress?.Report(includeDeno ? p / 3 : p)), token).ConfigureAwait(false);
                }
                bool deno = includeDeno || !File.Exists(Path.Combine(YtDlp.Cache, YtDlp.DenoName));
                if (deno)
                    using (var release = await ReleaseAsync(DenoApi, token).ConfigureAwait(false))
                    {
                        string zip = Path.Combine(stage, "deno.zip");
                        await FetchAsset(release.RootElement, "deno-x86_64-pc-windows-msvc.zip", zip, 256L * 1024 * 1024, new Progress<int>(p => progress?.Report(33 + p * 2 / 3)), token).ConfigureAwait(false);
                        using (var archive = ZipFile.OpenRead(zip))
                        {
                            var exe = archive.Entries.Single(e => e.FullName.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                            if (exe.Length > 512L * 1024 * 1024) throw new IOException("The Deno executable is too large.");
                            exe.ExtractToFile(Path.Combine(stage, "deno.exe"));
                        }
                        File.Delete(zip);
                    }
                if (!YtDlp.Version(ytdlp, token).StartsWith(tag.Substring(0, Math.Min(4, tag.Length)), StringComparison.Ordinal)) throw new IOException("The downloaded yt-dlp did not start correctly.");
                Directory.CreateDirectory(YtDlp.Cache);
                foreach (string file in Directory.GetFiles(stage))
                {
                    string destination = Path.Combine(YtDlp.Cache, Path.GetFileName(file));
                    string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(file, temp);
                    if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
                }
                File.WriteAllText(VersionFile, tag);
                File.WriteAllText(Path.Combine(YtDlp.Cache, "SOURCE.txt"), "yt-dlp " + tag + " from https://github.com/yt-dlp/yt-dlp (Unlicense)\r\nDeno from https://github.com/denoland/deno (MIT)\r\n" +
                    "Both are separate programs, downloaded from their official GitHub releases and verified with GitHub's SHA-256 digests.\r\n");
                progress?.Report(100);
                return Path.Combine(YtDlp.Cache, YtDlp.ExecutableName);
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }

        private async Task<JsonDocument> ReleaseAsync(string api, CancellationToken token)
        {
            using (var output = new MemoryStream())
            {
                await Fetch(api, output, 4 * 1024 * 1024, null, token).ConfigureAwait(false);
                return Json.Parse(Encoding.UTF8.GetString(output.ToArray()), 64);
            }
        }

        private async Task FetchAsset(JsonElement release, string name, string path, long limit, IProgress<int> progress, CancellationToken token)
        {
            var asset = Json.Array(release, "assets").Where(a => Json.String(a, "name") == name).Select(a => (JsonElement?)a).FirstOrDefault();
            if (asset == null) throw new IOException("The latest release does not include " + name + " yet. Try again shortly.");
            string url = Json.String(asset, "browser_download_url"), digest = Json.String(asset, "digest");
            long.TryParse(Json.String(asset, "size"), out long size);
            if (size <= 0 || size > limit || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateService.ValidHash(digest.Substring(7)))
                throw new IOException(name + " does not have valid release metadata or a checksum.");
            using (var output = File.Create(path)) { await Fetch(url, output, size, progress, token).ConfigureAwait(false); if (output.Length != size) throw new IOException(name + " download was incomplete."); }
            if (!UpdateService.Hash(path).Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new IOException(name + "'s SHA-256 checksum did not match.");
        }

        internal static bool Allowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Port != 443 || u.UserInfo.Length != 0) return false;
            return u.AbsoluteUri == YtDlpApi || u.AbsoluteUri == DenoApi ||
                u.Host == "github.com" && (u.AbsolutePath.StartsWith("/yt-dlp/yt-dlp/releases/download/", StringComparison.Ordinal) || u.AbsolutePath.StartsWith("/denoland/deno/releases/download/", StringComparison.Ordinal)) ||
                u.Host == "release-assets.githubusercontent.com" || u.Host == "objects.githubusercontent.com";
        }

        private async Task Fetch(string url, Stream output, long limit, IProgress<int> progress, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                for (int redirects = 0; redirects < 6; redirects++)
                {
                    if (!Allowed(url)) throw new IOException("A download redirected outside GitHub's release servers.");
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        { if (response.Headers.Location == null) throw new IOException("Invalid download redirect."); url = new Uri(new Uri(url), response.Headers.Location).AbsoluteUri; continue; }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > limit) throw new IOException("A download is larger than expected.");
                        using (timeout.Token.Register(response.Dispose))
                        using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                        {
                            byte[] buffer = new byte[65536]; long total = 0; int last = -1;
                            while (true)
                            {
                                int n = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false); if (n == 0) break;
                                total += n; if (total > limit) throw new IOException("A download is too large.");
                                await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                                int percent = (int)(100 * total / limit); if (percent != last) { progress?.Report(percent); last = percent; }
                            }
                        }
                        return;
                    }
                }
                throw new IOException("Too many download redirects.");
            }
        }

        public void Dispose() => client.Dispose();
    }
}
