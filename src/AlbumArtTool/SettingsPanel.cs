using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Ui;
using AlbumArtTool.Updates;

namespace AlbumArtTool
{
    // Everything that is set once and rarely touched: appearance, folders, helper programs and updates.
    // Keeping it here lets the working screens show only the work.
    internal sealed class SettingsPanel : UserControl
    {
        private readonly Func<string> musicFolder;
        private readonly Func<Task> changeFolder, checkUpdates;
        private readonly Button systemTheme = Kit.Button("Match Windows", "chip"), lightTheme = Kit.Button("Light", "chip"), darkTheme = Kit.Button("Dark", "chip");
        private readonly TextBlock folderValue = Value(), backupValue = Value(), encoderValue = Value(), downloaderValue = Value(), cookiesValue = Value(), versionValue = Value();
        private readonly Button changeMusic = Kit.Button("Change…"), changeBackup = Kit.Button("Change…"), openBackup = Kit.Button("Open"),
            downloadEncoder = Kit.Button("Download"), chooseEncoder = Kit.Button("Choose…"), downloadDownloader = Kit.Button("Download"),
            chooseCookies = Kit.Button("Choose…"), checkNow = Kit.Button("Check for updates");
        private readonly TextBlock message = Kit.Label("", Kit.Small, cls: "muted");
        private CancellationTokenSource downloads;
        internal bool IsWorking { get; private set; }
        internal Action<ProcessStartInfo> StartExplorer = info => { using (Process.Start(info)) { } };

        internal SettingsPanel(Func<string> musicFolder, Func<Task> changeFolder, Func<Task> checkUpdates)
        {
            this.musicFolder = musicFolder; this.changeFolder = changeFolder; this.checkUpdates = checkUpdates;
            systemTheme.Named("Appearance: Match Windows").Tip("Follow the light or dark setting in Windows, switching automatically when Windows does.");
            lightTheme.Named("Appearance: Light").Tip("Always use the light appearance, whatever Windows uses.");
            darkTheme.Named("Appearance: Dark").Tip("Always use the dark appearance, whatever Windows uses.");
            systemTheme.Click += (s, e) => SetTheme(ThemeMode.System);
            lightTheme.Click += (s, e) => SetTheme(ThemeMode.Light);
            darkTheme.Click += (s, e) => SetTheme(ThemeMode.Dark);
            var themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var b in new[] { systemTheme, lightTheme, darkTheme }) themes.Children.Add(b);

            changeMusic.Named("Change music folder").Tip("Choose the top folder that contains your music. The app scans it again and remembers it for next time.");
            changeMusic.Click += async (s, e) => { await this.changeFolder(); Refresh(); };
            changeBackup.Named("Change backup folder").Tip("Choose where originals, removed copies and Undo information are kept. It must be on this PC, outside the music folder, so your player never indexes the backups.");
            changeBackup.Click += async (s, e) => await ChooseBackupAsync();
            openBackup.Named("Open backup folder").Tip("Open the backup folder in File Explorer. Each run has its own folder with the originals, journal.json (used by Undo) and a readable report.csv.");
            openBackup.Click += (s, e) => Open(BackupSettings.Root);
            downloadEncoder.Named("Download FFmpeg").Tip("Download a Windows build of FFmpeg (about 170 MB, once) from BtbN/FFmpeg-Builds on GitHub. Its SHA-256 checksum is verified before anything is extracted or run.");
            downloadEncoder.Click += async (s, e) => await DownloadEncoderAsync();
            chooseEncoder.Named("Choose FFmpeg").Tip("Use an ffmpeg.exe you already have instead of downloading one. The location is remembered.");
            chooseEncoder.Click += async (s, e) => await ChooseEncoderAsync();
            downloadDownloader.Named("Download yt-dlp").Tip("Download yt-dlp and Deno from their official GitHub releases (verified with GitHub's SHA-256 digests). They are only used to replace low-bitrate songs from YouTube Music.");
            downloadDownloader.Click += async (s, e) => await DownloadDownloaderAsync();
            chooseCookies.Named("YouTube cookies file").Tip("Only needed if YouTube says \"confirm you're not a bot\": choose a cookies.txt exported from a browser where you are signed in to YouTube. Click again to stop using it.");
            chooseCookies.Click += async (s, e) => await ChooseCookiesAsync();
            checkNow.Named("Check for updates").Tip("Check GitHub for a newer version. A download waits for your scans and queued work to finish before the app restarts to install it.");
            checkNow.Click += async (s, e) => await this.checkUpdates();
            message.Named("Settings status"); message.TextWrapping = TextWrapping.Wrap; message.TextTrimming = TextTrimming.None;

