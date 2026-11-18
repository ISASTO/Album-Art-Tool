using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;
using AlbumArtTool.Updates;

namespace AlbumArtTool
{
    internal sealed partial class MainWindow
    {
        private Window updateWindow;
        private bool checkingUpdate, installingUpdate, restartForUpdate;
        internal Func<UpdateService> CreateUpdateService = () => new UpdateService();
        internal Version InstalledVersion;
        /// <summary>How this copy updates (tests choose the Windows installer path on every platform).</summary>
        internal UpdateService.Package UpdatePackage = UpdateService.CurrentPackage;
        /// <summary>Opens the release page for an update to install by hand.</summary>
        internal Action<string> OpenPage = url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        /// <summary>Starts the replaced AppImage in the current music folder.</summary>
        internal Action<string, string> RelaunchAppImage = (appImage, folder) =>
        {
            var info = new System.Diagnostics.ProcessStartInfo(appImage) { UseShellExecute = false };
            info.ArgumentList.Add(folder);
            System.Diagnostics.Process.Start(info)?.Dispose();
        };
        internal Func<string, string, System.Diagnostics.Process> LaunchInstaller = UpdateInstaller.Launch;
        internal bool CanInstallUpdate => !busy && !writing && !queueRunning && !maintenanceRunning && (organizePage == null || !organizePage.IsWorking) && pending == null &&
            (healthPage == null || !healthPage.IsScanning) && (youtubePage == null || !youtubePage.IsWorking) && (settingsPage == null || !settingsPage.IsWorking) &&
            (addMusicPage == null || !addMusicPage.Queue.IsRunning && !addMusicPage.IsWorking);
        internal Window UpdateWindow => updateWindow;

