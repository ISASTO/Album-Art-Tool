using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // First run, keyboard shortcuts and page transitions.
    internal sealed partial class MainWindow
    {
        private Control welcomePage;
        private readonly TextBlock welcomeFolder = Kit.Label("", Kit.Body);
        private bool firstRunPending;

        /// <summary>Shortcuts shown by F1, in the order they are listed.</summary>
        internal static readonly (string Keys, string Action)[] Shortcuts =
        {
            ("Ctrl+1 … Ctrl+5", "Albums, Artists, Health, Organize, Activity"),
            ("Ctrl+,", "Settings"),
            ("Ctrl+F", "Search albums"),
            ("Ctrl+O", "Choose a different music folder"),
            ("F5", "Rescan the music folder (Shift+F5 reads every file again)"),
            ("Ctrl+Enter", "Apply the cover, or set the track artists"),
            ("Esc", "Discard the cover being previewed"),
            ("Ctrl+Z", "Undo the last cover or artist change"),
            ("F1", "Show these shortcuts"),
        };

        // Welcome: where the music is, and one clear first step. Nothing is scanned until it is clicked.
        private Control BuildWelcome()
        {
            var mark = new Image { Source = Kit.Mark, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            var title = Kit.Label("Welcome to Music Library Manager", 24, true);
            var intro = Kit.Label("Choose the folder that holds your music. The app scans it and shows what needs attention: missing covers, untitled songs, low-bitrate albums and duplicates. " +
                "Nothing changes until you pick a fix, and every change is previewed and backed up first.", Kit.Body, cls: "muted");
            intro.TextWrapping = TextWrapping.Wrap; intro.TextTrimming = TextTrimming.None; intro.Margin = new Thickness(0, 6, 0, 18);
            var folderIcon = Kit.Glyph(Icons.Folder, 18); folderIcon.Margin = new Thickness(0, 0, 10, 0);
            welcomeFolder.Named("Music folder to scan first");
            var place = Kit.Grid("Auto,*", "Auto").Add(folderIcon.Cell(0, 0), welcomeFolder.Cell(0, 1));
            var placeCard = Kit.Inset(place); placeCard.Padding = new Thickness(14, 10); placeCard.Margin = new Thickness(0, 0, 0, 16);
            var start = Kit.Button("Scan this folder", "primary").Tip("Scan the folder shown above. Afterwards, Health shows what in your library needs attention.");
            var other = Kit.Button("Choose another folder…").Tip("Pick the top folder that contains your albums, then scan it.");
            foreach (var b in new[] { start, other }) { b.Height = 38; b.Padding = new Thickness(18, 2); b.HorizontalAlignment = HorizontalAlignment.Left; b.FontSize = Kit.Body; }
            start.Click += async (s, e) => await StartFirstScanAsync(false);
            other.Click += async (s, e) => await StartFirstScanAsync(true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(start); buttons.Children.Add(other);
            var stack = new StackPanel();
            foreach (var c in new Control[] { mark, title, intro, placeCard, buttons }) stack.Children.Add(c);
            var card = Kit.Frost(stack, new Thickness(36, 32, 36, 32));
            card.MaxWidth = 640; card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            return new Panel { Children = { card } };
        }

        private void ShowWelcome()
        {
            welcomePage ??= BuildWelcome();
            if (!pageHost.Children.Contains(welcomePage)) AddPage(welcomePage);
            welcomeFolder.Text = Kit.MiddleEllipsis(Root, 70); welcomeFolder.Tip(Root);
            firstRunPending = true;
            ShowPage(Page.Welcome);
        }

        private async Task StartFirstScanAsync(bool choose)
        {
            if (choose)
            {
                var picked = await PickFolderAsync("Choose the top folder containing your music", Root);
                if (picked == null) return;
                Root = picked; welcomeFolder.Text = Kit.MiddleEllipsis(Root, 70); welcomeFolder.Tip(Root);
            }
            ShowPage(Page.Albums);
            await ScanAsync();
        }

        // After the first scan, the summary of what needs attention is the most useful place to land.
        private void FinishFirstRun()
        {
            if (!firstRunPending) return;
            firstRunPending = false;
            if (hasScanned && library.Albums.Count > 0) Navigate(Page.Health);
        }

        private Window shortcutsWindow;

        internal Window ShowShortcuts()
        {
            if (shortcutsWindow != null) { shortcutsWindow.Activate(); return shortcutsWindow; }
            var window = shortcutsWindow = new Window { Title = "Keyboard shortcuts", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var rows = Kit.Grid("170,*", string.Join(",", System.Linq.Enumerable.Repeat("32", Shortcuts.Length)));
            for (int i = 0; i < Shortcuts.Length; i++)
            {
                var keys = Kit.Label(Shortcuts[i].Keys, Kit.Small, true);
                rows.Add(keys.Cell(i, 0), Kit.Label(Shortcuts[i].Action, Kit.Body, cls: "muted").Cell(i, 1));
            }
            rows.Named("Keyboard shortcuts");
            var close = Kit.Button("Close", "primary"); close.Width = 96; close.Height = 32; close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 14, 0, 0);
            close.Click += (s, e) => window.Close();
            var layout = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
            layout.Children.Add(Kit.Label("Keyboard shortcuts", 17.33, true).Also(t => t.Margin = new Thickness(0, 0, 0, 10)));
            layout.Children.Add(rows); layout.Children.Add(close);
            Kit.Prepare(window, layout);
            window.Closed += (s, e) => { if (shortcutsWindow == window) shortcutsWindow = null; };
            window.Show(this);
            return window;
        }

        // Pages fade in over 150 ms (skipped when Windows asks for fewer animations). A dispatcher timer
        // drives it, like the appearance wipe, so it also finishes on machines without a GPU clock.
        internal static bool PageFade = true;
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
        private DispatcherTimer fadeTimer;
        private Control fading;

        private void FadeIn(Control page)
        {
            fadeTimer?.Stop();
            if (fading != null) fading.Opacity = 1;
            fading = null;
            if (page == null) return;
            if (!PageFade || !Appearance.MotionAllowed) { page.Opacity = 1; return; }
            fading = page; page.Opacity = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            fadeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (s, e) =>
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds);
                page.Opacity = 1 - Math.Pow(1 - t, 3);
                if (t < 1) return;
                fadeTimer.Stop(); page.Opacity = 1; fading = null;
            });
            fadeTimer.Start();
        }
    }
}
