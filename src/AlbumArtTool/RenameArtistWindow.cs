using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Renames an artist across the whole library: every Artist (and optionally Album Artist) entry with that name.
    // A big change, so it says how many songs it touches, warns, and hands over to Organize to preview first.
    internal sealed class RenameArtistWindow : Window
    {
        private sealed class Usage { internal string Name; internal int Songs, Albums; }
        private readonly Dictionary<string, Usage> artists = new Dictionary<string, Usage>(StringComparer.Ordinal);
        private readonly Album[] library;
        private readonly AutoCompleteBox from = new AutoCompleteBox { FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 1, Watermark = "Start typing an artist in your library" };
        private readonly TextBox to = new TextBox { Watermark = "The name to use everywhere" };
        private readonly CheckBox albumArtists = Kit.Check("Also change Album Artist tags", true);
        private readonly TextBlock summary = Kit.Label("", Kit.Body, true);
        private readonly Button preview = Kit.Button("Preview changes", "primary"), cancel = Kit.Button("Cancel");
        internal Action<string, string, bool> Start;

        internal RenameArtistWindow(IEnumerable<Album> albums)
        {
            library = albums.ToArray();
            Title = "Rename an artist"; Width = 560; SizeToContent = SizeToContent.Height; CanResize = false; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            foreach (var album in library)
                foreach (var track in album.Tracks)
                    foreach (string name in track.Performers.Concat(track.AlbumArtists).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
                    {
                        string key = ArtistRules.Normalize(name);
                        if (!artists.TryGetValue(key, out var usage)) artists[key] = usage = new Usage { Name = name.Trim() };
                    }
            from.ItemsSource = artists.Values.Select(a => a.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            from.Named("Artist to rename").Tip("The artist name as it appears in your tags. Every song credited to it is changed, whatever its letter case.");
            to.Named("New artist name").Tip("The name every matching Artist" + " entry becomes, for example to fix the spelling or the capitalization.");
            albumArtists.Named("Also change Album Artist tags").Tip("Also rename the artist where it is the album artist, so players keep listing those albums under one name.");
            summary.Named("Rename summary"); summary.TextWrapping = TextWrapping.Wrap; summary.TextTrimming = TextTrimming.None;
            var warning = Kit.Label("This is a big change: it rewrites the artist tags of every matching song in your library. Organize lists each change before anything is written, " +
                "originals are backed up, and Organize › Preview Undo restores them.", Kit.Small);
            warning.TextWrapping = TextWrapping.Wrap; warning.TextTrimming = TextTrimming.None; warning.Named("Rename warning");
            var warningIcon = Kit.Glyph(Icons.Warning, 18); warningIcon.Margin = new Thickness(0, 0, 10, 0); warningIcon.VerticalAlignment = VerticalAlignment.Top;
            var warningBox = new Border { Child = Kit.Grid("Auto,*", "Auto").Add(warningIcon.Cell(0, 0), warning.Cell(0, 1)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Margin = new Thickness(0, 12, 0, 0) };
            warningBox.Brush(Border.BackgroundProperty, "BadgeWarning");
            preview.Named("Preview artist rename").Tip("Open Organize with this rename only and list every song it changes. Nothing is written until you click Apply there.");
            cancel.Click += (s, e) => Close();
            preview.Click += (s, e) => { string a = Match(), b = (to.Text ?? "").Trim(); if (a == null || !Valid(a, b)) return; Close(); Start?.Invoke(a, b, albumArtists.On()); };
            from.TextChanged += (s, e) => { if (string.IsNullOrEmpty(to.Text) && Match() is string m) to.Text = m; Update(); };
            to.TextChanged += (s, e) => Update(); albumArtists.IsCheckedChanged += (s, e) => Update();
            foreach (var b in new[] { preview, cancel }) { b.Height = 34; b.Padding = new Thickness(16, 2); }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel); buttons.Children.Add(preview);
            var layout = new StackPanel { Margin = new Thickness(22, 18, 22, 16), Spacing = 4 };
            layout.Children.Add(Kit.Label("Rename an artist everywhere", 17.33, true));
            layout.Children.Add(Kit.Label("Artist", Kit.Small, cls: "muted").Also(l => l.Margin = new Thickness(0, 10, 0, 0))); layout.Children.Add(from);
            layout.Children.Add(Kit.Label("New name", Kit.Small, cls: "muted").Also(l => l.Margin = new Thickness(0, 8, 0, 0))); layout.Children.Add(to);
            layout.Children.Add(albumArtists); layout.Children.Add(summary); layout.Children.Add(warningBox); layout.Children.Add(buttons);
            Kit.Prepare(this, layout);
            Update();
        }

        private string Match() => artists.TryGetValue(ArtistRules.Normalize(from.Text ?? ""), out var usage) ? usage.Name : null;

        private static bool Valid(string current, string next) => ArtistRules.Specific(next) && next.Length <= 300 && !next.Any(char.IsControl) && !string.Equals(current, next, StringComparison.Ordinal);

        /// <summary>Songs and albums the rename would change.</summary>
        internal (int Songs, int Albums) Count(string name, bool includeAlbumArtists)
        {
            string key = ArtistRules.Normalize(name);
            bool Hit(Track t) => t.Performers.Any(p => ArtistRules.Normalize(p) == key) || includeAlbumArtists && t.AlbumArtists.Any(p => ArtistRules.Normalize(p) == key);
            return (library.Sum(a => a.Tracks.Count(Hit)), library.Count(a => a.Tracks.Any(Hit)));
        }

        private void Update()
        {
            string current = Match(), next = (to.Text ?? "").Trim();
            if (current == null) { summary.Text = string.IsNullOrWhiteSpace(from.Text) ? "Choose the artist to rename." : "No artist with that name in your library."; preview.IsEnabled = false; return; }
            var (songs, albums) = Count(current, albumArtists.On());
            summary.Text = !Valid(current, next) ? "Type the new name for " + current + "." :
                "Changes " + songs + (songs == 1 ? " song" : " songs") + " in " + albums + (albums == 1 ? " album" : " albums") + ": “" + current + "” becomes “" + next + "”.";
            preview.IsEnabled = Valid(current, next) && songs > 0;
            preview.Content = preview.IsEnabled ? "Preview changes to " + songs + (songs == 1 ? " song" : " songs") : "Preview changes";
        }

        internal void Fill(string artist, string newName, bool both) { from.Text = artist; to.Text = newName; albumArtists.IsChecked = both; Update(); }
    }
}
