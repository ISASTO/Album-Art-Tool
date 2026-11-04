using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
        internal Func<string, string, System.Diagnostics.Process> LaunchInstaller = UpdateInstaller.Launch;
        internal bool CanInstallUpdate => !busy && !writing && !queueRunning && !maintenanceRunning && (managerWindow == null || !managerWindow.IsWorking) && pending == null &&
            (duplicatesWindow == null || !duplicatesWindow.IsScanning);
        internal Window UpdateWindow => updateWindow;

        internal async Task GetUpdatesAsync()
        {
            if (updateWindow != null) { updateWindow.Activate(); return; }
            if (checkingUpdate) return;
            checkingUpdate = true;
            var dialog = updateWindow = new Window { Title = "Get updates", Width = 530, Height = 222, MinWidth = 530, MinHeight = 222, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var version = Kit.Label("Installed version: " + UpdateService.DisplayVersion, Kit.Body, true);
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
                    var release = await service.CheckAsync(token, InstalledVersion);
                    token.ThrowIfCancellationRequested();
                    if (release == null)
                    {
                        message.Text = "You are up to date. Version " + UpdateService.DisplayVersion + " is installed.";
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
    }
}
