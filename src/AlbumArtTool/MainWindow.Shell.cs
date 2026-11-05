using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // One window: a sidebar of pages instead of a header full of buttons and a stack of tool windows.
    internal sealed partial class MainWindow
    {
        internal enum Page { Albums, Artists, AddMusic, Health, Organize, YouTube, Activity, Settings, Welcome }

        private sealed class NavItem
        {
            internal Page Page;
            internal Button Button;
            internal Border Badge;
            internal TextBlock BadgeText;
        }

        private readonly Dictionary<Page, NavItem> nav = new Dictionary<Page, NavItem>();
        private readonly Panel pageHost = new Panel();
        private readonly TextBlock pageTitle = Kit.Label("Albums", 20, true), pageSubtitle = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Button closePage = Kit.Button("Close", "ghost");
        private Page current = Page.Albums;
        private Control libraryView, viewChips;
        private HealthPage healthPage;
        private OrganizePage organizePage;
        private YouTubeMusicPage youtubePage;
        private SettingsPanel settingsPage;
        private Control activityPage;
        private int libraryVersion, healthVersion = -1;

        internal Page CurrentPage => current;
        internal HealthPage Health => healthPage;
        internal OrganizePage Organize => organizePage;
        internal DuplicatePanel Duplicates => healthPage?.Duplicates;
        internal Control LibraryView => libraryView;
        internal string Badge(Page page) => nav.TryGetValue(page, out var item) && item.Badge.IsVisible ? item.BadgeText.Text : "";

        private Control BuildSidebar()
        {
            var mark = new Image { Source = Kit.Mark, Width = 26, Height = 26, Margin = new Thickness(0, 0, 9, 0) };
            var name = Kit.Label("Music Library Manager", 13, true);
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 18) };
            brand.Children.Add(mark); brand.Children.Add(name);
            brand.Named("Music Library Manager");
            var pages = new StackPanel { Spacing = 2 };
            pages.Children.Add(AddNav(Page.Albums, Icons.Album, "Albums", "Add, change and standardize album covers (Ctrl+1)."));
            pages.Children.Add(AddNav(Page.Artists, Icons.Artist, "Artists", "Collapse guest credits into each album's main artist (Ctrl+2)."));
            pages.Children.Add(AddNav(Page.AddMusic, Icons.AddMusic, "Add music", "Search YouTube Music for artists, albums and songs and add them to your library as MP3 320 (Ctrl+3)."));
            pages.Children.Add(AddNav(Page.Health, Icons.Health, "Health", "Missing titles, extra pictures, low-bitrate albums and duplicates, each with a fix (Ctrl+4)."));
            pages.Children.Add(AddNav(Page.Organize, Icons.Organize, "Organize", "Convert, standardize, tidy and reorganize the whole library with presets. Every change is previewed and can be undone (Ctrl+5)."));
            pages.Children.Add(AddNav(Page.YouTube, Icons.Download, "YouTube Music", "Replace low-bitrate songs with fresh downloads from YouTube Music. Opened from Health."));
            pages.Children.Add(AddNav(Page.Activity, Icons.Activity, "Activity", "Work in progress, recent results, problems and Undo (Ctrl+6)."));
            nav[Page.YouTube].Button.IsVisible = false;
            var settings = AddNav(Page.Settings, Icons.Settings, "Settings", "Appearance, music and backup folders, helper programs and app updates (Ctrl+,).");
            // Where the music is, at the foot of the sidebar.
            browse.HorizontalAlignment = HorizontalAlignment.Stretch; browse.HorizontalContentAlignment = HorizontalAlignment.Left; browse.Height = 34;
            scan.Height = 34;
            var place = Kit.Grid("*,Auto", "Auto").Add(browse.Cell(0, 0), scan.Cell(0, 1));
            var divider = new Border { Height = 1, Margin = new Thickness(6, 8, 6, 8) };
            divider.Brush(Border.BackgroundProperty, "Border");
            var foot = new StackPanel { Spacing = 2 };
            foot.Children.Add(settings); foot.Children.Add(divider); foot.Children.Add(Kit.Caption("Music folder").Also(c => c.Margin = new Thickness(10, 0, 0, 2))); foot.Children.Add(place);
            var dock = new DockPanel();
            DockPanel.SetDock(brand, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(brand); dock.Children.Add(foot); dock.Children.Add(pages);
            var panel = new Border { Child = dock, Padding = new Thickness(10, 16, 10, 12), BorderThickness = new Thickness(0, 0, 1, 0) };
            panel.Brush(Border.BackgroundProperty, "Sidebar"); panel.Brush(Border.BorderBrushProperty, "Border");
            return panel;
        }

        private Button AddNav(Page page, string icon, string label, string tip)
        {
            var badgeText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Child = badgeText, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
            badge.Brush(Border.BackgroundProperty, "BadgeAccent");
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            var content = Kit.Grid("Auto,*,Auto", "*").Add(Kit.Glyph(icon, 16).Cell(0, 0), text.Cell(0, 1), badge.Cell(0, 2));
            var button = new Button { Content = content, Classes = { "nav" } }.Named(label).Tip(tip);
            Avalonia.Automation.AutomationProperties.SetControlTypeOverride(button, Avalonia.Automation.Peers.AutomationControlType.TabItem);
            button.Click += (s, e) => Navigate(page);
            nav[page] = new NavItem { Page = page, Button = button, Badge = badge, BadgeText = badgeText };
            return button;
        }

        /// <summary>Shows a page. Albums and Artists share the album list and switch its details pane.</summary>
        internal void Navigate(Page page)
        {
            if (page == Page.Albums && showingArtists) { if (!busy) SwitchView(view); return; }
            if (page == Page.Artists && !showingArtists) { if (!busy) SwitchArtists(); return; }
            if (page == Page.Health && !EnsureHealth()) return;
            // The sidebar returns to the folder Organize was last on (New music after Tidy up), switchable on the page.
            if (page == Page.Organize) EnsureOrganize(organizePage != null && organizePage.WorksOnNewMusic ? organizePage.Root : null);
            if (page == Page.AddMusic) EnsureAddMusic().Refresh();
            if (page == Page.Settings) EnsureSettings().Refresh();
            if (page == Page.YouTube && youtubePage == null) return;
            ShowPage(page);
        }

        private void ShowPage(Page page)
        {
            current = page;
            Control shown = page == Page.Albums || page == Page.Artists ? libraryView : page == Page.Health ? (Control)healthPage : page == Page.Organize ? (Control)organizePage :
                page == Page.YouTube ? (Control)youtubePage : page == Page.Activity ? activityPage : page == Page.Welcome ? welcomePage : page == Page.AddMusic ? addMusicPage : (Control)settingsPage;
            bool changed = !shown.IsVisible;
            foreach (Control child in pageHost.Children) child.IsVisible = ReferenceEquals(child, shown);
            if (changed) FadeIn(shown);
            if (viewChips != null) viewChips.IsVisible = !showingArtists;
            foreach (var item in nav.Values) item.Button.Classes.Set("selected", item.Page == page);
            closePage.IsVisible = page == Page.YouTube;
            renameArtist.IsVisible = page == Page.Artists;
            if (page == Page.YouTube) youtubePage.Start();
            if (page == Page.Organize) organizePage.Refresh();
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            int albumsCount = library.Albums.Count, tracks = library.Albums.Sum(a => a.Tracks.Count);
            switch (current)
            {
                case Page.Albums: pageTitle.Text = "Albums"; pageSubtitle.Text = !hasScanned ? "Scanning your music folder…" : albumsCount + (albumsCount == 1 ? " album · " : " albums · ") + tracks + (tracks == 1 ? " track" : " tracks"); break;
                case Page.Artists: pageTitle.Text = "Artists"; pageSubtitle.Text = "Collapse guest credits into each album's main artist. Already-correct tracks are left alone."; break;
                case Page.Health: pageTitle.Text = "Library health"; pageSubtitle.Text = "Based on the last scan. Nothing changes until you choose a fix and apply it."; break;
                case Page.Organize:
                    pageTitle.Text = "Organize";
                    pageSubtitle.Text = organizePage != null && organizePage.WorksOnNewMusic
                        ? "Tidy up New music before it joins your library: pick your preset, preview, apply, then go back to Add music."
                        : "Choose what to change in " + Kit.MiddleEllipsis(organizePage?.Root ?? Root, 60) + ". Every change is previewed first and every original is backed up.";
                    break;
                case Page.AddMusic: pageTitle.Text = "Add music"; pageSubtitle.Text = "Search YouTube Music and add albums or songs. They download as MP3 320 to New music, ready to tidy and add to your library."; break;
                case Page.YouTube: pageTitle.Text = "Replace from YouTube Music"; pageSubtitle.Text = "Checked songs are downloaded at YouTube Music's best free quality and saved as MP3 320 with your tags and cover. Originals are backed up first."; break;
                case Page.Activity: pageTitle.Text = "Activity"; pageSubtitle.Text = "Work in progress, recent results, problems and Undo."; break;
                case Page.Welcome: pageTitle.Text = ""; pageSubtitle.Text = ""; break;
                default: pageTitle.Text = "Settings"; pageSubtitle.Text = "Appearance, folders, helper programs and updates."; break;
            }
        }

        private void AddPage(Control page)
        {
            page.IsVisible = false;
            pageHost.Children.Add(page);
        }

        private bool EnsureHealth()
        {
            if (!hasScanned || library.Albums.Count == 0) return false;
            if (healthPage == null)
            {
                healthPage = new HealthPage(() => library.Albums.ToArray(), info => StartExplorer(info), () => ShowOrganize().PreviewTitleFill(), () => ShowOrganize().PreviewArtworkFix(),
                    result => { ShowUpgrade(result); return Task.CompletedTask; }, () => !busy && !maintenanceRunning && library.Albums.Count > 0, () => ShowOrganize().PreviewAlbumArtistFill(), () => ShowOrganize().PreviewShrinkCovers());
                AddPage(healthPage);
                healthVersion = libraryVersion;
            }
            else if (healthVersion != libraryVersion && !healthPage.IsScanning) { healthPage.Check(); healthVersion = libraryVersion; }
            return true;
        }

        private OrganizePage EnsureOrganize(string folder = null)
        {
            // Organize works on the music folder, or on New music before it joins the library. A different folder gets a
            // fresh page, unless a run is still going in the old one.
            folder ??= organizePage != null && organizePage.IsWorking ? organizePage.Root : Root;
            if (organizePage != null && !organizePage.IsWorking && !string.Equals(organizePage.Root, folder, StringComparison.OrdinalIgnoreCase))
            { pageHost.Children.Remove(organizePage); organizePage = null; }
            if (organizePage == null)
            {
                bool library = string.Equals(folder, Root, StringComparison.OrdinalIgnoreCase);
                organizePage = new OrganizePage(folder,
                    () => !busy && !queueRunning && !maintenanceRunning && pending == null && (healthPage == null || !healthPage.IsScanning) && (library || addMusicPage == null || !addMusicPage.Queue.IsRunning),
                    value => { maintenanceRunning = value; UpdateQueueControls(); },
                    library ? async root => { lastEdit = null; Root = root; await ScanAsync(); } : _ => { addMusicPage?.Refresh(); return Task.CompletedTask; },
                    () => Navigate(Page.Settings));
                organizePage.Blocked = () => pending != null ? "!Apply or discard the cover waiting on the Albums page first." :
                    busy ? (writing ? "the album update" : "the library scan") : queueRunning ? "the album covers being saved" : maintenanceRunning ? "another library task" :
                    healthPage?.IsScanning == true ? "the duplicate scan" : !library && addMusicPage?.Queue.IsRunning == true ? "the downloads" : null;
                organizePage.SwitchFolder = f => ShowOrganize(f);
                organizePage.RenameArtists = f => _ = ShowRenameArtistAsync(f);
                AddPage(organizePage);
            }
            organizePage.SetFolders(Root, Management.AddMusic.Folder);
            return organizePage;
        }

        private readonly Button renameArtist = Kit.IconButton(Icons.Artist, "Rename artists…", "ghost");
        private RenameArtistWindow renameWindow;

        /// <summary>The rename dialog; its Preview opens Organize with the rename on its own.</summary>
        internal RenameArtistWindow ShowRenameArtist() => ShowRenameArtist(Root, library.Albums);

        /// <summary>Rename artists in another folder (New music): its songs are read first, then the same dialog opens.</summary>
        internal async Task<RenameArtistWindow> ShowRenameArtistAsync(string folder)
        {
            if (string.Equals(folder, Root, StringComparison.OrdinalIgnoreCase)) return ShowRenameArtist();
            if (renameWindow != null) { renameWindow.Activate(); return renameWindow; }
            status.Text = "Reading the songs in " + Kit.MiddleEllipsis(folder, 50) + "…";
            ScanResult scan;
            try { scan = await Task.Run(() => scanner.Scan(folder, System.Threading.CancellationToken.None, useCache: false)); }
            catch (Exception e) { status.Text = "Could not read " + folder + ": " + e.Message; return null; }
            status.Text = scan.Albums.Count == 0 ? "There are no songs in " + Kit.MiddleEllipsis(folder, 50) + " to rename." : "Ready";
            return ShowRenameArtist(folder, scan.Albums);
        }

        private RenameArtistWindow ShowRenameArtist(string folder, List<Album> albums)
        {
            if (renameWindow != null) { renameWindow.Activate(); return renameWindow; }
            if (busy || albums.Count == 0) return null;
            bool newMusic = !string.Equals(folder, Root, StringComparison.OrdinalIgnoreCase);
            var window = renameWindow = new RenameArtistWindow(albums, folderName: newMusic ? "New music" : null);
            window.Start = (from, to, both) => _ = RenameAsync(EnsureOrganize(folder), from, to, both);
            window.Closed += (s, e) => { if (renameWindow == window) renameWindow = null; };
            window.Show(this);
            return window;
        }

        private Task RenameAsync(OrganizePage page, string[] from, string to, bool both) { ShowPage(Page.Organize); return page.PreviewArtistRename(from, to, both); }

        private AddMusicPage addMusicPage;
        internal AddMusicPage AddMusicView => addMusicPage;

        private AddMusicPage EnsureAddMusic()
        {
            if (addMusicPage != null) return addMusicPage;
            var music = YouTubeMusicForTest ?? (addMusicService ??= new Online.YouTubeMusic());
            var download = DownloadForTest ?? ((url, folder, token, progress) => Management.YtDlp.Download(Management.YtDlp.Find(), Management.Encoder.Find(), url, folder, token, progress));
            AddMusicPage.DownloadsInjected = DownloadForTest != null;
            var queue = new Management.DownloadQueue(() => Management.AddMusic.Folder, Management.Encoder.Find, download, music.ThumbnailAsync);
            addMusicPage = new AddMusicPage(music, queue, () => Root, () => !busy && !queueRunning && !maintenanceRunning && (organizePage == null || !organizePage.IsWorking),
                folder => { ShowOrganize(folder); return Task.CompletedTask; }, async () => { lastEdit = null; await ScanAsync(); }, () => Navigate(Page.Settings), info => StartExplorer(info));
            addMusicPage.RenameArtists = folder => ShowRenameArtistAsync(folder);
            addMusicPage.ChangeCovers = ShowNewMusicCoversAsync;
            addMusicPage.Changed += UpdateBadges;
            AddPage(addMusicPage);
            return addMusicPage;
        }

        private Online.YouTubeMusic addMusicService;
        private NewMusicCoversWindow coversWindow;

        /// <summary>The cover window for the albums in New music.</summary>
        internal async Task<NewMusicCoversWindow> ShowNewMusicCoversAsync()
        {
            if (coversWindow != null) { coversWindow.Activate(); return coversWindow; }
            var window = coversWindow = new NewMusicCoversWindow(Management.AddMusic.Folder, coverSearch);
            window.Changed += () => addMusicPage?.Refresh();
            window.Closed += (s, e) => { if (coversWindow == window) coversWindow = null; };
            window.Show(this);
            await window.LoadAsync();
            return window;
        }
        internal AddMusicPage ShowAddMusic() { Navigate(Page.AddMusic); return addMusicPage; }

        private SettingsPanel EnsureSettings()
        {
            if (settingsPage == null)
            {
                settingsPage = new SettingsPanel(() => Root, ChangeFolderAsync, GetUpdatesAsync) { StartExplorer = info => StartExplorer(info), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
                AddPage(settingsPage);
            }
            return settingsPage;
        }

        internal OrganizePage ShowOrganize(string folder = null) { EnsureOrganize(folder); ShowPage(Page.Organize); return organizePage; }
        internal SettingsPanel ShowSettings() { Navigate(Page.Settings); return settingsPage; }

        /// <summary>Closes the YouTube Music page (refused while songs are being replaced).</summary>
        internal bool CloseYouTube()
        {
            if (youtubePage == null) return true;
            if (!youtubePage.Shutdown()) { ShowPage(Page.YouTube); return false; }
            pageHost.Children.Remove(youtubePage); youtubePage = null;
            nav[Page.YouTube].Button.IsVisible = false;
            if (current == Page.YouTube) Navigate(healthPage != null ? Page.Health : Page.Albums);
            return true;
        }

        private void UpdateBadges()
        {
            int missing = library.Albums.Count(a => a.MissingCount > 0), artistChanges = library.Albums.Count(a => a.ArtistChangeCount > 0);
            SetBadge(Page.Albums, hasScanned && missing > 0 ? missing.ToString() : null, missing + " albums are missing cover art");
            SetBadge(Page.Artists, hasScanned && artistChanges > 0 ? artistChanges.ToString() : null, artistChanges + " albums have guest credits to collapse");
            int downloads = addMusicPage?.Queue.Waiting ?? 0;
            SetBadge(Page.AddMusic, downloads > 0 ? downloads.ToString() : null, downloads + " songs waiting to download");
            SetBadge(Page.Activity, QueuedCount > 0 ? QueuedCount.ToString() : issues.Count > 0 ? issues.Count.ToString() : null,
                QueuedCount > 0 ? QueuedCount + " albums queued or in progress" : issues.Count + " problems to review", warning: QueuedCount == 0 && issues.Count > 0);
            nav[Page.Health].Button.IsEnabled = !busy && hasScanned && library.Albums.Count > 0;
            nav[Page.Artists].Button.IsEnabled = !busy;
            nav[Page.Albums].Button.IsEnabled = !busy || !showingArtists;
        }

        private void SetBadge(Page page, string text, string tip, bool warning = false)
        {
            var item = nav[page];
            item.Badge.IsVisible = text != null;
            item.BadgeText.Text = text ?? "";
            item.Badge.Brush(Border.BackgroundProperty, warning ? "BadgeWarning" : "BadgeAccent");
            ToolTip.SetTip(item.Badge, text == null ? null : tip);
            Avalonia.Automation.AutomationProperties.SetHelpText(item.Button, text == null ? null : tip);
        }
    }

    internal static class ControlExtensions
    {
        internal static T Also<T>(this T value, Action<T> change) { change(value); return value; }
    }
}
