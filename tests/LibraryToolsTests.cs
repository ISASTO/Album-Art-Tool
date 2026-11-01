using System;
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
using AlbumArtTool.Online;

internal static partial class Tests
{
    private static void SetArtists(string path, string[] performers, string[] albumArtists, string title = null)
    {
        using (var audio = TagLib.File.Create(path))
        {
            audio.Tag.Performers = performers; audio.Tag.AlbumArtists = albumArtists;
            if (title != null) audio.Tag.Title = title;
            audio.Save();
        }
    }
    private static void ArtistRulesTests()
    {
        string root = NewFolder("artist-rules");
        string[] credits = { "BENEE", "BENEE", "BENEE, Guest 1", "BENEE, Guest 2", "BENEE, Guest 3, Guest 4", "BENEE, Guest 2" };
        for (int i = 0; i < credits.Length; i++) SetArtists(CopyTrack(root, i + ".mp3", "BENEE album", "BENEE"), new[] { credits[i] }, new[] { "BENEE" }, "Song " + i);
        var album = ScanAlbum(root);
        Assert(album.MainArtist == "BENEE" && album.ArtistChangeCount == 4, "BENEE featured-artist example suggests BENEE and counts four changed tracks");
        foreach (var track in album.Tracks) SetArtists(track.Path, track.Performers, new string[0]);
        album = ScanAlbum(root);
        Assert(album.MainArtist == "BENEE" && album.Artist == "BENEE", "common lead artist is inferred when album artist tags are absent");
        var edit = Editor.ApplyArtist(album, "BENEE");
        Assert(edit.TracksWritten == 4 && ScanAlbum(root).ArtistChangeCount == 0, "artist cleanup changes guest credits without rewriting already-correct tracks with missing album artist tags");
        string clean = NewFolder("artist-already-correct"), cleanPath = CopyTrack(clean, "one.mp3", "10,000 gecs", "100 gecs");
        SetArtists(cleanPath, new[] { "100 gecs" }, new string[0]);
        string cleanHash = CoverEditor.Hash(cleanPath); var cleanAlbum = ScanAlbum(clean);
        Assert(cleanAlbum.ArtistChangeCount == 0 && Editor.ApplyArtist(cleanAlbum, "100 gecs").TracksWritten == 0 && CoverEditor.Hash(cleanPath) == cleanHash,
            "matching track artists with missing Album artist metadata are not offered or rewritten as cleanup");
        Assert(!ArtistRules.NeedsChange(new Track { Performers = new[] { " 100  GECS " }, AlbumArtists = new[] { "A different album credit" } }, "100 gecs"),
            "capitalization, whitespace and unrelated album credits alone do not create track-artist cleanup");
        Assert(ArtistRules.NeedsChange(new Track { Performers = new[] { "100 gecs", "Guest" }, AlbumArtists = new[] { "100 gecs" } }, "100 gecs"),
            "real additional track-artist credits still require cleanup");
        string bands = NewFolder("artist-band-name");
        SetArtists(CopyTrack(bands, "one.mp3"), new[] { "Earth, Wind & Fire" }, new string[0]);
        SetArtists(CopyTrack(bands, "two.mp3"), new[] { "Earth, Wind & Fire" }, new string[0]);
        Assert(ScanAlbum(bands).MainArtist == "Earth, Wind & Fire", "a band's comma and ampersand are preserved");
        string compilation = NewFolder("artist-compilation");
        SetArtists(CopyTrack(compilation, "one.mp3"), new[] { "First Artist" }, new string[0]);
        SetArtists(CopyTrack(compilation, "two.mp3"), new[] { "Second Artist" }, new string[0]);
        Assert(ScanAlbum(compilation).MainArtist == "", "mixed-artist compilations are not guessed as one artist");
        string before = CoverEditor.Hash(album.Tracks[0].Path);
        Assert(Throws(() => Editor.ApplyArtist(album, "")) && CoverEditor.Hash(album.Tracks[0].Path) == before, "empty artist cannot modify a track");
        var stale = ScanAlbum(root);
        using (var audio = TagLib.File.Create(stale.Tracks[0].Path)) { audio.Tag.Title = "An outside edit"; audio.Save(); }
        System.IO.File.SetLastWriteTimeUtc(stale.Tracks[0].Path, DateTime.UtcNow.AddSeconds(3));
        edit = Editor.ApplyArtist(stale, "Different name");
        Assert(edit.TracksWritten == 5 && edit.Issues.Count == 1, "artist cleanup skips stale tracks and reports partial failures");
    }

