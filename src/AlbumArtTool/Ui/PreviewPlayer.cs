using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AlbumArtTool.Management;
using AlbumArtTool.Online;

namespace AlbumArtTool.Ui
{
    // Plays a 30-second preview of a YouTube Music track: yt-dlp finds the audio stream, FFmpeg decodes
    // 30 seconds of it to a temporary WAV, and Windows plays that. Nothing is added to the library.
    internal static partial class PreviewPlayer
    {
        internal const int ClipSeconds = 30;
        internal static event Action Changed;
        internal static YtmTrack Playing { get; private set; }
        internal static YtmTrack Loading { get; private set; }
        internal static string Problem { get; private set; }
        /// <summary>Makes the clip file (replaced by the tests).</summary>
        internal static Func<YtmTrack, CancellationToken, Task<string>> MakeClip = DefaultClipAsync;
        /// <summary>Starts and stops playback (replaced by the tests).</summary>
        internal static Action<string> Output = PlayFile;
        private static CancellationTokenSource source;
        private static DispatcherTimer timer;
        private static string clip;

        internal static bool IsActive(YtmTrack track) => track != null && (ReferenceEquals(Playing, track) || ReferenceEquals(Loading, track));

        /// <summary>Plays the track, or stops it when it is already playing.</summary>
        internal static async Task ToggleAsync(YtmTrack track)
        {
            if (IsActive(track)) { Stop(); return; }
            Stop();
            var cancel = source = new CancellationTokenSource();
            Loading = track; Problem = null; Changed?.Invoke();
            try
            {
                string file = await MakeClip(track, cancel.Token);
                if (cancel.IsCancellationRequested) { Delete(file); return; }
                clip = file; Loading = null; Playing = track;
                Output(file);
                timer = new DispatcherTimer(TimeSpan.FromSeconds(ClipSeconds + 0.5), DispatcherPriority.Background, (s, e) => { if (ReferenceEquals(Playing, track)) Stop(); });
                timer.Start();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (ReferenceEquals(source, cancel)) { Loading = null; Problem = "Preview unavailable: " + e.Message; } }
            finally { if (ReferenceEquals(source, cancel)) Changed?.Invoke(); }
        }

        internal static void Stop()
        {
            source?.Cancel(); source = null;
            timer?.Stop(); timer = null;
            bool was = Playing != null || Loading != null;
            if (Playing != null) Output(null);
            Playing = null; Loading = null;
            Delete(clip); clip = null;
            if (was) Changed?.Invoke();
        }

        private static void Delete(string file) { try { if (file != null && File.Exists(file)) File.Delete(file); } catch { } }

        private static async Task<string> DefaultClipAsync(YtmTrack track, CancellationToken token)
        {
            string ytdlp = YtDlp.Find(), ffmpeg = Encoder.Find();
            if (ytdlp == null || ffmpeg == null) throw new IOException("set up FFmpeg and yt-dlp in Settings first.");
            if (!OperatingSystem.IsWindows()) throw new IOException("previews play on Windows.");
            return await Task.Run(() =>
            {
                string stream = YtDlp.StreamUrl(ytdlp, track.Url, token);
                double length = track.Duration.TotalSeconds;
                // Start a little way in, where most songs have got going.
                double start = length > ClipSeconds * 2 ? Math.Min(45, length / 3) : 0;
                string file = Path.Combine(Path.GetTempPath(), "AlbumArtTool-preview-" + Guid.NewGuid().ToString("N") + ".wav");
                Encoder.Run(ffmpeg, new[] { "-v", "error", "-nostdin", "-ss", start.ToString("0.#", CultureInfo.InvariantCulture), "-t", ClipSeconds.ToString(CultureInfo.InvariantCulture),
                    "-i", stream, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", "-y", file }, token, timeoutSeconds: 90);
                return file;
            }, token);
        }

        private static void PlayFile(string file)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (file == null) PlaySound(null, IntPtr.Zero, 0);
                else PlaySound(file, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
        }

        private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndFilename = 0x00020000;

        [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PlaySound(string sound, IntPtr module, uint flags);
    }
}
