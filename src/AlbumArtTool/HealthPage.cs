using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Library health: songs without a Title or Album Artist tag, songs with several or oversized embedded pictures,
    // albums with low-bitrate songs and duplicate copies. Works from the main window's scan and never changes a file.
    // The checks are listed on the left; the selected one shows what it found and its fix, which hands the work to
    // the page that makes it (Organize or YouTube Music).
    internal sealed class HealthPage : UserControl
    {
        private readonly Func<Album[]> currentAlbums;
        private readonly Action<ProcessStartInfo> openFolder;
        private readonly Func<Task> fillTitles, fixPictures, fillAlbumArtists, shrinkLarge;
        private readonly Func<LibraryCheckResult, Task> replaceLow;
        private readonly Func<bool> canFindDuplicates;
        // One fix per check; only the selected check's fix is shown.
        private readonly Button fill = Kit.Button("Fill titles from filenames…"), fillArtists = Kit.Button("Fill album artists from the artist…"),
            onePicture = Kit.Button("Keep one front cover per song…"), shrinkCovers = Kit.Button("Shrink covers to " + Artwork.EmbedSide + " px…"),
            replace = Kit.Button("Replace from YouTube Music…"), findDuplicates = Kit.Button("Find duplicates");
        private readonly ListBox list = new ListBox { Classes = { "grid" } };
        private readonly TextBlock summary = Kit.Label("", Kit.Small, cls: "muted");
        private readonly ComboBox threshold = new ComboBox { Width = 132 };
        private readonly Button refresh = Kit.IconButton(Icons.Refresh, "Check again", "ghost"), save = Kit.IconButton(Icons.Download, "Save report…", "ghost"),
            show = Kit.IconButton(Icons.Open, "Show selected", "ghost");
        private readonly TextBlock title = Kit.Heading("", 20), description = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Pill count = new Pill();
        private readonly PathIcon icon = Kit.Glyph(Icons.Health, 22);
        private readonly Panel fixHost = new Panel(), content = new Panel();
        private readonly Control optionHost;
        private readonly Border findings;
        private readonly EmptyState allGood = new EmptyState("Health check", withAction: false);
        private readonly List<HealthCheck> checks = new List<HealthCheck>();
        private HealthCheck selected;
        private readonly HealthCheck titles, albumArtists, pictures, large, bitrate, duplicates;
        internal DuplicatePanel Duplicates { get; private set; }
        internal bool IsScanning => Duplicates != null && Duplicates.IsScanning;
        internal LibraryCheckResult Result { get; private set; }
        /// <summary>How many checks found something to fix.</summary>
        internal int Attention => checks.Count(c => !c.Clean && c.Found > 0);
        /// <summary>Every finding, grouped under one header row per check (the order of the checks on the left).</summary>
        internal IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
        /// <summary>The bitrate measurement started by the last check (completed when there is nothing to measure).</summary>
        internal Task Verification { get; private set; } = Task.CompletedTask;
        private System.Threading.CancellationTokenSource measuring;
        private string measureStatus = "";

        internal sealed class Row
        {
            internal string Text, Detail, Path, Tip;
            internal bool Header, Folder;
        }

        private sealed class HealthCheck
        {
            internal string Name, About, Glyph;
            internal Button Item;
            internal TextBlock Status;
            internal Button Fix;
            internal readonly List<Row> Items = new List<Row>();
            internal int Found;
            internal bool Clean;
        }

        internal HealthPage(Func<Album[]> albums, Action<ProcessStartInfo> openFolder, Func<Task> fillTitles = null, Func<Task> fixPictures = null, Func<LibraryCheckResult, Task> replaceLow = null, Func<bool> canFindDuplicates = null, Func<Task> fillAlbumArtists = null, Func<Task> shrinkLarge = null)
        {
            currentAlbums = albums; this.openFolder = openFolder; this.fillTitles = fillTitles; this.fixPictures = fixPictures; this.replaceLow = replaceLow;
            this.fillAlbumArtists = fillAlbumArtists; this.shrinkLarge = shrinkLarge;
            this.canFindDuplicates = canFindDuplicates ?? (() => true);

            const string preview = " Nothing is written until you apply them.";
            fill.Named("Fill titles from filenames").Tip("Opens Organize with only \"Fill missing titles from filenames\" selected and previews the changes." + preview);
            fill.Click += async (s, e) => { if (this.fillTitles != null) await this.fillTitles(); };
            fillArtists.Named("Fill album artists from the artist").Tip("Opens Organize with only \"Fill missing album artists from the artist\" selected and previews the changes." + preview);
            fillArtists.Click += async (s, e) => { if (this.fillAlbumArtists != null) await this.fillAlbumArtists(); };
            onePicture.Named("Fix embedded pictures").Tip("Opens Organize with only \"One front cover per song\" selected (the best cover stays, in the front-cover slot; extra pictures are removed) and previews the changes." + preview);
            onePicture.Click += async (s, e) => { if (this.fixPictures != null) await this.fixPictures(); };
            shrinkCovers.Named("Shrink large covers").Tip("Opens Organize with only \"Shrink covers over " + Artwork.EmbedSide + " px\" selected and previews the changes: each larger cover becomes a " + Artwork.EmbedSide + " px JPEG." + preview);
            shrinkCovers.Click += async (s, e) => { if (this.shrinkLarge != null) await this.shrinkLarge(); };
            replace.Named("Replace from YouTube Music").Tip("Opens a page that finds each low-bitrate album on YouTube Music and replaces the low songs with fresh downloads (best free stream, saved as MP3 320 with your tags and cover). " +
                "Nothing is replaced until you check songs and click Replace; originals are backed up and can be restored.");
            replace.IsEnabled = false;
            replace.Click += async (s, e) => { if (this.replaceLow != null && Result != null) await this.replaceLow(Result); };
            findDuplicates.Named("Find duplicates").Tip("Compare every song's audio to find confirmed duplicate songs and album copies, plus possible copies that share tags and length. Nothing is deleted. Click again while it runs to stop.");
            findDuplicates.Click += async (s, e) => { if (IsScanning) Duplicates.Stop(); else await FindDuplicatesAsync(); };
            foreach (var b in new[] { fill, fillArtists, onePicture, shrinkCovers, replace, findDuplicates })
            {
                b.Classes.Add("primary"); b.Height = 36; b.Padding = new Thickness(16, 2); b.Margin = new Thickness(0, 0, 10, 0); b.HorizontalAlignment = HorizontalAlignment.Left; b.FontSize = 11;
                b.IsVisible = false; fixHost.Children.Add(b);
            }

            foreach (int kbps in LibraryChecks.BitrateChoices) threshold.Items.Add("< " + kbps + " kbps");
            threshold.SelectedIndex = Array.IndexOf(LibraryChecks.BitrateChoices, LibraryChecks.DefaultLowBitrate);
            threshold.Named("Low bitrate threshold").Tip("Albums with lossy songs (MP3, AAC, Ogg, Opus, WMA…) below this bitrate are listed. Lossless files are never listed.\n\n" +
                "The bitrate in a file's header can be wrong (common with VBR MP3s), so each candidate is measured with ffprobe/FFmpeg: the total size of its audio packets divided by their duration. " +
                "Songs whose real bitrate is high enough are dropped from the list. Without FFmpeg, header bitrates are shown and marked as unverified.");
            threshold.SelectionChanged += (s, e) => { if (Result != null) Check(); };
            threshold.Margin = new Thickness(0); threshold.MinHeight = 34; threshold.VerticalAlignment = VerticalAlignment.Center;
            var below = Kit.Label("Songs below", Kit.Small, cls: "muted"); below.Margin = new Thickness(4, 0, 8, 0);
            optionHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Children = { below, threshold } };

            titles = Add("Missing titles", Icons.Title, fill, "Songs without a Title tag show up in players under their filename. The fix fills each title from its filename, without the track number.");
            albumArtists = Add("No album artist", Icons.Artist, fillArtists, "Without an Album Artist tag, players may split an album or file it under a guest. The fix copies in the album's main artist; guests stay on the Artist tag.");
            pictures = Add("Extra pictures", Icons.Image, onePicture, "Songs with several embedded pictures are larger than they need to be, and players may show any of them as the cover. The fix keeps the best front cover.");
            large = Add("Large covers", Icons.Resize, shrinkCovers, "Covers bigger than " + Artwork.EmbedSide + " px make every song file larger without looking better on a player. The fix turns each one into a " + Artwork.EmbedSide + " px JPEG.");
            bitrate = Add("Low bitrate", Icons.Speed, replace, "Lossy songs below the threshold sound noticeably worse. Bitrates are measured with FFmpeg rather than read from the file header. The fix replaces them with fresh downloads from YouTube Music.");
            duplicates = Add("Duplicates", Icons.Duplicates, findDuplicates, "Copies of the same song or album, found by comparing the audio rather than names. Nothing is deleted here: remove confirmed copies with Organize › Remove confirmed duplicates.");

            // Left: the checks.
            refresh.Tip("Check again using the latest library scan (rescan first if you changed files outside the app).");
            refresh.Click += (s, e) => Check();
            refresh.Height = 30; refresh.Margin = new Thickness(0); refresh.Padding = new Thickness(8, 0);
            var caption = Kit.Caption("Checks"); caption.Margin = new Thickness(6, 0, 0, 0);
            var top = Kit.Grid("*,Auto", "Auto").Add(caption.Cell(0, 0), refresh.Cell(0, 1));
            top.Margin = new Thickness(0, 0, 0, 8);
            var items = new StackPanel { Spacing = 3 };
            foreach (var check in checks) items.Children.Add(check.Item);
            summary.Named("Tag check summary"); summary.TextWrapping = TextWrapping.Wrap; summary.TextTrimming = TextTrimming.None; summary.Margin = new Thickness(6, 10, 6, 6);
            save.Tip("Save every finding as a CSV report you can open in Excel.");
            save.Click += async (s, e) => await SaveAsync();
            save.HorizontalAlignment = HorizontalAlignment.Left; save.Height = 32; save.Margin = new Thickness(0);
            var left = Kit.Frost(Kit.Grid("*", "Auto,Auto,*,Auto,Auto").Add(top.Cell(0, 0), items.Cell(1, 0), summary.Cell(3, 0), save.Cell(4, 0)), new Thickness(10, 12, 10, 10));
            left.Margin = new Thickness(0, 0, 14, 0);

            // Right: the selected check.
            // The check's symbol glows amber in a small display window.
            icon.Foreground = new SolidColorBrush(Skin.Amber); icon.Effect = Skin.Glow(Skin.Amber, 8, 0.6);
            var tile = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(6), Child = icon, VerticalAlignment = VerticalAlignment.Top };
            tile.Brush(Border.BackgroundProperty, "Glass"); tile.Brush(Border.BoxShadowProperty, "GlassDepth");
            title.Named("Health check title");
            count.Margin = new Thickness(10, 2, 0, 0);
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(title); titleRow.Children.Add(count);
            description.TextWrapping = TextWrapping.Wrap; description.TextTrimming = TextTrimming.None; description.MaxWidth = 720; description.HorizontalAlignment = HorizontalAlignment.Left;
            description.Margin = new Thickness(0, 4, 0, 0);
            var words = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            words.Children.Add(titleRow); words.Children.Add(description);
            var head = Kit.Grid("Auto,*", "Auto").Add(tile.Cell(0, 0), words.Cell(0, 1));
            show.IsEnabled = false; show.Click += (s, e) => ShowSelected();
            show.Tip("Highlights the selected song in File Explorer, or opens the selected album's folder.");
            show.Height = 34; show.Margin = new Thickness(0);
            // The fix, its option, and Show selected; on a narrow window they wrap instead of running off the panel.
            var actions = new WrapPanel { Margin = new Thickness(60, 14, 0, 14) };
            optionHost.Margin = new Thickness(0, 0, 14, 0);
            foreach (var c in new Control[] { fixHost, optionHost, show }) { c.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(c); }
            list.Named("Tag check results");
            list.ItemTemplate = new FuncDataTemplate<Row>((row, scope) => new RowView(), true);
            list.SelectionChanged += (s, e) => show.IsEnabled = (list.SelectedItem as Row)?.Path != null;
            findings = Kit.Inset(list); findings.Padding = new Thickness(0);
            allGood.Margin = new Thickness(24);
            content.Children.Add(findings); content.Children.Add(allGood);
            var right = Kit.Frost(Kit.Grid("*", "Auto,Auto,*").Add(head.Cell(0, 0), actions.Cell(1, 0), content.Cell(2, 0)), new Thickness(20, 18, 20, 18));
            Content = Kit.Grid("290,*", "*").Add(left.Cell(0, 0), right.Cell(0, 1));
            duplicates.Status.Text = "Not checked"; duplicates.Status.Classes.Add("muted");
            Check();
        }

        private HealthCheck Add(string name, string glyph, Button fix, string about)
        {
            var check = new HealthCheck { Name = name, Glyph = glyph, Fix = fix, About = about };
            // Each check is a key with an indicator: red when it found something, green when all is well, orange while it runs.
            var led = Kit.Led(); led.Margin = new Thickness(4, 6, 12, 0); led.VerticalAlignment = VerticalAlignment.Top;
            var label = CapsText.Create(name); label.FontFamily = Skin.Key; label.FontWeight = FontWeight.SemiBold; label.FontSize = 11.5; label.LetterSpacing = 0.9;
            check.Status = Kit.Label("", Kit.Small);
            void Light() => Kit.SetLed(led, check.Status.Classes.Contains("warning") ? "red" : check.Status.Classes.Contains("positive") ? "green" : (check.Status.Text ?? "").EndsWith("…") ? "on" : null);
            check.Status.Classes.CollectionChanged += (s, e) => Light();
            check.Status.PropertyChanged += (s, e) => { if (e.Property == TextBlock.TextProperty) Light(); };
            var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(label); words.Children.Add(check.Status);
            check.Item = new Button { Content = Kit.Grid("Auto,*", "Auto").Add(led.Cell(0, 0), words.Cell(0, 1)), Classes = { "item" } }.Named("Health check " + name).Tip(about);
            check.Item.Click += (s, e) => Select(check);
            checks.Add(check);
            return check;
        }

        private Window Owner => TopLevel.GetTopLevel(this) as Window;
        internal void Stop() { measuring?.Cancel(); Duplicates?.Stop(); }
        internal bool ShowingDuplicates => Duplicates != null && Duplicates.IsVisible;

        private void Select(HealthCheck check)
        {
            selected = check;
            foreach (var c in checks) c.Item.Classes.Set("selected", c == check);
            Kit.SetIcon(icon, check.Glyph);
            title.Text = check.Name; description.Text = check.About;
            foreach (var b in fixHost.Children.OfType<Button>()) b.IsVisible = b == check.Fix && (check == duplicates || check.Found > 0);
            optionHost.IsVisible = check == bitrate;
            bool dupes = check == duplicates;
            if (Duplicates != null) Duplicates.IsVisible = dupes;
            // A check with nothing to list says so instead of showing a one-line table.
            findings.IsVisible = !dupes && !check.Clean;
            allGood.IsVisible = !findings.IsVisible && !(dupes && Duplicates != null);
            if (dupes) allGood.Show(Icons.Duplicates, "Not checked yet", "Click Find duplicates to compare every song's audio. It can take a few minutes on a large library; you can keep working meanwhile.");
            else allGood.Show(Icons.Check, "All good", check.Items.FirstOrDefault()?.Text ?? "Nothing to fix.");
            list.ItemsSource = findings.IsVisible ? check.Items : null;
            show.IsVisible = !dupes; show.IsEnabled = false;
            count.Set(dupes ? (Duplicates?.Result == null ? null : DuplicateCount()) : check.Found == 0 ? "All good" : check.Status.Text, check.Found == 0 && !(dupes && Duplicates?.Result == null) ? "Positive" : "Warning");
        }

        private string DuplicateCount()
        {
            var result = Duplicates.Result;
            int groups = result.Songs.Count + result.Albums.Count;
            return groups == 0 ? "All good" : Count(groups, "group");
        }

        /// <summary>Shows the duplicates check and scans (again) from the latest library scan.</summary>
        internal async Task FindDuplicatesAsync()
        {
            if (IsScanning) { Select(duplicates); return; }
            if (!canFindDuplicates()) { duplicates.Status.Text = "Wait for the current scan or task to finish"; Select(duplicates); return; }
            if (Duplicates == null)
            {
                Duplicates = new DuplicatePanel(currentAlbums(), currentAlbums, openFolder);
                Duplicates.Embed();
                Duplicates.StateChanged += UpdateDuplicatesCard;
                content.Children.Add(Duplicates);
            }
            Select(duplicates);
            try { await Duplicates.ScanAsync(); }
            finally { UpdateDuplicatesCard(); }
        }

        private void UpdateDuplicatesCard()
        {
            var result = Duplicates?.Result;
            findDuplicates.Content = IsScanning ? "Stop scan" : result == null ? "Find duplicates" : "Scan again";
            var status = duplicates.Status;
            status.Classes.Set("muted", false); status.Classes.Set("positive", false); status.Classes.Set("warning", false);
            if (IsScanning) { status.Text = "Checking…"; status.Classes.Set("muted", true); }
            else if (result == null) { status.Text = "Not checked"; status.Classes.Set("muted", true); duplicates.Found = 0; }
            else
            {
                int confirmed = result.Songs.Count(g => g.Confirmed) + result.Albums.Count(g => g.Confirmed);
                int possible = result.Songs.Count(g => !g.Confirmed) + result.Albums.Count(g => !g.Confirmed);
                duplicates.Found = confirmed + possible;
                status.Text = duplicates.Found == 0 ? "All good" : confirmed + " confirmed · " + possible + " possible";
                status.Classes.Set(duplicates.Found == 0 ? "positive" : "warning", true);
            }
            if (selected == duplicates) Select(duplicates);
        }

        private sealed class RowView : Border
        {
            private readonly TextBlock text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock where = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Classes = { "muted" } };
            private readonly TextBlock detail = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Classes = { "muted" } };
            internal RowView()
            {
                Height = 34; Padding = new Thickness(14, 0, 12, 0); Background = Brushes.Transparent;
                Child = Kit.Grid("*,0.8*,Auto", "*").Add(text.Cell(0, 0), where.Cell(0, 1), detail.Cell(0, 2));
                DataContextChanged += (s, e) =>
                {
                    if (!(DataContext is Row row)) return;
                    // Songs by name, with the folders they are in; albums by name. The full path is in the tooltip.
                    bool song = row.Path != null && !row.Folder;
                    text.Text = song ? System.IO.Path.GetFileName(row.Path) : row.Text;
                    where.Text = row.Path == null ? "" : Folders(song ? System.IO.Path.GetDirectoryName(row.Path) : row.Path);
                    Grid.SetColumnSpan(text, row.Path == null ? 2 : 1);
                    detail.Text = row.Detail;
                    text.FontWeight = row.Header ? FontWeight.Bold : FontWeight.Normal;
                    ToolTip.SetTip(this, row.Tip);
                };
            }

            private static string Folders(string path)
            {
                if (string.IsNullOrEmpty(path)) return "";
                var parts = path.TrimEnd('/', '\\').Split('/', '\\').Where(p => p.Length > 0).ToArray();
                return string.Join(" › ", parts.Skip(Math.Max(0, parts.Length - 2)));
            }
        }

        internal void Check()
        {
            int limit = LibraryChecks.BitrateChoices[Math.Max(0, threshold.SelectedIndex)];
            Result = LibraryChecks.Run(currentAlbums(), limit);
            measuring?.Cancel();
            measureStatus = Result.LowBitrate.Count > 0 ? "measuring " + Result.LowBitrate.Count + " songs…" : "";
            ShowRows();
            Verification = Result.LowBitrate.Count > 0 ? VerifyAsync(Result) : Task.CompletedTask;
        }

        // Header bitrates are shown at once; the real bitrates replace them when measured (in the background, cancelled by a new check).
        private async Task VerifyAsync(LibraryCheckResult result)
        {
            var source = measuring = new System.Threading.CancellationTokenSource();
            var progress = new Throttled<ProgressInfo>(p => { if (ReferenceEquals(Result, result) && !source.IsCancellationRequested) { measureStatus = "measuring " + p.Completed + "/" + p.Total + "…"; summary.Text = Summary(); bitrate.Status.Text = BitrateStatus(); } });
            var candidates = result.LowBitrate.ToArray();
            LibraryChecks.Measurements measured;
            try { measured = await Task.Run(() => LibraryChecks.Measure(candidates, source.Token, progress)); }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(Result, result) || source.IsCancellationRequested) return;
            LibraryChecks.Apply(result, measured);
            measureStatus = result.Verified ? "" : "header bitrates (set up FFmpeg in Settings to verify)";
            ShowRows();
        }

        private void ShowRows()
        {
            int limit = Result.LowBitrateLimit;
            var rows = new List<Row>();
            void Section(HealthCheck check, string heading, int found, IEnumerable<Row> items, string clean)
            {
                rows.Add(new Row { Header = true, Text = heading, Detail = found.ToString() });
                check.Items.Clear(); check.Found = found;
                check.Items.AddRange(items);
                check.Clean = check.Items.Count == 0;
                if (check.Clean) check.Items.Add(new Row { Text = clean });
                rows.AddRange(check.Items);
            }
            Section(titles, "Songs missing a Title tag", Result.MissingTitles.Count, Result.MissingTitles.Select(t =>
                new Row { Text = t.Path, Detail = Describe(t), Path = t.Path, Tip = t.Path + "\nShown as: " + Path.GetFileNameWithoutExtension(t.Path) + "\nFilled title would be: " + LibraryChecks.SuggestedTitle(t.Path) }), "Every song has a Title tag.");
            Section(albumArtists, "Songs without an Album Artist tag", Result.MissingAlbumArtists.Count, Result.MissingAlbumArtists.Select(t =>
                new Row { Text = t.Path, Detail = Describe(t), Path = t.Path, Tip = t.Path + "\nArtist: " + t.ArtistText + "\nNo Album Artist tag: players may split this album or list it under a guest artist." }), "Every song with an artist also has an album artist.");
            Section(pictures, "Songs with more than one embedded picture", Result.MultiplePictures.Count, Result.MultiplePictures.Select(t =>
                new Row { Text = t.Path, Detail = t.PictureSummary, Path = t.Path, Tip = t.Path + "\n" + t.PictureSummary + (t.CoverPictures > 1 ? "\nSeveral front/unlabeled covers: players may show any of them." : "") }), "No song has more than one embedded picture.");
            Section(large, "Songs with a cover larger than " + Artwork.EmbedSide + " px", Result.LargeCovers.Count, Result.LargeCovers.Select(t =>
                new Row { Text = t.Path, Detail = t.LargestPicture + " px", Path = t.Path, Tip = t.Path + "\nLargest embedded picture: " + t.LargestPicture + " px on its longest side. Shrinking makes it a " + Artwork.EmbedSide + " px JPEG." }), "Every embedded cover is " + Artwork.EmbedSide + " px or smaller.");
            var albums = Result.LowBitrateAlbums();
            string basis = Result.Verified ? "measured" : "header";
            var low = new List<Row>();
            foreach (var album in albums)
            {
                string name = string.Join(" — ", new[] { album.Artist, album.Album }.Where(v => !string.IsNullOrWhiteSpace(v)));
                string formats = string.Join("/", album.Tracks.Select(LibraryChecks.Format).Distinct());
                var songs = album.Tracks.Select(t => "  " + Result.Bitrate(t) + " kbps" + (Result.Measured.TryGetValue(t.Path, out int m) && Math.Abs(m - t.Bitrate) > 2 ? " (header said " + t.Bitrate + ")" : "") +
                    "  " + System.IO.Path.GetFileName(t.Path));
                low.Add(new Row { Text = name.Length > 0 ? name : album.Folder, Detail = Count(album.Tracks.Count) + " · " + album.Range + " " + basis + " · " + formats,
                    Path = album.Folder, Folder = true, Tip = album.Folder + "\n" + string.Join("\n", songs.Take(40)) + (album.Tracks.Count > 40 ? "\n  …" : "") });
            }
            if (Result.HeaderWrong.Count > 0)
                low.Add(new Row { Text = Count(Result.HeaderWrong.Count) + " not listed: their headers reported a low bitrate, but they measure at least " + limit + " kbps", Tip =
                    string.Join("\n", Result.HeaderWrong.Take(40).Select(t => "header " + t.Bitrate + " kbps → measured " + Result.Bitrate(t) + " kbps  " + t.Path)) + (Result.HeaderWrong.Count > 40 ? "\n…" : "") });
            if (Result.MeasureIssues.Count > 0)
                low.Add(new Row { Text = Count(Result.MeasureIssues.Count) + " could not be measured and show their header bitrate", Tip = string.Join("\n", Result.MeasureIssues.Take(40)) });
            Section(bitrate, "Albums with lossy songs below " + limit + " kbps", albums.Count, low, Result.Verified ? "No lossy song is really below " + limit + " kbps." : "No lossy song is below " + limit + " kbps.");
            Rows = rows;
            summary.Text = Summary();

            SetStatus(titles, Result.MissingTitles.Count, "song");
            SetStatus(albumArtists, Result.MissingAlbumArtists.Count, "song");
            SetStatus(pictures, Result.MultiplePictures.Count, "song");
            SetStatus(large, Result.LargeCovers.Count, "song");
            SetStatus(bitrate, albums.Count, "album");
            bitrate.Status.Text = BitrateStatus();
            // Offered once the bitrates are measured (or known to be header-only), so the list it opens is final.
            replace.IsEnabled = replaceLow != null && albums.Count > 0 && (measureStatus.Length == 0 || measureStatus.StartsWith("header", StringComparison.Ordinal));
            fill.IsEnabled = fillTitles != null && Result.MissingTitles.Count > 0;
            onePicture.IsEnabled = fixPictures != null && Result.MultiplePictures.Count > 0;
            fillArtists.IsEnabled = fillAlbumArtists != null && Result.MissingAlbumArtists.Count > 0;
            shrinkCovers.IsEnabled = shrinkLarge != null && Result.LargeCovers.Count > 0;
            save.IsEnabled = Result.MissingTitles.Count + Result.MissingAlbumArtists.Count + Result.LargeCovers.Count + Result.MultiplePictures.Count + Result.LowBitrate.Count > 0;
            // The first time, open the first check that found something; afterwards keep the one being looked at.
            Select(selected ?? checks.FirstOrDefault(c => c != duplicates && c.Found > 0) ?? titles);
        }

        private string BitrateStatus()
        {
            int albums = bitrate.Found;
            string state = measureStatus.StartsWith("measuring", StringComparison.Ordinal) ? " · measuring…" : "";
            return (albums == 0 ? "All good" : Count(albums, "album")) + state;
        }

        private static void SetStatus(HealthCheck check, int found, string noun)
        {
            check.Status.Text = found == 0 ? "All good" : Count(found, noun);
            check.Status.Classes.Set("positive", found == 0); check.Status.Classes.Set("warning", found > 0); check.Status.Classes.Set("muted", false);
        }

        private string Summary() => Result.MissingTitles.Count + " missing a Title tag · " + Result.MultiplePictures.Count + " with several embedded pictures (" +
            Result.MultiplePictures.Count(t => t.CoverPictures > 1) + " with several covers) · " + Count(Result.LowBitrateAlbums().Count, "album") + " below " + Result.LowBitrateLimit + " kbps" +
            (measureStatus.Length > 0 ? " (" + measureStatus + ")" : Result.Verified ? " (measured)" : "") + " · " + Result.TracksChecked + " songs checked";

        private static string Count(int n, string noun = "song") => n + " " + noun + (n == 1 ? "" : "s");
        private static string Describe(Track t) => string.Join(" · ", new[] { t.ArtistText, t.AlbumTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

        /// <summary>Selects a row of <see cref="Rows"/>, opening the check it belongs to.</summary>
        internal void Select(int index)
        {
            if (index < 0 || index >= Rows.Count) return;
            var row = Rows[index];
            var check = checks.FirstOrDefault(c => c.Items.Contains(row));
            if (check != null && check != selected) Select(check);
            list.SelectedItem = row;
        }

        private void ShowSelected()
        {
            if (!(list.SelectedItem is Row { Path: string path } row)) return;
            string arguments = row.Folder ? Updates.UpdateInstaller.QuoteArgument(path) : "/select," + Updates.UpdateInstaller.QuoteArgument(path);
            try { openFolder(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
            catch (Exception e) { _ = Dialogs.ShowMessage(Owner, "Could not open folder", e.Message); }
        }

        private async Task SaveAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save library check report", SuggestedFileName = "music-library-check.csv", DefaultExtension = "csv",
              FileTypeChoices = new[] { new FilePickerFileType("CSV report") { Patterns = new[] { "*.csv" } } } });
            string path = file?.TryGetLocalPath();
            if (path == null) return;
            try { File.WriteAllText(path, LibraryChecks.Csv(Result), new UTF8Encoding(true)); }
            catch (Exception e) { await Dialogs.ShowMessage(Owner, "Could not save report", e.Message); }
        }
    }
}
