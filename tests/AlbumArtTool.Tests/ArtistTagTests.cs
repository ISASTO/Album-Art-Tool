using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;

internal static partial class Tests
{
    private static string ArtistTagFolder(string name, out string solo, out string guest, out string tagged)
    {
        string root = NewFolder(name), album = Path.Combine(root, "BENEE", "Hey u x"); Directory.CreateDirectory(album);
        solo = CopyTrack(album, "01.mp3", "Hey u x", "BENEE"); guest = CopyTrack(album, "02.mp3", "Hey u x", "BENEE"); tagged = CopyTrack(album, "03.flac", "Hey u x", "BENEE");
        SetArtists(solo, new[] { "BENEE" }, new string[0], "Happen to Me");
        SetArtists(guest, new[] { "BENEE", "Grouplove" }, new string[0], "Same Effect");
        SetArtists(tagged, new[] { "Benee" }, new[] { "BENEE" }, "Night Garden");
        return root;
    }

    private static void ArtistTagTests()
    {
        string root = ArtistTagFolder("artist-tags", out string solo, out string guest, out string tagged);
        var check = LibraryChecks.Run(Scanner.Scan(root, CancellationToken.None).Albums);
        Assert(check.MissingAlbumArtists.Select(t => t.Path).OrderBy(p => p).SequenceEqual(new[] { solo, guest }.OrderBy(p => p)), "the check finds songs with an artist but no album artist");

        var options = Options(root, NewFolder("artist-tags-backups")); options.AlbumArtistFromArtist = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var fills = plan.Actions.Where(a => a.HasWork).ToList();
        Assert(fills.Count == 2 && fills.All(a => a.AlbumArtistFilled && a.NewAlbumArtists.SequenceEqual(new[] { "BENEE" }) && a.NewArtists == null && !a.Move), "missing album artists are filled with the main artist, never a guest");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        using (var file = TagLib.File.Create(guest))
            Assert(journal.Issues.Count == 0 && file.Tag.AlbumArtists.SequenceEqual(new[] { "BENEE" }) && file.Tag.Performers.SequenceEqual(new[] { "BENEE", "Grouplove" }) && file.Tag.Title == "Same Effect",
                "the album artist is written and the artists stay as they were: " + string.Join(" | ", journal.Issues));
        Assert(LibraryChecks.Run(Scanner.Scan(root, CancellationToken.None, useCache: false).Albums).MissingAlbumArtists.Count == 0, "afterwards no song is missing an album artist");
        LibraryRunner.Undo(journal, CancellationToken.None);
        using (var file = TagLib.File.Create(solo)) Assert(file.Tag.AlbumArtists.Length == 0, "Undo removes the filled album artist again");

        // Renaming an artist everywhere, whatever its letter case; guests keep their credit.
        options = Options(root, NewFolder("artist-rename-backups")); options.RenameArtistsFrom = new[] { "benee" }; options.RenameArtistTo = "BENEE ✿";
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        var renames = plan.Actions.Where(a => a.HasWork).ToList();
        Assert(renames.Count == 3 && renames.Single(a => a.Source == guest).NewArtists.SequenceEqual(new[] { "BENEE ✿", "Grouplove" }) &&
            renames.Single(a => a.Source == tagged).NewAlbumArtists.SequenceEqual(new[] { "BENEE ✿" }) && renames.All(a => a.Description == "Rename artist"), "the rename finds every spelling of the artist and keeps guests");
        journal = LibraryRunner.Run(plan, CancellationToken.None);
        using (var file = TagLib.File.Create(tagged))
            Assert(journal.Issues.Count == 0 && file.Tag.Performers.SequenceEqual(new[] { "BENEE ✿" }) && file.Tag.AlbumArtists.SequenceEqual(new[] { "BENEE ✿" }), "Artist and Album Artist tags are renamed: " + string.Join(" | ", journal.Issues));
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 3, "the rename can be undone");
        using (var file = TagLib.File.Create(tagged)) Assert(file.Tag.FirstPerformer == "Benee", "Undo restores the original spelling");

