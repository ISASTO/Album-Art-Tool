using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;
using SkiaSharp;

internal static partial class Tests
{
    private static int assertions;
    private static string stage = "initial checks";
    private static readonly Scanner Scanner = new Scanner();
    private static readonly CoverEditor Editor = new CoverEditor();
    private static readonly byte[] Cover = MakeCover(new SKColor(0, 128, 128));
    private static string fixtures, workspace, output;

    private static int Main(string[] args)
    {
        fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        workspace = Path.Combine(Path.GetTempPath(), "AlbumArtTool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        // Keep the real per-user app data out of the tests.
        Environment.SetEnvironmentVariable("ALBUMART_DATA", Path.Combine(workspace, "appdata"));
        var options = args.Where(a => a.StartsWith("--")).ToArray();
        output = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(workspace, "results");
        Directory.CreateDirectory(output);
        Timer watchdog = null;
        try
        {
            if (options.Contains("--live-search")) return LiveSearch().GetAwaiter().GetResult();
            if (options.Contains("--live-update")) return LiveUpdateTest().GetAwaiter().GetResult();
            if (options.Contains("--live-encoder")) return LiveEncoderSetup().GetAwaiter().GetResult();
            if (options.Contains("--live-youtube-music")) return LiveYouTubeMusic().GetAwaiter().GetResult();
            if (options.Contains("--live-ytdlp")) return LiveYtDlpSetup().GetAwaiter().GetResult();
            if (options.Contains("--benchmark")) { Benchmark(); return 0; }
            watchdog = new Timer(_ => { Console.Error.WriteLine("FAIL: watchdog at " + stage); Environment.Exit(1); }, null, TimeSpan.FromMinutes(12), Timeout.InfiniteTimeSpan);
            Run("artwork", ArtworkTests);
            Run("online", () => OnlineTests().GetAwaiter().GetResult());
            Run("updates", () => UpdateTests().GetAwaiter().GetResult());
            foreach (var path in Fixtures()) Run("round trip " + Path.GetExtension(path), () => RoundTrip(path));
            Run("scan cache", PerformanceTests);
            Run("management", ManagementTests);
            Run("album cleanup", AlbumCleanupTests);
            Run("artists", ArtistRulesTests);
            Run("duplicates", DuplicateTests);
            Run("tag checks", LibraryCheckTests);
            Run("YouTube Music replacement", UpgradeTests);
            Run("presets and preferences", PreferenceTests);
            Run("add music parsing", AddMusicParsingTests);
            Run("add music saving, merging and queue", AddMusicSaveTests);
            foreach (var path in Fixtures()) Run("artist round trip " + Path.GetExtension(path), () => ArtistRoundTrip(path));
            Run("scanning", () => { PartialAlbums(); MixedAlbums(); Guards(); FolderArt(); CancelScan(); CountedScanProgress(); InvalidArt(); UntaggedMp3(); });
            if (!options.Contains("--no-gui")) Run("GUI", GuiAll);
            Console.WriteLine("PASS: " + assertions + " assertions, real codec round trips, audio integrity and headless GUI checks.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL during " + stage + ": " + e); return 1; }
        finally { watchdog?.Dispose(); try { Directory.Delete(workspace, true); } catch { } }
    }

    private static void Run(string name, Action test)
    {
        stage = name; var clock = Stopwatch.StartNew();
        test();
        Console.WriteLine("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)");
    }

    internal static IEnumerable<string> Fixtures() => Directory.GetFiles(fixtures).Where(p => Scanner.Extensions.Contains(Path.GetExtension(p))).OrderBy(p => p);

    internal static void Assert(bool condition, string message)
    { Interlocked.Increment(ref assertions); if (!condition) throw new Exception("FAIL: " + message); }

    internal static string NewFolder(string name)
    { string path = Path.Combine(workspace, name); Directory.CreateDirectory(path); return path; }

    internal static string CopyTrack(string destination, string filename, string album = "Test album", string artist = "Test artist")
    {
        string path = Path.Combine(destination, filename);
        File.Copy(Path.Combine(fixtures, "tone" + Path.GetExtension(filename)), path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Album = album; file.Tag.AlbumArtists = new[] { artist }; file.Tag.Performers = new[] { artist };
            file.Tag.Title = "Keep this title"; file.Tag.Year = 2024; file.Tag.Track = 2;
            file.Tag.Comment = "Keep this comment"; file.Tag.Genres = new[] { "Test genre" };
            file.Save();
        }
        return path;
    }

    internal static Album ScanAlbum(string folder) => Scanner.Scan(folder, CancellationToken.None).Albums.Single();

    internal static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    internal static async Task<bool> Rejects(Func<Task> action) { try { await action(); return false; } catch { return true; } }

    internal static string Metadata(string path)
    {
        using (var file = TagLib.File.Create(path))
            return string.Join("|", file.Tag.Title, file.Tag.Album, string.Join(";", file.Tag.Performers), string.Join(";", file.Tag.AlbumArtists),
                file.Tag.Year, file.Tag.Track, file.Tag.Comment, string.Join(";", file.Tag.Genres));
    }

    internal static string Ffmpeg
    {
        get
        {
            string ffmpeg = Environment.GetEnvironmentVariable("ALBUMART_FFMPEG");
            if (string.IsNullOrEmpty(ffmpeg)) throw new Exception("Set ALBUMART_FFMPEG to the FFmpeg executable to verify audio integrity.");
            return ffmpeg;
        }
    }

    internal static string AudioHash(string path)
    {
        var info = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-map", "0:a:0", "-f", "hash", "-hash", "sha256", "-" }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info))
        {
            var error = process.StandardError.ReadToEndAsync();
            string result = process.StandardOutput.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Audio decode failed: " + error.Result);
            return result.Trim();
        }
    }

    internal static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 92)
    {
        using (var image = SKImage.FromBitmap(bitmap)) using (var data = image.Encode(format, quality)) return data.ToArray();
    }

