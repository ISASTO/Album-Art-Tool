using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Updates;

namespace AlbumArtTool
{
    internal sealed partial class MainForm
    {
        private readonly Button getUpdates = Button("Get updates");
        private Form updateWindow;
        private bool checkingUpdate, installingUpdate, restartForUpdate;
        internal Func<UpdateService> CreateUpdateService = () => new UpdateService();
        internal bool CanInstallUpdate => !busy && !writing && !queueRunning && pending == null && (duplicatesWindow == null || !duplicatesWindow.IsScanning);

        internal async Task GetUpdatesAsync()
        {
            if (updateWindow != null && !updateWindow.IsDisposed) { updateWindow.Activate(); return; }
            if (checkingUpdate) return;
            checkingUpdate = true;
            var dialog = updateWindow = new Form { Text = "Get updates", ClientSize = new Size(530, 222), MinimumSize = new Size(530, 260),
                StartPosition = FormStartPosition.CenterParent, BackColor = Surface, ForeColor = Ink, Font = new Font("Segoe UI", 10),
                MaximizeBox = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var version = Label("Installed version: " + UpdateService.DisplayVersion, 10, true);
            var message = Label("Checking GitHub for a newer release…", 10); message.AccessibleName = "App update status";
            var downloadProgress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Margin = new Padding(0, 4, 0, 4), AccessibleName = "App download progress" };
            var close = Button("Cancel"); close.Dock = DockStyle.Right; close.Width = 110;
            close.Click += (s, e) => dialog.Close();
            layout.Controls.Add(version, 0, 0); layout.Controls.Add(message, 0, 1);
            layout.Controls.Add(downloadProgress, 0, 2); layout.Controls.Add(close, 0, 3); dialog.Controls.Add(layout);
            var source = new CancellationTokenSource(); var token = source.Token;
            FormClosedEventHandler cancel = (s, e) => source.Cancel();
            dialog.FormClosed += cancel;
            dialog.FormClosing += (s, e) => { if (installingUpdate && !restartForUpdate) e.Cancel = true; };
            dialog.Show(this);
            string directory = null;
            bool handedOff = false, downloading = false;
            try
            {
                using (var service = CreateUpdateService())
                {
                    var release = await service.CheckAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (release == null)
                    {
                        message.Text = "You are up to date. Version " + UpdateService.DisplayVersion + " is installed.";
                        return;
                    }
                    directory = UpdateInstaller.CreateWorkspace();
                    downloading = true;
                    downloadProgress.Style = ProgressBarStyle.Continuous; downloadProgress.Value = 0;
                    message.Text = "Downloading version " + release.Version.ToString(3) + " · 0%";
                    var reporter = new Progress<int>(percent =>
                    {
                        if (!downloading || dialog.IsDisposed || token.IsCancellationRequested) return;
                        downloadProgress.Value = Math.Max(0, Math.Min(100, percent));
                        message.Text = "Downloading version " + release.Version.ToString(3) + " · " + percent + "%";
                    });
                    await service.DownloadAsync(release, Path.Combine(directory, "Download.exe"), reporter, token);
                    downloading = false;
                    token.ThrowIfCancellationRequested();
                    downloadProgress.Value = 100;
                    while (!CanInstallUpdate)
                    {
                        message.Text = pending != null
                            ? "Update downloaded. Apply or discard your staged cover to restart and install it. You can cancel this update."
                            : "Update downloaded. Waiting for scans and the work queue to finish before restarting. You can cancel this update.";
                        await Task.Delay(200, token);
                    }
                    // This check and disabling input occur together on the UI thread: no new job can slip into the handoff.
                    token.ThrowIfCancellationRequested();
                    installingUpdate = true; Enabled = false;
                    close.Enabled = false; dialog.ControlBox = false;
                    message.Text = "Installing version " + release.Version.ToString(3) + ". The app will reopen in your current music folder…";
                    string target = Application.ExecutablePath, root = folder.Text;
                    UpdateJob job;
                    using (var parent = Process.GetCurrentProcess())
                        job = await Task.Run(() => UpdateInstaller.Prepare(directory, target, root, release, parent), token);
                    await UpdateInstaller.StartHelperAsync(directory, job, token);
                    handedOff = true; restartForUpdate = true;
                    Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!dialog.IsDisposed) message.Text = "Update could not finish. " + e.Message;
            }
            finally
            {
                downloading = false; checkingUpdate = false;
                if (!handedOff && directory != null) UpdateInstaller.CleanWorkspace(directory);
                if (!IsDisposed && !handedOff) { installingUpdate = false; Enabled = true; }
                if (!dialog.IsDisposed)
                {
                    downloadProgress.Visible = false; close.Enabled = true; close.Text = "Close"; dialog.ControlBox = true;
                }
                dialog.FormClosed -= cancel; source.Dispose();
            }
        }
    }
}
