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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed class OrganizePage : UserControl
    {
        private readonly string root;
        private readonly Func<bool> canStart;
        private readonly Action<bool> exclusive;
        private readonly Func<string, Task> refreshLibrary;
        private readonly CheckBox convert = Kit.Check("Convert lossless to MP3 320", false), compatibility = Kit.Check("Also convert other lossy formats", false),
            square = Kit.Check("Crop artwork to squares", false), shrink = Kit.Check("One front cover per song (JPEG ≤ " + Artwork.EmbedSide + " px)", false),
            titleFromName = Kit.Check("Fill missing titles from filenames", false), cleanTitles = Kit.Check("Remove “feat.” credits from titles", false),
            id3v23 = Kit.Check("Standardize MP3 tags to ID3v2.3", false), albumArtist = Kit.Check("Fill missing album artists from the artist", false),
            organize = Kit.Check("Sort into Artist / Album folders", false), rename = Kit.Check("Rename files from their titles", false), combine = Kit.Check("Combine multi-disc albums", false),
            duplicates = Kit.Check("Remove confirmed duplicates", false), archive = Kit.Check("Move old tool backups off the player", false);
        private readonly Dictionary<string, CheckBox> optionBoxes;
        private readonly TextBlock state = Kit.Label("", Kit.Body), encoderState = Kit.Label("", Kit.Small, cls: "muted");
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 2) };
        private readonly Button preview = Kit.Button("Preview changes", "primary"), apply = Kit.Button("Apply selected changes"), undo = Kit.IconButton(Icons.Undo, "Preview Undo", "ghost"), stop = Kit.Button("Stop"),
            setupEncoder = Kit.Button("Set up in Settings", "link"), openBackup = Kit.IconButton(Icons.Folder, "Open backups", "ghost");
        // Presets: built-in ones plus the user's own, saved on this PC.
        private readonly WrapPanel presetChips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button savePreset = Kit.IconButton(Icons.Add, "Save as preset…", "link"), deletePreset = Kit.IconButton(Icons.Delete, "Delete preset", "link"),
            confirmSave = Kit.Button("Save preset", "primary"), cancelSave = Kit.Button("Cancel");
        private readonly TextBox presetName = new TextBox { Watermark = "Name this preset, for example \"Before syncing my phone\"" };
        private readonly Control saveRow;
        private List<OrganizePreset> customPresets = new List<OrganizePreset>();
        private bool applyingPreset;
        private readonly Action openSettings;
        private readonly ListBox files = new ListBox { Classes = { "grid" } };
        private readonly EmptyState hint = new EmptyState("Organize preview", withAction: false);
        private readonly Control optionsPanel;
        private List<FileRow> rows = new List<FileRow>();
        private Dictionary<string, List<FileRow>> mergeGroups = new Dictionary<string, List<FileRow>>();
        private CancellationTokenSource source;
        private LibraryPlan plan;
        private LibraryJournal undoJournal;
        private string encoder;
        private bool syncingMergeSelection;
        internal bool IsWorking { get; private set; }
        internal LibraryPlan CurrentPlan => plan;
        internal IReadOnlyList<FileRow> Rows => rows;

        internal sealed class FileRow
        {
            internal object Tag;
            internal string Action, Source, Destination, Details, Tooltip;
            internal bool Muted, HasCheck = true;
            internal bool IsChecked { get; private set; }
            internal event Action Changed;
            internal Func<FileRow, bool, bool> Toggle;
            internal void SetChecked(bool value) { if (IsChecked == value) return; IsChecked = value; Changed?.Invoke(); }
            internal bool RequestChecked(bool value) => Toggle == null || Toggle(this, value);
        }

        internal OrganizePage(string musicRoot, Func<bool> ready, Action<bool> setExclusive, Func<string, Task> refresh, Action openSettings = null)
        {
            root = musicRoot; canStart = ready; exclusive = setExclusive; refreshLibrary = refresh; this.openSettings = openSettings;
            optionBoxes = new Dictionary<string, CheckBox>
            {
                [OrganizePresets.Convert] = convert, [OrganizePresets.Compatibility] = compatibility, [OrganizePresets.Square] = square, [OrganizePresets.Shrink] = shrink,
                [OrganizePresets.TitleFromName] = titleFromName, [OrganizePresets.CleanTitles] = cleanTitles, [OrganizePresets.Id3v23] = id3v23, [OrganizePresets.AlbumArtistFromArtist] = albumArtist,
                [OrganizePresets.Organize] = organize, [OrganizePresets.Rename] = rename, [OrganizePresets.Combine] = combine,
                [OrganizePresets.Duplicates] = duplicates, [OrganizePresets.Archive] = archive,
            };
            convert.Named("Convert lossless to MP3 320"); compatibility.Named("Also convert other lossy formats"); square.Named("Crop artwork to squares");
            rename.Named("Rename song files from Title tags"); combine.Named("Combine multi-disc albums"); cleanTitles.Named("Remove featured artists from titles");
            shrink.Named("Standardize embedded artwork");
            titleFromName.Named("Fill missing Title tags from filenames");
            id3v23.Named("Standardize MP3 tags to ID3v2.3");
            albumArtist.Named("Fill missing album artists from the artist");
            organize.Named("Organize into Artist / Album folders"); duplicates.Named("Remove confirmed duplicates"); archive.Named("Move old tool backups off the player");
            AddTips();

            // Presets
            presetChips.Named("Organize presets");
            savePreset.Named("Save as preset").Click += (s, e) => ShowSaveRow(true);
            deletePreset.Named("Delete preset").Click += (s, e) => DeleteMatchingPreset();
            confirmSave.Click += (s, e) => SaveCurrentAsPreset(presetName.Text);
            cancelSave.Click += (s, e) => ShowSaveRow(false);
            presetName.Named("Preset name");
            presetName.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) { SaveCurrentAsPreset(presetName.Text); e.Handled = true; }
                else if (e.Key == Avalonia.Input.Key.Escape) { ShowSaveRow(false); e.Handled = true; }
            };
            var presetLabel = Kit.Caption("Presets"); presetLabel.Margin = new Thickness(2, 0, 12, 0);
            var presetActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            presetActions.Children.Add(savePreset); presetActions.Children.Add(deletePreset);
            var presets = Kit.Grid("Auto,*,Auto", "Auto").Add(presetLabel.Cell(0, 0), presetChips.Cell(0, 1), presetActions.Cell(0, 2));
            presets.Margin = new Thickness(0, 0, 0, 6);
            presetName.Margin = new Thickness(0, 0, 6, 0);
            var save = Kit.Grid("*,120,90", "34").Add(presetName.Cell(0, 0), confirmSave.Cell(0, 1), cancelSave.Cell(0, 2));
            save.Margin = new Thickness(0, 0, 0, 6);
            saveRow = save; saveRow.IsVisible = false;

            // Options, grouped by what they change.
            encoderState.TextWrapping = TextWrapping.Wrap; encoderState.TextTrimming = TextTrimming.None; encoderState.VerticalAlignment = VerticalAlignment.Top;
            setupEncoder.Click += (s, e) => this.openSettings?.Invoke();
            var helper = new StackPanel { Spacing = 2 };
            var helperCaption = Kit.Caption("Needs"); helperCaption.Margin = new Thickness(0, 0, 0, 4);
            helper.Children.Add(helperCaption); helper.Children.Add(encoderState); helper.Children.Add(setupEncoder);
            var options = Kit.Grid("*,*,*", "Auto,Auto").Add(
                Group("Audio", convert, compatibility).Cell(0, 0), Group("Artwork", square, shrink).Cell(0, 1), Group("Cleanup", duplicates, archive).Cell(0, 2),
                Group("Tags", titleFromName, albumArtist, cleanTitles, id3v23).Cell(1, 0), Group("Files & folders", organize, rename, combine).Cell(1, 1), helper.Cell(1, 2)).Named("Library management options");
            helper.Margin = new Thickness(8, 8, 8, 4);
            optionsPanel = options;
            var optionsCard = Kit.Frost(options, new Thickness(10, 6, 10, 6), 14); optionsCard.Margin = new Thickness(0, 0, 0, 8);
            foreach (var check in optionBoxes.Values) check.IsCheckedChanged += (s, e) => { if (!applyingPreset) { InvalidatePlan(); UpdatePresetState(); } };

            var actions = Kit.Grid("Auto,Auto,*,90,165,180", "*").Add(undo.Cell(0, 0), openBackup.Cell(0, 1), stop.Cell(0, 3), preview.Cell(0, 4), apply.Cell(0, 5));
            actions.Margin = new Thickness(0, 8, 0, 0);
            state.Text = "Pick a preset or the options you want, then Preview changes.";
            state.TextWrapping = TextWrapping.Wrap; state.TextTrimming = TextTrimming.CharacterEllipsis; state.MaxLines = 2;
            state.Named("Library management status");
            files.Named("Planned library changes");
            files.ItemTemplate = new FuncDataTemplate<FileRow>((row, scope) => new FileRowView(), true);
            var header = Kit.Grid("17*,24*,25*,34*", "28").Add(Kit.Label("Action", Kit.Small, cls: "muted").Cell(0, 0), Kit.Label("Source", Kit.Small, cls: "muted").Cell(0, 1),
                Kit.Label("Destination / retained copy", Kit.Small, cls: "muted").Cell(0, 2), Kit.Label("Details", Kit.Small, cls: "muted").Cell(0, 3));
            header.Margin = new Thickness(34, 0, 6, 0);
            var headerBar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
            headerBar.Brush(Border.BorderBrushProperty, "Border"); headerBar.Brush(Border.BackgroundProperty, "InsetHeader");
            hint.Show(Icons.Organize, "Nothing to preview yet", "Choose a preset or the options you want, then click Preview changes. Every change is listed here before anything happens.");
            hint.Margin = new Thickness(24);
            var listHost = new Panel(); listHost.Children.Add(files); listHost.Children.Add(hint);
            var table = Kit.Inset(Kit.Grid("*", "Auto,*").Add(headerBar.Cell(0, 0), listHost.Cell(1, 0))); table.Padding = new Thickness(0);
            table.Margin = new Thickness(0, 6, 0, 0);
            progress.Named("Library management progress"); progress.IsVisible = false;
            // A whole-library artist rename (started from Artists) is shown above the options until it is applied or cancelled.
            renameText.TextWrapping = TextWrapping.Wrap; renameText.TextTrimming = TextTrimming.None; renameText.Named("Artist rename");
            cancelRename.Tip("Stop renaming this artist. Nothing has been changed yet.");
            cancelRename.Click += (s, e) => { SetRename(null, null, true); InvalidatePlan(); UpdatePresetState(); };
            renameBanner = new Border { Child = Kit.Grid("*,Auto", "Auto").Add(renameText.Cell(0, 0), cancelRename.Cell(0, 1)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 8), IsVisible = false };
            renameBanner.Brush(Border.BackgroundProperty, "BadgeWarning");
            // Which folder this works on: the music folder, or New music before it joins the library.
            libraryTarget.Named("Organize the music folder").Tip("Work on your music folder.");
            newTarget.Named("Organize New music").Tip("Work on the New music folder (your downloads from Add music) before they join the library.");
            libraryTarget.Click += (s, e) => { if (!IsWorking && libraryFolder != null) SwitchFolder?.Invoke(libraryFolder); };
            newTarget.Click += (s, e) => { if (!IsWorking && newMusicFolder != null) SwitchFolder?.Invoke(newMusicFolder); };
            var targets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            targets.Children.Add(libraryTarget); targets.Children.Add(newTarget);
            folderText.Named("Organize folder"); folderText.Margin = new Thickness(12, 0, 0, 0);
            var folderLabel = Kit.Caption("Folder"); folderLabel.Margin = new Thickness(2, 0, 12, 0);
            var target = Kit.Grid("Auto,Auto,*", "Auto").Add(folderLabel.Cell(0, 0), targets.Cell(0, 1), folderText.Cell(0, 2));
            target.Margin = new Thickness(0, 0, 0, 8);
            var top = new StackPanel(); top.Children.Add(target); top.Children.Add(presets);
            var layout = Kit.Grid("*", "Auto,Auto,Auto,Auto,Auto,*,Auto,Auto").Add(top.Cell(0, 0), saveRow.Cell(1, 0), renameBanner.Cell(2, 0), optionsCard.Cell(3, 0),
                state.Cell(4, 0), table.Cell(5, 0), progress.Cell(6, 0), actions.Cell(7, 0));
            Content = layout;
            preview.Click += async (s, e) => await PreviewAsync(); apply.Click += async (s, e) => await ApplyAsync();
            undo.Click += (s, e) => PreviewUndo(); stop.Click += (s, e) => source?.Cancel();
            openBackup.Click += (s, e) => { try { Directory.CreateDirectory(BackupSettings.Root); using (Process.Start(new ProcessStartInfo(BackupSettings.Root) { UseShellExecute = true })) { } } catch (Exception ex) { state.Text = ex.Message; } };
            AttachedToVisualTree += (s, e) => Refresh();
            try { customPresets = OrganizePresets.LoadCustom(); } catch (Exception) { customPresets = new List<OrganizePreset>(); }
            RebuildPresets();
            encoder = Management.Encoder.Find(); UpdateEncoder(); SetWorking(false);
        }

        internal string Root => root;

        private readonly Button libraryTarget = Kit.IconButton(Icons.Folder, "Music folder", "chip"), newTarget = Kit.IconButton(Icons.AddMusic, "New music", "chip");
        private readonly TextBlock folderText = Kit.Label("", Kit.Small, cls: "muted");
        private string libraryFolder, newMusicFolder;
        /// <summary>Opens Organize on another folder (set by the main window).</summary>
        internal Action<string> SwitchFolder;

        /// <summary>The two folders Organize can work on; the one this page works on is highlighted.</summary>
        internal void SetFolders(string library, string newMusic)
        {
            libraryFolder = library; newMusicFolder = newMusic;
            bool isNew = newMusic != null && string.Equals(root, newMusic, StringComparison.OrdinalIgnoreCase);
            bool isLibrary = library != null && string.Equals(root, library, StringComparison.OrdinalIgnoreCase);
            libraryTarget.Classes.Set("selected", isLibrary); newTarget.Classes.Set("selected", isNew);
            folderText.Text = Kit.MiddleEllipsis(root, 70); folderText.Tip(root);
        }
        internal bool WorksOnNewMusic => newMusicFolder != null && string.Equals(root, newMusicFolder, StringComparison.OrdinalIgnoreCase);

        /// <summary>Called when the page is shown: picks up an FFmpeg set up in Settings meanwhile.</summary>
        internal void Refresh() { if (!IsWorking) { encoder = Management.Encoder.Find(); UpdateEncoder(); } }

        internal void Stop() => source?.Cancel();

        private static Control Group(string title, params CheckBox[] boxes)
        {
            var stack = new StackPanel { Spacing = 0, Margin = new Thickness(8, 8, 8, 4) };
            var caption = Kit.Caption(title); caption.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(caption);
            foreach (var box in boxes) stack.Children.Add(box);
            return stack;
        }

        internal IEnumerable<string> SelectedOptions => OrganizePresets.Keys.Where(k => optionBoxes[k].On());
        internal IReadOnlyList<OrganizePreset> CustomPresets => customPresets;
        internal IEnumerable<OrganizePreset> AllPresets => OrganizePresets.BuiltIn.Concat(customPresets);

        private void RebuildPresets()
        {
            presetChips.Children.Clear();
            foreach (var preset in AllPresets)
            {
                var chip = Kit.Button(preset.Name, "chip").Named("Preset " + preset.Name);
                chip.Margin = new Thickness(0, 0, 6, 0);
                chip.Tip((preset.BuiltIn ? preset.Description : "Your preset.") + "\n\nSelects: " +
                    string.Join(", ", OrganizePresets.Keys.Where(preset.Options.Contains).Select(k => optionBoxes[k].Content as string)) + ". Nothing changes until you preview and apply.");
                chip.Tag = preset;
                chip.Click += (s, e) => ApplyPreset(preset);
                presetChips.Children.Add(chip);
            }
            UpdatePresetState();
        }

        internal void ApplyPreset(OrganizePreset preset)
        {
            if (IsWorking || preset == null) return;
            applyingPreset = true;
            try { foreach (var pair in optionBoxes) pair.Value.IsChecked = preset.Options.Contains(pair.Key); }
            finally { applyingPreset = false; }
            ShowSaveRow(false); InvalidatePlan(); UpdatePresetState();
            state.Text = preset.Name + " selected. Adjust any option, then Preview changes.";
        }

        private OrganizePreset MatchingPreset() { var selected = SelectedOptions.ToArray(); return AllPresets.FirstOrDefault(p => p.Matches(selected)); }

        private void UpdatePresetState()
        {
            var match = MatchingPreset();
            foreach (var chip in presetChips.Children.OfType<Button>()) chip.Classes.Set("selected", ReferenceEquals(chip.Tag, match));
            deletePreset.IsVisible = match != null && !match.BuiltIn;
            savePreset.IsEnabled = !IsWorking && SelectedOptions.Any() && match == null;
            preview.IsEnabled = !IsWorking && (SelectedOptions.Any() || renameFrom != null);
        }

        private void ShowSaveRow(bool show)
        {
            saveRow.IsVisible = show;
            if (show) { presetName.Text = ""; presetName.Focus(); }
        }

        /// <summary>Saves the selected options under a name (replacing a custom preset with that name). False when the name cannot be used.</summary>
        internal bool SaveCurrentAsPreset(string name)
        {
            string clean = OrganizePresets.CleanName(name);
            if (clean == null) { state.Text = "Type a name for the preset."; return false; }
            if (OrganizePresets.IsReserved(clean)) { state.Text = "\"" + clean + "\" is a built-in preset. Choose another name."; return false; }
            if (!SelectedOptions.Any()) { state.Text = "Select at least one option to save as a preset."; return false; }
            var saved = OrganizePresets.Upsert(customPresets, clean, SelectedOptions);
            if (saved == null) { state.Text = customPresets.Count >= OrganizePresets.MaxCustom ? "You have the maximum of " + OrganizePresets.MaxCustom + " presets. Delete one first." : "The preset could not be saved on this PC."; return false; }
            ShowSaveRow(false); RebuildPresets();
            state.Text = "Saved preset " + clean + ". It is kept on this PC for next time.";
            return true;
        }

        /// <summary>Deletes the custom preset that matches the selected options.</summary>
        internal bool DeleteMatchingPreset()
        {
            var match = MatchingPreset();
            if (match == null || match.BuiltIn) return false;
            customPresets.Remove(match);
            bool saved = OrganizePresets.SaveCustom(customPresets);
            RebuildPresets();
            state.Text = saved ? "Deleted preset " + match.Name + ". Your selected options are unchanged." : "The preset was removed, but the change could not be saved on this PC.";
            return saved;
        }

        // Mouse-over help for every control. Nothing runs until Preview changes, so each tip says what the preview will propose.
        private void AddTips()
        {
            const string safe = "\n\nNothing changes until you click Preview changes, review the list, and Apply. Originals are backed up to the PC backup folder and Undo restores them exactly.";
            convert.Tip("Re-encodes lossless files (FLAC, WAV, AIFF, ALAC, WavPack…) and any audio above 320 kbps as MP3 320 kbps CBR.\n\n" +
                "Existing MP3s at or below 320 kbps and lower-bitrate lossy files are left alone, so nothing is encoded twice. Tags and embedded artwork are carried over; " +
                "each new MP3's bitrate and duration are verified and the whole file is decoded before it replaces the original. Files with a video track are skipped. Needs FFmpeg." + safe);
            compatibility.Tip("Also converts other non-MP3 lossy audio (AAC/M4A, Ogg Vorbis, Opus, WMA) to MP3 320, for players that only handle MP3.\n\n" +
                "This is another lossy encode: it cannot improve a low-bitrate source and loses a little quality. Only use it when compatibility matters more. Needs FFmpeg and \"Convert lossless\"." + safe);
            square.Tip("Center-crops every non-square embedded picture to a square at its full resolution (1800 × 1000 becomes 1000 × 1000). Nothing is stretched or enlarged.\n\n" +
                "Square pictures keep their original bytes. JPEGs stay JPEG; other images are saved as PNG. Folder images (cover.jpg) are not cropped. " +
                "With \"Standardize embedded artwork\" also selected, the crop happens first and the result is then resized to 600 px." + safe);
            duplicates.Tip("Removes extra copies of the same song on the same album. Only confirmed matches count: identical file or audio contents with the same album and title.\n\n" +
                "Copies on different albums, different recordings, and songs that only share a name are kept. The copy with artwork is preferred, and names like \"(1)\" or \"copy\" are removed first. " +
                "The preview names the copy that stays. Removed copies go to the PC backup folder, not the recycle bin." + safe);
            organize.Tip("Moves every song into Music folder \\ Artist \\ Album, using the Album Artist (or Artist) and Album tags, from any depth of subfolders.\n\n" +
                "Year, Downloads and doubled artist folders disappear. Disc subfolders are kept for multi-disc releases and Various Artists albums stay together. " +
                "Booklets, cover.jpg, cue sheets and lyrics move with their album. Songs without usable artist/album tags stay where they are. Empty old folders are removed." + safe);
            archive.Tip("Moves leftover backups from older versions of this tool (.album-art-backups, .before-square-crop, .covercrop-tmp- files) out of the music folder and into the PC backup folder.\n\n" +
                "Some music players index those hidden folders as extra songs, which shows up as duplicates." + safe);
            rename.Tip("Renames each song file to its Title tag: \"01 - World Eater.mp3\" becomes \"World Eater.mp3\". Works in every subfolder, on its own or with the other options.\n\n" +
                "The extension is kept. Characters Windows can't use are replaced and very long titles are shortened. A name clash keeps both files with \" (2)\". " +
                "Songs without a Title tag keep their filename (combine with \"Fill missing Title tags\" to give them one first). Only the name changes; audio and tags are untouched." + safe);
            combine.Tip("Merges albums split into Disc 1 / CD2 folders, \"(Disc 2)\" album names or disc tags into one album folder.\n\n" +
                "Tracks are renumbered 1, 2, 3… across all discs, the track total is set, disc tags become 1 of 1, and \"(Disc N)\" is removed from the album name. " +
                "Filenames are not changed; a clash between discs gets \" (2)\". Each merge is selected or cleared as a whole album in the preview." + safe);
            cleanTitles.Tip("Removes feat. / ft. / featuring credits from Title tags and filenames: \"Song (feat. Guest)\" becomes \"Song\". Brackets, capitalization and trailing credits are all handled.\n\n" +
                "Version labels such as (Live), (Remix) or (2014 Remaster) stay. Artist tags are not changed. A title is never emptied." + safe);
            shrink.Tip("Brings every song's embedded artwork to one standard: exactly one picture, in the front-cover slot (ID3 type 3), as a JPEG of at most " +
                Artwork.EmbedSide + " × " + Artwork.EmbedSide + " at quality " + Artwork.EmbedQuality + ".\n\n" +
                "When a song has several pictures, the best cover is kept (a front cover first, then an unlabeled picture, then the largest image) and back covers, booklet pages and duplicates are removed. " +
                "Larger or non-JPEG pictures are re-encoded; smaller pictures are never enlarged; pictures already in that form are left byte for byte. Leftover tag padding is removed, so files really get smaller. " +
                "Audio and other tags are unchanged." + safe);
            albumArtist.Tip("Gives songs with an Artist but no Album Artist tag an album artist, so players keep each album together under one artist.\n\n" +
                "The album's main artist is used when the song credits it (guests stay on the Artist tag only); otherwise the song's first artist. Songs that already have an album artist are left alone." + safe);
            titleFromName.Tip("Gives songs without a Title tag their filename as the title, without the extension or a leading track number: \"01 - Song.mp3\" → \"Song\".\n\n" +
                "Songs that already have a title are left alone and files keep their names. Files with no tags at all get one. " +
                "Combined with \"Remove featured artists\" the new title is cleaned too." + safe);
            id3v23.Tip("Rewrites MP3 tags as ID3v2.3, the version every player and Windows reads.\n\n" +
                "Text that Latin-1 can hold (English and Western European accents such as é, ñ, ü) is kept in, or converted to, Latin-1. A field with any other character (星, Ж, emoji…) is written as UTF-16, so nothing is ever replaced with \"?\". " +
                "Each saved file is reread and every field compared, character for character, before the original is replaced.\n\n" +
                "ID3v2.4 dates are written as their v2.3 fields (TYER, TDAT, TIME, TORY). Old ID3v2.2 tags are upgraded. An MP3 with only an ID3v1 tag gets an ID3v2.3 tag with the same fields; " +
                "an extra APE tag is merged in and removed (with its second copy of the cover). Duplicate frames left by older versions of this tool are repaired. ID3 tags in AIFF and WAV files are converted too. Files already in this form are left alone." + safe);
            encoderState.Tip("FFmpeg converts audio to MP3. It is only needed for the two Audio options; every other option works without it.");
            setupEncoder.Tip("Open Settings to download FFmpeg (about 170 MB, once, checksum-verified) or choose a copy you already have.");
            openBackup.Tip("Opens the PC backup folder in File Explorer, where each run's originals, journal.json and report.csv are kept. Change its location in Settings.");
            savePreset.Tip("Save the options selected now as your own preset, so you can choose them again with one click. Presets are kept on this PC.");
            deletePreset.Tip("Delete your preset that matches the selected options. The options stay selected; built-in presets cannot be deleted.");
            presetName.Tip("A short name for your preset, up to " + OrganizePresets.MaxCustom + " presets of " + OrganizePresets.MaxName + " characters. Saving with an existing name replaces that preset.");
            confirmSave.Tip("Save the selected options under this name. Press Enter to save or Esc to cancel.");
            cancelSave.Tip("Close the preset name box without saving anything. Your selected options stay as they are.");
            preview.Tip("Scans the music folder and lists every change the selected options would make, with source, destination and the reason for each.\n\n" +
                "Nothing is moved, converted or rewritten during the preview. Uncheck any rows you don't want, then click Apply selected changes.");
            apply.Tip("Carries out the checked rows of the current preview (or, after Preview Undo, restores the checked files).\n\n" +
                "Each change is written to a temporary copy and verified before it replaces the original, and every finished step is recorded as it goes, so stopping part-way never leaves a half-written song. " +
                "The library is rescanned when it finishes.");
            undo.Tip("Lists the changes made by the last library run, even after restarting the app. Check the ones to reverse, then click Undo selected changes.\n\n" +
                "Originals are restored byte for byte. Files you edited after the run are not overwritten.");
            stop.Tip("Stops the current preview or run after the file in progress is finished safely. Completed changes stay recorded and can still be undone.");
            progress.Tip("Progress of the current preview, run or Undo.");
        }

        // Used by the Check library window: select just one option and preview it.
        internal Task PreviewTitleFill() => PreviewOnly(titleFromName);
        internal Task PreviewAlbumArtistFill() => PreviewOnly(albumArtist);

        private readonly TextBlock renameText = Kit.Label("", Kit.Body);
        private readonly Button cancelRename = Kit.Button("Cancel rename", "link");
        private Border renameBanner;
        private string[] renameFrom;
        private string renameTo;
        private bool renameAlbumArtists = true;
        internal string[] RenameFrom => renameFrom;

        private void SetRename(IEnumerable<string> from, string to, bool albumArtists)
        {
            var names = from?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            renameFrom = names == null || names.Length == 0 ? null : names; renameTo = to?.Trim(); renameAlbumArtists = albumArtists;
            renameBanner.IsVisible = renameFrom != null;
            string list = renameFrom == null ? "" : renameFrom.Length == 1 ? "the artist “" + renameFrom[0] + "”" : renameFrom.Length + " artists (" + string.Join(", ", renameFrom.Select(n => "“" + n + "”")) + ")";
            renameText.Text = renameFrom == null ? "" : "Renaming " + list + " to “" + renameTo + "” in every song's Artist" + (albumArtists ? " and Album Artist" : "") +
                " tag. This is a big change: review the list, then Apply. Originals are backed up and Preview Undo restores them.";
        }

        /// <summary>Previews renaming artists across the whole folder (on its own, with every other option cleared).</summary>
        internal Task PreviewArtistRename(string from, string to, bool albumArtists) => PreviewArtistRename(new[] { from }, to, albumArtists);

        internal Task PreviewArtistRename(IEnumerable<string> from, string to, bool albumArtists)
        {
            if (IsWorking) return Task.CompletedTask;
            applyingPreset = true;
            try { foreach (var check in optionBoxes.Values) check.IsChecked = false; }
            finally { applyingPreset = false; }
            SetRename(from, to, albumArtists);
            InvalidatePlan(); UpdatePresetState();
            return PreviewAsync();
        }
        internal Task PreviewArtworkFix() => PreviewOnly(shrink);

        private Task PreviewOnly(CheckBox option)
        {
            if (IsWorking) return Task.CompletedTask;
            applyingPreset = true;
            try { foreach (var check in optionBoxes.Values) check.IsChecked = check == option; }
            finally { applyingPreset = false; }
            InvalidatePlan(); UpdatePresetState();
            return PreviewAsync();
        }

        private sealed class FileRowView : Border
        {
            private readonly CheckBox check = new CheckBox { Margin = new Thickness(6, 0, 0, 0), MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock action = Cell(), from = Cell(), to = Cell(), details = Cell();
            private FileRow row;
            private bool updating;
            private static TextBlock Cell() => new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12.5 };
            internal FileRowView()
            {
                Height = 30; Background = Brushes.Transparent;
                Child = Kit.Grid("28,17*,24*,25*,34*", "*").Add(check.Cell(0, 0), action.Cell(0, 1), from.Cell(0, 2), to.Cell(0, 3), details.Cell(0, 4));
                check.IsCheckedChanged += (s, e) =>
                {
                    if (updating || row == null) return;
                    if (!row.RequestChecked(check.IsChecked == true)) { updating = true; check.IsChecked = row.IsChecked; updating = false; }
                };
                DataContextChanged += (s, e) =>
                {
                    if (row != null) row.Changed -= Update;
                    row = DataContext as FileRow;
                    if (row != null) row.Changed += Update;
                    Update();
                };
            }
            private void Update()
            {
                if (row == null) return;
                updating = true; check.IsVisible = row.HasCheck; check.IsChecked = row.IsChecked; updating = false;
                action.Text = row.Action; from.Text = row.Source; to.Text = row.Destination; details.Text = row.Details;
                foreach (var cell in new[] { action, from, to, details }) cell.Classes.Set("muted", row.Muted);
                ToolTip.SetTip(this, row.Tooltip);
            }
        }

        private string Relative(string path) => BackupSettings.Within(path, root) ? path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar) : path;
        private void UpdateEncoder()
        {
            encoderState.Text = encoder == null ? "Converting audio needs FFmpeg, a free helper program. Everything else works without it." : "FFmpeg is ready for converting audio.";
            setupEncoder.IsVisible = encoder == null && openSettings != null;
        }
        private void InvalidatePlan() { if (IsWorking) return; plan = null; undoJournal = null; SetRows(new List<FileRow>()); UpdateApply(); }

        private void SetRows(List<FileRow> next)
        {
            rows = next;
            mergeGroups = rows.Where(r => r.Tag is LibraryAction a && !string.IsNullOrEmpty(a.MergeGroup)).GroupBy(r => ((LibraryAction)r.Tag).MergeGroup).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var row in rows) row.Toggle = OnToggle;
            files.ItemsSource = rows;
            hint.IsVisible = rows.Count == 0 && !IsWorking;
        }

        // Rows stay readable and scrollable while work runs, but selections are locked.
        // A multi-disc merge is selected or cleared as one complete album.
        private bool OnToggle(FileRow row, bool value)
        {
            if (IsWorking) return false;
            row.SetChecked(value);
            if (!syncingMergeSelection && row.Tag is LibraryAction action && !string.IsNullOrEmpty(action.MergeGroup) && mergeGroups.TryGetValue(action.MergeGroup, out var group))
            {
                syncingMergeSelection = true;
                try { foreach (var other in group) other.SetChecked(value); }
                finally { syncingMergeSelection = false; }
            }
            UpdateApply();
            return true;
        }

        internal void SetRowChecked(int index, bool value) => rows[index].RequestChecked(value);
        internal int CheckedCount => rows.Count(r => r.HasCheck && r.IsChecked);

        private void SetWorking(bool value)
        {
            IsWorking = value;
            foreach (var check in optionBoxes.Values) check.IsEnabled = !value;
            foreach (Control control in new Control[] { preview, undo, presetChips, savePreset, deletePreset, confirmSave, presetName, libraryTarget, newTarget }) control.IsEnabled = !value;
            stop.IsEnabled = value; progress.IsVisible = value; UpdateApply(); UpdatePresetState();
            hint.IsVisible = rows.Count == 0 && !value;
        }

        private void UpdateApply()
        {
            apply.IsEnabled = !IsWorking && (plan != null || undoJournal != null) && rows.Any(r => r.IsChecked && (r.Tag is LibraryAction a && a.HasWork || r.Tag is JournalEntry));
            apply.Content = undoJournal != null ? "Undo selected changes" : "Apply selected changes";
            // One primary action at a time: Preview until there is something to apply, then Apply.
            bool reviewing = plan != null || undoJournal != null;
            apply.Classes.Set("primary", reviewing); preview.Classes.Set("primary", !reviewing);
        }

        internal IProgress<ProgressInfo> Reporter()
        {
            return new Progress<ProgressInfo>(p =>
            {
                if (!IsWorking) return;
                progress.IsIndeterminate = p.Total <= 0;
                progress.Value = p.Total > 0 ? Math.Max(0, Math.Min(100, (int)(100 * (p.Completed + p.Fraction) / p.Total))) : 0;
                state.Text = p.Message + (p.Total > 0 ? " · " + (int)progress.Value + "%" : "");
            });
        }

        internal async Task PreviewAsync()
        {
            if (IsWorking || !Ready()) return;
            encoder = Management.Encoder.Find(); UpdateEncoder();
            var options = new LibraryOptions { Root = root, BackupRoot = BackupSettings.Root, EncoderPath = encoder, Convert = convert.On(), ConvertOtherLossy = compatibility.On(),
                Square = square.On(), Shrink = shrink.On(), Duplicates = duplicates.On(), Organize = organize.On(), ArchiveOldBackups = archive.On(), RenameFromTitle = rename.On(), CombineDiscs = combine.On(), RemoveFeaturedTitles = cleanTitles.On(), TitleFromFilename = titleFromName.On(), Id3v23 = id3v23.On(),
                AlbumArtistFromArtist = albumArtist.On(), RenameArtistsFrom = renameFrom, RenameArtistTo = renameTo, RenameAlbumArtists = renameAlbumArtists };
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            SetWorking(true); exclusive(true); SetRows(new List<FileRow>()); plan = null; undoJournal = null;
            try
            {
                var reporter = Reporter(); var built = await Task.Run(() => LibraryPlanner.Build(options, token, reporter));
                plan = built;
                var next = new List<FileRow>(built.Actions.Count + built.Issues.Count);
                foreach (var action in built.Actions)
                {
                    var row = new FileRow { Tag = action, Action = action.HasWork ? action.Description : "Keep", Source = Relative(action.Source),
                        Destination = action.Keeper != null ? "Keep: " + Relative(action.Keeper) : action.Target != null ? Relative(action.Target) : "PC backup folder",
                        Details = action.Reason ?? "", Muted = !action.HasWork, Tooltip = action.Source + "\n" + (action.Target ?? action.Keeper ?? "PC backup folder") + "\n" + (action.Reason ?? "") };
                    row.SetChecked(action.HasWork); next.Add(row);
                }
                foreach (string issue in built.Issues) next.Add(new FileRow { Action = "Skipped", Source = issue, Muted = true, Tooltip = issue });
                SetRows(next);
                long bytes = built.Actions.Where(a => a.HasWork && (a.Archive || a.Convert || a.Square || a.ChangesTags)).Sum(a => a.Stamp.Length);
                state.Text = built.Actions.Count(a => a.HasWork) + " changes across " + built.Actions.Count + " files · originals need about " + (bytes / 1048576.0).ToString("N0") + " MB in the backup folder · " + built.Issues.Count + " skipped/problems. Review, uncheck unwanted changes, then Apply.";
            }
            catch (OperationCanceledException) { state.Text = "Preview stopped. No files were changed."; }
            catch (Exception e) { state.Text = "Preview could not finish: " + e.Message; }
            finally { exclusive(false); SetWorking(false); }
        }

        private bool Ready()
        { if (canStart()) return true; state.Text = "Wait for scans, artwork/artist jobs and duplicate scans to finish. Apply or discard any staged cover in the main window first."; return false; }

        internal async Task ApplyAsync()
        {
            if (IsWorking || !Ready()) return;
            if (plan == null && undoJournal == null) return;
            if (plan != null) foreach (var row in rows) if (row.Tag is LibraryAction action) action.Selected = row.IsChecked;
            var selectedUndo = new HashSet<JournalEntry>(rows.Where(r => r.IsChecked && r.Tag is JournalEntry).Select(r => (JournalEntry)r.Tag));
            source?.Dispose(); source = new CancellationTokenSource(); var token = source.Token;
            SetWorking(true); exclusive(true); var reporter = Reporter();
            bool wrote = false;
            try
            {
                if (undoJournal != null)
                {
                    var journal = undoJournal; int before = journal.Issues.Count;
                    int restored = await Task.Run(() => LibraryRunner.Undo(journal, token, reporter, selectedUndo)); wrote = restored > 0;
                    state.Text = restored + " changes restored · " + (journal.Issues.Count - before) + " issues. See the backup journal for details.";
                }
                else
                {
                    var current = plan;
                    var result = await Task.Run(() => LibraryRunner.Run(current, token, reporter));
                    wrote = result.Entries.Any(e => e.State == "Committed" || e.State == "TargetCreated");
                    state.Text = (result.Stopped ? "Stopped safely. " : "Finished. ") + result.Entries.Count(e => e.State == "Committed") + " changes completed · " + result.Issues.Count + " issues. Backups and Undo journal: " + result.Directory;
                }
            }
            catch (OperationCanceledException) { state.Text = "Stopped. Completed changes remain recorded in the backup journal."; wrote = true; }
            catch (Exception e) { state.Text = "Task could not finish: " + e.Message + " Completed changes can be reviewed in the backup journal."; wrote = true; }
            finally
            {
                bool renamed = plan != null && renameFrom != null;
                plan = null; undoJournal = null; exclusive(false); SetWorking(false); apply.IsEnabled = false;
                if (renamed) SetRename(null, null, true);
                if (wrote) await refreshLibrary(root);
            }
        }

        private void PreviewUndo()
        {
            try
            {
                undoJournal = LibraryJournal.Last(); plan = null; SetRows(new List<FileRow>());
                if (undoJournal == null) { state.Text = "There is no saved library run to undo on this PC."; return; }
                var next = new List<FileRow>();
                foreach (var entry in undoJournal.Entries.Where(e => e.State == "Committed" || e.State == "Prepared" || e.State == "TargetCreated"))
                {
                    var row = new FileRow { Tag = entry, Action = "Restore original", Source = entry.Target ?? entry.Backup, Destination = entry.Source, Details = entry.Error ?? "Verified backup / move history" };
                    row.SetChecked(true); next.Add(row);
                }
                SetRows(next);
                state.Text = "Undo preview for " + undoJournal.Root + ". Only files unchanged since that run will be restored. Check the changes to undo, then click Undo selected changes.";
            }
            catch (Exception e) { undoJournal = null; state.Text = "Could not read the last library journal: " + e.Message; }
            finally { UpdateApply(); }
        }

        internal void SetWorkingForTest(bool value) => SetWorking(value);
        internal Control OptionsPanel => optionsPanel;
    }
}
