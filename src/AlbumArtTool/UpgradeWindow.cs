using System;
using System.Collections.Generic;
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
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Replaces low-bitrate songs with fresh downloads from YouTube Music: every listed album is searched automatically,
    // each of your songs is paired with its YouTube Music track by title and length, and checked songs are downloaded
    // (best free stream), saved as MP3 320 with your own tags and cover, and swapped in with a backup and Undo.
    internal sealed class UpgradeWindow : Window
    {
        internal sealed class AlbumState
        {
            internal LowBitrateAlbum Low;
            internal Album Album;
            internal List<Track> Tracks;
            internal List<YtmAlbum> Candidates = new List<YtmAlbum>();
            internal Dictionary<YtmAlbum, int> Scores = new Dictionary<YtmAlbum, int>();
            internal YtmAlbum Chosen;
            internal List<TrackPairing> Pairs = new List<TrackPairing>();
            internal string Query, Status = "Waiting to search…", Tone = "Accent", Problem;
            internal bool Searched, Searching;
            internal event Action Changed;
            internal void Set(string status, string tone) { Status = status; Tone = tone; Changed?.Invoke(); }
            internal void Refresh() => Changed?.Invoke();
            internal int LowCount => Pairs.Count > 0 ? Pairs.Count(p => p.Low) : Low.Tracks.Count;
            internal int SelectedCount => Pairs.Count(p => p.Selected && p.CanReplace);
            internal string Title => string.Join(" — ", new[] { Low.Artist, Low.Album }.Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        private readonly string root;
        private readonly Func<bool> canStart;
        private readonly Action<bool> exclusive;
        private readonly Func<string, Task> refreshLibrary;
        private readonly Action<ProcessStartInfo> open;
        private readonly IYouTubeMusic music;
        private readonly Func<string, string, CancellationToken, Action<double>, string> download;
        private readonly int limit;
        private readonly List<AlbumState> states;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private CancellationTokenSource running;
        private LibraryJournal lastJournal;
        private AlbumState shown;
        private Task toolUpdate = Task.CompletedTask;

        private readonly ListBox albumList = new ListBox { Classes = { "albums" } };
        private readonly ListBox candidateList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        private readonly ListBox trackList = new ListBox { Classes = { "grid" } };
        private readonly TextBox query = new TextBox { Watermark = "Search YouTube Music" };
        private readonly TextBlock heading = Kit.Label("", 17.33, true), subheading = Kit.Label("", Kit.Body, cls: "muted"), status = Kit.Label("", Kit.Body),
            toolText = Kit.Label("", Kit.Body), tableNote = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Image localCover = new Image { Stretch = Stretch.UniformToFill };
        private readonly Border toolsCard;
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false, Height = 4, MinHeight = 4, Margin = new Thickness(0, 0, 0, 6) };
        private readonly Button search = Kit.Button("Search"), setup = Kit.Button("Set up", "primary"), cookies = Kit.Button("Cookies…"),
            replaceAlbum = Kit.Button("Replace 0 songs", "primary"), replaceAll = Kit.Button("Replace all checked"), stop = Kit.Button("Stop"), undo = Kit.Button("Undo replacements"),
            openYtm = Kit.Button("Open on YouTube Music ↗"), pickLow = Kit.Button("Low songs"), pickAll = Kit.Button("All matched"), pickNone = Kit.Button("None");

        internal IReadOnlyList<AlbumState> Albums => states;
        internal AlbumState Shown => shown;
        internal Task Searching { get; private set; } = Task.CompletedTask;
        internal bool IsWorking { get; private set; }
        internal LibraryJournal LastJournal => lastJournal;
        internal string StatusText => status.Text;

        internal UpgradeWindow(string musicRoot, IReadOnlyList<Album> library, LibraryCheckResult result, Func<bool> ready, Action<bool> setExclusive, Func<string, Task> refresh,
            Action<ProcessStartInfo> openLink, IYouTubeMusic youtube = null, Func<string, string, CancellationToken, Action<double>, string> fetch = null)
        {
            root = musicRoot; canStart = ready; exclusive = setExclusive; refreshLibrary = refresh; open = openLink; music = youtube ?? new YouTubeMusic(); download = fetch;
            limit = result.LowBitrateLimit;
            states = result.LowBitrateAlbums().Select(low =>
            {
                var album = library.FirstOrDefault(a => a.Tracks.Any(t => low.Tracks.Any(l => l.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase))));
                var tracks = (album?.Tracks ?? low.Tracks).OrderBy(t => t.Disc).ThenBy(t => t.Number).ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
                return new AlbumState { Low = low, Album = album, Tracks = tracks, Query = AlbumUpgrade.Query(low.Artist, low.Album) };
            }).ToList();
            var kbps = new Func<Track, int>(result.Bitrate);
            Kbps = kbps;
            Title = "Replace low-quality songs"; Width = 1180; Height = 740; MinWidth = 980; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Header
            var title = Kit.Label("Replace low-quality songs from YouTube Music", 20, true);
            var intro = Kit.Label("Each album below is matched on YouTube Music automatically. Checked songs are downloaded at YouTube Music's best free quality (Opus, about 130–160 kbps) " +
                "and saved as MP3 320 with your own tags and cover. Originals are backed up first, and Undo puts them back.", Kit.Body, cls: "muted");
            intro.TextWrapping = TextWrapping.Wrap; intro.TextTrimming = TextTrimming.None;
            var header = new StackPanel { Spacing = 4 }; header.Children.Add(title); header.Children.Add(intro);

            // Tools banner (only when something is missing)
            toolText.TextWrapping = TextWrapping.Wrap; toolText.TextTrimming = TextTrimming.None;
            setup.MinWidth = 120; setup.Click += async (s, e) => await SetupAsync(false);
            toolsCard = Kit.Frost(Kit.Grid("*,Auto", "Auto").Add(toolText.Cell(0, 0), setup.Cell(0, 1)), new Thickness(14, 10, 10, 10), 14);
            toolsCard.Margin = new Thickness(0, 10, 0, 0);

            // Left: albums
            albumList.Named("Albums with low-bitrate songs");
            albumList.ItemTemplate = new FuncDataTemplate<AlbumState>((s, scope) => new AlbumTile(), true);
            albumList.ItemsSource = states;
            albumList.SelectionChanged += (s, e) => { if (albumList.SelectedItem is AlbumState state) Show(state); };
            var albumsLabel = Kit.Label("ALBUMS", cls: "caption"); albumsLabel.Margin = new Thickness(8, 0, 0, 6);
            var left = Kit.Grid("*", "Auto,*").Add(albumsLabel.Cell(0, 0), albumList.Cell(1, 0));

            // Right: selected album, YouTube Music matches, track pairing
            var coverFrame = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = localCover, Margin = new Thickness(0, 0, 14, 0) };
            coverFrame.Brush(Border.BackgroundProperty, "CoverPlaceholder");
            heading.Named("Selected album"); subheading.Named("Selected album details");
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 }; titles.Children.Add(heading); titles.Children.Add(subheading);
            openYtm.Classes.Add("chrome"); openYtm.Click += (s, e) => OpenChosen();
            openYtm.Tip("Open the chosen album on music.youtube.com in your browser, to check it is the right release.");
            var albumHeader = Kit.Grid("Auto,*,Auto", "Auto").Add(coverFrame.Cell(0, 0), titles.Cell(0, 1), openYtm.Cell(0, 2));

            query.Named("YouTube Music search").Tip("Change the search if the right album isn't among the matches, then press Enter or Search.");
            query.KeyDown += async (s, e) => { if (e.Key == Key.Enter && shown != null) { e.Handled = true; await SearchShownAsync(); } };
            search.Click += async (s, e) => { if (shown != null) await SearchShownAsync(); };
            var matchesLabel = Kit.Label("ON YOUTUBE MUSIC", cls: "caption");
            var searchRow = Kit.Grid("*,260,Auto", "Auto").Add(matchesLabel.Cell(0, 0), query.Cell(0, 1), search.Cell(0, 2));
            search.Margin = new Thickness(6, 0, 0, 0);
            candidateList.Named("YouTube Music matches");
            candidateList.ItemsPanel = new FuncTemplate<Panel>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 });
            candidateList.ItemTemplate = new FuncDataTemplate<YtmAlbum>((a, scope) => new MatchCard(this), true);
            candidateList.SelectionChanged += async (s, e) => { if (candidateList.SelectedItem is YtmAlbum chosen && shown != null && !ReferenceEquals(chosen, shown.Chosen)) await ChooseAsync(shown, chosen); };
            ScrollViewer.SetHorizontalScrollBarVisibility(candidateList, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(candidateList, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            candidateList.Height = 112;
            var candidateScroll = candidateList;

            var columns = Kit.Grid(PairRow.Columns, "28").Add(Kit.Label("", Kit.Small).Cell(0, 0), Kit.Label("Your song", Kit.Small, cls: "muted").Cell(0, 1), Kit.Label("Now", Kit.Small, cls: "muted").Cell(0, 2),
                Kit.Label("YouTube Music", Kit.Small, cls: "muted").Cell(0, 3), Kit.Label("Length", Kit.Small, cls: "muted").Cell(0, 4));
            columns.Margin = new Thickness(10, 0, 8, 0);
            var columnBar = new Border { Child = columns, BorderThickness = new Thickness(0, 0, 0, 1) };
            columnBar.Brush(Border.BorderBrushProperty, "Border"); columnBar.Brush(Border.BackgroundProperty, "InsetHeader");
            trackList.Named("Song replacements");
            trackList.ItemTemplate = new FuncDataTemplate<TrackPairing>((p, scope) => new PairRow(this), true);
            var table = Kit.Inset(Kit.Grid("*", "Auto,*").Add(columnBar.Cell(0, 0), trackList.Cell(1, 0))); table.Padding = new Thickness(0);
            foreach (var b in new[] { pickLow, pickAll, pickNone }) { b.Classes.Add("chrome"); b.Padding = new Thickness(8, 2, 8, 2); b.MinHeight = 24; }
            pickLow.Click += (s, e) => Pick(p => p.Low && p.Quality == PairQuality.Exact); pickLow.Tip("Check the low-bitrate songs with an exact match (same title, same length).");
            pickAll.Click += (s, e) => Pick(p => p.Quality != PairQuality.Missing); pickAll.Tip("Check every song that has a YouTube Music match, low bitrate or not.");
            pickNone.Click += (s, e) => Pick(p => false);
            var picks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            picks.Children.Add(Kit.Label("Select:", Kit.Small, cls: "muted")); picks.Children.Add(pickLow); picks.Children.Add(pickAll); picks.Children.Add(pickNone);
            tableNote.TextTrimming = TextTrimming.CharacterEllipsis;
            var tableFooter = Kit.Grid("Auto,*", "Auto").Add(picks.Cell(0, 0), tableNote.Cell(0, 1)); tableNote.Margin = new Thickness(12, 0, 0, 0);
            tableFooter.Margin = new Thickness(0, 6, 0, 0);
            var right = Kit.Grid("*", "Auto,Auto,Auto,*,Auto").Add(albumHeader.Cell(0, 0), searchRow.Cell(1, 0), candidateScroll.Cell(2, 0), table.Cell(3, 0), tableFooter.Cell(4, 0));
            searchRow.Margin = new Thickness(0, 14, 0, 4); table.Margin = new Thickness(0, 8, 0, 0);
            var leftCard = Kit.Frost(left, new Thickness(8, 12, 8, 8)); var rightCard = Kit.Frost(right, new Thickness(16, 14, 16, 12));
            leftCard.Margin = new Thickness(0, 0, 10, 0);
            var body = Kit.Grid("330,*", "*").Add(leftCard.Cell(0, 0), rightCard.Cell(0, 1)); body.Margin = new Thickness(0, 12, 0, 0);

            // Footer
            status.Named("Replacement status"); status.TextWrapping = TextWrapping.Wrap; status.MaxLines = 2;
            replaceAlbum.Named("Replace checked songs in this album"); replaceAll.Named("Replace all checked songs");
            replaceAlbum.MinWidth = 170; replaceAlbum.Click += async (s, e) => { if (shown != null) await ReplaceAsync(new[] { shown }); };
            replaceAll.Click += async (s, e) => await ReplaceAsync(states);
            stop.Click += (s, e) => running?.Cancel(); stop.IsEnabled = false;
            undo.Click += async (s, e) => await UndoAsync(); undo.IsEnabled = false;
            cookies.Classes.Add("chrome"); cookies.Click += async (s, e) => await ChooseCookiesAsync();
            replaceAlbum.Tip("Downloads the checked songs of this album from YouTube Music and replaces your files with MP3 320 copies (your tags and cover are kept). Originals go to the PC backup folder.");
            replaceAll.Tip("Does the same for every checked song in every album in the list.");
            stop.Tip("Stops after the song in progress. Finished replacements stay and can be undone.");
            undo.Tip("Puts back the original files replaced in this window, byte for byte. Later, Manage library → Preview Undo can also undo the last run.");
            UpdateCookiesTip();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in new Control[] { cookies, undo, stop, replaceAll, replaceAlbum }) buttons.Children.Add(b);
            var footer = Kit.Grid("*,Auto", "Auto,Auto").Add(progress.Cell(0, 0, 2), status.Cell(1, 0), buttons.Cell(1, 1));
            footer.Margin = new Thickness(0, 12, 0, 0); status.Margin = new Thickness(2, 0, 16, 0);

            var layout = Kit.Grid("*", "Auto,Auto,*,Auto").Add(header.Cell(0, 0), toolsCard.Cell(1, 0), body.Cell(2, 0), footer.Cell(3, 0));
            layout.Margin = new Thickness(20, 16, 20, 16);
            Kit.Prepare(this, layout);
            UpdateTools();
            Opened += (s, e) =>
            {
                if (states.Count > 0) albumList.SelectedIndex = 0;
                Searching = SearchAllAsync(closing.Token);
                toolUpdate = UpdateYtDlpAsync(closing.Token);
            };
            Closing += (s, e) => { if (IsWorking) { e.Cancel = true; status.Text = "Replacements are running. Click Stop first; the song in progress finishes safely."; } };
            Closed += (s, e) => { closing.Cancel(); if (youtube == null) music.Dispose(); };
            if (states.Count == 0) status.Text = "No albums have low-bitrate songs at this threshold.";
        }

        internal Func<Track, int> Kbps { get; }

        // ---- Tools ----

        private string Ffmpeg => Management.Encoder.Find();
        private string Downloader => YtDlp.Find();
        private bool ToolsReady => download != null || Downloader != null && (YtDlp.FindDeno(Downloader) != null || !OperatingSystem.IsWindows()) && Ffmpeg != null;

        private void UpdateTools()
        {
            var missing = new List<string>();
            if (download == null && Downloader == null) missing.Add("yt-dlp (downloads the audio)");
            if (download == null && Downloader != null && YtDlp.FindDeno(Downloader) == null && OperatingSystem.IsWindows()) missing.Add("Deno (runs YouTube's player code for yt-dlp)");
            if (Ffmpeg == null) missing.Add("FFmpeg (makes the MP3 320 files)");
            toolsCard.IsVisible = missing.Count > 0;
            toolText.Text = "To download, this needs " + string.Join(", ", missing) + ". Set up installs them once into this PC's app folder from their official GitHub releases (about " +
                (missing.Count >= 2 ? "230" : missing.Any(m => m.StartsWith("FFmpeg")) ? "170" : "60") + " MB), checking each download's SHA-256. Searching and matching work already.";
            UpdateButtons();
        }

        private async Task SetupAsync(bool updateOnly)
        {
            if (IsWorking) return;
            SetWorking(true, cancellable: true); var token = running.Token;
            try
            {
                if (!updateOnly && Ffmpeg == null)
                {
                    status.Text = "Downloading FFmpeg…";
                    using (var setupFfmpeg = new EncoderSetup()) { string exe = await setupFfmpeg.InstallAsync(Percent("Downloading FFmpeg"), token); Management.Encoder.Remember(exe); }
                }
                if (updateOnly || Downloader == null || YtDlp.FindDeno(Downloader) == null)
                {
                    status.Text = "Downloading yt-dlp" + (updateOnly ? "" : " and Deno") + "…";
                    using (var setupYtDlp = new YtDlpSetup()) await setupYtDlp.InstallAsync(!updateOnly && (Downloader == null || YtDlp.FindDeno(Downloader) == null), Percent("Downloading yt-dlp and Deno"), token);
                }
                status.Text = "Tools ready. Check the songs to replace, then click Replace.";
            }
            catch (OperationCanceledException) { status.Text = "Setup stopped."; }
            catch (Exception e) when (e is IOException || e is System.Net.Http.HttpRequestException || e is UnauthorizedAccessException || e is InvalidDataException || e is System.ComponentModel.Win32Exception)
            { status.Text = "Setup could not finish: " + e.Message; }
            finally { SetWorking(false); UpdateTools(); }
        }

        // Keeps the copy of yt-dlp this app installed current: YouTube changes often, and old versions stop working.
        private async Task UpdateYtDlpAsync(CancellationToken token)
        {
            if (download != null || !YtDlpSetup.Installed || !OperatingSystem.IsWindows()) return;
            string stamp = Path.Combine(YtDlp.Cache, "CHECKED.txt");
            try { if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromHours(20)) return; } catch { }
            try
            {
                using (var setupYtDlp = new YtDlpSetup())
                {
                    string latest = await setupYtDlp.LatestTagAsync(token);
                    if (latest.Length > 0 && latest != YtDlpSetup.InstalledTag) await setupYtDlp.InstallAsync(false, null, token);
                }
                File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception e) when (e is IOException || e is System.Net.Http.HttpRequestException || e is UnauthorizedAccessException || e is InvalidDataException || e is OperationCanceledException) { }
        }

        private async Task ChooseCookiesAsync()
        {
            if (YtDlp.Cookies != null)
            {
                YtDlp.Cookies = null; UpdateCookiesTip(); status.Text = "Cookies file removed. Downloads are anonymous again."; return;
            }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a cookies.txt exported from a browser signed in to YouTube", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Cookies file") { Patterns = new[] { "*.txt" } } } });
            string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path == null) return;
            YtDlp.Cookies = path; UpdateCookiesTip(); status.Text = "Downloads will use " + Path.GetFileName(path) + ".";
        }

        private void UpdateCookiesTip()
        {
            bool set = YtDlp.Cookies != null;
            cookies.Content = set ? "Remove cookies" : "Cookies…";
            cookies.Tip(set ? "Downloads use " + YtDlp.Cookies + ". Click to stop using it." :
                "Only needed if YouTube says \"confirm you're not a bot\": choose a cookies.txt exported from a browser where you are signed in to YouTube (for example with a \"Get cookies.txt\" extension). " +
                "It is passed to yt-dlp and never leaves your PC.");
        }

        // ---- Searching and matching ----

        private async Task SearchAllAsync(CancellationToken token)
        {
            foreach (var state in states)
            {
                if (token.IsCancellationRequested) return;
                if (state.Searched || state.Searching) continue;
                await SearchAsync(state, state.Query, token);
                try { await Task.Delay(350, token); } catch (OperationCanceledException) { return; }
            }
            if (!IsWorking) status.Text = Summary();
        }

        private async Task SearchShownAsync()
        {
            var state = shown;
            string text = (query.Text ?? "").Trim();
            if (text.Length == 0) return;
            state.Query = text; state.Searched = false;
            await SearchAsync(state, text, closing.Token);
        }

        internal async Task SearchAsync(AlbumState state, string text, CancellationToken token)
        {
            state.Searching = true; state.Problem = null; state.Set("Searching YouTube Music…", "Accent");
            try
            {
                var found = await music.SearchAlbumsAsync(text, token);
                var ranked = found.Select(a => (Album: a, Score: AlbumUpgrade.AlbumScore(state.Low.Artist, state.Low.Album, state.Tracks.Count, a)))
                    .OrderByDescending(r => r.Score).Take(8).ToList();
                state.Candidates = ranked.Select(r => r.Album).ToList();
                state.Scores = ranked.ToDictionary(r => r.Album, r => r.Score);
                // Open the likeliest albums and keep the one whose tracks pair best with yours.
                YtmAlbum best = null; List<TrackPairing> bestPairs = null; (int, int, int) bestKey = (-1, -1, -1);
                foreach (var (album, score) in ranked.Where(r => r.Score >= 45).Take(3))
                {
                    await music.LoadAlbumAsync(album, token);
                    var pairs = Pair(state, album);
                    int rescored = AlbumUpgrade.AlbumScore(state.Low.Artist, state.Low.Album, state.Tracks.Count, album);
                    state.Scores[album] = rescored;
                    var key = (pairs.Count(p => p.Low && p.Quality == PairQuality.Exact), pairs.Count(p => p.Remote != null), rescored);
                    if (key.CompareTo(bestKey) > 0) { best = album; bestPairs = pairs; bestKey = key; }
                }
                state.Chosen = best; state.Pairs = bestPairs ?? Pair(state, null);
                state.Searched = true;
                UpdateStatus(state);
                _ = LoadThumbnailsAsync(state, token);
            }
            catch (OperationCanceledException) { state.Set("Search stopped", "Warning"); }
            catch (Exception e) when (e is IOException || e is System.Net.Http.HttpRequestException || e is InvalidDataException || e is System.Text.Json.JsonException)
            { state.Problem = e.Message; state.Set("Search failed — try again", "Warning"); }
            finally { state.Searching = false; if (ReferenceEquals(state, shown)) Show(state); UpdateButtons(); }
        }

        private List<TrackPairing> Pair(AlbumState state, YtmAlbum album) =>
            AlbumUpgrade.Pair(state.Tracks, new HashSet<string>(state.Low.Tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase), album, Kbps);

        private async Task ChooseAsync(AlbumState state, YtmAlbum album)
        {
            try
            {
                await music.LoadAlbumAsync(album, closing.Token);
                state.Chosen = album; state.Pairs = Pair(state, album); state.Scores[album] = AlbumUpgrade.AlbumScore(state.Low.Artist, state.Low.Album, state.Tracks.Count, album);
                UpdateStatus(state);
            }
            catch (Exception e) when (e is IOException || e is System.Net.Http.HttpRequestException || e is InvalidDataException || e is OperationCanceledException || e is System.Text.Json.JsonException)
            { status.Text = "That album could not be opened: " + e.Message; }
            if (ReferenceEquals(state, shown)) Show(state);
        }

        private async Task LoadThumbnailsAsync(AlbumState state, CancellationToken token)
        {
            foreach (var album in state.Candidates.Where(a => a.Thumbnail == null && a.ThumbnailUrl.Length > 0).ToList())
            {
                try { album.Thumbnail = await music.ThumbnailAsync(Sized(album.ThumbnailUrl, 226), token); }
                catch (Exception e) when (e is IOException || e is System.Net.Http.HttpRequestException || e is OperationCanceledException || e is TaskCanceledException) { return; }
                if (ReferenceEquals(state, shown)) RefreshCandidates();
            }
        }

        // Google image URLs carry their size ("=w544-h544-l90-rj"); a smaller size loads faster.
        private static string Sized(string url, int side)
        {
            int at = url.LastIndexOf("=w", StringComparison.Ordinal);
            return at > 0 ? url.Substring(0, at) + "=w" + side + "-h" + side + "-l90-rj" : url;
        }

        private void UpdateStatus(AlbumState state)
        {
            int low = state.Pairs.Count(p => p.Low && !p.Replaced), ready = state.Pairs.Count(p => p.Low && !p.Replaced && p.Quality == PairQuality.Exact), replaced = state.Pairs.Count(p => p.Replaced);
            if (replaced > 0 && low == 0) state.Set("✓ " + replaced + (replaced == 1 ? " song" : " songs") + " replaced", "Positive");
            else if (state.Chosen == null) state.Set(state.Candidates.Count == 0 ? "Not found on YouTube Music" : "No close match — pick one", "Warning");
            else if (ready == low) state.Set("Ready · " + ready + (ready == 1 ? " song" : " songs") + " matched", "Positive");
            else if (ready > 0) state.Set(ready + " of " + low + " low songs matched", "Accent");
            else state.Set("Found — check the tracks", "Warning");
            UpdateButtons();
        }

        // ---- Display ----

        private void Show(AlbumState state)
        {
            shown = state;
            heading.Text = state.Title.Length > 0 ? state.Title : state.Low.Folder;
            subheading.Text = state.Tracks.Count + (state.Tracks.Count == 1 ? " song" : " songs") + " · " + state.LowCount + " below " + limit + " kbps (" + state.Low.Range + ") · " + state.Low.Folder;
            ToolTip.SetTip(subheading, state.Low.Folder);
            localCover.Source = ThumbnailCache.Get(state.Album?.Thumbnail);
            if (!query.IsFocused) query.Text = state.Query;
            RefreshCandidates();
            trackList.ItemsSource = state.Pairs.Count > 0 ? state.Pairs : null;
            tableNote.Text = state.Searching ? "Searching YouTube Music…" : state.Problem != null ? state.Problem :
                state.Chosen == null ? (state.Candidates.Count == 0 ? "No album found. Try a different search above." : "None of the results matches closely. Click the right album above.") :
                "Checked songs are replaced. Orange lengths differ from your file — listen on YouTube Music before checking them.";
            openYtm.IsEnabled = state.Chosen != null;
            UpdateButtons();
        }

        private void RefreshCandidates()
        {
            if (shown == null) return;
            candidateList.ItemsSource = null;
            candidateList.ItemsSource = shown.Candidates;
            candidateList.SelectedItem = shown.Chosen;
        }

        internal int Score(YtmAlbum album) => shown != null && shown.Scores.TryGetValue(album, out int score) ? score : 0;
        internal bool IsChosen(YtmAlbum album) => shown != null && ReferenceEquals(shown.Chosen, album);

        private void Pick(Func<TrackPairing, bool> rule)
        {
            if (shown == null || IsWorking) return;
            foreach (var pair in shown.Pairs) pair.Selected = pair.CanReplace && rule(pair);
            trackList.ItemsSource = null; trackList.ItemsSource = shown.Pairs;
            UpdateButtons();
        }

        internal void Toggled() => UpdateButtons();

        private void UpdateButtons()
        {
            int here = shown?.SelectedCount ?? 0, all = states.Sum(s => s.SelectedCount), albumsWithPicks = states.Count(s => s.SelectedCount > 0);
            replaceAlbum.Content = "Replace " + here + (here == 1 ? " song" : " songs");
            replaceAll.Content = "Replace all checked (" + all + ")";
            replaceAll.Tip("Downloads and replaces every checked song: " + all + (all == 1 ? " song" : " songs") + " in " + albumsWithPicks + (albumsWithPicks == 1 ? " album" : " albums") + ".");
            replaceAlbum.IsEnabled = !IsWorking && here > 0 && ToolsReady;
            replaceAll.IsEnabled = !IsWorking && all > 0 && ToolsReady;
            undo.IsEnabled = !IsWorking && lastJournal != null && lastJournal.Entries.Any(e => e.State == "Committed");
            foreach (var b in new[] { pickLow, pickAll, pickNone, search }) b.IsEnabled = !IsWorking;
            setup.IsEnabled = !IsWorking;
        }

        private string Summary()
        {
            int ready = states.Count(s => s.Chosen != null), missing = states.Count(s => s.Searched && s.Chosen == null);
            return "Matched " + ready + " of " + states.Count + (states.Count == 1 ? " album" : " albums") + (missing > 0 ? " · " + missing + " not found" : "") +
                " · " + states.Sum(s => s.SelectedCount) + " songs checked. Review each album, then Replace.";
        }

        private void OpenChosen()
        {
            if (shown?.Chosen == null) return;
            try { open(new ProcessStartInfo(shown.Chosen.Url) { UseShellExecute = true }); } catch (Exception e) { status.Text = e.Message; }
        }

        internal void Preview(TrackPairing pairing)
        {
            if (pairing?.Remote == null) return;
            try { open(new ProcessStartInfo(pairing.Remote.Url) { UseShellExecute = true }); } catch (Exception e) { status.Text = e.Message; }
        }

        // ---- Replacing ----

        private void SetWorking(bool value, bool cancellable = true)
        {
            IsWorking = value;
            if (value) { running?.Dispose(); running = new CancellationTokenSource(); }
            stop.IsEnabled = value && cancellable; progress.IsVisible = value; progress.Value = 0; progress.IsIndeterminate = value;
            albumList.IsEnabled = true; candidateList.IsEnabled = !value; query.IsEnabled = !value; cookies.IsEnabled = !value;
            UpdateButtons();
        }

        private IProgress<int> Percent(string message) => new Progress<int>(p => { progress.IsIndeterminate = false; progress.Value = p; status.Text = message + " · " + p + "%"; });

        private IProgress<ProgressInfo> Reporter() => new Progress<ProgressInfo>(p =>
        {
            if (!IsWorking) return;
            progress.IsIndeterminate = p.Total <= 0;
            progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, 100 * (p.Completed + p.Fraction) / p.Total)) : 0;
            status.Text = p.Message;
        });

        internal async Task ReplaceAsync(IEnumerable<AlbumState> which)
        {
            if (IsWorking) return;
            if (!ToolsReady) { status.Text = "Click Set up first: downloading needs yt-dlp, Deno and FFmpeg."; return; }
            if (!canStart()) { status.Text = "Wait for scans and other library jobs to finish first."; return; }
            var chosen = which.ToList();
            var pairs = chosen.SelectMany(s => s.Pairs).Where(p => p.Selected && p.CanReplace).ToList();
            if (pairs.Count == 0) return;
            await toolUpdate;
            var options = new LibraryOptions { Root = root, BackupRoot = BackupSettings.Root, EncoderPath = Ffmpeg, DownloaderPath = Downloader, Download = download };
            SetWorking(true); exclusive(true);
            bool wrote = false;
            try
            {
                var plan = AlbumUpgrade.Plan(pairs, options);
                var token = running.Token; var reporter = Reporter();
                var journal = await Task.Run(() => LibraryRunner.Run(plan, token, reporter));
                lastJournal = journal;
                var committed = new HashSet<string>(journal.Entries.Where(e => e.State == "Committed").Select(e => e.Source), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in pairs.Where(p => committed.Contains(p.Local.Path))) { pair.Replaced = true; pair.Selected = false; }
                wrote = committed.Count > 0;
                foreach (var state in chosen) UpdateStatus(state);
                var problems = plan.Issues.Concat(journal.Issues).ToList();
                status.Text = "Replaced " + committed.Count + " of " + pairs.Count + (pairs.Count == 1 ? " song" : " songs") + (journal.Stopped ? " (stopped)" : "") +
                    (problems.Count > 0 ? " · " + problems.Count + " not replaced: " + Shorten(problems[0]) : ". Originals are in the backup folder; Undo puts them back.");
                ToolTip.SetTip(status, problems.Count > 0 ? string.Join("\n", problems.Take(30)) : journal.Directory);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { status.Text = "Could not replace: " + e.Message; }
            finally
            {
                // The library rescan runs while this window still shows as working, so nothing starts on stale files.
                exclusive(false);
                if (wrote) { try { await refreshLibrary(root); } catch (Exception) { } }
                SetWorking(false);
                if (shown != null) Show(shown);
            }
        }

        private static string Shorten(string issue)
        {
            int colon = issue.IndexOf(": ", StringComparison.Ordinal);
            string file = colon > 0 ? Path.GetFileName(issue.Substring(0, colon)) : "";
            return (file.Length > 0 ? file + " — " : "") + (colon > 0 ? issue.Substring(colon + 2) : issue);
        }

        internal async Task UndoAsync()
        {
            if (IsWorking || lastJournal == null || !canStart()) return;
            SetWorking(true); exclusive(true);
            try
            {
                var journal = lastJournal; var token = running.Token; var reporter = Reporter();
                int restored = await Task.Run(() => LibraryRunner.Undo(journal, token, reporter));
                var back = new HashSet<string>(journal.Entries.Where(e => e.State == "Restored").Select(e => e.Source), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in states.SelectMany(s => s.Pairs).Where(p => p.Replaced && back.Contains(p.Local.Path))) { pair.Replaced = false; pair.Selected = pair.Low && pair.Quality == PairQuality.Exact; }
                foreach (var state in states) if (state.Searched) UpdateStatus(state);
                status.Text = "Restored " + restored + (restored == 1 ? " original." : " originals.");
                if (restored > 0) lastJournal = null;
                exclusive(false);
                if (restored > 0) { try { await refreshLibrary(root); } catch (Exception) { } }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { status.Text = "Undo could not finish: " + e.Message; }
            finally { exclusive(false); SetWorking(false); if (shown != null) Show(shown); }
        }

        // ---- Item views ----

        private sealed class AlbumTile : Border
        {
            private readonly Image cover = new Image { Stretch = Stretch.UniformToFill };
            private readonly TextBlock title = Kit.Label("", 13.33, true), detail = Kit.Label("", Kit.Small, cls: "muted"), badgeText = new TextBlock { FontSize = Kit.Small, TextTrimming = TextTrimming.CharacterEllipsis };
            private readonly Border badge;
            private AlbumState state;
            private IDisposable badgeBrush, toneBrush;

            internal AlbumTile()
            {
                Height = 74; Padding = new Thickness(2); Background = Brushes.Transparent;
                var frame = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = cover, Margin = new Thickness(8, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
                frame.Brush(Border.BackgroundProperty, "CoverPlaceholder");
                badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 7, 2), Margin = new Thickness(-1, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = badgeText };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
                text.Children.Add(title); text.Children.Add(detail); text.Children.Add(badge);
                Child = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Classes = { "tile" }, Child = Kit.Grid("Auto,*", null).Add(frame.Cell(0, 0), text.Cell(0, 1)) };
                DataContextChanged += (s, e) =>
                {
                    if (state != null) state.Changed -= Update;
                    state = DataContext as AlbumState;
                    if (state == null) return;
                    state.Changed += Update;
                    title.Text = string.IsNullOrWhiteSpace(state.Low.Album) ? Path.GetFileName(state.Low.Folder) : state.Low.Album;
                    cover.Source = ThumbnailCache.Get(state.Album?.Thumbnail);
                    ToolTip.SetTip(this, state.Low.Folder);
                    Update();
                };
            }

            private void Update()
            {
                if (state == null) return;
                detail.Text = (state.Low.Artist.Length > 0 ? state.Low.Artist + " · " : "") + state.LowCount + " of " + state.Tracks.Count + " songs at " + state.Low.Range;
                badgeText.Text = state.Status;
                badgeBrush?.Dispose(); toneBrush?.Dispose();
                badgeBrush = badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("Badge" + state.Tone));
                toneBrush = badgeText.Bind(TextBlock.ForegroundProperty, badgeText.GetResourceObservable(state.Tone));
            }
        }

        private sealed class MatchCard : Border
        {
            private readonly UpgradeWindow owner;
            private readonly Image cover = new Image { Stretch = Stretch.UniformToFill };
            private readonly TextBlock title = Kit.Label("", Kit.Small, true), detail = Kit.Label("", Kit.Tiny, cls: "muted"), score = Kit.Label("", Kit.Tiny);

            internal MatchCard(UpgradeWindow owner)
            {
                this.owner = owner;
                Width = 236; Height = 92; Padding = new Thickness(6); CornerRadius = new CornerRadius(12); BorderThickness = new Thickness(1); Cursor = new Cursor(StandardCursorType.Hand);
                Classes.Add("frost");
                var frame = new Border { Width = 78, Height = 78, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = cover, Margin = new Thickness(0, 0, 9, 0) };
                frame.Brush(Border.BackgroundProperty, "CoverPlaceholder");
                title.TextWrapping = TextWrapping.Wrap; title.MaxLines = 2; detail.TextWrapping = TextWrapping.Wrap; detail.MaxLines = 2;
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                text.Children.Add(title); text.Children.Add(detail); text.Children.Add(score);
                Child = Kit.Grid("Auto,*", "*").Add(frame.Cell(0, 0), text.Cell(0, 1));
                DataContextChanged += (s, e) =>
                {
                    if (!(DataContext is YtmAlbum album)) return;
                    title.Text = album.Title; detail.Text = album.Summary;
                    cover.Source = ThumbnailCache.Get(album.Thumbnail);
                    int value = owner.Score(album);
                    score.Text = owner.IsChosen(album) ? "✓ Chosen · " + value + "% match" : value + "% match";
                    score.Classes.Set("accent", owner.IsChosen(album));
                    this.Brush(Border.BorderBrushProperty, owner.IsChosen(album) ? "SelectedBorder" : "Border");
                    this.Named(album.Title + " on YouTube Music");
                    ToolTip.SetTip(this, album.Title + "\n" + album.Summary + "\nClick to use this album.");
                };
            }
        }

        private sealed class PairRow : Border
        {
            internal const string Columns = "34,*,74,*,118";
            private readonly UpgradeWindow owner;
            private readonly CheckBox check = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock local = Cell(), kbps = Cell(), remote = Cell(), length = Cell();
            private TrackPairing pairing;
            private bool binding;
            private IDisposable tone;

            private static TextBlock Cell() => new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 12.5 };

            internal PairRow(UpgradeWindow owner)
            {
                this.owner = owner;
                Height = 34; Padding = new Thickness(10, 0, 8, 0); Background = Brushes.Transparent;
                remote.Cursor = new Cursor(StandardCursorType.Hand);
                remote.PointerPressed += (s, e) => { if (e.ClickCount == 2) owner.Preview(pairing); };
                Child = Kit.Grid(Columns, "*").Add(check.Cell(0, 0), local.Cell(0, 1), kbps.Cell(0, 2), remote.Cell(0, 3), length.Cell(0, 4));
                check.IsCheckedChanged += (s, e) => { if (binding || pairing == null) return; pairing.Selected = check.IsChecked == true && pairing.CanReplace; owner.Toggled(); };
                DataContextChanged += (s, e) => Bind(DataContext as TrackPairing);
            }

            private void Bind(TrackPairing next)
            {
                pairing = next;
                if (pairing == null) return;
                binding = true;
                var t = pairing.Local;
                string name = t.HasTitleTag ? t.Title : Path.GetFileNameWithoutExtension(t.Path);
                local.Text = (t.Number > 0 ? t.Number + ". " : "") + name;
                kbps.Text = pairing.Replaced ? "320 ✓" : pairing.LocalKbps > 0 ? pairing.LocalKbps + " kbps" : "—";
                kbps.FontWeight = pairing.Low && !pairing.Replaced ? FontWeight.SemiBold : FontWeight.Normal;
                remote.Text = pairing.Remote == null ? "No match on this album" : pairing.Remote.Title + (pairing.Remote.MusicVideo ? "  · video" : "");
                remote.Classes.Set("muted", pairing.Remote == null);
                string length = pairing.Remote == null ? "" : Format(pairing.Remote.Duration);
                string delta = pairing.Remote == null ? "" : pairing.Quality == PairQuality.Exact ? " · same" : pairing.SecondsApart >= 999 ? "" :
                    " · " + (pairing.Remote.Duration > t.Duration ? "+" : "−") + Math.Round(pairing.SecondsApart) + " s";
                this.length.Text = length + delta;
                tone?.Dispose();
                tone = pairing.Remote != null && pairing.Quality != PairQuality.Exact ? this.length.Bind(TextBlock.ForegroundProperty, this.length.GetResourceObservable("Warning")) : null;
                if (tone == null) this.length.ClearValue(TextBlock.ForegroundProperty);
                check.IsEnabled = pairing.CanReplace && !owner.IsWorking;
                check.IsChecked = pairing.Selected && pairing.CanReplace;
                check.Named("Replace " + name);
                ToolTip.SetTip(this, t.Path + "\nYours: " + Format(t.Duration) + (pairing.LocalKbps > 0 ? " · " + pairing.LocalKbps + " kbps" : "") +
                    (pairing.Remote == null ? "\nNo matching track on the chosen YouTube Music album." :
                    "\nYouTube Music: " + pairing.Remote.Title + (pairing.Remote.Artists.Length > 0 ? " — " + pairing.Remote.Artists : "") + " · " + Format(pairing.Remote.Duration) +
                    (pairing.Remote.MusicVideo ? "\nThis entry is the official music video; its audio can include an intro or outro — compare lengths." : "") +
                    (pairing.Quality == PairQuality.Exact ? "" : "\nThe length differs from your file. Double-click the YouTube Music title to listen first.")));
                binding = false;
            }

            private static string Format(TimeSpan time) => time <= TimeSpan.Zero ? "—" : time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }
    }
}