        internal async Task GetUpdatesAsync()
        {
            if (updateWindow != null) { updateWindow.Activate(); return; }
            if (checkingUpdate) return;
            checkingUpdate = true;
            var dialog = updateWindow = new Window { Title = "Check for updates", Width = 530, Height = 222, MinWidth = 530, MinHeight = 222, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var version = Kit.Heading("Muzikette " + UpdateService.DisplayVersion, 18);
            var message = Kit.Label("Checking GitHub for a newer release…", Kit.Body).Named("App update status");
            message.TextWrapping = TextWrapping.Wrap; message.TextTrimming = TextTrimming.None; message.VerticalAlignment = VerticalAlignment.Top; message.Margin = new Thickness(0, 6, 0, 0);
            var downloadProgress = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 }.Named("App download progress");
            var close = Kit.Button("Cancel", "primary"); close.Width = 110; close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Click += (s, e) => dialog.Close();
            var layout = Kit.Grid("*", "28,*,22,44").Add(version.Cell(0, 0), message.Cell(1, 0), downloadProgress.Cell(2, 0), close.Cell(3, 0));
            layout.Margin = new Thickness(18);
            var source = new CancellationTokenSource(); var token = source.Token;
            EventHandler cancel = (s, e) => source.Cancel();
            dialog.Closed += cancel;
            dialog.Closed += (s, e) => { if (updateWindow == dialog) updateWindow = null; };
            dialog.Closing += (s, e) => { if (installingUpdate && !restartForUpdate) e.Cancel = true; };
            Kit.Prepare(dialog, layout);
            dialog.Show(this);
            string directory = null;
            bool handedOff = false, downloading = false;
            try
            {
                using (var service = CreateUpdateService())
                {
                    var release = await service.CheckAsync(token, InstalledVersion, UpdatePackage);
                    token.ThrowIfCancellationRequested();
                    if (release == null)
                    {
                        message.Text = "You are up to date. Version " + UpdateService.DisplayVersion + " is installed.";
                        return;
                    }
                    // A Flatpak or a plain folder: the new version is downloaded from the release page.
                    if (release.Manual)
                    {
                        message.Text = "Version " + release.Version.ToString(3) + " is available. " + (Platform.Flatpak
                            ? "Download Muzikette.flatpak from the release page and open it to update."
                            : "Download it from the release page.");
                        var open = Kit.Button("Open release page", "primary"); open.MinWidth = 170; open.HorizontalAlignment = HorizontalAlignment.Left; open.Named("Open release page");
                        open.Click += (s, e) => { try { OpenPage(release.PageUrl); } catch (Exception ex) { message.Text = "Could not open the page: " + ex.Message; } };
                        layout.Children.Add(open.Cell(3, 0));
                        return;
                    }
                    if (UpdatePackage == UpdateService.Package.AppImage)
                    {
                        await ReplaceAppImageAsync(service, release, message, downloadProgress, close, token);
                        handedOff = restartForUpdate;
                        return;
                    }
                    directory = UpdateInstaller.CreateWorkspace();
                    downloading = true;
                    downloadProgress.IsIndeterminate = false; downloadProgress.Value = 0;
                    message.Text = "Downloading version " + release.Version.ToString(3) + " · 0%";
                    var reporter = new Progress<int>(percent =>
                    {
                        if (!downloading || token.IsCancellationRequested) return;
                        downloadProgress.Value = Math.Max(0, Math.Min(100, percent));
                        message.Text = "Downloading version " + release.Version.ToString(3) + " · " + percent + "%";
                    });
                    string setup = Path.Combine(directory, UpdateInstaller.SetupFile);
                    await Task.Run(() => service.DownloadAsync(release, setup, reporter, token), token);
                    downloading = false;
                    token.ThrowIfCancellationRequested();
                    downloadProgress.Value = 100;
                    while (!CanInstallUpdate)
                    {
                        message.Text = pending != null
                            ? "Update downloaded. Apply or discard your staged cover to restart and install it. You can cancel this update."
                            : "Update downloaded. Waiting for background tasks to finish before restarting. You can cancel this update.";
                        await Task.Delay(200, token);
                    }
                    // This check and disabling input occur together on the UI thread: no new job can slip into the handoff.
                    token.ThrowIfCancellationRequested();
                    installingUpdate = true; IsEnabled = false;
                    close.IsEnabled = false;
                    message.Text = "Installing version " + release.Version.ToString(3) + ". The app will reopen in your current music folder…";
                    // Re-verify immediately before running it: the file sat on disk while we waited.
                    await Task.Run(() => UpdateService.VerifyInstaller(setup, release.Sha256, release.Version), token);
                    using (LaunchInstaller(directory, Root)) { }
                    handedOff = true; restartForUpdate = true;
                    Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { message.Text = "Update could not finish. " + e.Message; }
            finally
            {
                downloading = false; checkingUpdate = false;
                if (!handedOff && directory != null) UpdateInstaller.CleanWorkspace(directory);
                if (!handedOff) { installingUpdate = false; IsEnabled = true; }
                downloadProgress.IsVisible = false; close.IsEnabled = true; close.Content = "Close";
                dialog.Closed -= cancel; source.Dispose();
            }
        }

        // An AppImage updates itself: the new one is downloaded next to it, checked against GitHub's SHA-256, made
        // executable and moved over the old file (the running copy keeps its own), then started in the same music folder.
        internal string AppImagePath = Platform.AppImage;

        private async Task ReplaceAppImageAsync(UpdateService service, AppRelease release, TextBlock message, ProgressBar bar, Button close, CancellationToken token)
        {
            string target = AppImagePath, temporary = target + ".update-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // The new file has to sit beside the old one so it can replace it in one move.
                try { using (File.Create(temporary)) { } File.Delete(temporary); }
                catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
                {
                    message.Text = "Version " + release.Version.ToString(3) + " is available, but the folder this AppImage is in cannot be written to. Download the new AppImage from the release page.";
                    return;
                }
                bar.IsIndeterminate = false; bar.Value = 0;
                message.Text = "Downloading version " + release.Version.ToString(3) + " · 0%";
                var reporter = new Progress<int>(percent => { if (token.IsCancellationRequested) return; bar.Value = Math.Max(0, Math.Min(100, percent)); message.Text = "Downloading version " + release.Version.ToString(3) + " · " + percent + "%"; });
                await Task.Run(() => service.DownloadAsync(release, temporary, reporter, token), token);
                bar.Value = 100;
                while (!CanInstallUpdate)
                {
                    message.Text = pending != null
                        ? "Update downloaded. Apply or discard your staged cover to restart into it. You can cancel this update."
                        : "Update downloaded. Waiting for background tasks to finish before restarting. You can cancel this update.";
                    await Task.Delay(200, token);
                }
                token.ThrowIfCancellationRequested();
                installingUpdate = true; IsEnabled = false; close.IsEnabled = false;
                message.Text = "Installing version " + release.Version.ToString(3) + ". Muzikette will reopen in your current music folder…";
                if (!UpdateService.Hash(temporary).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded AppImage's checksum did not match. Your current app has not changed.");
                Platform.MakeExecutable(temporary);
                File.Move(temporary, target, true);
                RelaunchAppImage(target, Root);
                restartForUpdate = true;
                Close();
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