            var sections = new StackPanel { Spacing = 12 };
            sections.Children.Add(Section("Appearance", Row("Theme", themes)));
            sections.Children.Add(Section("Library", Row("Music folder", folderValue, changeMusic), Row("Backups", backupValue, changeBackup, openBackup)));
            sections.Children.Add(Section("Helper programs", Row("FFmpeg", encoderValue, downloadEncoder, chooseEncoder), Row("yt-dlp", downloaderValue, downloadDownloader), Row("YouTube cookies", cookiesValue, chooseCookies)));
            sections.Children.Add(Section("Updates", Row("Version", versionValue, checkNow)));
            sections.Children.Add(message);
            Content = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Appearance.Changed += Refresh;
            DetachedFromVisualTree += (s, e) => { Appearance.Changed -= Refresh; downloads?.Cancel(); };
            Refresh();
        }

        private static TextBlock Value()
        {
            var value = Kit.Label("", Kit.Small, cls: "muted");
            value.Margin = new Thickness(0, 0, 10, 0);
            return value;
        }

        private static Control Section(string title, params Control[] rows)
        {
            var stack = new StackPanel { Spacing = 2 };
            var caption = Kit.Caption(title); caption.Margin = new Thickness(2, 0, 0, 6);
            stack.Children.Add(caption);
            foreach (var row in rows) stack.Children.Add(row);
            return Kit.Frost(stack, new Thickness(16, 12, 12, 12), 14);
        }