    private static void ArtistRoundTrip(string fixture)
    {
        string extension = Path.GetExtension(fixture), root = NewFolder("artist-roundtrip-" + extension.Substring(1));
        string path = CopyTrack(root, "track" + extension, "An album", "BENEE");
        SetArtists(path, new[] { "BENEE", "A guest" }, new[] { "BENEE" });
        Editor.Apply(ScanAlbum(root), Cover, false, false);
        using (var audio = TagLib.File.Create(path)) { audio.Tag.PerformersSort = new[] { "BENEE; A guest" }; audio.Save(); }
        string original = CoverEditor.Hash(path), audioHash = AudioHash(path), metadata = NonArtistMetadata(path);
        var edit = Editor.ApplyArtist(ScanAlbum(root), "BENEE");
        Assert(edit.TracksWritten == 1 && edit.Issues.Count == 0, extension + " artist fields can be simplified");
        using (var audio = TagLib.File.Create(path))
        {
            Assert(ArtistRules.Single(audio.Tag.Performers, "BENEE") && ArtistRules.Single(audio.Tag.AlbumArtists, "BENEE"), extension + " artist and album artist persist as BENEE");
            Assert(audio.Tag.PerformersSort.Length == 0 || ArtistRules.Single(audio.Tag.PerformersSort, "BENEE"), extension + " existing artist sort field no longer contains guests");
        }
        Assert(AudioHash(path) == audioHash && NonArtistMetadata(path) == metadata, extension + " artist cleanup preserves decoded audio, covers and other tags");
        Editor.Undo(edit);
        Assert(CoverEditor.Hash(path) == original, extension + " artist Undo restores the exact original file");
    }
    private static string NonArtistMetadata(string path)
    {
        using (var file = TagLib.File.Create(path)) return string.Join("|", file.Tag.Title, file.Tag.Album, file.Tag.Year, file.Tag.Track, file.Tag.Comment,
            string.Join(";", file.Tag.Genres), string.Join(";", file.Tag.Pictures.Select(p => p.Type + ":" + Convert.ToBase64String(p.Data.Data))));
    }

