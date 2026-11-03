using System;
using System.IO;
using System.Linq;
using System.Threading;
using AlbumArtTool.Core;
using SkiaSharp;

namespace AlbumArtTool
{
    // Exercises tag reading/writing in the shipped (native, trimmed) build:
    //   AlbumArtTool.exe --self-test <folder with audio files> <report file>
    // Used by CI; harmless for users. Works on a private copy of the folder.
    internal static class SelfTest
    {
        internal static int Run(string source, string report)
        {
            try
            {
                string work = Path.Combine(Path.GetTempPath(), "AlbumArtTool-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(work, "broken.mp3"), "not audio");
                Environment.SetEnvironmentVariable("ALBUMART_DATA", work + "-data");
                ScanCache.Persistent = false;
                var scanner = new Scanner();
                var before = scanner.Scan(work, CancellationToken.None, useCache: false);
                byte[] cover;
                using (var bitmap = new SKBitmap(300, 300))
                {
                    bitmap.Erase(SKColors.Teal);
                    using (var image = SKImage.FromBitmap(bitmap)) using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90)) cover = Artwork.Normalize(data.ToArray());
                }
                var editor = new CoverEditor();
                int written = 0;
                foreach (var album in before.Albums) written += editor.Apply(album, cover, false, false).TracksWritten;
                var after = scanner.Scan(work, CancellationToken.None, useCache: false);
                int missing = after.Albums.Sum(a => a.MissingCount);
                string result = "tracks=" + before.FilesRead + " issues=" + before.Issues.Count + " written=" + written + " missingAfter=" + missing;
                File.WriteAllText(report, result);
                try { Directory.Delete(work, true); Directory.Delete(work + "-data", true); } catch { }
                return before.FilesRead > 0 && before.Issues.Count == 1 && written == before.FilesRead && missing == 0 ? 0 : 2;
            }
            catch (Exception e) { File.WriteAllText(report, "error: " + e); return 1; }
        }
    }
}