        private static Control Row(string label, Control value, params Button[] buttons)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in buttons) { b.MinWidth = 86; b.Height = 30; actions.Children.Add(b); }
            var grid = Kit.Grid("140,*,Auto", "36").Add(Kit.Label(label, Kit.Body).Cell(0, 0), value.Cell(0, 1), actions.Cell(0, 2));
            return grid;
        }

        internal void Refresh()
        {
            var mode = Appearance.Mode;
            systemTheme.Classes.Set("selected", mode == ThemeMode.System);
            lightTheme.Classes.Set("selected", mode == ThemeMode.Light);
            darkTheme.Classes.Set("selected", mode == ThemeMode.Dark);
            string folder = musicFolder();
            folderValue.Text = Kit.MiddleEllipsis(folder, 64); folderValue.Tip(folder);
            string backup = BackupSettings.Root;
            backupValue.Text = Kit.MiddleEllipsis(backup, 64); backupValue.Tip(backup);
            if (!IsWorking)
            {
                string encoder = Encoder.Find();
                encoderValue.Text = encoder == null ? "Not set up · needed to convert audio and to measure real bitrates" : "Ready · " + Kit.MiddleEllipsis(encoder, 52);
                encoderValue.Tip(encoder ?? "FFmpeg converts audio to MP3 and measures real bitrates. Every other feature works without it.");
                downloadEncoder.SetText(encoder == null ? "Download" : "Download again");
                string downloader = YtDlp.Find();
                string tag = YtDlpSetup.InstalledTag;
                downloaderValue.Text = downloader == null ? "Not set up · downloaded the first time you replace songs" : "Ready" + (tag.Length > 0 ? " · version " + tag : "");
                downloaderValue.Tip(downloader ?? "yt-dlp downloads songs from YouTube Music. It is set up automatically when you first use it.");
                downloadDownloader.SetText(downloader == null ? "Download" : "Update");
            }
            string cookies = YtDlp.Cookies;
            cookiesValue.Text = cookies == null ? "Not used · only needed if YouTube asks to confirm you're not a bot" : Path.GetFileName(cookies);
            cookiesValue.Tip(cookies);
            chooseCookies.SetText(cookies == null ? "Choose…" : "Remove");
            versionValue.Text = UpdateService.DisplayVersion;
        }

        private void SetTheme(ThemeMode mode)
        {
            if (!Appearance.SetMode(mode, animate: true)) message.Text = "The appearance changed, but the choice could not be saved on this PC.";
            Refresh();
        }

        private IStorageProvider Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

        private async Task ChooseBackupAsync()
        {
            if (Storage == null) return;
            var result = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a backup folder on your PC, outside the music folder", AllowMultiple = false });
            string picked = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (picked == null) return;
            SetBackupFolder(picked);
        }

        /// <summary>Saves a new backup folder; refused inside the music folder.</summary>
        internal bool SetBackupFolder(string path)
        {
            try
            {
                if (BackupSettings.Within(path, musicFolder())) { message.Text = "Choose a backup folder outside your music folder, so your player never shows the backups as songs."; return false; }
                BackupSettings.Root = path; message.Text = "Backups will be saved in " + path + ".";
                Refresh(); return true;
            }
            catch (Exception e) when (Scanner.IsFileError(e) || e is ArgumentException) { message.Text = "That folder cannot be used: " + e.Message; return false; }
        }

        private void Open(string path)
        {
            try { Directory.CreateDirectory(path); StartExplorer(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { message.Text = "Could not open the folder: " + e.Message; }
        }

        private async Task ChooseEncoderAsync()
        {
            if (Storage == null) return;
            var result = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose ffmpeg.exe", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("FFmpeg") { Patterns = new[] { Encoder.ExecutableName } }, new FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } } } });
            string picked = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (picked == null) return;
            try { Encoder.Remember(picked); message.Text = "FFmpeg will be used from " + picked + "."; }
            catch (Exception e) when (Scanner.IsFileError(e)) { message.Text = "Could not remember that location: " + e.Message; }
            Refresh();
        }

        private async Task ChooseCookiesAsync()
        {
            if (YtDlp.Cookies != null) { YtDlp.Cookies = null; message.Text = "Cookies file removed. Downloads are anonymous again."; Refresh(); return; }
            if (Storage == null) return;
            var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a cookies.txt exported from a browser signed in to YouTube", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Cookies file") { Patterns = new[] { "*.txt" } } } });
            string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path == null) return;
            YtDlp.Cookies = path; message.Text = "YouTube Music downloads will use " + Path.GetFileName(path) + ".";
            Refresh();
        }

        private void SetWorking(bool value)
        {
            IsWorking = value;
            foreach (var b in new[] { downloadEncoder, chooseEncoder, downloadDownloader }) b.IsEnabled = !value;
        }

        private async Task DownloadEncoderAsync()
        {
            if (IsWorking) return;
            downloads?.Dispose(); downloads = new CancellationTokenSource(); var token = downloads.Token;
            SetWorking(true);
            try
            {
                var progress = new Progress<int>(n => { if (IsWorking) encoderValue.Text = "Downloading FFmpeg · " + n + "%"; });
                using (var setup = new EncoderSetup()) await Task.Run(() => setup.InstallAsync(progress, token));
                message.Text = "FFmpeg is ready.";
            }
            catch (OperationCanceledException) { message.Text = "FFmpeg download cancelled."; }
            catch (Exception e) { message.Text = "FFmpeg could not be set up: " + e.Message; }
            finally { SetWorking(false); Refresh(); }
        }

        private async Task DownloadDownloaderAsync()
        {
            if (IsWorking) return;
            downloads?.Dispose(); downloads = new CancellationTokenSource(); var token = downloads.Token;
            SetWorking(true);
            try
            {
                string current = YtDlp.Find();
                bool deno = current == null || YtDlp.FindDeno(current) == null;
                var progress = new Progress<int>(n => { if (IsWorking) downloaderValue.Text = "Downloading yt-dlp" + (deno ? " and Deno" : "") + " · " + n + "%"; });
                using (var setup = new YtDlpSetup()) await Task.Run(() => setup.InstallAsync(deno, progress, token));
                message.Text = "yt-dlp is ready.";
            }
            catch (OperationCanceledException) { message.Text = "yt-dlp download cancelled."; }
            catch (Exception e) { message.Text = "yt-dlp could not be set up: " + e.Message; }
            finally { SetWorking(false); Refresh(); }
        }
    }
}