        options.RenameArtistsFrom = new[] { "BENEE", "Grouplove" }; options.RenameArtistTo = "One Name";
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Single(a => a.Source == guest).NewArtists.SequenceEqual(new[] { "One Name" }) && plan.Actions.Count(a => a.HasWork) == 3, "several artists can become one name");
        options.RenameArtistsFrom = new[] { "benee" }; options.RenameArtistTo = "BENEE ✿";
        options.RenameAlbumArtists = false;
        plan = LibraryPlanner.Build(options, CancellationToken.None);
        Assert(plan.Actions.Where(a => a.HasWork).All(a => a.NewAlbumArtists == null), "album artists can be left as they are");
    }

    private static void LargeCoverTests()
    {
        string root = NewFolder("large-covers"), album = Path.Combine(root, "Artist", "Album"); Directory.CreateDirectory(album);
        string big = CopyTrack(album, "01.mp3", "Album", "Artist"), small = CopyTrack(album, "02.mp3", "Album", "Artist");
        TagLib.IPicture Picture(byte[] data) => new TagLib.Picture(new TagLib.ByteVector(data)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" };
        using (var file = TagLib.File.Create(big)) { file.Tag.Pictures = new[] { Picture(Artwork.Normalize(MakeCover(SkiaSharp.SKColors.Coral, 1400), 1600)) }; file.Save(); }
        using (var file = TagLib.File.Create(small)) { file.Tag.Pictures = new[] { Picture(Artwork.EmbedCover(MakeCover(SkiaSharp.SKColors.Coral, 500))) }; file.Save(); }
        var scan = Scanner.Scan(root, CancellationToken.None, useCache: false);
        var check = LibraryChecks.Run(scan.Albums);
        Assert(check.LargeCovers.Count == 1 && check.LargeCovers[0].Path == big && check.LargeCovers[0].LargestPicture == 1400, "the check finds covers larger than 600 px and their size");
        var options = Options(root, NewFolder("large-covers-backups")); options.EncoderPath = null; options.Shrink = true;
        var plan = LibraryPlanner.Build(options, CancellationToken.None);
        var work = plan.Actions.Where(a => a.HasWork).ToList();
        Assert(work.Count == 1 && work[0].Source == big && work[0].Shrink && !work[0].SinglePicture && work[0].Description == "Resize art", "Shrink covers only resizes the large one, on its own");
        LibraryRunner.Run(plan, CancellationToken.None);
        using (var file = TagLib.File.Create(big))
            Assert(file.Tag.Pictures.Length == 1 && Size(file.Tag.Pictures[0].Data.Data).Width == Artwork.EmbedSide && Artwork.IsJpeg(file.Tag.Pictures[0].Data.Data), "the large cover becomes a 600 × 600 JPEG");
        Assert(LibraryChecks.Run(Scanner.Scan(root, CancellationToken.None, useCache: false).Albums).LargeCovers.Count == 0, "afterwards no cover is over 600 px");

        string presets = Path.Combine(NewFolder("old-presets"), "organize-presets.txt");
        File.WriteAllText(presets, "Old artwork\tsquare,shrink");
        var loaded = OrganizePresets.LoadCustom(presets).Single();
        Assert(loaded.Options.SetEquals(new[] { OrganizePresets.Square, OrganizePresets.Shrink, OrganizePresets.SingleCover }), "a preset saved before the split keeps doing both");
        OrganizePresets.SaveCustom(new[] { new OrganizePreset { Name = "Only shrink", Options = new System.Collections.Generic.HashSet<string> { OrganizePresets.Shrink } } }, presets);
        Assert(OrganizePresets.LoadCustom(presets).Single().Options.SetEquals(new[] { OrganizePresets.Shrink }), "new presets keep shrink on its own");
    }

    private static async Task ArtistTagsGui()
    {
        string root = ArtistTagFolder("artist-tags-gui", out string solo, out string guest, out string tagged);
        BackupSettings.Root = NewFolder("artist-tags-gui-backups");
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover));
        window.Show();
        try
        {
            await window.ScanAsync();
            Click(Named<Button>(window, "Health")); await Settle();
            Assert(window.Health.Result.MissingAlbumArtists.Count == 2 && Named<Button>(window, "Fill album artists from the artist").IsEffectivelyVisible, "Health counts songs without an album artist and offers to fill them");
            Click(Named<Button>(window, "Fill album artists from the artist"));
            await Until(() => window.Organize != null && !window.Organize.IsWorking && window.Organize.CurrentPlan != null, "the fill is previewed in Organize", 20000);
            Assert(window.Organize.CurrentPlan.Actions.Count(a => a.AlbumArtistFilled) == 2 && Named<CheckBox>(window, "Fill missing album artists from the artist").IsChecked == true, "Organize previews only the album artist fill");

            Click(Named<Button>(window, "Artists"));
            Assert(Named<Button>(window, "Rename an artist").IsVisible, "Artists offers renaming an artist everywhere");
            var dialog = window.ShowRenameArtist(); await Settle();
            dialog.Fill(new[] { "benee", "Grouplove" }, "One Name", true); await Settle();
            Assert(dialog.Choices.Count(c => c.Checked) == 2 && Named<TextBlock>(dialog, "Rename summary").Text.Contains("“Grouplove” become “One Name”"), "several artists can be ticked to become one name: " + Named<TextBlock>(dialog, "Rename summary").Text);
            Shot(dialog, "rename-artists.png");
            dialog.Fill("benee", "BENEE ✿", true); await Settle();
            Assert(Named<TextBlock>(dialog, "Rename summary").Text.StartsWith("Changes 3 songs in ") && Named<TextBlock>(dialog, "Rename warning").Text.StartsWith("This is a big change"),
                "the rename says how many songs change and warns first: " + Named<TextBlock>(dialog, "Rename summary").Text);
            Assert(dialog.Count("BENEE", false).Songs == 3, "names match whatever their letter case");
            Click(ButtonStarting(dialog, "Preview changes to 3 songs"));
            await Until(() => !window.Organize.IsWorking && window.Organize.CurrentPlan != null && window.Organize.RenameFrom != null, "the rename is previewed in Organize", 20000);
            Assert(window.CurrentPage == MainWindow.Page.Organize && window.Organize.CurrentPlan.Actions.Count(a => a.HasWork) == 3 && Named<TextBlock>(window, "Artist rename").IsEffectivelyVisible,
                "Organize lists the rename, with a banner saying what it does");
            Shot(window, "rename-artist-preview.png");
            await window.Organize.ApplyAsync();
            using (var file = TagLib.File.Create(guest)) Assert(file.Tag.Performers.SequenceEqual(new[] { "BENEE ✿", "Grouplove" }), "applying renames the artist in the files");
            Assert(window.Organize.RenameFrom == null, "the rename is done once it is applied");
        }
        finally { window.Close(); }
    }
}
