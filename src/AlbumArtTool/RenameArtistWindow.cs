using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Renames one or more artists across the whole library to a single name: every Artist (and optionally Album Artist)
    // entry with one of the ticked names. For example an artist who released under four names becomes one artist.
    // A big change, so it says how many songs it touches, warns, and hands over to Organize to preview first.
    internal sealed class RenameArtistWindow : Window
    {
        internal sealed class Choice
        {
            internal string Key, Name;
            internal int Songs, Albums;
            internal bool Checked { get; private set; }
            internal event Action Changed;
            internal void Set(bool value) { if (Checked == value) return; Checked = value; Changed?.Invoke(); }
        }

        private readonly List<Choice> choices = new List<Choice>();
        private readonly Album[] library;
        private readonly TextBox find = new TextBox { Watermark = "Find artists in your library" };
        private readonly ListBox list = new ListBox { Classes = { "grid" }, Height = 230 };
        private readonly TextBlock picked = Kit.Label("", Kit.Small, cls: "muted");
        private readonly TextBox to = new TextBox { Watermark = "The one name to use everywhere" };
        private readonly CheckBox albumArtists = Kit.Check("Also change Album Artist tags", true);
        private readonly TextBlock summary = Kit.Label("", Kit.Body, true);
        private readonly Button preview = Kit.Button("Preview changes", "primary"), cancel = Kit.Button("Cancel"), clear = Kit.Button("Clear selection", "link");
        internal Action<string[], string, bool> Start;

        internal RenameArtistWindow(IEnumerable<Album> albums, IEnumerable<string> preselected = null)
        {
            library = albums.ToArray();
            Title = "Rename artists"; Width = 600; SizeToContent = SizeToContent.Height; CanResize = false; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var byKey = new Dictionary<string, Choice>(StringComparer.Ordinal);
            foreach (var album in library)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var track in album.Tracks)
                    foreach (string name in track.Performers.Concat(track.AlbumArtists).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
                    {
                        string key = ArtistRules.Normalize(name);
                        if (!byKey.TryGetValue(key, out var choice)) byKey[key] = choice = new Choice { Key = key, Name = name };
                        if (seen.Add(key)) choice.Albums++;
                    }
            }
            foreach (var track in library.SelectMany(a => a.Tracks))
                foreach (string key in track.Performers.Concat(track.AlbumArtists).Where(n => !string.IsNullOrWhiteSpace(n)).Select(ArtistRules.Normalize).Distinct(StringComparer.Ordinal))
                    if (byKey.TryGetValue(key, out var choice)) choice.Songs++;
            choices.AddRange(byKey.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
            foreach (var choice in choices) choice.Changed += Update;
            foreach (string name in preselected ?? Enumerable.Empty<string>())
                if (byKey.TryGetValue(ArtistRules.Normalize(name), out var choice)) choice.Set(true);

            find.Named("Find artists to rename").Tip("Type part of a name to narrow the list, then tick every name to change. All ticked names become the new name.");
            find.TextChanged += (s, e) => Filter();
            list.Named("Artists to rename");
            list.ItemTemplate = new FuncDataTemplate<Choice>((c, scope) => new ChoiceRow(), true);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            clear.Tip("Untick every artist."); clear.Click += (s, e) => { foreach (var c in choices) c.Set(false); };
            to.Named("New artist name").Tip("The name every ticked artist becomes, in every matching Artist (and Album Artist) tag.");
            albumArtists.Named("Also change Album Artist tags").Tip("Also rename them where they are the album artist, so players list those albums under the one name.");
            summary.Named("Rename summary"); summary.TextWrapping = TextWrapping.Wrap; summary.TextTrimming = TextTrimming.None;
            var warning = Kit.Label("This is a big change: it rewrites the artist tags of every matching song in your library. Organize lists each change before anything is written, " +
                "originals are backed up, and Organize › Preview Undo restores them.", Kit.Small);
            warning.TextWrapping = TextWrapping.Wrap; warning.TextTrimming = TextTrimming.None; warning.Named("Rename warning");
            var warningIcon = Kit.Glyph(Icons.Warning, 18); warningIcon.Margin = new Thickness(0, 0, 10, 0); warningIcon.VerticalAlignment = VerticalAlignment.Top;
            var warningBox = new Border { Child = Kit.Grid("Auto,*", "Auto").Add(warningIcon.Cell(0, 0), warning.Cell(0, 1)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Margin = new Thickness(0, 12, 0, 0) };
            warningBox.Brush(Border.BackgroundProperty, "BadgeWarning");
            preview.Named("Preview artist rename").Tip("Open Organize with this rename only and list every song it changes. Nothing is written until you click Apply there.");
            cancel.Click += (s, e) => Close();
            preview.Click += (s, e) =>
            {
                var names = Selected; string next = (to.Text ?? "").Trim();
                if (!Valid(names, next)) return;
                Close(); Start?.Invoke(names.Select(c => c.Name).ToArray(), next, albumArtists.On());
            };
            to.TextChanged += (s, e) => Update(); albumArtists.IsCheckedChanged += (s, e) => Update();
            foreach (var b in new[] { preview, cancel }) { b.Height = 34; b.Padding = new Thickness(16, 2); }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel); buttons.Children.Add(preview);
            var pickedRow = Kit.Grid("*,Auto", "Auto").Add(picked.Cell(0, 0), clear.Cell(0, 1));
            var intro = Kit.Label("Tick every name to change (for example all the names one artist released under) and type the one name they should become.", Kit.Small, cls: "muted");
            intro.TextWrapping = TextWrapping.Wrap; intro.TextTrimming = TextTrimming.None;
            var layout = new StackPanel { Margin = new Thickness(22, 18, 22, 16), Spacing = 4 };
            layout.Children.Add(Kit.Label("Rename artists everywhere", 17.33, true));
            layout.Children.Add(intro);
            layout.Children.Add(Kit.Label("Artists", Kit.Small, cls: "muted").Also(l => l.Margin = new Thickness(0, 10, 0, 0))); layout.Children.Add(find);
            layout.Children.Add(Kit.Inset(list).Also(b => b.Padding = new Thickness(2))); layout.Children.Add(pickedRow);
            layout.Children.Add(Kit.Label("New name", Kit.Small, cls: "muted").Also(l => l.Margin = new Thickness(0, 8, 0, 0))); layout.Children.Add(to);
            layout.Children.Add(albumArtists); layout.Children.Add(summary); layout.Children.Add(warningBox); layout.Children.Add(buttons);
            Kit.Prepare(this, layout);
            Filter(); Update();
        }

        private sealed class ChoiceRow : Border
        {
            private readonly CheckBox check = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock name = Kit.Label("", Kit.Body), detail = Kit.Label("", Kit.Small, cls: "muted");
            private Choice choice;
            private bool updating;
            internal ChoiceRow()
            {
                Padding = new Thickness(8, 0); Height = 34; Background = Brushes.Transparent;
                Child = Kit.Grid("Auto,*,Auto", "*").Add(check.Cell(0, 0), name.Cell(0, 1), detail.Cell(0, 2));
                name.Margin = new Thickness(4, 0, 8, 0);
                check.IsCheckedChanged += (s, e) => { if (!updating) choice?.Set(check.IsChecked == true); };
                DataContextChanged += (s, e) =>
                {
                    if (choice != null) choice.Changed -= Show;
                    choice = DataContext as Choice;
                    if (choice != null) choice.Changed += Show;
                    Show();
                };
            }
            private void Show()
            {
                if (choice == null) return;
                updating = true; check.IsChecked = choice.Checked; updating = false;
                name.Text = choice.Name; detail.Text = choice.Songs + (choice.Songs == 1 ? " song" : " songs") + " · " + choice.Albums + (choice.Albums == 1 ? " album" : " albums");
                check.Named("Rename " + choice.Name);
            }
        }

        private List<Choice> Selected => choices.Where(c => c.Checked).ToList();

        private void Filter()
        {
            string text = (find.Text ?? "").Trim();
            var culture = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
            // Ticked artists stay listed at the top while searching for more.
            list.ItemsSource = choices.Where(c => c.Checked || text.Length == 0 || culture.IndexOf(c.Name, text, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0)
                .OrderByDescending(c => c.Checked && text.Length > 0).ToList();
        }

        private static bool Valid(List<Choice> names, string next) => names.Count > 0 && ArtistRules.Specific(next) && next.Length <= 300 && !next.Any(char.IsControl) &&
            !(names.Count == 1 && string.Equals(names[0].Name, next, StringComparison.Ordinal));

        /// <summary>Songs and albums a rename of these names would change.</summary>
        internal (int Songs, int Albums) Count(IEnumerable<string> names, bool includeAlbumArtists)
        {
            var keys = new HashSet<string>(names.Select(ArtistRules.Normalize), StringComparer.Ordinal);
            bool Hit(Track t) => t.Performers.Any(p => keys.Contains(ArtistRules.Normalize(p))) || includeAlbumArtists && t.AlbumArtists.Any(p => keys.Contains(ArtistRules.Normalize(p)));
            return (library.Sum(a => a.Tracks.Count(Hit)), library.Count(a => a.Tracks.Any(Hit)));
        }

        internal (int Songs, int Albums) Count(string name, bool includeAlbumArtists) => Count(new[] { name }, includeAlbumArtists);

        private void Update()
        {
            var names = Selected;
            if (string.IsNullOrWhiteSpace(to.Text) && names.Count > 0) to.Text = names[0].Name;
            string next = (to.Text ?? "").Trim();
            picked.Text = names.Count == 0 ? "No artist ticked" : names.Count == 1 ? "1 artist ticked: " + names[0].Name : names.Count + " artists ticked: " + string.Join(", ", names.Select(c => c.Name));
            picked.Tip(picked.Text);
            clear.IsVisible = names.Count > 0;
            if (names.Count == 0) { summary.Text = "Tick the artists to rename."; preview.IsEnabled = false; preview.Content = "Preview changes"; return; }
            var (songs, albums) = Count(names.Select(c => c.Name), albumArtists.On());
            summary.Text = !Valid(names, next) ? "Type the new name." :
                "Changes " + songs + (songs == 1 ? " song" : " songs") + " in " + albums + (albums == 1 ? " album" : " albums") + ": " +
                string.Join(", ", names.Select(c => "“" + c.Name + "”")) + " " + (names.Count == 1 ? "becomes" : "become") + " “" + next + "”.";
            preview.IsEnabled = Valid(names, next) && songs > 0;
            preview.Content = preview.IsEnabled ? "Preview changes to " + songs + (songs == 1 ? " song" : " songs") : "Preview changes";
        }

        /// <summary>Ticks the given artists (any letter case) and sets the new name.</summary>
        internal void Fill(IEnumerable<string> names, string newName, bool both)
        {
            var keys = new HashSet<string>(names.Select(ArtistRules.Normalize), StringComparer.Ordinal);
            foreach (var c in choices) c.Set(keys.Contains(c.Key));
            to.Text = newName; albumArtists.IsChecked = both; Filter(); Update();
        }

        internal void Fill(string artist, string newName, bool both) => Fill(new[] { artist }, newName, both);
        internal IReadOnlyList<Choice> Choices => choices;
    }
}
