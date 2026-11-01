using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
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
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbumArtTool/" + UpdateService.DisplayVersion);
        }
        internal async Task<string> InstallAsync(IProgress<int> progress, CancellationToken token)
        {
            if (!Environment.Is64BitOperatingSystem) throw new IOException("Automatic FFmpeg setup needs 64-bit Windows. Choose a compatible local FFmpeg folder instead.");
            string stage = Path.Combine(Path.GetTempPath(), "AlbumArtTool-encoder-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                string json;
                using (var output = new MemoryStream()) { await Fetch(Api, output, 2 * 1024 * 1024, null, token); json = Encoding.UTF8.GetString(output.ToArray()); }
                var metadata = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
                var assets = (object[])metadata["assets"];
                var asset = assets.Cast<Dictionary<string, object>>().Single(a => (string)a["name"] == "ffmpeg-master-latest-win64-lgpl.zip");
                string url = (string)asset["browser_download_url"], digest = (string)asset["digest"];
                long size = Convert.ToInt64(asset["size"]);
                if (!url.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal) || size <= 0 || size > 256L * 1024 * 1024 ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateService.ValidHash(digest.Substring(7))) throw new IOException("The FFmpeg download does not have valid release metadata/checksum.");
                string zip = Path.Combine(stage, "encoder.zip");
                using (var output = File.Create(zip)) { await Fetch(url, output, size, progress, token); if (output.Length != size) throw new IOException("The FFmpeg download was incomplete."); }
                if (!UpdateService.Hash(zip).Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new IOException("FFmpeg's SHA-256 checksum did not match.");
                token.ThrowIfCancellationRequested();
                string extracted = Path.Combine(stage, "tools"); Directory.CreateDirectory(extracted);
                Extract(zip, extracted);
                string exe = Path.Combine(extracted, "ffmpeg.exe"); Encoder.Validate(exe, token);
                Directory.CreateDirectory(Encoder.Cache);
                foreach (string file in Directory.GetFiles(extracted))
                {
                    string destination = Path.Combine(Encoder.Cache, Path.GetFileName(file));
                    string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(file, temp);
                    if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
                }
                File.WriteAllText(Path.Combine(Encoder.Cache, "SOURCE.txt"), Repository + "\r\n" + url + "\r\n" + digest + "\r\nFFmpeg is a separate command-line program. Its license notices are supplied here.\r\n");
                return Path.Combine(Encoder.Cache, "ffmpeg.exe");
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }
        internal static void Extract(string zip, string output)
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                var exe = archive.Entries.Single(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (exe.Length > 350L * 1024 * 1024) throw new IOException("The FFmpeg executable is too large.");
                exe.ExtractToFile(Path.Combine(output, "ffmpeg.exe"));
                int count = 0;
                foreach (var entry in archive.Entries.Where(e => e.Length > 0 && e.Length < 2 * 1024 * 1024 &&
                    (Path.GetFileName(e.FullName).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(e.FullName).StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))))
                    entry.ExtractToFile(Path.Combine(output, (++count) + "-" + Path.GetFileName(entry.FullName)));
                if (count == 0) throw new IOException("The encoder archive did not include license notices.");
            }
        }
        internal static bool Allowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Port != 443 || u.UserInfo.Length != 0) return false;
            return u.AbsoluteUri == Api || u.Host == "github.com" && u.AbsolutePath.StartsWith("/BtbN/FFmpeg-Builds/releases/download/", StringComparison.Ordinal) ||
                u.Host == "release-assets.githubusercontent.com" || u.Host == "objects.githubusercontent.com";
        }
        private async Task Fetch(string url, Stream output, long limit, IProgress<int> progress, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                for (int redirects = 0; redirects < 6; redirects++)
                {
                    if (!Allowed(url)) throw new IOException("The encoder download redirected outside GitHub's release servers.");
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        { if (response.Headers.Location == null) throw new IOException("Invalid encoder redirect."); url = new Uri(new Uri(url), response.Headers.Location).AbsoluteUri; continue; }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > limit) throw new IOException("The encoder download is larger than expected.");
                        using (var registration = timeout.Token.Register(response.Dispose))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            byte[] buffer = new byte[65536]; long total = 0; int last = -1;
                            while (true)
                            {
                                int n = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false); if (n == 0) break;
                                total += n; if (total > limit) throw new IOException("The encoder download is too large.");
                                await output.WriteAsync(buffer, 0, n, timeout.Token).ConfigureAwait(false);
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
