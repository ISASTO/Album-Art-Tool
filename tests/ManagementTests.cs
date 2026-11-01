using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using Encoder = AlbumArtTool.Management.Encoder;

internal static partial class Tests
{
    private static LibraryOptions Options(string root, string backups) => new LibraryOptions { Root = root, BackupRoot = backups, EncoderPath = Environment.GetEnvironmentVariable("ALBUMART_FFMPEG") };
    private static byte[] RectangleArt(int width = 1800, int height = 1000)
    {
        using (var bitmap = new Bitmap(width, height))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var output = new MemoryStream())
        {
            graphics.Clear(Color.Green);
            graphics.FillRectangle(Brushes.Red, 0, 0, Math.Max(0, (width - height) / 2), height);
            graphics.FillRectangle(Brushes.Blue, width - Math.Max(0, (width - height) / 2), 0, Math.Max(0, (width - height) / 2), height);
            bitmap.Save(output, ImageFormat.Png); return output.ToArray();
        }
    }
    private static void AddRectangleArt(string path)
    {
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures = new TagLib.IPicture[] {
                new TagLib.Picture(new TagLib.ByteVector(RectangleArt())) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png", Description = "Front" },
                new TagLib.Picture(new TagLib.ByteVector(RectangleArt(600, 400))) { Type = TagLib.PictureType.BackCover, MimeType = "image/png", Description = "Back" } };
            file.Save();
        }
    }
    private static Dictionary<string, string> TreeHashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p.Substring(root.Length), CoverEditor.Hash);
    private static void ManagementTests()
    {
        byte[] image = RectangleArt(); byte[] cropped = Artwork.Square(image);
        using (var square = Artwork.Decode(cropped))
            Assert(square.Width == 1000 && square.Height == 1000 && square.GetPixel(0, 0).ToArgb() == Color.Green.ToArgb(), "square crop uses the center at original resolution, without resizing");
        Assert(Artwork.Square(cropped).SequenceEqual(cropped), "already-square artwork remains byte identical");
        foreach (string extension in new[] { ".mp3", ".m4a" })
            Assert(!LibraryPaths.ShouldConvert(new Track { Path = "x" + extension, Bitrate = 256 }, new LibraryOptions { Convert = true }), "default conversion does not upscale lower-bitrate lossy " + extension);
        Assert(!LibraryPaths.ShouldConvert(new Track { Path = "video.mp4", HasVideo = true, Bitrate = 500 }, new LibraryOptions { Convert = true, ConvertOtherLossy = true }), "video containers are not silently replaced with audio-only files");
        Assert(!LibraryPaths.ShouldConvert(new Track { Path = "x.mp3", Bitrate = 320 }, new LibraryOptions { Convert = true }), "existing MP3 320 is not re-encoded");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.flac", Bitrate = 100, Lossless = true }, new LibraryOptions { Convert = true }), "lossless source is convertible even at a low compressed bitrate");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.m4a", Bitrate = 400 }, new LibraryOptions { Convert = true }), "over-320 lossy source is convertible");
        Assert(LibraryPaths.ShouldConvert(new Track { Path = "x.m4a", Bitrate = 128 }, new LibraryOptions { Convert = true, ConvertOtherLossy = true }), "explicit compatibility mode converts other non-MP3 audio");
        Assert(LibraryPaths.Component("AC/DC: Live?") == "AC_DC- Live_" && LibraryPaths.Component("CON") == "_CON", "Windows folder names are sanitized without creating extra directory levels");
        foreach (string fixture in Directory.GetFiles(fixtures).Where(p => Scanner.Extensions.Contains(Path.GetExtension(p)))) SquareRoundTrip(fixture);
        ManagementCombined(); ManagementConflicts(); ManagementStaleAndCancel(); ManagementRecovery(); ManagementUnknownDuplicates();
        FilenameTests();
    }
    private static void SquareRoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture), root = NewFolder("manager-square-" + extension.Substring(1)), backups = NewFolder("manager-square-backup-" + extension.Substring(1));
        string path = CopyTrack(root, "one" + extension); AddRectangleArt(path);
        string original = CoverEditor.Hash(path), audio = AudioHash(path), metadata = Metadata(path);
        TagLib.PictureType[] originalTypes;
        using (var file = TagLib.File.Create(path)) originalTypes = file.Tag.Pictures.Select(p => p.Type).ToArray();
        var options = Options(root, backups); options.Square = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(a => a.Square) == 1 && CoverEditor.Hash(path) == original, extension + " crop preview leaves original untouched");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.Single().State == "Committed", extension + " square crop commits successfully");
        using (var file = TagLib.File.Create(path))
        {
            Assert(file.Tag.Pictures.Select(p => p.Type).SequenceEqual(originalTypes), extension + " square crop preserves embedded image count, types and order");
            using (var art = Artwork.Decode(file.Tag.Pictures[0].Data.Data)) Assert(art.Width == 1000 && art.Height == 1000, extension + " full-size front artwork is square");
            using (var art = Artwork.Decode(file.Tag.Pictures[1].Data.Data)) Assert(art.Width == 400 && art.Height == 400, extension + " other embedded artwork is square too");
        }
        Assert(AudioHash(path) == audio && Metadata(path) == metadata, extension + " crop preserves decoded audio and music tags");
        int restored = LibraryRunner.Undo(journal, CancellationToken.None);
        Assert(restored == 1 && CoverEditor.Hash(path) == original, extension + " persisted library Undo restores byte-exact original art and file");
    }
    private static void ManagementCombined()
    {
        string root = NewFolder("manager-combined"), backups = NewFolder("manager-combined-backups");
        string nested = Path.Combine(root, "Artist", "Artist", "2019", "Album - An album", "Disc 1"), copy = Path.Combine(root, "Copies", "An album", "Disc 1"), loose = Path.Combine(root, "Downloads", "Mixed");
        Directory.CreateDirectory(nested); Directory.CreateDirectory(copy); Directory.CreateDirectory(loose);
        string first = CopyTrack(nested, "01 - Song.flac", "An album", "Real Artist"); AddRectangleArt(first);
        using (var audio = TagLib.File.Create(first)) { audio.Tag.Disc = 1; audio.Tag.DiscCount = 2; audio.Save(); }
        string duplicate = Path.Combine(copy, "01 - Song.flac"); File.Copy(first, duplicate);
        string lower = CopyTrack(loose, "quiet.mp3", "Lower album", "Quiet Artist");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(nested), "booklet.pdf"), "Test booklet");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(nested), "cover.png"), RectangleArt());
        string missing = CopyTrack(root, "untagged.mp3");
        using (var audio = TagLib.File.Create(missing)) { audio.RemoveTags(TagLib.TagTypes.AllTags); audio.Save(); }
        string oldBackups = Path.Combine(nested, ".album-art-backups", "old-run"); Directory.CreateDirectory(oldBackups); File.Copy(lower, Path.Combine(oldBackups, "old.mp3"));
        var original = TreeHashes(root);
        var options = Options(root, backups); options.Convert = options.Square = options.Organize = options.Duplicates = options.ArchiveOldBackups = true;
        var updates = new List<ProgressInfo>(); var plan = LibraryPlanner.Build(options, CancellationToken.None, new DirectProgress<ProgressInfo>(updates.Add));
        Assert(original.All(x => CoverEditor.Hash(root + x.Key) == x.Value), "combined preview does not move, remove or edit any file");
        Assert(plan.Actions.Count(a => a.Keeper != null) == 1 && plan.Actions.Count(a => a.Convert) == 1, "duplicate lossless album copy is removed once, keeper alone is converted");
        Assert(plan.Actions.Single(a => a.Source == missing).Target == missing, "missing artist/album tags leave the loose file in place");
        var journal = LibraryRunner.Run(plan, CancellationToken.None, new DirectProgress<ProgressInfo>(updates.Add));
        Assert(journal.Issues.Count == 0, "combined run completes without failures: " + string.Join(";", journal.Issues));
        string target = Path.Combine(root, "Real Artist", "An album", "Disc 1", "01 - Song.mp3");
        Assert(File.Exists(target) && !File.Exists(first) && !File.Exists(duplicate), "nested Artist/Artist/Year layouts collapse into canonical tagged album/disc folders with one MP3");
        using (var file = TagLib.File.Create(target))
        { Assert(file.Properties.AudioBitrate >= 318 && file.Tag.Album == "An album" && file.Tag.FirstPerformer == "Real Artist", "converted MP3 is 320 kbps and retains main metadata"); using (var art = Artwork.Decode(file.Tag.Pictures[0].Data.Data)) Assert(art.Width == art.Height && art.Width == 1000, "combined conversion retains and squares original full-resolution artwork"); }
        Assert(File.Exists(Path.Combine(root, "Real Artist", "An album", "booklet.pdf")), "booklet stays with recognized album during recursive organization");
        Assert(CoverEditor.Hash(Path.Combine(root, "Quiet Artist", "Lower album", "quiet.mp3")) == original[lower.Substring(root.Length)], "lower-bitrate MP3 is moved without any re-encoding or metadata edits");
        Assert(!Directory.Exists(oldBackups) && journal.Entries.Any(e => e.Archive && e.Source.Contains(".album-art-backups")), "legacy playable backups are archived outside the active player tree");
        Assert(LibraryJournal.Last().FilePath == journal.FilePath, "last library run journal survives reconstruction from disk");
        int restored = LibraryRunner.Undo(LibraryJournal.Last(), CancellationToken.None);
        Assert(restored == journal.Entries.Count && original.Count == TreeHashes(root).Count && original.All(x => CoverEditor.Hash(root + x.Key) == x.Value), "combined conversion/removal/crop/move Undo restores the entire original tree and exact bytes");
    }
    private static void ManagementConflicts()
    {
        string root = NewFolder("manager-conflicts"), backups = NewFolder("manager-conflict-backups"), incoming = Path.Combine(root, "Unsorted"), canonical = Path.Combine(root, "Artist", "Album");
        Directory.CreateDirectory(incoming); Directory.CreateDirectory(canonical);
        string first = CopyTrack(incoming, "song.mp3", "Album", "Artist"), existing = CopyTrack(canonical, "song.mp3", "Album", "Artist");
        using (var file = TagLib.File.Create(existing)) { file.Tag.Title = "A different version"; file.Save(); }
        string hash = CoverEditor.Hash(existing);
        var options = Options(root, backups); options.Organize = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Single(a => a.Source == first).Target.EndsWith("song (2).mp3"), "filename conflicts allocate another name rather than overwriting a different file");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && CoverEditor.Hash(existing) == hash, "existing destination stays byte identical during a conflicting move");
        using (var file = TagLib.File.Create(plan.Actions.Single(a => a.Source == first).Target)) { file.Tag.Title = "Later user edit"; file.Save(); }
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 0 && journal.Issues.Count > 0, "Undo refuses to overwrite a later edit to a moved track");
    }
    private static void ManagementStaleAndCancel()
    {
        string root = NewFolder("manager-stale"), backups = NewFolder("manager-stale-backups");
        string path = CopyTrack(root, "source.flac"); AddRectangleArt(path);
        var options = Options(root, backups); options.Convert = options.Square = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10)); string hash = CoverEditor.Hash(path);
        var result = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(result.Issues.Count == 1 && CoverEditor.Hash(path) == hash && !File.Exists(Path.ChangeExtension(path, ".mp3")), "files changed after preview are skipped before replacement/removal");
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); result = LibraryRunner.Run(plan, cancel.Token);
            Assert(result.Stopped && result.Entries.Count == 0 && CoverEditor.Hash(path) == hash, "stopping before processing leaves original files untouched");
        }
        options.BackupRoot = Path.Combine(root, "bad-backups");
        Assert(Throws(() => LibraryPlanner.Build(options, CancellationToken.None)), "backups inside the active music/player tree are rejected");
    }
    private static void ManagementUnknownDuplicates()
    {
        string root = NewFolder("manager-unknown-duplicates"), backup = NewFolder("manager-unknown-duplicate-backups"), a = Path.Combine(root, "Unknown album A"), b = Path.Combine(root, "Unknown album B");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string track = CopyTrack(a, "01.mp3");
        using (var audio = TagLib.File.Create(track)) { audio.RemoveTags(TagLib.TagTypes.AllTags); audio.Save(); }
        File.Copy(track, Path.Combine(b, "01.mp3"));
        var options = Options(root, backup); options.Duplicates = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.All(x => !x.Archive), "untagged identical audio in different unknown albums is not automatically removed");
        File.Copy(track, Path.Combine(a, "01 (1).mp3"));
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Count(x => x.Archive) == 1, "an obvious same-folder untagged file copy can still be removed safely");
    }
    private static void ManagementRecovery()
    {
        string root = NewFolder("manager-recovery"), backups = NewFolder("manager-recovery-backup");
        string first = CopyTrack(root, "one.mp3"), target = Path.Combine(root, "Nested", "one.mp3");
        var journal = new LibraryJournal { Root = root, Directory = Path.Combine(backups, "run") }; journal.Save();
        Directory.CreateDirectory(BackupSettings.AppData); File.WriteAllText(LibraryJournal.LastFile, journal.FilePath);
        var entry = new JournalEntry { Index = 0, Source = first, Target = target, OriginalHash = CoverEditor.Hash(first), WrittenHash = CoverEditor.Hash(first), MoveOnly = true, State = "Prepared" };
        journal.Entries.Add(entry); journal.Save(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(first, target);
        File.AppendAllText(Path.Combine(journal.Directory, "events.jsonl"), "{unfinished");
        var recovered = LibraryJournal.Last();
        Assert(recovered.Entries.Count == 1 && recovered.Entries[0].State == "Prepared", "recovery replays write-ahead records and ignores a partially written tail");
        Assert(LibraryRunner.Undo(recovered, CancellationToken.None) == 1 && File.Exists(first) && !File.Exists(target), "Undo recovers a move interrupted before its final completion record");
        string other = Path.Combine(root, "must-keep.txt"); File.WriteAllText(other, "keep");
        Assert(Throws(() => LibraryRunner.Copy(first, other, CancellationToken.None)) && File.ReadAllText(other) == "keep", "failed CreateNew copy never deletes an existing destination");
    }
    private static async Task<int> LiveEncoderSetup()
    {
        using (var setup = new EncoderSetup())
        {
            string exe = await setup.InstallAsync(null, CancellationToken.None);
            Encoder.Validate(exe, CancellationToken.None);
            string folder = NewFolder("live-encoder-conversion"), input = CopyTrack(folder, "input.flac"); AddRectangleArt(input);
            Encoder.Convert(exe, ScanAlbum(folder).Tracks.Single(), Path.Combine(folder, "verified.mp3"), CancellationToken.None, null);
            Console.WriteLine("LIVE ENCODER: verified upstream checksum, license extraction, startup and actual MP3 320 conversion with retained artwork.");
        }
        return 0;
    }
    private static void ManagementGuiSmoke(string output)
    {
        string root = NewFolder("manager-gui"), backups = NewFolder("manager-gui-backups"), folder = Path.Combine(root, "BENEE", "BENEE", "2020", "Hey u x"); Directory.CreateDirectory(folder);
        string file = CopyTrack(folder, "01 - Happen to Me.flac", "Hey u x", "BENEE"); AddRectangleArt(file); SetFilenameTitle(file, "Happen to Me");
        File.Copy(file, Path.Combine(folder, "01 - Happen to Me (1).flac"));
        Exception failure = null;
        using (var form = new LibraryManagerWindow(root, () => true, _ => { }, _ => Task.CompletedTask))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    Descendants(form).OfType<TextBox>().Single(t => t.AccessibleName == "PC backup folder").Text = backups;
                    // Exercise the same progress callbacks as a long scan, sampling
                    // the screen while the disabled options remain under the mouse.
                    var setWorking = typeof(LibraryManagerWindow).GetMethod("SetWorking", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    setWorking.Invoke(form, new object[] { true });
                    var reporter = (IProgress<ProgressInfo>)typeof(LibraryManagerWindow).GetMethod("Reporter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                    var optionsPanel = Descendants(form).Single(c => c.AccessibleName == "Library management options");
                    Cursor.Position = optionsPanel.PointToScreen(new Point(70, 20)); await Task.Delay(60);
                    using (var initial = ScreenImage(optionsPanel.RectangleToScreen(optionsPanel.ClientRectangle)))
                    {
                        for (int n = 1; n <= 16; n++)
                        {
                            reporter.Report(new ProgressInfo { Completed = n * 300, Total = 6589, Message = n * 300 + "/6589 tracks scanned · Artist/Album " + n });
                            await Task.Delay(25);
                            using (var frame = ScreenImage(optionsPanel.RectangleToScreen(optionsPanel.ClientRectangle)))
                                Assert(DifferentPixels(initial, frame) == 0, "scan progress does not alter or flash the options panel");
                        }
                    }
                    var planned = Descendants(form).OfType<ListView>().Single(l => l.AccessibleName == "Planned library changes");
                    Assert(planned.Enabled, "the management list keeps its dark background and remains scrollable during work");
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "manager-scanning-stable-options.png"), ImageFormat.Png);
                    setWorking.Invoke(form, new object[] { false });
                    await form.PreviewAsync();
                    Assert(form.CurrentPlan != null && form.CurrentPlan.Actions.Any(a => a.Convert) && form.CurrentPlan.Actions.Any(a => a.Keeper != null), "manager UI previews conversion and duplicate removal together");
                    var rename = Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Rename song files from Title tags");
                    Assert(!rename.Checked && form.CurrentPlan.Actions.Where(a => !a.Archive).All(a => !a.Rename), "filename cleanup is an explicit optional setting");
                    rename.Checked = true;
                    Assert(form.CurrentPlan == null, "changing filename cleanup invalidates the previous preview");
                    await form.PreviewAsync();
                    Assert(form.CurrentPlan.Actions.Any(a => a.Rename && a.Convert && a.Target.EndsWith("Happen to Me.mp3")), "manager previews Title filenames together with conversion, cropping, duplicates and folder organization");
                    var button = Descendants(form).OfType<Button>().Single(b => b.Text == "Apply selected changes");
                    Assert(button.Enabled && !form.IsWorking, "manager exposes an explicit reviewable Apply action after preview");
                    Directory.CreateDirectory(output);
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "library-manager.png"), ImageFormat.Png); }
                    setWorking.Invoke(form, new object[] { true });
                    bool checkedBefore = planned.Items[0].Checked; planned.Items[0].Checked = !checkedBefore;
                    Assert(planned.Enabled && planned.Items[0].Checked == checkedBefore, "running management keeps preview rows readable while locking selected changes");
                    await Task.Delay(40);
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "manager-working-readable-list.png"), ImageFormat.Png);
                    setWorking.Invoke(form, new object[] { false });
                    await form.ApplyAsync();
                    Assert(File.Exists(Path.Combine(root, "BENEE", "Hey u x", "Happen to Me.mp3")), "manager UI applies conversion, canonical organization and the metadata filename");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
