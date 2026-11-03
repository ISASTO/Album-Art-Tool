using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

namespace AlbumArtTool.Ui
{
    internal sealed class CoverSearchPanel : UserControl
    {
        private readonly ICoverSearch service;
        private readonly TextBox query = new TextBox();
        private readonly CheckBox automatic = Kit.Check("Online suggestions", true);
        private readonly Button find = Kit.Button("Search");
        private readonly Button bandcamp = Kit.Button("Bandcamp ↗");
        private readonly TextBlock summary = Kit.Label("", 11.33, cls: "muted");
        private readonly WrapPanel choices = new WrapPanel { Margin = new Thickness(3) };
        private readonly ScrollViewer viewport;
        private CancellationTokenSource cancellation;
        private Album selected;
        private int version;
        private string signature = "";
        private bool hasResults, active;
        internal Task CurrentSearch { get; private set; } = Task.CompletedTask;
        internal Task CurrentApply { get; private set; } = Task.CompletedTask;
        internal Func<string, CoverCandidate, Task> UseCover { get; set; }
        internal Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        internal List<CoverCandidate> DisplayedCandidates { get; private set; } = new List<CoverCandidate>();
        internal IEnumerable<CoverCard> Cards => choices.Children.OfType<CoverCard>();

        internal CoverSearchPanel(ICoverSearch service)
        {
            this.service = service;
            query.Margin = new Thickness(0, 3, 5, 3); query.FontSize = Kit.Small;
            query.Named("Online artwork search query");
            var searchRow = Kit.Grid("*,72,103", "*").Add(query.Cell(0, 0), find.Cell(0, 1), bandcamp.Cell(0, 2));
            automatic.Classes.Add("muted"); automatic.HorizontalAlignment = HorizontalAlignment.Right;
            var instructions = Kit.Grid("*,Auto", "*").Add(Kit.Label("Click a cover to queue it.", Kit.Small, cls: "muted").Cell(0, 0), automatic.Cell(0, 1));
            viewport = new ScrollViewer { Content = choices, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            viewport.SizeChanged += (s, e) => { foreach (var card in Cards) card.FitHeight(viewport.Bounds.Height); };
            var results = Kit.Inset(viewport).Named("Artwork search results");
            results.Padding = new Thickness(4);
            var layout = Kit.Grid("*", "30,25,*,22").Add(searchRow.Cell(0, 0), instructions.Cell(1, 0), results.Cell(2, 0), summary.Cell(3, 0));
            Content = layout;
            find.Click += (s, e) => CurrentSearch = SearchAsync(true);
            query.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; CurrentSearch = SearchAsync(true); } };
            automatic.IsCheckedChanged += (s, e) =>
            {
                if (automatic.On() && active) CurrentSearch = SearchAsync(false);
                else if (!automatic.On()) { Cancel(); summary.Text = "Automatic search off. Click Search for a one-time lookup."; }
            };
            bandcamp.Click += (s, e) => OpenPage("https://bandcamp.com/search?q=" + Uri.EscapeDataString(query.Text ?? "") + "&item_type=a");
            bandcamp.Tip("Open the same album search on Bandcamp in your browser.");
            automatic.Tip("Search only the selected album. Turn off to work offline. Music files are never uploaded.");
        }

        internal void SetAlbum(Album album, bool active)
        {
            bool changed = selected?.Key != album?.Key;
            selected = album;
            SetActive(active);
            if (changed)
            {
                Cancel(); ClearChoices(); hasResults = false; signature = "";
                query.Text = album == null ? "" : CoverQuery.DefaultText(album.Title, album.Artist);
                summary.Text = album == null ? "Select an album to find cover art." : "Ready to find covers.";
            }
            if (this.active && automatic.On() && !hasResults)
                CurrentSearch = SearchAsync(false, true);
        }

        internal void SetActive(bool value)
        {
            active = value && selected != null;
            // A read-only text box keeps its color and still allows copying.
            query.IsReadOnly = !active;
            find.IsEnabled = bandcamp.IsEnabled = automatic.IsEnabled = active;
            foreach (var card in Cards) card.SetActive(active);
        }