    internal static byte[] MakeCover(SKColor color, int size = 600)
    {
        using (var bitmap = new SKBitmap(size, size))
        using (var canvas = new SKCanvas(bitmap))
        using (var fill = new SKPaint { Color = color, IsAntialias = true })
        using (var dark = new SKPaint { Color = new SKColor(40, 48, 52), IsAntialias = true })
        {
            canvas.Clear(new SKColor(233, 225, 210));
            canvas.DrawRect(0, 0, size, size * .65f, fill);
            canvas.DrawCircle(size / 2f, size * .38f, size * .23f, dark);
            canvas.DrawCircle(size / 2f, size * .38f, size * .04f, fill);
            canvas.DrawRect(size * .06f, size * .78f, size * .7f, size * .06f, dark);
            canvas.Flush();
            return Encode(bitmap, SKEncodedImageFormat.Jpeg);
        }
    }

    internal static byte[] RectangleArt(int width = 1800, int height = 1000)
    {
        using (var bitmap = new SKBitmap(width, height))
        using (var canvas = new SKCanvas(bitmap))
        using (var red = new SKPaint { Color = SKColors.Red })
        using (var blue = new SKPaint { Color = SKColors.Blue })
        {
            canvas.Clear(new SKColor(0, 128, 0));
            int band = Math.Max(0, (width - height) / 2);
            canvas.DrawRect(0, 0, band, height, red);
            canvas.DrawRect(width - band, 0, band, height, blue);
            canvas.Flush();
            return Encode(bitmap, SKEncodedImageFormat.Png, 100);
        }
    }

    internal static (int Width, int Height) Size(byte[] image) => Artwork.ReadInfo(image);

    internal sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        public DirectProgress(Action<T> action) { this.action = action; }
        public void Report(T value) { action(value); }
    }
}
