using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;

internal static partial class Tests
{
    private static int assertions;
    private static readonly Scanner Scanner = new Scanner();
    private static readonly CoverEditor Editor = new CoverEditor();
    private static readonly byte[] Cover = MakeCover(Color.Teal);
    private static string fixtures, workspace;

    [STAThread]
    private static int Main(string[] args)
    {
        fixtures = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
        workspace = Path.Combine(Path.GetTempPath(), "AlbumArtTool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            if (args.Length > 0 && args[0] == "--live-search") return LiveSearch(args.Length > 1 ? args[1] : workspace).GetAwaiter().GetResult();
            OnlineTests().GetAwaiter().GetResult();
            foreach (var path in Directory.GetFiles(fixtures).Where(p => Scanner.Extensions.Contains(Path.GetExtension(p)))) RoundTrip(path);
            PartialAlbums(); MixedAlbums(); Guards(); FolderArt(); CancelScan(); InvalidArt(); UntaggedMp3();
            GuiSmoke(args.Length > 0 ? args[0] : workspace);
            Console.WriteLine("PASS: " + assertions + " assertions, real codec round trips, audio integrity and GUI smoke checks.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { try { Directory.Delete(workspace, true); } catch { } }
    }

    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("FAIL: " + message); }

    private static string NewFolder(string name)
    { string path = Path.Combine(workspace, name); Directory.CreateDirectory(path); return path; }

    private static string CopyTrack(string destination, string filename, string album = "Test album", string artist = "Test artist")
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

    private static Album ScanAlbum(string folder) => Scanner.Scan(folder, CancellationToken.None).Albums.Single();

    private static void RoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture);
        string directory = NewFolder("roundtrip-" + extension.Substring(1));
        string path = CopyTrack(directory, "track" + extension);
        string original = CoverEditor.Hash(path), metadata = Metadata(path), audio = AudioHash(path);
        var album = ScanAlbum(directory);
        Assert(album.MissingCount == 1, extension + " missing cover detected");
        var result = Editor.Apply(album, Cover, true, false);
        Assert(result.TracksWritten == 1 && result.Issues.Count == 0, extension + " embed: " + string.Join(";", result.Issues));
        Assert(ScanAlbum(directory).MissingCount == 0, extension + " cover persisted after reopening");
        Assert(Metadata(path) == metadata, extension + " existing metadata preserved");
        Assert(AudioHash(path) == audio, extension + " decoded audio is unchanged");
        Assert(Scanner.Scan(directory, CancellationToken.None).FilesRead == 1, "backups excluded from scan");
        var undone = Editor.Undo(result);
        Assert(undone.Issues.Count == 0 && CoverEditor.Hash(path) == original, extension + " byte-for-byte undo");
        Console.WriteLine("PASS " + extension + " embed / metadata / decoded audio / exact undo");
    }

    private static void PartialAlbums()
    {
        string root = NewFolder("partial");
        string first = CopyTrack(root, "one.mp3");
        CopyTrack(root, "two.mp3");
        using (var file = TagLib.File.Create(first))
        {
            file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(Cover)) { Type = TagLib.PictureType.FrontCover },
                new TagLib.Picture(new TagLib.ByteVector(Cover)) { Type = TagLib.PictureType.BackCover, Description = "Back cover" } };
            file.Save();
        }
        var album = ScanAlbum(root);
        Assert(album.Tracks.Count == 2 && album.MissingCount == 1 && album.HasAnyCover, "partial album belongs in both views");
        string hash = CoverEditor.Hash(first);
        var result = Editor.Apply(album, MakeCover(Color.Orange), true, false);
        Assert(result.TracksWritten == 1 && CoverEditor.Hash(first) == hash, "fill-only keeps existing art untouched");
        result = Editor.Apply(ScanAlbum(root), MakeCover(Color.BlueViolet), false, false);
        Assert(result.TracksWritten == 2, "replacement updates every track");
        using (var file = TagLib.File.Create(first))
            Assert(file.Tag.Pictures.Count(p => p.Type == TagLib.PictureType.BackCover) == 1, "back-cover artwork preserved");
    }

    private static void MixedAlbums()
    {
        string root = NewFolder("mixed");
        CopyTrack(root, "a.mp3", "First album"); CopyTrack(root, "b.mp3", "Second album");
        var child = Path.Combine(root, "Nested", "Disc 1"); Directory.CreateDirectory(child);
        CopyTrack(child, "c.flac", "First album");
        File.WriteAllText(Path.Combine(root, "broken.mp3"), "This is not an MP3.");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "ignore me");
        var result = Scanner.Scan(root, CancellationToken.None);
        Assert(result.Albums.Count == 3 && result.FilesRead == 3 && result.Issues.Count == 1,
            "recursive scan, same-folder albums, duplicate editions and corrupt-file reporting: " +
            result.Albums.Count + " albums, " + result.FilesRead + " tracks, " + result.Issues.Count + " issues");
        string compilation = NewFolder("compilation");
        string a = CopyTrack(compilation, "one.mp3"); string b = CopyTrack(compilation, "two.mp3");
        using (var file = TagLib.File.Create(a)) { file.Tag.AlbumArtists = new string[0]; file.Tag.Performers = new[] { "Artist A" }; file.Save(); }
        using (var file = TagLib.File.Create(b)) { file.Tag.AlbumArtists = new string[0]; file.Tag.Performers = new[] { "Artist B" }; file.Save(); }
        Assert(ScanAlbum(compilation).Artist == "Various artists", "compilations are not split by track artist");
    }

    private static void Guards()
    {
        string root = NewFolder("guards"); string path = CopyTrack(root, "track.mp3"); var album = ScanAlbum(root);
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = "Changed outside the app"; file.Save(); }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        string current = CoverEditor.Hash(path);
        var blocked = Editor.Apply(album, Cover, false, false);
        Assert(blocked.TracksWritten == 0 && blocked.Issues.Count == 1 && CoverEditor.Hash(path) == current, "stale scan cannot overwrite external edits");
        album = ScanAlbum(root); File.SetAttributes(path, FileAttributes.ReadOnly);
        blocked = Editor.Apply(album, Cover, false, false);
        File.SetAttributes(path, FileAttributes.Normal);
        Assert(blocked.TracksWritten == 0 && blocked.Issues.Count == 1, "read-only file is skipped");
        var edit = Editor.Apply(ScanAlbum(root), Cover, false, false);
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = "Later edit"; file.Save(); }
        current = CoverEditor.Hash(path);
        var undo = Editor.Undo(edit);
        Assert(undo.Restored == 0 && undo.Issues.Count == 1 && CoverEditor.Hash(path) == current && File.Exists(edit.Entries[0].Backup), "undo protects later edits and retains recovery backup");
        Assert(!Directory.GetFiles(root).Any(p => Path.GetFileName(p).StartsWith(".aat-")), "temporary copies cleaned up");
    }

    private static void FolderArt()
    {
        string root = NewFolder("folder-cover"); CopyTrack(root, "track.mp3");
        string cover = Path.Combine(root, "folder.png"); File.WriteAllBytes(cover, Artwork.Normalize(Cover, 256, ImageFormat.Png));
        var album = ScanAlbum(root);
        Assert(album.HasAnyCover && album.MissingCount == 1 && album.FolderCovers.Count == 1 && album.Thumbnail != null, "folder art is previewed but missing embedded art is still flagged");
        string original = CoverEditor.Hash(cover);
        var result = Editor.Apply(album, MakeCover(Color.Salmon), true, true);
        Assert(result.Entries.Count == 2 && CoverEditor.Hash(cover) != original, "folder cover updated alongside track");
        using (var image = Image.FromFile(cover)) Assert(image.RawFormat.Guid == ImageFormat.Png.Guid, "sidecar retains its correct image encoding");
        Editor.Undo(result);
        Assert(CoverEditor.Hash(cover) == original, "undo restores exact folder image");
        File.Delete(cover);
        result = Editor.Apply(ScanAlbum(root), Cover, true, true);
        Assert(File.Exists(Path.Combine(root, "cover.jpg")), "missing sidecar created");
        Editor.Undo(result);
        Assert(!File.Exists(Path.Combine(root, "cover.jpg")), "undo removes newly created sidecar");
    }

    private static void CancelScan()
    {
        var token = new CancellationTokenSource(); token.Cancel();
        Assert(Scanner.Scan(workspace, token.Token).Cancelled, "scan cancellation"); token.Dispose();
    }

    private static void InvalidArt()
    {
        string root = NewFolder("invalid-cover"); string path = CopyTrack(root, "track.mp3"); string hash = CoverEditor.Hash(path);
        bool rejected = false;
        try { Editor.Apply(ScanAlbum(root), new byte[] { 1, 2, 3 }, false, false); } catch { rejected = true; }
        Assert(rejected && hash == CoverEditor.Hash(path), "invalid images rejected before writes");
        using (var image = new Bitmap(3200, 1800))
        using (var buffer = new MemoryStream())
        {
            image.Save(buffer, ImageFormat.Png);
            using (var normalized = Artwork.Decode(Artwork.Normalize(buffer.ToArray())))
                Assert(normalized.Width == 1600 && normalized.Height == 900, "large image scaled without cropping");
        }
    }

    private static void UntaggedMp3()
    {
        string root = NewFolder("untagged"); string path = CopyTrack(root, "track.mp3");
        using (var file = TagLib.File.Create(path)) { file.RemoveTags(TagLib.TagTypes.AllTags); file.Save(); }
        var album = ScanAlbum(root);
        Assert(album.Title == "untagged", "untagged files use folder name");
        var result = Editor.Apply(album, Cover, false, false);
        Assert(result.TracksWritten == 1 && ScanAlbum(root).MissingCount == 0, "cover added to previously untagged MP3");
    }

    private static string Metadata(string path)
    {
        using (var file = TagLib.File.Create(path))
            return string.Join("|", file.Tag.Title, file.Tag.Album, string.Join(";", file.Tag.Performers), string.Join(";", file.Tag.AlbumArtists),
                file.Tag.Year, file.Tag.Track, file.Tag.Comment, string.Join(";", file.Tag.Genres));
    }

    private static string AudioHash(string path)
    {
        string ffmpeg = Environment.GetEnvironmentVariable("ALBUMART_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg)) throw new Exception("Set ALBUMART_FFMPEG to ffmpeg.exe to verify audio integrity.");
        var info = new ProcessStartInfo(ffmpeg, "-v error -i \"" + path + "\" -map 0:a:0 -f hash -hash sha256 -")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(info))
        {
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Audio decode failed: " + error);
            return output.Trim();
        }
    }

    private static byte[] MakeCover(Color color)
    {
        using (var image = new Bitmap(600, 600))
        using (var graphics = Graphics.FromImage(image))
        using (var brush = new SolidBrush(color))
        using (var circle = new SolidBrush(Color.FromArgb(40, 48, 52)))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.FromArgb(233, 225, 210)); graphics.FillRectangle(brush, 0, 0, 600, 390);
            graphics.FillEllipse(circle, 160, 90, 280, 280); graphics.FillEllipse(brush, 275, 205, 50, 50);
            using (var font = new Font("Segoe UI", 24, FontStyle.Bold)) graphics.DrawString("MORNING MILES", font, circle, 34, 450);
            image.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
        }
    }

    private static void GuiSmoke(string output)
    {
        string root = NewFolder("gui");
        string first = Path.Combine(root, "Morning Miles"), second = Path.Combine(root, "Quiet Hours"), third = Path.Combine(root, "The Long Way Home");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(third);
        CopyTrack(first, "one.mp3", "Morning Miles", "Atlas Sessions"); CopyTrack(first, "two.mp3", "Morning Miles", "Atlas Sessions");
        CopyTrack(second, "one.flac", "Quiet Hours", "Paper Lanterns");
        CopyTrack(third, "one.m4a", "The Long Way Home", "Sunday Club");
        Editor.Apply(ScanAlbum(third), MakeCover(Color.DarkSalmon), false, true);
        string imagePath = Path.Combine(workspace, "cover.jpg"); File.WriteAllBytes(imagePath, Cover);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Exception failure = null;
        using (var form = new MainForm(root, false, new FakeCoverSearch(Cover)))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    await form.ScanAsync();
                    var lists = Descendants(form).OfType<AlbumList>().Single();
                    Assert(lists.AlbumCount == 2, "GUI missing tab lists only incomplete albums");
                    var existing = Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Change existing album art"));
                    Assert(existing.Bottom <= existing.Parent.ClientSize.Height, "tab button fits inside its visible row");
                    existing.PerformClick();
                    Assert(lists.AlbumCount == 1 && lists.SelectedAlbum.Thumbnail != null, "GUI existing tab shows cover thumbnails");
                    Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Add missing album art")).PerformClick();
                    var data = new DataObject(DataFormats.FileDrop, new[] { imagePath });
                    Assert(MainForm.SingleImagePath(data) == imagePath, "Windows file drop accepts a single image");
                    Assert(MainForm.SingleImagePath(new DataObject(DataFormats.FileDrop, new[] { imagePath, imagePath })) == null, "ambiguous multiple-image drop rejected");
                    var dropTarget = Descendants(form).OfType<PictureBox>().Single(p => p.AccessibleName == "Album cover preview and image drop target");
                    typeof(Control).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(dropTarget, new object[] { new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                    Assert(Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Apply cover")).Enabled, "drop preview enables Apply");
                    Directory.CreateDirectory(output);
                    await Task.Delay(150);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "album-art-tool.png"), ImageFormat.Png); }
                    await form.ApplyAsync();
                    Assert(lists.AlbumCount == 1, "completed album disappears from missing tab");
                    existing.PerformClick();
                    Assert(lists.AlbumCount == 2, "completed album appears in existing tab");
                    await OnlineGuiTest(form, lists, output);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