        internal async Task SearchAsync(bool refresh, bool debounce = false)
        {
            if (selected == null || !active || string.IsNullOrWhiteSpace(query.Text)) return;
            Cancel();
            var source = cancellation = new CancellationTokenSource();
            var token = source.Token;
            int request = version;
            string key = selected.Key;
            var requestQuery = new CoverQuery { Title = selected.Title, Artist = selected.Artist, Text = query.Text.Trim() };
            hasResults = true;
            summary.Text = "Searching Bandcamp, Deezer and Cover Art Archive…";
            if (refresh) { ClearChoices(); signature = ""; }
            var progress = new Progress<CoverSearchUpdate>(update =>
            {
                if (request != version || selected?.Key != key || token.IsCancellationRequested) return;
                ShowResults(update, key);
            });
            try
            {
                if (debounce) await Task.Delay(450, token);
                await Task.Run(() => service.SearchAsync(requestQuery, refresh, progress, token), token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (CoverSearchService.Expected(e))
            { if (request == version) { summary.Text = "Search unavailable. Try again or choose a local image."; summary.Tip(e.Message); } }
        }

        private void ShowResults(CoverSearchUpdate update, string key)
        {
            string next = string.Join("\n", update.Candidates.Select(c => c.ImageUrl));
            if (next != signature)
            {
                signature = next; ClearChoices();
                DisplayedCandidates = update.Candidates.ToList();
                foreach (var candidate in DisplayedCandidates)
                {
                    var card = new CoverCard(candidate);
                    card.FitHeight(viewport.Bounds.Height);
                    card.SetActive(active);
                    card.Apply += () =>
                    {
                        if (!active || selected?.Key != key || UseCover == null) return;
                        CurrentApply = ApplyChoiceAsync(key, candidate);
                    };
                    card.OpenSource += () => OpenPage(candidate.PageUrl);
                    choices.Children.Add(card);
                }
            }
            foreach (var card in Cards) card.UpdateResolution();
            summary.Text = update.Candidates.Count == 0
                ? update.Complete ? "No covers found. Edit the query or try Bandcamp in your browser." : "Searching for artwork…"
                : update.Candidates.Count + " covers" + (update.Complete ? " · Best matches first." : " · More sources loading…");
            if (update.Complete && update.Issues.Count > 0) summary.Text += " Some sources unavailable.";
            summary.Tip(string.Join(Environment.NewLine, update.Issues));
        }

        private async Task ApplyChoiceAsync(string key, CoverCandidate candidate)
        {
            try { await UseCover(key, candidate); }
            catch (Exception e) { summary.Text = "Could not apply this cover."; summary.Tip(e.Message); }
        }

        private void OpenPage(string url)
        {
            if (!WebTransport.AllowedUrl(url)) return;
            try { OpenUrl(url); }
            catch (Exception e) { summary.Text = "Could not open your browser."; summary.Tip(e.Message); }
        }

        private void Cancel()
        {
            version++;
            var old = cancellation; cancellation = null;
            old?.Cancel(); old?.Dispose();
        }

        private void ClearChoices()
        {
            foreach (var card in Cards.ToArray()) card.Release();
            choices.Children.Clear(); DisplayedCandidates.Clear();
        }

        internal void Shutdown() { Cancel(); ClearChoices(); }
    }

    internal sealed class CoverCard : Border
    {
        private readonly CoverCandidate candidate;
        private readonly Image image;
        private readonly TextBlock resolution;
        private readonly Button use;
        internal event Action Apply, OpenSource;
        internal TextBlock Resolution => resolution;
        internal Button UseButton => use;

        internal CoverCard(CoverCandidate candidate)
        {
            this.candidate = candidate;
            Classes.Add("frost"); CornerRadius = new CornerRadius(12);
            Width = 166; Margin = new Thickness(5); Padding = new Thickness(6, 5, 6, 5);
            string name = candidate.Title + " by " + candidate.Artist + " from " + candidate.Source;
            this.Named(name);
            image = new Image { Stretch = Stretch.Uniform, Height = 104, Cursor = new Cursor(StandardCursorType.Hand), Source = Kit.Decode(candidate.Thumbnail) };
            image.Named("Apply " + name).Tip(candidate.Title + "\n" + candidate.Artist + "\nClick to queue this cover, then continue with another album.");
            var title = Kit.Label(candidate.Title, 11.33, true).Tip(candidate.Title);
            var artist = Kit.Label(candidate.Artist, Kit.Tiny, cls: "muted");
            var origin = new Button { Content = candidate.Source + (candidate.MatchScore < 80 ? " · Similar" : ""), Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, FontSize = Kit.Tiny }.Tip(candidate.PageUrl);
            origin.Brush(Button.ForegroundProperty, "Accent");
            resolution = Kit.Label(candidate.Resolution, Kit.Tiny).Named("Full-size cover resolution")
                .Tip("Dimensions of the full-size image at the source. Embedded artwork is stored as one JPEG front cover of at most 600 × 600 (cover.jpg files keep up to 1600 px).");
            use = Kit.Button("Apply this cover", "card"); use.Height = 24;
            title.Cursor = artist.Cursor = new Cursor(StandardCursorType.Hand);
            image.PointerPressed += (s, e) => Apply?.Invoke();
            title.PointerPressed += (s, e) => Apply?.Invoke();
            artist.PointerPressed += (s, e) => Apply?.Invoke();
            use.Click += (s, e) => Apply?.Invoke();
            origin.Click += (s, e) => OpenSource?.Invoke();
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(image); stack.Children.Add(title); stack.Children.Add(artist); stack.Children.Add(origin); stack.Children.Add(resolution); stack.Children.Add(use);
            Child = stack;
        }

        // Keep the action visible on smaller laptop screens; extra results still scroll.
        internal void FitHeight(double viewportHeight) => image.Height = Math.Max(46, Math.Min(104, viewportHeight - 122));
        internal void UpdateResolution() => resolution.Text = candidate.Resolution;
        internal void SetActive(bool value) { use.IsEnabled = value; image.Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default; }
        internal void Release() { var old = image.Source as IDisposable; image.Source = null; old?.Dispose(); }
    }
}
