using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

namespace AlbumArtTool.Management
{
    /// <summary>The tags a newly downloaded song is saved with.</summary>
    internal sealed class NewTrack
    {
        internal string Title = "", Album = "", AlbumArtist = "", Year = "";
        internal string[] Artists = Array.Empty<string>();
        internal int Number, Count;
        internal double Seconds;
    }

    // Adding music: songs from YouTube Music are saved as MP3 320 into a "New music" folder (Artist \ Album),
    // where Organize can tidy them, and are then moved into the library.
    internal static partial class AddMusic
    {
        internal static string SettingFile => Path.Combine(BackupSettings.AppData, "new-music-folder.txt");
        internal static string DefaultFolder => Path.Combine(BackupSettings.AppData, "New music");

        /// <summary>Where downloads go before they join the library. Always on this PC, outside the music folder.</summary>
        internal static string Folder
        {
            get { string value = AppSettings.Read(SettingFile); return string.IsNullOrWhiteSpace(value) ? DefaultFolder : value; }
            set => AppSettings.Write(SettingFile, Path.GetFullPath(value));
        }

        [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
        [GeneratedRegex(@"=w\d+-h\d+")] private static partial Regex Size();

        /// <summary>A file or folder name Windows accepts: no reserved characters or names, no trailing dots or spaces.</summary>
        internal static string SafeName(string text, string fallback)
        {
            var builder = new StringBuilder();
            foreach (char c in text ?? "") builder.Append(c < ' ' || "<>:\"/\\|?*".IndexOf(c) >= 0 ? (c == ':' ? '-' : c == '/' || c == '\\' ? '-' : ' ') : c);
            string name = Spaces().Replace(builder.ToString(), " ").Trim().TrimEnd('.', ' ');
            if (name.Length > 100) name = name.Substring(0, 100).TrimEnd('.', ' ');
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) name = "_" + name;
            return name.Length == 0 || name.All(c => c == '.') ? fallback : name;
        }

        /// <summary>New music \ Album artist \ Album \ "01 - Title.mp3" (a name already taken gets " (2)").</summary>
        internal static string TargetPath(string folder, NewTrack track)
        {
            string directory = Path.Combine(folder, SafeName(track.AlbumArtist, "Unknown artist"), SafeName(track.Album, "Unknown album"));
            string name = (track.Number > 0 ? track.Number.ToString("00", CultureInfo.InvariantCulture) + " - " : "") + SafeName(track.Title, "Track");
            string path = Path.Combine(directory, name + ".mp3");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(directory, name + " (" + n + ").mp3");
            return path;
        }

        /// <summary>The largest version of a YouTube Music cover ("=w544-h544" becomes "=w1200-h1200").</summary>
        internal static string LargeCover(string url) => string.IsNullOrEmpty(url) ? "" : Size().IsMatch(url) ? Size().Replace(url, "=w1200-h1200", 1) : url;