    private static void DuplicateTests()
    {
        string root = NewFolder("duplicates"), a = Path.Combine(root, "Original album"), b = Path.Combine(root, "Album copy");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string first = CopyTrack(a, "one.mp3", "An album", "BENEE"); SetArtists(first, new[] { "BENEE" }, new[] { "BENEE" }, "One");
        string second = CopyTrack(a, "two.flac", "An album", "BENEE"); SetArtists(second, new[] { "BENEE" }, new[] { "BENEE" }, "Two");
        System.IO.File.Copy(first, Path.Combine(b, "renamed.mp3")); System.IO.File.Copy(second, Path.Combine(b, "renamed.flac"));
        var scanner = new DuplicateScanner();
        var library = Scanner.Scan(root, CancellationToken.None);
        var exact = scanner.Scan(library.Albums, CancellationToken.None);
        Assert(exact.Songs.Count(g => g.Confirmed) == 2 && exact.Albums.Count(g => g.Confirmed) == 1, "exact song and complete album copies are found across different folder/file names");
        var copyAlbum = ScanAlbum(b);
        Editor.Apply(copyAlbum, MakeCover(Color.Coral), false, false);
        library = Scanner.Scan(root, CancellationToken.None);
        var changes = new System.Collections.Generic.List<ProgressInfo>();
        var tagged = scanner.Scan(library.Albums, CancellationToken.None, new DirectProgress<ProgressInfo>(changes.Add));
        Assert(tagged.Songs.Count(g => g.Confirmed && g.Basis.StartsWith("Identical audio")) == 2 && tagged.Albums.Count(g => g.Confirmed) == 1,
            "MP3 and FLAC copies remain confirmed after artwork changes");
        Assert(changes.Last().Completed == 4 && changes.Last().Total == 4, "duplicate progress finishes with an exact checked/total counter");
        Assert(!System.IO.Directory.Exists(Path.Combine(b, "unexpected")) && tagged.Songs.All(g => g.Tracks.All(t => !t.Path.Contains(".album-art-backups"))), "duplicate scan excludes artwork backups");
        string report = DuplicateWindow.Csv(tagged);
        Assert(report.Contains("Confirmed") && report.Contains(first) && report.Contains("Same complete set"), "report includes paths, confirmation and album match basis");
        string[] hashes = library.Albums.SelectMany(x => x.Tracks).Select(t => CoverEditor.Hash(t.Path)).ToArray();
        scanner.Scan(library.Albums, CancellationToken.None);
        Assert(hashes.SequenceEqual(library.Albums.SelectMany(x => x.Tracks).Select(t => CoverEditor.Hash(t.Path))), "duplicate scan never changes music files");
        System.IO.File.Delete(Path.Combine(b, "renamed.flac"));
        var incomplete = scanner.Scan(Scanner.Scan(root, CancellationToken.None).Albums, CancellationToken.None);
        Assert(incomplete.Albums.Count == 0, "a partial album copy is not reported as a complete duplicate album");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); Assert(Throws(() => scanner.Scan(library.Albums, cancel.Token)), "duplicate scan honors cancellation");
        }
        System.IO.File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
        Assert(scanner.Scan(library.Albums, CancellationToken.None).Issues.Count >= 1, "changed or missing files are skipped instead of being compared from stale data");

        // Same metadata and duration, genuinely different encoded audio: only a possible match.
        string possible = NewFolder("duplicate-possible");
        string old = CopyTrack(possible, "one.mp3");
        string other = Path.Combine(possible, "other.mp3");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ALBUMART_FFMPEG"), "-v error -y -f lavfi -i sine=frequency=880:sample_rate=44100:duration=0.3 -c:a libmp3lame " + UpdatesQuote(other))
            { UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(info)) { process.WaitForExit(); Assert(process.ExitCode == 0, "second distinct test recording generated"); }
        SetArtists(other, new[] { "Test artist" }, new[] { "Test artist" }, "Keep this title");
        var candidates = scanner.Scan(Scanner.Scan(possible, CancellationToken.None).Albums, CancellationToken.None);
        Assert(candidates.Songs.Count == 1 && !candidates.Songs[0].Confirmed, "matching tags and duration do not prove different recordings are identical");
    }
    private static string UpdatesQuote(string value) => AlbumArtTool.Updates.UpdateInstaller.QuoteArgument(value);

    private static void LibraryToolsGuiSmoke(string output)
    {
        string root = NewFolder("library-tools-gui"), first = Path.Combine(root, "BENEE"), second = Path.Combine(root, "Clean album"), copy = Path.Combine(root, "BENEE copy");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(copy);
        string one = CopyTrack(first, "one.mp3", "Hey u x", "BENEE"), two = CopyTrack(first, "two.mp3", "Hey u x", "BENEE");
        SetArtists(one, new[] { "BENEE" }, new[] { "BENEE" }, "Happen to Me");
        SetArtists(two, new[] { "BENEE", "Featured Artist" }, new[] { "BENEE" }, "A collaboration");
        System.IO.File.Copy(one, Path.Combine(copy, "one.mp3")); System.IO.File.Copy(two, Path.Combine(copy, "two.mp3"));
        SetArtists(Path.Combine(copy, "one.mp3"), new[] { "BENEE" }, new[] { "BENEE" }, "A different opener");
        string cleanFile = CopyTrack(second, "one.flac", "A clean album", "Another Artist"); SetArtists(cleanFile, new[] { "Another Artist" }, new string[0]);
        var service = new GatedCoverSearch(); Exception failure = null;
        using (var form = new MainForm(root, false, service))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    await form.ScanAsync();
                    Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Track artists (")).PerformClick();
                    var list = Descendants(form).OfType<AlbumList>().Single();
                    Assert(list.AlbumCount == 2, "artist cleanup tab finds eligible albums regardless of whether they have covers");
                    var filter = Descendants(form).OfType<ComboBox>().Single(c => c.AccessibleName == "Album filter");
                    var sort = Descendants(form).OfType<ComboBox>().Single(c => c.AccessibleName == "Album sort order");
                    filter.SelectedIndex = 0; Assert(list.AlbumCount == 3, "all albums can be reviewed including manually specified artist names");
                    sort.SelectedIndex = 2;
                    Assert(list.SelectedAlbum.ArtistChangeCount > 0, "artist changes first sort promotes eligible albums");
                    filter.SelectedIndex = 1;
                    var name = Descendants(form).OfType<TextBox>().Single(t => t.AccessibleName == "Main artist for this album");
                    Assert(name.Visible && name.Text == "BENEE", "suggested main artist is editable and visible");
                    var heading = Descendants(form).OfType<Label>().Single(c => c.AccessibleName == "Selected album for artist cleanup");
                    bool blanked = false; heading.TextChanged += (a, b) => blanked |= heading.Text == "Select an album";
                    for (int n = 0; n < 12; n++) { list.SelectedIndices.Clear(); list.SelectedIndices.Add(n % 2); }
                    list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
                    await Task.Delay(30);
                    Assert(!blanked && name.Text == "BENEE", "switching artist albums does not expose an empty intermediate details pane");
                    var preview = Descendants(form).OfType<ListView>().Single(l => l.AccessibleName == "Current track artists");
                    Assert(preview.Items.Cast<ListViewItem>().Count(i => i.SubItems[2].Text == "Unchanged") == 1 && preview.Items.Cast<ListViewItem>().Count(i => i.SubItems[2].Text == "BENEE") == 1,
                        "artist preview distinguishes unchanged tracks from the actual proposed credits");
                    Cursor.Position = preview.PointToScreen(new Point(90, 35)); await Task.Delay(40);
                    using (var prior = ScreenImage(AlbumArtTool.Design.ThemeScrollbars.For(preview).VisibleContent))
                    {
                        string oldTitle = preview.Items[0].Text;
                        list.SelectedIndices.Clear(); list.SelectedIndices.Add(1); await Task.Delay(80);
                        using (var after = ScreenImage(AlbumArtTool.Design.ThemeScrollbars.For(preview).VisibleContent))
                            Assert(preview.Items[0].Text != oldTitle && DifferentPixels(prior, after) > 30,
                                "album switching repaints track names without moving the pointer out of the preview");
                    }
                    list.SelectedIndices.Clear(); list.SelectedIndices.Add(0); await Task.Delay(40);
                    ProcessStartInfo opened = null; form.StartExplorer = info => opened = info;
                    var albumFolder = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Open artist-tab album folder");
                    var showTrack = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Reveal artist-tab selected track");
                    Assert(albumFolder.Visible && albumFolder.Enabled && !showTrack.Enabled, "artist tab offers album folder access and waits for a selected track before revealing a file");
                    albumFolder.PerformClick(); Assert(opened.FileName == list.SelectedAlbum.Folder && opened.UseShellExecute, "artist-tab album button opens the currently selected album folder");
                    preview.Items[1].Selected = true; showTrack.PerformClick();
                    Assert(opened.FileName == "explorer.exe" && opened.Arguments == "/select," + UpdatesQuote(((Track)preview.Items[1].Tag).Path), "selected-track action reveals that exact file in Explorer with a quoted path");
                    Directory.CreateDirectory(output);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "track-artists.png"), ImageFormat.Png); }
                    string firstKey = list.SelectedAlbum.Key;
                    // A paused artwork job shares the same queue with artist edits.
                    var artwork = form.ApplyOnlineCoverAsync(firstKey, new CoverCandidate { Source = "Test", ImageUrl = "gated" });
                    await service.Started.Task;
                    list.SelectedIndices.Clear(); list.SelectedIndices.Add(1); name.Text = "BENEE";
                    string target = list.SelectedAlbum.Folder;
                    var artist = form.ApplyArtistAsync();
                    Assert(!artist.IsCompleted && form.QueuedCount == 2 && list.Enabled, "artist jobs queue behind artwork while browsing stays responsive");
                    name.Text = "An edit after enqueueing";
                    service.Release.TrySetResult(true); await Task.WhenAll(artwork, artist); await form.QueueCompletion;
                    Assert(ScanAlbum(target).Tracks.All(t => ArtistRules.Single(t.Performers, "BENEE")), "queued artist job keeps the captured name and album");
                    Assert(list.AlbumCount == 1, "completed cleanup disappears from the eligible-album filter");
                    Descendants(form).OfType<Button>().Single(b => b.Text == "Undo last change").PerformClick();
                    for (int n = 0; n < 200 && list.AlbumCount != 2; n++) await Task.Delay(20);
                    Assert(list.AlbumCount == 2 && ScanAlbum(target).ArtistChangeCount == 1, "Undo restores guest credits and the album returns to the filter");
                    await form.FindDuplicatesAsync();
                    var dialog = form.OwnedForms.OfType<DuplicateWindow>().Single();
                    Assert(dialog.Result.Songs.Any(g => g.Confirmed) && dialog.Result.Albums.Any(g => g.Confirmed), "duplicate window shows confirmed songs and album copies");
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                    { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "duplicates.png"), ImageFormat.Png); }
                    dialog.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { service.Release.TrySetResult(true); form.ClearWaitingJobs(); await form.QueueCompletion; form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
