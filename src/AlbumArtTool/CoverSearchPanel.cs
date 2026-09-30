using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

namespace AlbumArtTool
{
    internal sealed class CoverSearchPanel : UserControl
    {
        private readonly ICoverSearch service;
        private readonly TextBox query = new TextBox();
        private readonly CheckBox automatic = new ThemeCheckBox { Text = "Online suggestions", Checked = true, AutoSize = true };
        private readonly Button find = MakeButton("Search");
        private readonly Button bandcamp = MakeButton("Bandcamp ↗");
        private readonly Label summary = new Label();
        private readonly FlowLayoutPanel choices = new FlowLayoutPanel();
        private readonly ToolTip tips = new ToolTip();
        private CancellationTokenSource cancellation;
        private Album selected;
        private int version;
        private string signature = "";
        private bool hasResults;
        private bool active;
        internal Task CurrentSearch { get; private set; } = Task.CompletedTask;
        internal Task CurrentApply { get; private set; } = Task.CompletedTask;
        internal Func<string, CoverCandidate, Task> UseCover { get; set; }
        internal List<CoverCandidate> DisplayedCandidates { get; private set; } = new List<CoverCandidate>();

        internal CoverSearchPanel(ICoverSearch service)
        {
            this.service = service;
            Dock = DockStyle.Fill; BackColor = MainForm.Surface; ForeColor = MainForm.Ink;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 103));
            query.Dock = DockStyle.Fill; query.BackColor = Color.FromArgb(31, 33, 36); query.ForeColor = MainForm.Ink;
            query.BorderStyle = BorderStyle.FixedSingle; query.Font = new Font("Segoe UI", 9); query.Margin = new Padding(0, 3, 5, 3);
            query.AccessibleName = "Online artwork search query";
            searchRow.Controls.Add(query, 0, 0); searchRow.Controls.Add(find, 1, 0); searchRow.Controls.Add(bandcamp, 2, 0);
            var instructions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            instructions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            instructions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); instructions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            instructions.Controls.Add(new Label { Text = "Click a cover to apply it immediately.", ForeColor = MainForm.Accent, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9) }, 0, 0);
            automatic.Font = new Font("Segoe UI", 9); automatic.ForeColor = MainForm.Muted; automatic.Dock = DockStyle.Fill;
            instructions.Controls.Add(automatic, 1, 0);
            choices.Dock = DockStyle.Fill; choices.AutoScroll = true; choices.BackColor = Color.FromArgb(18, 19, 22);
            choices.WrapContents = true; choices.Padding = new Padding(3); choices.Margin = Padding.Empty;
            choices.SizeChanged += (s, e) =>
            {
                foreach (var card in choices.Controls.OfType<CoverChoice>()) card.FitHeight(choices.ClientSize.Height);
            };
            summary.Dock = DockStyle.Fill; summary.ForeColor = MainForm.Muted; summary.Font = new Font("Segoe UI", 8.5f);
            summary.AutoEllipsis = true; summary.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(searchRow, 0, 0); layout.Controls.Add(instructions, 0, 1); layout.Controls.Add(choices, 0, 2); layout.Controls.Add(summary, 0, 3);
            Controls.Add(layout);
            find.Click += (s, e) => CurrentSearch = SearchAsync(true);
            query.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CurrentSearch = SearchAsync(true); } };
            automatic.CheckedChanged += (s, e) =>
            {
                if (automatic.Checked && active) CurrentSearch = SearchAsync(false);
                else { Cancel(); summary.Text = "Automatic search off. Click Search for a one-time lookup."; }
            };
            bandcamp.Click += (s, e) => OpenPage("https://bandcamp.com/search?q=" + Uri.EscapeDataString(query.Text) + "&item_type=a");
            tips.SetToolTip(bandcamp, "Open the same album search on Bandcamp in your browser.");
            tips.SetToolTip(automatic, "Search only the selected album. Turn off to work offline. Music files are never uploaded.");
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
            if (this.active && automatic.Checked && !hasResults)
                CurrentSearch = SearchAsync(false, true);
        }

        internal void SetActive(bool value)
        {
            active = value && selected != null;
            // Keep labels enabled so Windows does not paint disabled text black.
            // A read-only text box also retains its foreground color and allows copying.
            query.ReadOnly = !active; query.ForeColor = active ? MainForm.Ink : MainForm.Muted;
            find.Enabled = bandcamp.Enabled = automatic.Enabled = active;
            foreach (var card in choices.Controls.OfType<CoverChoice>()) card.SetActive(active);
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
                if (IsDisposed || request != version || selected?.Key != key || token.IsCancellationRequested) return;
                ShowResults(update, key);
            });
            try
            {
                if (debounce) await Task.Delay(450, token);
                await service.SearchAsync(requestQuery, refresh, progress, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (CoverSearchService.Expected(e))
            { if (!IsDisposed && request == version) { summary.Text = "Search unavailable. Try again or choose a local image."; tips.SetToolTip(summary, e.Message); } }
        }

        private void ShowResults(CoverSearchUpdate update, string key)
        {
            string next = string.Join("\n", update.Candidates.Select(c => c.ImageUrl));
            if (next != signature)
            {
                signature = next; ClearChoices();
                DisplayedCandidates = update.Candidates.ToList();
                choices.SuspendLayout();
                foreach (var candidate in DisplayedCandidates)
                {
                    var card = new CoverChoice(candidate, tips);
                    card.FitHeight(choices.ClientSize.Height);
                    card.SetActive(active);
                    card.Apply += (s, e) =>
                    {
                        if (!active || selected?.Key != key || UseCover == null) return;
                        CurrentApply = ApplyChoiceAsync(key, candidate);
                    };
                    card.OpenSource += (s, e) => OpenPage(candidate.PageUrl);
                    choices.Controls.Add(card);
                }
                choices.ResumeLayout();
            }
            foreach (var card in choices.Controls.OfType<CoverChoice>()) card.UpdateResolution();
            summary.Text = update.Candidates.Count == 0
                ? update.Complete ? "No covers found. Edit the query or try Bandcamp in your browser." : "Searching for artwork…"
                : update.Candidates.Count + " covers" + (update.Complete ? " · Best matches first." : " · More sources loading…");
            if (update.Complete && update.Issues.Count > 0) summary.Text += " Some sources unavailable.";
            tips.SetToolTip(summary, string.Join(Environment.NewLine, update.Issues));
        }

        private async Task ApplyChoiceAsync(string key, CoverCandidate candidate)
        {
            try { await UseCover(key, candidate); }
            catch (Exception e) { if (!IsDisposed) { summary.Text = "Could not apply this cover."; tips.SetToolTip(summary, e.Message); } }
        }

        private void OpenPage(string url)
        {
            if (!Online.WebTransport.AllowedUrl(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { summary.Text = "Could not open your browser."; tips.SetToolTip(summary, e.Message); }
        }
        private void Cancel()
        {
            version++;
            var old = cancellation; cancellation = null;
            old?.Cancel(); old?.Dispose();
        }
        private void ClearChoices()
        {
            foreach (Control child in choices.Controls.Cast<Control>().ToArray()) child.Dispose();
            choices.Controls.Clear(); DisplayedCandidates.Clear();
        }
        private static Button MakeButton(string text) => new ThemeButton { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = MainForm.Surface, ForeColor = MainForm.Ink, Font = new Font("Segoe UI", 9), Margin = new Padding(2), Cursor = Cursors.Hand };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Cancel(); ClearChoices(); tips.Dispose(); }
            base.Dispose(disposing);
        }

        private sealed class CoverChoice : Panel
        {
            private readonly PictureBox image;
            private readonly Label title, artist, resolution;
            private readonly CoverCandidate candidate;
            private readonly LinkLabel origin;
            private readonly Button use;
            public event EventHandler Apply;
            public event EventHandler OpenSource;
            public CoverChoice(CoverCandidate candidate, ToolTip tips)
            {
                this.candidate = candidate;
                Size = new Size(163, 212); Margin = new Padding(4); BackColor = MainForm.Surface;
                AccessibleName = candidate.Title + " by " + candidate.Artist + " from " + candidate.Source;
                image = new PictureBox { Location = new Point(6, 5), Size = new Size(151, 104), SizeMode = PictureBoxSizeMode.Zoom,
                    Cursor = Cursors.Hand, Image = Artwork.Decode(candidate.Thumbnail), AccessibleName = "Apply " + AccessibleName };
                title = new Label { Text = candidate.Title, Location = new Point(6, 111), Size = new Size(151, 19),
                    ForeColor = MainForm.Ink, AutoEllipsis = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand };
                artist = new Label { Text = candidate.Artist, Location = new Point(6, 130), Size = new Size(151, 17),
                    ForeColor = MainForm.Muted, AutoEllipsis = true, Font = new Font("Segoe UI", 8), Cursor = Cursors.Hand };
                origin = new LinkLabel { Text = candidate.Source + (candidate.MatchScore < 80 ? " · Similar" : ""), Location = new Point(6, 147),
                    Size = new Size(151, 17), LinkColor = MainForm.Accent, ActiveLinkColor = MainForm.Ink, Font = new Font("Segoe UI", 8) };
                resolution = new Label { Text = candidate.Resolution, Location = new Point(6, 164), Size = new Size(151, 18),
                    ForeColor = MainForm.Ink, Font = new Font("Segoe UI", 8), AccessibleName = "Full-size cover resolution" };
                use = new ThemeButton { Text = "Apply this cover", Location = new Point(6, 183), Size = new Size(151, 24),
                    BackColor = Color.FromArgb(37, 65, 59), ForeColor = MainForm.Accent, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
                image.Click += (s, e) => Apply?.Invoke(this, EventArgs.Empty);
                title.Click += (s, e) => Apply?.Invoke(this, EventArgs.Empty);
                artist.Click += (s, e) => Apply?.Invoke(this, EventArgs.Empty);
                use.Click += (s, e) => Apply?.Invoke(this, EventArgs.Empty);
                origin.LinkClicked += (s, e) => OpenSource?.Invoke(this, EventArgs.Empty);
                tips.SetToolTip(image, candidate.Title + "\n" + candidate.Artist + "\nClick to download and apply. Undo is available.");
                tips.SetToolTip(title, candidate.Title); tips.SetToolTip(origin, candidate.PageUrl);
                tips.SetToolTip(resolution, "Dimensions of the full-size image at the source. Embedded artwork is limited to 1600 pixels on the longest side.");
                Controls.AddRange(new Control[] { image, title, artist, origin, resolution, use });
            }
            internal void FitHeight(int viewportHeight)
            {
                // Keep the action visible on smaller laptop screens; extra results still scroll.
                int imageHeight = Math.Max(40, Math.Min(104, viewportHeight - 122));
                Height = imageHeight + 108; image.Height = imageHeight;
                title.Top = imageHeight + 7; artist.Top = imageHeight + 26;
                origin.Top = imageHeight + 43; resolution.Top = imageHeight + 60; use.Top = imageHeight + 79;
            }
            internal void UpdateResolution() { resolution.Text = candidate.Resolution; }
            internal void SetActive(bool value)
            {
                use.Enabled = value;
                image.Cursor = title.Cursor = artist.Cursor = value ? Cursors.Hand : Cursors.Default;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { var old = image.Image; image.Image = null; old?.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