        /// <summary>"A, B & C" as three artists.</summary>
        internal static string[] SplitArtists(string text) =>
            (text ?? "").Split(new[] { ", ", " & " }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        internal static NewTrack Describe(YtmAlbum album, YtmTrack track)
        {
            var artists = SplitArtists(track.Artists);
            string albumArtist = !string.IsNullOrWhiteSpace(album?.Artist) ? album.Artist.Split(new[] { " • " }, StringSplitOptions.None)[0].Trim() : artists.FirstOrDefault() ?? "";
            return new NewTrack
            {
                Title = track.Title, Album = album?.Title ?? track.AlbumTitle ?? "", AlbumArtist = albumArtist, Year = album?.Year ?? "",
                Artists = artists.Length > 0 ? artists : SplitArtists(albumArtist), Number = album == null ? 0 : track.Index, Count = album?.TrackCount ?? 0, Seconds = track.Duration.TotalSeconds
            };
        }

        /// <summary>
        /// Encodes a downloaded stream to MP3 320 with the new song's tags and one front cover (JPEG, at most 600 px),
        /// saved as ID3v2.3, verified (320 kbps, the listed length, a clean decode) and only then moved into place.
        /// </summary>
        internal static void Save(string ffmpeg, string input, string target, NewTrack track, byte[] cover, CancellationToken token, Action<double> progress = null)
        {
            if (ffmpeg == null) throw new IOException("Set up FFmpeg in Settings first: it makes the MP3 320 files.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = Path.Combine(Path.GetDirectoryName(target), ".aat-" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                Encoder.Run(ffmpeg, new[] { "-v", "error", "-nostdin", "-i", input, "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-c:a", "libmp3lame", "-b:a", "320k",
                    "-id3v2_version", "3", "-write_id3v1", "0", "-ac", "2", "-progress", "pipe:1", "-nostats", "-y", temp }, token, line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.Substring(12), out long microseconds) && track.Seconds > 0)
                        progress?.Invoke(Math.Min(1, microseconds / 1000000.0 / track.Seconds));
                });
                using (var file = TagLib.File.Create(temp))
                {
                    file.Tag.Title = track.Title;
                    file.Tag.Performers = track.Artists;
                    file.Tag.AlbumArtists = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artists : new[] { track.AlbumArtist };
                    file.Tag.Album = track.Album;
                    if (track.Number > 0) file.Tag.Track = (uint)track.Number;
                    if (track.Count > 0) file.Tag.TrackCount = (uint)track.Count;
                    if (uint.TryParse(track.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint year)) file.Tag.Year = year;
                    byte[] embedded = null;
                    try { if (cover != null) embedded = Artwork.EmbedCover(cover); } catch (Exception e) when (Artwork.IsImageError(e)) { }
                    file.Tag.Pictures = embedded == null ? Array.Empty<TagLib.IPicture>() :
                        new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(embedded)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" } };
                    Id3Standard.Apply(file);
                    file.Save();
                }
                using (var check = TagLib.File.Create(temp))
                {
                    double seconds = check.Properties.Duration.TotalSeconds;
                    if (check.Properties.AudioBitrate < 318 || check.Properties.AudioBitrate > 322) throw new IOException("The new MP3 is not 320 kbps.");
                    if (track.Seconds > 0 && Math.Abs(seconds - track.Seconds) > Math.Max(3, track.Seconds * 0.01))
                        throw new IOException("The download is " + Math.Round(seconds) + " s long, not the " + Math.Round(track.Seconds) + " s YouTube Music lists.");
                    if (check.Tag.Title != track.Title) throw new IOException("The tags could not be saved.");
                }
                Encoder.Run(ffmpeg, new[] { "-v", "error", "-xerror", "-nostdin", "-i", temp, "-map", "0:a:0", "-f", "null", "-" }, token);
                File.Move(temp, target);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        /// <summary>Audio files waiting in the New music folder.</summary>
        internal static List<string> Waiting(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Scanner.Extensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith(".aat-", StringComparison.Ordinal)).ToList();
        }
    }

    /// <summary>One song in the download queue.</summary>
    internal sealed class DownloadItem
    {
        internal YtmAlbum Album;
        internal YtmTrack Track;
        internal string State = "Waiting", Error, Path;
        internal double Progress;
        internal bool FromVideo;
        internal bool Finished => State == "Done" || State == "Failed" || State == "Skipped" || State == "Cancelled";
    }

    /// <summary>A release (or a single song) added to the queue; its songs download one after another.</summary>
    internal sealed class DownloadGroup
    {
        internal string Key = "", Title = "", Subtitle = "", ThumbnailUrl = "";
        internal byte[] Thumbnail;
        internal readonly List<DownloadItem> Items = new List<DownloadItem>();
        internal bool Preparing = true;
        internal string Problem;
        internal event Action Changed;
        internal void Raise() => Changed?.Invoke();
        internal int Done => Items.Count(i => i.State == "Done");
        internal int Failed => Items.Count(i => i.State == "Failed");
        internal int Skipped => Items.Count(i => i.State == "Skipped");
        internal bool Finished => !Preparing && Items.All(i => i.Finished);
        internal bool Cancelled => Items.Count > 0 && Items.All(i => i.State == "Cancelled");
        internal double Progress => Items.Count == 0 ? 0 : Items.Sum(i => i.Finished ? 1 : i.Progress) / Items.Count;

        internal string Status
        {
            get
            {
                if (Problem != null) return Problem;
                if (Preparing) return "Finding the songs…";
                int songs = Items.Count(i => i.State != "Skipped");
                var active = Items.FirstOrDefault(i => i.State == "Downloading" || i.State == "Saving");
                if (active != null) return (active.State == "Saving" ? "Saving " : "Downloading ") + active.Track.Title + " · " + Done + " of " + songs;
                if (!Finished) return Done > 0 ? Done + " of " + songs + " ready · waiting" : "Waiting";
                if (Cancelled) return "Cancelled";
                var parts = new List<string> { Done + (Done == 1 ? " song ready" : " songs ready") };
                if (Failed > 0) parts.Add(Failed + " failed");
                if (Skipped > 0) parts.Add(Skipped + " music video only, skipped");
                return string.Join(" · ", parts);
            }
        }
    }

    // Downloads queued songs one at a time while browsing continues. Every change is raised on the thread that
    // created the queue (the UI thread), so pages can bind to it directly.
    internal sealed class DownloadQueue
    {
        private readonly Func<string> folder, ffmpeg;
        private readonly Func<string, string, CancellationToken, Action<double>, string> download;
        private readonly Func<string, CancellationToken, Task<byte[]>> coverLoader;
        private readonly Dictionary<string, byte[]> covers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly SynchronizationContext context;
        private CancellationTokenSource current;
        private Task worker = Task.CompletedTask;
        internal readonly List<DownloadGroup> Groups = new List<DownloadGroup>();
        internal event Action Changed;
        internal bool IsRunning { get; private set; }
        internal Task Completion => worker;
        internal int Waiting => Groups.Sum(g => g.Items.Count(i => !i.Finished)) + Groups.Count(g => g.Preparing);

        internal DownloadQueue(Func<string> folder, Func<string> ffmpeg, Func<string, string, CancellationToken, Action<double>, string> download, Func<string, CancellationToken, Task<byte[]>> cover)
        {
            this.folder = folder; this.ffmpeg = ffmpeg; this.download = download; coverLoader = cover;
            context = SynchronizationContext.Current;
        }

        internal bool Contains(string key) => Groups.Any(g => g.Key == key && !g.Cancelled && g.Problem == null);

        /// <summary>Adds a group whose songs are being looked up; call Ready once they are known.</summary>
        internal DownloadGroup Add(string key, string title, string subtitle, string thumbnailUrl, byte[] thumbnail)
        {
            var group = new DownloadGroup { Key = key, Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnailUrl, Thumbnail = thumbnail };
            Groups.Insert(0, group);
            Changed?.Invoke();
            return group;
        }

        internal void Ready(DownloadGroup group, IEnumerable<DownloadItem> items)
        {
            group.Items.AddRange(items);
            group.Preparing = false;
            foreach (var item in group.Items.Where(i => i.Track.VideoOnly && !i.FromVideo)) item.State = "Skipped";
            group.Raise(); Changed?.Invoke();
            Start();
        }

        internal void Fail(DownloadGroup group, string problem) { group.Preparing = false; group.Problem = problem; group.Raise(); Changed?.Invoke(); }

        private void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            worker = RunAsync();
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    // Oldest first: groups are listed newest first.
                    var item = Groups.AsEnumerable().Reverse().SelectMany(g => g.Items.Select(i => (Group: g, Item: i))).FirstOrDefault(p => p.Item.State == "Waiting");
                    if (item.Item == null) break;
                    await RunOneAsync(item.Group, item.Item);
                }
            }
            finally { IsRunning = false; Changed?.Invoke(); }
        }

        private async Task RunOneAsync(DownloadGroup group, DownloadItem item)
        {
            current?.Dispose(); current = new CancellationTokenSource(); var token = current.Token;
            item.State = "Downloading"; item.Progress = 0; group.Raise();
            string stage = Path.Combine(Path.GetTempPath(), "AlbumArtTool-add-" + Guid.NewGuid().ToString("N"));
            try
            {
                var info = AddMusic.Describe(item.Album, item.Track);
                byte[] cover = await CoverAsync(item.Album?.ThumbnailUrl ?? group.ThumbnailUrl, token);
                string root = folder(), exe = ffmpeg(); var dl = download;
                string target = await Task.Run(() =>
                {
                    string audio = dl(item.Track.Url, stage, token, f => Post(() => { item.Progress = f * 0.7; group.Raise(); }));
                    Post(() => { item.State = "Saving"; group.Raise(); });
                    string path = AddMusic.TargetPath(root, info);
                    AddMusic.Save(exe, audio, path, info, cover, token, f => Post(() => { item.Progress = 0.7 + f * 0.3; group.Raise(); }));
                    return path;
                }, token);
                item.Path = target; item.State = "Done"; item.Progress = 1;
            }
            catch (OperationCanceledException) { item.State = "Cancelled"; }
            catch (Exception e) { item.State = "Failed"; item.Error = e.Message; }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
                group.Raise(); Changed?.Invoke();
            }
        }

        private async Task<byte[]> CoverAsync(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url) || coverLoader == null) return null;
            if (covers.TryGetValue(url, out var known)) return known;
            byte[] bytes = null;
            try { bytes = await coverLoader(AddMusic.LargeCover(url), token) ?? await coverLoader(url, token); }
            catch (Exception e) when (!(e is OperationCanceledException)) { try { bytes = await coverLoader(url, token); } catch (Exception) when (!token.IsCancellationRequested) { } }
            covers[url] = bytes;
            return bytes;
        }

        private void Post(Action action) { if (context != null) context.Post(_ => action(), null); else action(); }

        /// <summary>Cancels the group's waiting songs (and the one downloading). Finished songs stay in New music.</summary>
        internal void Cancel(DownloadGroup group)
        {
            foreach (var item in group.Items.Where(i => i.State == "Waiting")) item.State = "Cancelled";
            if (group.Items.Any(i => i.State == "Downloading" || i.State == "Saving")) current?.Cancel();
            group.Raise(); Changed?.Invoke();
        }

        internal void Retry(DownloadGroup group)
        {
            foreach (var item in group.Items.Where(i => i.State == "Failed" || i.State == "Cancelled")) { item.State = "Waiting"; item.Error = null; item.Progress = 0; }
            group.Raise(); Changed?.Invoke(); Start();
        }

        /// <summary>Removes finished groups from the list (their songs stay in New music).</summary>
        internal void ClearFinished() { Groups.RemoveAll(g => g.Finished || g.Problem != null); Changed?.Invoke(); }

        internal void Stop() { foreach (var group in Groups) foreach (var item in group.Items.Where(i => i.State == "Waiting")) item.State = "Cancelled"; current?.Cancel(); }
    }
}
