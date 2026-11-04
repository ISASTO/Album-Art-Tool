using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Add music: search YouTube Music (music.youtube.com only), open an artist or a release, and add albums or songs.
    // Every addition starts downloading at once into the New music folder, as MP3 320 with tags and a cover; music
    // videos are never used unless you ask. Tidy New music with Organize, then add it to the library.
    internal sealed class AddMusicPage : UserControl
    {
        private readonly IYouTubeMusic music;
        private readonly Func<string> libraryRoot;
        private readonly Func<bool> canMerge;
        private readonly Func<string, Task> organize;
        private readonly Func<Task> refreshLibrary;
        private readonly Action openSettings;
        private readonly Action<ProcessStartInfo> openFolder;
        internal readonly DownloadQueue Queue;

        private readonly TextBox query = new TextBox { Watermark = "Search for an artist, album or song" };
        private readonly Button searchButton = Kit.Button("Search", "primary");
        private readonly Button back = Kit.IconButton(Icons.Back, "Back", "ghost");
        private readonly ContentControl view = new ContentControl();
        private readonly TextBlock browseStatus = Kit.Label("", Kit.Small, cls: "muted");
        private readonly List<Func<Control>> history = new List<Func<Control>>();
        private readonly EmptyState home = new EmptyState("Add music", withAction: false);
        private readonly Border toolsBanner;
        private readonly TextBlock toolsText = Kit.Label("", Kit.Small);

        // Queue panel
        private readonly ObservableCollection<DownloadGroup> groups = new ObservableCollection<DownloadGroup>();
        private readonly ListBox queueList = new ListBox { Classes = { "grid" } };
        private readonly EmptyState queueEmpty = new EmptyState("Download queue", withAction: false);
        private readonly TextBlock waitingText = Kit.Label("", Kit.Body, true), folderText = Kit.Label("", Kit.Small, cls: "muted"), mergeText = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Button tidy = Kit.IconButton(Icons.Organize, "Tidy up in Organize…"), merge = Kit.Button("Add to library", "primary"),
            undoMerge = Kit.IconButton(Icons.Undo, "Undo last add", "ghost"), openNew = Kit.IconButton(Icons.Folder, "Open", "ghost"),
            changeNew = Kit.Button("Change…", "ghost"), clearFinished = Kit.Button("Clear finished", "link");
        private CancellationTokenSource searching;
        private readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<Bitmap>> loadingThumbs = new Dictionary<string, Task<Bitmap>>(StringComparer.Ordinal);
        private readonly SemaphoreSlim thumbGate = new SemaphoreSlim(6);
        private bool merging;

        /// <summary>The last search's results, the open artist and the open release (for tests).</summary>
        internal (List<YtmArtist> Artists, List<YtmAlbum> Albums, List<YtmTrack> Songs) Results { get; private set; }
        internal YtmArtist Artist { get; private set; }
        internal YtmAlbum Release { get; private set; }
        internal Task Loading { get; private set; } = Task.CompletedTask;
        internal string Filter { get; private set; } = "All";
        internal bool IsWorking => merging;
        internal IEnumerable<Control> Cards => Kit.Descendants(view).OfType<ReleaseCard>();

        internal AddMusicPage(IYouTubeMusic music, DownloadQueue queue, Func<string> libraryRoot, Func<bool> canMerge, Func<string, Task> organize, Func<Task> refreshLibrary,
            Action openSettings, Action<ProcessStartInfo> openFolder)
        {
            this.music = music; Queue = queue; this.libraryRoot = libraryRoot; this.canMerge = canMerge; this.organize = organize; this.refreshLibrary = refreshLibrary;
            this.openSettings = openSettings; this.openFolder = openFolder;

            // Search bar
            query.Named("Search YouTube Music").Tip("Type an artist, album or song and press Enter. Only music.youtube.com is searched, never regular YouTube.");
            query.FontSize = 14; query.MinHeight = 38; query.Margin = new Thickness(0, 0, 8, 0);
            query.KeyDown += (s, e) => { if (e.Key == Key.Enter) { _ = SearchAsync(query.Text); e.Handled = true; } };
            searchButton.Height = 38; searchButton.Padding = new Thickness(18, 2); searchButton.Margin = new Thickness(0);
            searchButton.Click += async (s, e) => await SearchAsync(query.Text);
            searchButton.Tip("Search YouTube Music for artists, albums and songs.");
            back.Height = 38; back.Margin = new Thickness(0, 0, 6, 0); back.IsVisible = false;
            back.Named("Back").Tip("Back to the previous results.");
            back.Click += (s, e) => GoBack();
            var bar = Kit.Grid("Auto,*,Auto", "Auto").Add(back.Cell(0, 0), query.Cell(0, 1), searchButton.Cell(0, 2));
            bar.Margin = new Thickness(0, 0, 0, 6);

            toolsText.TextWrapping = TextWrapping.Wrap; toolsText.TextTrimming = TextTrimming.None;
            var setup = Kit.Button("Open Settings", "link"); setup.Click += (s, e) => openSettings?.Invoke();
            setup.Tip("FFmpeg and yt-dlp are free helper programs, downloaded once from their official GitHub releases with checksums verified.");
            toolsBanner = new Border { Child = Kit.Grid("*,Auto", "Auto").Add(toolsText.Cell(0, 0), setup.Cell(0, 1)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 8) };
            toolsBanner.Brush(Border.BackgroundProperty, "BadgeWarning");

            home.Show(Icons.Search, "Find music on YouTube Music", "Search for an artist to see their albums, singles and EPs, or search for an album or a song. Anything you add starts downloading right away.");
            browseStatus.Named("Add music status"); browseStatus.Margin = new Thickness(2, 0, 0, 6);
            view.Content = home;
            var left = Kit.Grid("*", "Auto,Auto,Auto,*").Add(bar.Cell(0, 0), toolsBanner.Cell(1, 0), browseStatus.Cell(2, 0), view.Cell(3, 0));
            left.Margin = new Thickness(0, 0, 16, 0);

            // Queue
            queueList.Named("Download queue");
            queueList.ItemsSource = groups;
            queueList.ItemTemplate = new FuncDataTemplate<DownloadGroup>((g, scope) => new QueueRow(this), true);
            ScrollViewer.SetHorizontalScrollBarVisibility(queueList, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            queueEmpty.Show(Icons.Download, "Nothing downloading", "Albums and songs you add appear here and download one after another.");
            queueEmpty.MaxWidth = 260;
            var queueHost = new Panel(); queueHost.Children.Add(queueList); queueHost.Children.Add(queueEmpty);
            var queueTitle = Kit.Grid("*,Auto", "Auto").Add(Kit.Caption("Downloads").Cell(0, 0), clearFinished.Cell(0, 1));
            clearFinished.Tip("Remove finished and cancelled items from this list. Their songs stay in New music.");
            clearFinished.Click += (s, e) => { Queue.ClearFinished(); SyncQueue(); };
            waitingText.Named("Songs in New music");
            folderText.TextWrapping = TextWrapping.NoWrap;
            openNew.Tip("Open the New music folder in File Explorer."); openNew.Click += (s, e) => OpenNew();
            changeNew.Tip("Choose where new downloads are saved before they join your library. It must be outside the music folder.");
            changeNew.Click += async (s, e) => await ChangeFolderAsync();
            tidy.Named("Tidy up in Organize").Tip("Open Organize on the New music folder, so you can run your preset (artwork, tags, file names…) before the songs join the library. Every change is previewed first.");
            tidy.Click += async (s, e) => { if (this.organize != null) await this.organize(AddMusic.Folder); };
            merge.Named("Add to library").Tip(MergeTip);
            merge.Click += async (s, e) => await MergeAsync();
            undoMerge.Named("Undo last add").Tip("Move the songs from the last Add to library back into New music (songs changed since are left in place).");
            undoMerge.Click += async (s, e) => await UndoMergeAsync();
            foreach (var b in new[] { tidy, merge }) { b.Height = 36; b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 6, 0, 0); }
            mergeText.TextWrapping = TextWrapping.Wrap; mergeText.TextTrimming = TextTrimming.None; mergeText.Named("Add to library status");
            var folderRow = Kit.Grid("*,Auto,Auto", "Auto").Add(folderText.Cell(0, 0), openNew.Cell(0, 1), changeNew.Cell(0, 2));
            var footer = new StackPanel { Spacing = 2 };
            footer.Children.Add(Kit.Caption("New music"));
            footer.Children.Add(waitingText); footer.Children.Add(folderRow); footer.Children.Add(tidy); footer.Children.Add(merge); footer.Children.Add(mergeText); footer.Children.Add(undoMerge);
            var footerCard = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 8, 0, 0) };
            footerCard.Brush(Border.BorderBrushProperty, "Border");
            var right = Kit.Frost(Kit.Grid("*", "Auto,*,Auto").Add(queueTitle.Cell(0, 0), queueHost.Cell(1, 0), footerCard.Cell(2, 0)), new Thickness(14, 12, 14, 12), 14);
            Content = Kit.Grid("*,330", "*").Add(left.Cell(0, 0), right.Cell(0, 1));

            Queue.Changed += SyncQueue;
            PreviewPlayer.Changed += OnPreviewChanged;
            DetachedFromVisualTree += (s, e) => PreviewPlayer.Stop();
            SyncQueue(); UpdateTools();
        }

        internal void Refresh() { UpdateTools(); SyncQueue(); }

        private void UpdateTools()
        {
            var missing = new List<string>();
            if (Encoder.Find() == null) missing.Add("FFmpeg");
            if (!DownloadsInjected && YtDlp.Find() == null) missing.Add("yt-dlp");
            toolsBanner.IsVisible = missing.Count > 0;
            toolsText.Text = "Downloading needs " + string.Join(" and ", missing) + ". Browsing and searching work already.";
        }

        internal static bool DownloadsInjected;
        private const string MergeTip = "Move everything in New music into your music folder, keeping the Artist \\ Album folders. Nothing in the library is overwritten: a different file with the same name gets \" (2)\", an identical one is left out. You can undo this.";

        // ---- Browsing ----

        private void Show(Func<Control> build, bool remember = true)
        {
            if (remember) history.Add(build);
            view.Content = build();
            back.IsVisible = history.Count > 1;
        }

        private void GoBack()
        {
            if (history.Count < 2) return;
            history.RemoveAt(history.Count - 1);
            view.Content = history[history.Count - 1]();
            back.IsVisible = history.Count > 1;
            browseStatus.Text = "";
        }

        internal async Task SearchAsync(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            query.Text = text;
            searching?.Cancel(); var cancel = searching = new CancellationTokenSource(); var token = cancel.Token;
            browseStatus.Text = "Searching YouTube Music for “" + text + "”…";
            var task = Task.WhenAll(Safe(music.SearchArtistsAsync(text, token)), Safe(music.SearchAlbumsAsync(text, token)), Safe(music.SearchSongsAsync(text, token)));
            Loading = task;
            try
            {
                var all = await task;
                if (token.IsCancellationRequested) return;
                var found = (Artists: ((List<YtmArtist>)all[0]) ?? new List<YtmArtist>(), Albums: ((List<YtmAlbum>)all[1]) ?? new List<YtmAlbum>(), Songs: (((List<YtmTrack>)all[2]) ?? new List<YtmTrack>()).Where(s => !s.MusicVideo).ToList());
                Results = found;
                history.Clear();
                Show(() => BuildResults(found));
                browseStatus.Text = found.Artists.Count + found.Albums.Count + found.Songs.Count == 0 ? "Nothing found for “" + text + "” on YouTube Music." : "";
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { browseStatus.Text = "YouTube Music could not be searched: " + e.Message; }
        }

        // Each search kind fails on its own (for example a rate limit), without hiding the others.
        private static async Task<object> Safe<T>(Task<T> task) where T : class
        {
            try { return await task; }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException || e is System.Text.Json.JsonException || e is InvalidDataException || e is TaskCanceledException) { return null; }
        }

        private Control BuildResults((List<YtmArtist> Artists, List<YtmAlbum> Albums, List<YtmTrack> Songs) found)
        {
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 12, 12) };
            if (found.Artists.Count > 0)
            {
                stack.Children.Add(Heading("Artists"));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var artist in found.Artists.Take(8)) row.Children.Add(new ArtistTile(this, artist));
                stack.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            }
            if (found.Albums.Count > 0)
            {
                stack.Children.Add(Heading("Albums, singles and EPs"));
                stack.Children.Add(Releases(found.Albums.Take(12)));
            }
            if (found.Songs.Count > 0)
            {
                stack.Children.Add(Heading("Songs"));
                var songs = new StackPanel { Spacing = 2 };
                foreach (var song in found.Songs.Take(10)) songs.Children.Add(new TrackRow(this, null, song, false));
                stack.Children.Add(songs);
            }
            if (stack.Children.Count == 0)
            {
                var none = new EmptyState("No results", withAction: false);
                none.Show(Icons.Search, "Nothing found", "Try the artist's name on its own, or check the spelling.");
                return none;
            }
            return Scroll(stack);
        }

        private static ScrollViewer Scroll(Control content) => new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        private static TextBlock Heading(string text)
        {
            var heading = Kit.Label(text, 15, true); heading.Margin = new Thickness(2, 12, 0, 4);
            return heading;
        }

        private WrapPanel Releases(IEnumerable<YtmAlbum> releases)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var release in releases) wrap.Children.Add(new ReleaseCard(this, release));
            return wrap;
        }

        internal async Task OpenArtistAsync(YtmArtist artist)
        {
            PreviewPlayer.Stop();
            Artist = artist; Filter = "All";
            browseStatus.Text = "Loading " + artist.Name + "…";
            var task = music.LoadArtistAsync(artist, CancellationToken.None);
            Loading = task;
            try { await task; browseStatus.Text = ""; }
            catch (Exception e) when (!(e is OperationCanceledException)) { browseStatus.Text = "This artist page could not be loaded: " + e.Message; return; }
            Show(() => BuildArtist(artist));
        }

        private Control BuildArtist(YtmArtist artist)
        {
            var picture = new Border { Width = 112, Height = 112, CornerRadius = new CornerRadius(56), ClipToBounds = true, Margin = new Thickness(0, 0, 18, 0) };
            picture.Brush(Border.BackgroundProperty, "CoverPlaceholder");
            var image = new Image { Stretch = Stretch.UniformToFill }; picture.Child = image;
            _ = SetThumbAsync(image, artist.ThumbnailUrl);
            var releases = artist.Releases ?? new List<YtmAlbum>();
            int albums = releases.Count(r => r.Kind == "Album"), singles = releases.Count - albums;
            var name = Kit.Label(artist.Name, 26, true).Named("Artist name");
            var counts = Kit.Label(albums + (albums == 1 ? " album · " : " albums · ") + singles + (singles == 1 ? " single or EP" : " singles and EPs"), Kit.Body, cls: "muted");
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 14, 0, 10) };
            var addShown = Kit.IconButton(Icons.Add, "Add all shown", "ghost").Named("Add all shown");
            addShown.Tip("Add every release shown below to the download queue.");
            void Fill(string filter)
            {
                Filter = filter;
                wrap.Children.Clear();
                foreach (var release in Shown(releases, filter)) wrap.Children.Add(new ReleaseCard(this, release));
                foreach (var chip in chips.Children.OfType<Button>()) chip.Classes.Set("selected", (string)chip.Tag == filter);
                addShown.IsEnabled = wrap.Children.Count > 0;
            }
            foreach (var (label, filter) in new[] { ("All releases (" + releases.Count + ")", "All"), ("Albums (" + albums + ")", "Album"), ("Singles & EPs (" + singles + ")", "Single") })
            {
                var chip = Kit.Button(label, "chip"); chip.Tag = filter; chip.Named("Show " + filter);
                chip.Click += (s, e) => Fill(filter);
                chips.Children.Add(chip);
            }
            addShown.Click += (s, e) => { foreach (var release in Shown(releases, Filter)) AddRelease(release); };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(Kit.Caption("Artist")); names.Children.Add(name); names.Children.Add(counts);
            var header = Kit.Grid("Auto,*", "Auto").Add(picture.Cell(0, 0), names.Cell(0, 1));
            var chipRow = Kit.Grid("*,Auto", "Auto").Add(chips.Cell(0, 0), addShown.Cell(0, 1));
            var stack = new StackPanel { Margin = new Thickness(0, 4, 12, 12) };
            stack.Children.Add(header); stack.Children.Add(chipRow); stack.Children.Add(wrap);
            if (releases.Count == 0) stack.Children.Add(Kit.Label("YouTube Music lists no albums, singles or EPs for this artist.", Kit.Body, cls: "muted"));
            Fill(Filter);
            return Scroll(stack);
        }

        internal static IEnumerable<YtmAlbum> Shown(IEnumerable<YtmAlbum> releases, string filter) =>
            releases.Where(r => filter == "All" || (filter == "Album" ? r.Kind == "Album" : r.Kind != "Album"));

        internal void ShowFilter(string filter) { if (Artist != null) { Filter = filter; Show(() => BuildArtist(Artist), remember: false); } }

        internal async Task OpenReleaseAsync(YtmAlbum release)
        {
            PreviewPlayer.Stop();
            Release = release;
            browseStatus.Text = "Loading " + release.Title + "…";
            var task = PrepareAsync(release, CancellationToken.None);
            Loading = task;
            try { await task; browseStatus.Text = ""; }
            catch (Exception e) when (!(e is OperationCanceledException)) { browseStatus.Text = "This release could not be loaded: " + e.Message; return; }
            Show(() => BuildRelease(release));
        }

        // The track list, with music-video entries swapped for the artist's audio uploads where they exist.
        private async Task PrepareAsync(YtmAlbum release, CancellationToken token)
        {
            await music.LoadAlbumAsync(release, token);
            await AlbumUpgrade.ResolveAudioAsync(music, release, release.Artist, token);
        }

        private Control BuildRelease(YtmAlbum release)
        {
            var cover = new Border { Width = 176, Height = 176, CornerRadius = new CornerRadius(12), ClipToBounds = true, Margin = new Thickness(0, 0, 20, 0) };
            cover.Brush(Border.BackgroundProperty, "CoverPlaceholder");
            var image = new Image { Stretch = Stretch.UniformToFill }; cover.Child = image;
            _ = SetThumbAsync(image, release.ThumbnailUrl);
            var tracks = release.Tracks ?? new List<YtmTrack>();
            var length = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration.TotalSeconds));
            var title = Kit.Label(release.Title, 24, true).Named("Release title"); title.TextWrapping = TextWrapping.Wrap; title.MaxLines = 2;
            var detail = Kit.Label(string.Join(" · ", new[] { release.Artist, release.Year, tracks.Count + (tracks.Count == 1 ? " song" : " songs"), (int)length.TotalMinutes + " min" }.Where(p => !string.IsNullOrWhiteSpace(p))), Kit.Body, cls: "muted");
            int videoOnly = tracks.Count(t => t.VideoOnly);
            var note = Kit.Label(videoOnly == 0 ? "Every song has an audio-only version." : videoOnly + (videoOnly == 1 ? " song is" : " songs are") + " only on YouTube Music as a music video, which can have intros, outros or silence. They are left out unless you tick them.",
                Kit.Small, cls: videoOnly == 0 ? "positive" : "warning");
            note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None; note.Named("Audio availability");
            var rows = tracks.Select(t => new TrackRow(this, release, t, true)).ToList();
            var addAll = Kit.IconButton(Icons.Add, "Add " + Kind(release).ToLowerInvariant(), "primary").Named("Add this release");
            var addChecked = Kit.Button("Add checked songs").Named("Add checked songs");
            void Update()
            {
                int count = rows.Count(r => r.Checked);
                addChecked.Content = "Add " + count + " checked " + (count == 1 ? "song" : "songs");
                addChecked.IsEnabled = count > 0;
                bool queued = Queue.Contains(release.BrowseId);
                addAll.IsEnabled = !queued && tracks.Any(t => !t.VideoOnly);
                addAll.SetText(queued ? "Added" : "Add " + Kind(release).ToLowerInvariant());
            }
            foreach (var row in rows) row.CheckedChanged += Update;
            addAll.Tip("Download every song with an audio-only version (" + tracks.Count(t => !t.VideoOnly) + ") into New music, starting now.");
            addChecked.Tip("Download only the ticked songs. Songs that exist only as a music video are downloaded from the video if you tick them.");
            addAll.Click += (s, e) => { AddRelease(release); Update(); };
            addChecked.Click += (s, e) => { AddTracks(release, rows.Where(r => r.Checked).Select(r => r.Track).ToList()); foreach (var r in rows) r.Checked = false; Update(); };
            var openSite = Kit.Button("Open on YouTube Music ↗", "link").Tip("Open this release on music.youtube.com in your browser.");
            openSite.Click += (s, e) => { try { openFolder(new ProcessStartInfo(release.Url) { UseShellExecute = true }); } catch (Exception) { } };
            foreach (var b in new[] { addAll, addChecked }) { b.Height = 36; b.Padding = new Thickness(16, 2); }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(addAll); buttons.Children.Add(addChecked); buttons.Children.Add(openSite);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 2 };
            names.Children.Add(Kit.Caption(Kind(release))); names.Children.Add(title); names.Children.Add(detail); names.Children.Add(note); names.Children.Add(buttons);
            var header = Kit.Grid("Auto,*", "Auto").Add(cover.Cell(0, 0), names.Cell(0, 1));
            header.Margin = new Thickness(0, 4, 0, 14);
            var list = new StackPanel { Spacing = 0 };
            foreach (var row in rows) list.Children.Add(row);
            var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
            stack.Children.Add(header); stack.Children.Add(Kit.Inset(list).Also(b => b.Padding = new Thickness(4)));
            Update();
            return Scroll(stack);
        }

        private static string Kind(YtmAlbum release) => string.IsNullOrEmpty(release.Kind) ? "Album" : release.Kind;

        // ---- Adding ----

        /// <summary>Adds a whole release: its songs start downloading as soon as the track list is known.</summary>
        internal DownloadGroup AddRelease(YtmAlbum release)
        {
            if (Queue.Contains(release.BrowseId)) return null;
            var group = Queue.Add(release.BrowseId, release.Title, string.Join(" · ", new[] { Kind(release), release.Artist, release.Year }.Where(p => !string.IsNullOrWhiteSpace(p))), release.ThumbnailUrl, null);
            _ = FillAsync(group, release, null);
            RefreshCards();
            return group;
        }

        internal DownloadGroup AddTracks(YtmAlbum release, List<YtmTrack> tracks)
        {
            if (tracks.Count == 0) return null;
            string key = release.BrowseId + "#" + string.Join(",", tracks.Select(t => t.VideoId));
            if (Queue.Contains(key)) return null;
            var group = Queue.Add(key, tracks.Count == 1 ? tracks[0].Title : release.Title + " (" + tracks.Count + " songs)", string.Join(" · ", new[] { release.Artist, release.Title }.Where(p => !string.IsNullOrWhiteSpace(p))), release.ThumbnailUrl, null);
            Queue.Ready(group, tracks.Select(t => new DownloadItem { Album = release, Track = t, FromVideo = t.VideoOnly }));
            return group;
        }

        /// <summary>Adds one song from search results, tagged from its album when YouTube Music links one.</summary>
        internal DownloadGroup AddSong(YtmTrack song)
        {
            if (Queue.Contains(song.VideoId)) return null;
            var group = Queue.Add(song.VideoId, song.Title, string.Join(" · ", new[] { "Song", song.Artists, song.AlbumTitle }.Where(p => !string.IsNullOrWhiteSpace(p))), null, null);
            _ = FillSongAsync(group, song);
            return group;
        }

        private async Task FillAsync(DownloadGroup group, YtmAlbum release, List<YtmTrack> only)
        {
            try
            {
                await PrepareAsync(release, CancellationToken.None);
                if (string.IsNullOrEmpty(group.ThumbnailUrl)) group.ThumbnailUrl = release.ThumbnailUrl;
                var tracks = (only ?? release.Tracks).ToList();
                if (tracks.Count == 0) { Queue.Fail(group, "YouTube Music lists no playable songs for this release."); return; }
                Queue.Ready(group, tracks.Select(t => new DownloadItem { Album = release, Track = t }));
            }
            catch (Exception e) { Queue.Fail(group, "Could not get the track list: " + e.Message); }
        }

        private async Task FillSongAsync(DownloadGroup group, YtmTrack song)
        {
            YtmAlbum album = null; var track = song;
            try
            {
                if (!string.IsNullOrEmpty(song.AlbumBrowseId))
                {
                    album = new YtmAlbum { BrowseId = song.AlbumBrowseId, Title = song.AlbumTitle };
                    await music.LoadAlbumAsync(album, CancellationToken.None);
                    track = album.Tracks.FirstOrDefault(t => t.VideoId == song.VideoId) ??
                        album.Tracks.FirstOrDefault(t => AlbumUpgrade.TitleSimilarity(t.Title, song.Title) >= 0.9) ?? song;
                    // Always the audio upload the search found, even where the album lists the music video.
                    if (track != song) { track.VideoId = song.VideoId; track.MusicVideo = false; if (song.Duration > TimeSpan.Zero) track.Duration = song.Duration; }
                    group.ThumbnailUrl = album.ThumbnailUrl;
                }
            }
            catch (Exception) { album = null; track = song; }
            if (album == null) album = new YtmAlbum { Title = song.AlbumTitle, Artist = AddMusic.SplitArtists(song.Artists).FirstOrDefault() ?? "", Tracks = new List<YtmTrack> { song }, TrackCount = 0 };
            Queue.Ready(group, new[] { new DownloadItem { Album = album, Track = track } });
        }

        private void RefreshCards() { foreach (var card in Kit.Descendants(view).OfType<ReleaseCard>()) card.Update(); }

        // ---- Queue panel ----

        private void SyncQueue()
        {
            var wanted = Queue.Groups.ToList();
            for (int i = groups.Count - 1; i >= 0; i--) if (!wanted.Contains(groups[i])) groups.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = groups.IndexOf(wanted[i]);
                if (at < 0) groups.Insert(i, wanted[i]); else if (at != i) groups.Move(at, i);
            }
            queueEmpty.IsVisible = groups.Count == 0;
            clearFinished.IsVisible = groups.Any(g => g.Finished || g.Problem != null);
            string folder = AddMusic.Folder;
            int waiting = AddMusic.Waiting(folder).Count;
            waitingText.Text = waiting == 0 ? "No songs waiting" : waiting + (waiting == 1 ? " song ready to add" : " songs ready to add");
            folderText.Text = Kit.MiddleEllipsis(folder, 38); folderText.Tip(folder);
            tidy.IsEnabled = waiting > 0 && !Queue.IsRunning && !merging;
            merge.IsEnabled = waiting > 0 && !Queue.IsRunning && !merging;
            merge.Tip(Queue.IsRunning ? "Wait for the downloads to finish, then add them to your library." : MergeTip);
            undoMerge.IsVisible = LibraryMerge.CanUndo; undoMerge.IsEnabled = !merging && !Queue.IsRunning;
            if (Queue.IsRunning) mergeText.Text = "Downloads are running. Keep browsing; you can add more at any time.";
            else if (mergeText.Text.StartsWith("Downloads are running", StringComparison.Ordinal)) mergeText.Text = "";
            RefreshCards();
            Changed?.Invoke();
        }

        internal event Action Changed;

        private void OpenNew()
        {
            try { Directory.CreateDirectory(AddMusic.Folder); openFolder(new ProcessStartInfo(AddMusic.Folder) { UseShellExecute = true }); }
            catch (Exception e) { mergeText.Text = "Could not open the folder: " + e.Message; }
        }

        private async Task ChangeFolderAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose where new music is saved before it joins your library", AllowMultiple = false });
            string path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (path == null) return;
            if (BackupSettings.Within(path, libraryRoot()) || BackupSettings.Within(libraryRoot(), path)) { mergeText.Text = "Choose a folder outside your music folder."; return; }
            if (Queue.IsRunning) { mergeText.Text = "Wait for the downloads to finish before changing the folder."; return; }
            if (!AppSettings.Write(AddMusic.SettingFile, Path.GetFullPath(path))) mergeText.Text = "The folder could not be saved on this PC.";
            SyncQueue();
        }

        /// <summary>Moves New music into the library and rescans it.</summary>
        internal async Task MergeAsync()
        {
            if (merging || Queue.IsRunning) return;
            if (!canMerge()) { mergeText.Text = "Wait for the current scan or task to finish, then try again."; return; }
            string folder = AddMusic.Folder, root = libraryRoot();
            merging = true; SyncQueue();
            try
            {
                var progress = new Progress<ProgressInfo>(p => mergeText.Text = p.Message);
                var (moved, already) = await Task.Run(() =>
                {
                    var plan = LibraryMerge.Plan(folder, root);
                    return (LibraryMerge.Run(plan, folder, root, CancellationToken.None, progress), plan.Count(m => m.AlreadyInLibrary));
                });
                mergeText.Text = moved + (moved == 1 ? " file added" : " files added") + " to your library" + (already > 0 ? " · " + already + " already there, left in New music" : "") + ".";
                if (moved > 0) await refreshLibrary();
            }
            catch (Exception e) { mergeText.Text = "Could not add to the library: " + e.Message; }
            finally { merging = false; SyncQueue(); }
        }

        internal async Task UndoMergeAsync()
        {
            if (merging || Queue.IsRunning) return;
            merging = true; SyncQueue();
            try
            {
                int restored = await Task.Run(LibraryMerge.Undo);
                mergeText.Text = restored + (restored == 1 ? " file" : " files") + " moved back to New music.";
                if (restored > 0) await refreshLibrary();
            }
            finally { merging = false; SyncQueue(); }
        }

        // ---- Thumbnails and preview ----

        internal async Task SetThumbAsync(Image image, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var bitmap = await ThumbAsync(url);
            if (bitmap != null) image.Source = bitmap;
        }

        private Task<Bitmap> ThumbAsync(string url)
        {
            if (thumbs.TryGetValue(url, out var known)) return Task.FromResult(known);
            if (!loadingThumbs.TryGetValue(url, out var task)) loadingThumbs[url] = task = LoadThumbAsync(url);
            return task;
        }

        private async Task<Bitmap> LoadThumbAsync(string url)
        {
            await thumbGate.WaitAsync();
            try
            {
                byte[] bytes = await music.ThumbnailAsync(url, CancellationToken.None);
                Bitmap bitmap = null;
                if (bytes != null) try { bitmap = await Task.Run(() => Kit.Decode(Artwork.Normalize(bytes, 360))); } catch (Exception) { }
                if (thumbs.Count > 400) thumbs.Clear();
                thumbs[url] = bitmap;
                return bitmap;
            }
            catch (Exception) { return null; }
            finally { thumbGate.Release(); loadingThumbs.Remove(url); }
        }

        private void OnPreviewChanged()
        {
            foreach (var row in Kit.Descendants(view).OfType<TrackRow>()) row.UpdatePreview();
            if (PreviewPlayer.Problem != null) browseStatus.Text = PreviewPlayer.Problem;
        }

        internal void Shutdown() { PreviewPlayer.Changed -= OnPreviewChanged; PreviewPlayer.Stop(); searching?.Cancel(); Queue.Stop(); }

        // ---- Item views ----

        // A square cover with title and year; the round button adds it to the queue without opening it.
        internal sealed class ReleaseCard : Border
        {
            private readonly AddMusicPage page;
            internal readonly YtmAlbum Release;
            internal readonly Button Add = Kit.IconButton(Icons.Add, "", "fab");

            internal ReleaseCard(AddMusicPage page, YtmAlbum release)
            {
                this.page = page; Release = release;
                Width = 172; Padding = new Thickness(6, 6, 6, 8); Margin = new Thickness(0, 0, 6, 10); CornerRadius = new CornerRadius(12); Classes.Add("release");
                Cursor = new Cursor(StandardCursorType.Hand);
                var cover = new Border { Width = 160, Height = 160, CornerRadius = new CornerRadius(9), ClipToBounds = true };
                cover.Brush(Border.BackgroundProperty, "CoverPlaceholder");
                var image = new Image { Stretch = Stretch.UniformToFill }; cover.Child = image;
                _ = page.SetThumbAsync(image, release.ThumbnailUrl);
                Add.HorizontalAlignment = HorizontalAlignment.Right; Add.VerticalAlignment = VerticalAlignment.Bottom; Add.Margin = new Thickness(0, 0, 8, 8);
                Add.Named("Add " + release.Title);
                Add.Click += (s, e) => { page.AddRelease(release); e.Handled = true; };
                var art = new Panel(); art.Children.Add(cover); art.Children.Add(Add);
                var title = Kit.Label(release.Title, Kit.Body, true); title.Margin = new Thickness(2, 8, 2, 0);
                var detail = Kit.Label(string.Join(" · ", new[] { release.Kind, release.Year }.Where(p => !string.IsNullOrWhiteSpace(p))), 11.5, cls: "muted"); detail.Margin = new Thickness(2, 1, 2, 0);
                var stack = new StackPanel(); stack.Children.Add(art); stack.Children.Add(title); stack.Children.Add(detail);
                Child = stack;
                ToolTip.SetTip(this, release.Title + "\n" + release.Summary + "\nClick to see the songs, or click + to add it straight away.");
                this.Named(release.Title + " release");
                PointerReleased += (s, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !(e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null)) _ = page.OpenReleaseAsync(release); };
                Update();
            }

            internal void Update()
            {
                bool queued = page.Queue.Contains(Release.BrowseId);
                Kit.SetIcon(Add, queued ? Icons.Check : Icons.Add);
                Add.IsEnabled = !queued;
                Add.Tip(queued ? "Added to the download queue." : "Add the whole " + Kind(Release).ToLowerInvariant() + " to the download queue. It starts downloading right away.");
            }
        }

        private sealed class ArtistTile : Border
        {
            internal ArtistTile(AddMusicPage page, YtmArtist artist)
            {
                Width = 138; Padding = new Thickness(6, 8); CornerRadius = new CornerRadius(12); Classes.Add("release");
                Cursor = new Cursor(StandardCursorType.Hand);
                var picture = new Border { Width = 118, Height = 118, CornerRadius = new CornerRadius(59), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
                picture.Brush(Border.BackgroundProperty, "CoverPlaceholder");
                var image = new Image { Stretch = Stretch.UniformToFill }; picture.Child = image;
                _ = page.SetThumbAsync(image, artist.ThumbnailUrl);
                var name = Kit.Label(artist.Name, Kit.Body, true); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 8, 0, 0);
                var kind = Kit.Label("Artist", 11.5, cls: "muted"); kind.HorizontalAlignment = HorizontalAlignment.Center;
                var stack = new StackPanel(); stack.Children.Add(picture); stack.Children.Add(name); stack.Children.Add(kind);
                Child = stack;
                this.Named(artist.Name + " artist");
                ToolTip.SetTip(this, artist.Name + (artist.Subtitle.Length > 0 ? "\n" + artist.Subtitle : "") + "\nClick to see their albums, singles and EPs.");
                PointerReleased += (s, e) => { if (e.InitialPressMouseButton == MouseButton.Left) _ = page.OpenArtistAsync(artist); };
            }
        }

        // A song: tick (on release pages), number, title, artists, audio badge, length, preview and add.
        internal sealed class TrackRow : Border
        {
            private readonly AddMusicPage page;
            internal readonly YtmAlbum Album;
            internal readonly YtmTrack Track;
            private readonly CheckBox check = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            internal readonly Button Preview = Kit.IconButton(Icons.Play, "", "ghost");
            private readonly Button add = Kit.IconButton(Icons.Add, "", "ghost");
            internal event Action CheckedChanged;
            internal bool Checked { get => check.IsChecked == true; set => check.IsChecked = value; }

            internal TrackRow(AddMusicPage page, YtmAlbum album, YtmTrack track, bool selectable)
            {
                this.page = page; Album = album; Track = track;
                Padding = new Thickness(8, 2, 6, 2); MinHeight = 42; CornerRadius = new CornerRadius(8); Classes.Add("track");
                var number = Kit.Label(selectable ? track.Index.ToString() : "", Kit.Body, cls: "muted"); number.HorizontalAlignment = HorizontalAlignment.Right; number.Margin = new Thickness(0, 0, 12, 0);
                var title = Kit.Label(track.Title, Kit.Body); var artists = Kit.Label(string.Join(" · ", new[] { track.Artists, selectable ? "" : track.AlbumTitle }.Where(p => !string.IsNullOrWhiteSpace(p))), 11.5, cls: "muted");
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(title); if (artists.Text.Length > 0) names.Children.Add(artists);
                var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1), Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
                var badgeText = new TextBlock { FontSize = 11 }; badge.Child = badgeText;
                if (track.VideoOnly) { badgeText.Text = "Music video only"; badge.Brush(Border.BackgroundProperty, "BadgeWarning"); badgeText.Classes.Add("warning"); ToolTip.SetTip(badge, "YouTube Music has no audio-only version of this song. Its music video can have an intro, an outro or silence; tick it to download it anyway."); }
                else if (track.ReplacedVideoId != null) { badgeText.Text = "Audio version"; badge.Brush(Border.BackgroundProperty, "BadgePositive"); badgeText.Classes.Add("positive"); ToolTip.SetTip(badge, "The album lists the music video; the artist's audio-only upload of the song is used instead."); }
                else badge.IsVisible = false;
                var length = Kit.Label(track.Duration > TimeSpan.Zero ? (int)track.Duration.TotalMinutes + ":" + track.Duration.Seconds.ToString("00") : "", Kit.Body, cls: "muted"); length.Margin = new Thickness(8, 0);
                Preview.Named("Preview " + track.Title); Preview.Height = 32;
                Preview.Click += async (s, e) => await PreviewPlayer.ToggleAsync(track);
                add.Named("Add " + track.Title); add.Height = 32;
                add.Click += (s, e) => { if (album == null) page.AddSong(track); else page.AddTracks(album, new List<YtmTrack> { track }); add.IsEnabled = false; Kit.SetIcon(add, Icons.Check); add.Tip("Added to the download queue."); };
                add.Tip(track.VideoOnly ? "Add this song from its music video (it may have an intro, an outro or silence)." : "Add just this song to the download queue.");
                check.IsVisible = selectable; check.Margin = new Thickness(0, 0, 10, 0); check.Named("Select " + track.Title);
                check.IsCheckedChanged += (s, e) => CheckedChanged?.Invoke();
                Child = Kit.Grid("Auto,34,*,Auto,Auto,Auto,Auto", "*").Add(check.Cell(0, 0), number.Cell(0, 1), names.Cell(0, 2), badge.Cell(0, 3), length.Cell(0, 4), Preview.Cell(0, 5), add.Cell(0, 6));
                if (!selectable) ((Grid)Child).ColumnDefinitions[1].Width = new GridLength(0);
                ToolTip.SetTip(names, track.Title + (track.Artists.Length > 0 ? "\n" + track.Artists : "") + (track.AlbumTitle.Length > 0 ? "\n" + track.AlbumTitle : ""));
                UpdatePreview();
            }

            internal void UpdatePreview()
            {
                bool loading = ReferenceEquals(PreviewPlayer.Loading, Track), playing = ReferenceEquals(PreviewPlayer.Playing, Track);
                Kit.SetIcon(Preview, playing || loading ? Icons.Stop : Icons.Play);
                Preview.Tip(loading ? "Loading the preview… click to cancel." : playing ? "Stop the preview." : "Play a " + PreviewPlayer.ClipSeconds + "-second preview.");
                Classes.Set("playing", playing || loading);
            }
        }

        // A queued release or song: cover, name, status, progress, and Cancel or Retry.
        private sealed class QueueRow : Border
        {
            private readonly AddMusicPage page;
            private readonly Image image = new Image { Stretch = Stretch.UniformToFill };
            private readonly TextBlock title = Kit.Label("", Kit.Body, true), status = Kit.Label("", 11.5, cls: "muted");
            private readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, Margin = new Thickness(0, 5, 0, 0) };
            private readonly Button action = Kit.IconButton(Icons.Close, "", "ghost");
            private DownloadGroup group;
            private string shownThumb;

            internal QueueRow(AddMusicPage page)
            {
                this.page = page;
                Padding = new Thickness(6, 6); Background = Brushes.Transparent;
                var art = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), Child = image };
                art.Brush(Border.BackgroundProperty, "CoverPlaceholder");
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(title); text.Children.Add(status); text.Children.Add(bar);
                action.Height = 30; action.VerticalAlignment = VerticalAlignment.Center;
                action.Click += (s, e) =>
                {
                    if (group == null) return;
                    if (group.Finished && (group.Failed > 0 || group.Cancelled)) page.Queue.Retry(group); else if (!group.Finished) page.Queue.Cancel(group);
                };
                Child = Kit.Grid("Auto,*,Auto", "Auto").Add(art.Cell(0, 0), text.Cell(0, 1), action.Cell(0, 2));
                DataContextChanged += (s, e) =>
                {
                    if (group != null) group.Changed -= Update;
                    group = DataContext as DownloadGroup;
                    if (group != null) group.Changed += Update;
                    Update();
                };
            }

            private void Update()
            {
                if (group == null) return;
                title.Text = group.Title; status.Text = group.Status;
                bar.Value = group.Progress; bar.IsVisible = !group.Finished && group.Problem == null;
                status.Classes.Set("warning", group.Failed > 0 || group.Problem != null);
                bool retry = group.Finished && (group.Failed > 0 || group.Cancelled);
                Kit.SetIcon(action, retry ? Icons.Refresh : Icons.Close);
                action.IsVisible = !group.Finished || retry;
                action.Tip(retry ? "Try the failed songs again." : "Cancel the songs of this item that are still waiting. Finished songs stay in New music.");
                ToolTip.SetTip(this, group.Title + "\n" + group.Subtitle + "\n" + group.Status +
                    string.Concat(group.Items.Where(i => i.Error != null).Take(5).Select(i => "\n• " + i.Track.Title + ": " + i.Error)));
                if (group.ThumbnailUrl != shownThumb) { shownThumb = group.ThumbnailUrl; image.Source = null; _ = page.SetThumbAsync(image, group.ThumbnailUrl); }
            }
        }
    }
}
