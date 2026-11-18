using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;
using AlbumArtTool.Updates;

namespace AlbumArtTool
{
    public partial class App : Application
    {
        internal static string[] Arguments = Array.Empty<string>();

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            Appearance.Initialize(this);
            Skin.Install(this);
        }

        private static readonly System.Collections.Generic.HashSet<string> showing = new System.Collections.Generic.HashSet<string>();

        private static async Task ReportAsync(Window owner, string message)
        {
            try { await Dialogs.ShowMessage(owner, "Something went wrong", message + "\n\nThe details are in errors.log in " + BackupSettings.AppData + "."); }
            finally { showing.Remove(message); }
        }

        /// <summary>An exception thrown inside the UI framework itself (its first frame is Avalonia's), such as a text layout bug.</summary>
        internal static bool IsFrameworkGlitch(Exception e)
        {
            string trace = (e.StackTrace ?? "").TrimStart();
            return trace.StartsWith("at Avalonia.", StringComparison.Ordinal) && !(e is OutOfMemoryException);
        }

        private static void LogError(Exception e)
        {
            try
            {
                string log = Path.Combine(BackupSettings.AppData, "errors.log");
                Directory.CreateDirectory(BackupSettings.AppData);
                if (File.Exists(log) && new FileInfo(log).Length > 512 * 1024) File.Delete(log);
                File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " Muzikette " + UpdateService.DisplayVersion + "\n" + e + "\n\n");
            }
            catch (Exception) { }
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    // Keep the app usable: every error is written to errors.log in the data folder. A glitch inside the UI
                    // framework's own layout or drawing (nothing you did, and nothing you can act on) is only logged;
                    // anything else is reported once, however often it repeats while its message is open.
                    e.Handled = true;
                    LogError(e.Exception);
                    if (IsFrameworkGlitch(e.Exception)) return;
                    var owner = desktop.MainWindow;
                    string message = e.Exception.Message;
                    if (owner == null || !owner.IsVisible || !showing.Add(message)) return;
                    _ = ReportAsync(owner, message);
                };
                Task.Run(UpdateInstaller.CleanOldWorkspaces);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.Exit += (s, e) => ScanCache.Flush();
                string root = ResolveRoot(Arguments, out bool firstRun);
                desktop.MainWindow = new MainWindow(root, firstRun: firstRun);
            }
            base.OnFrameworkInitializationCompleted();
        }

        // A folder on the command line wins. A portable copy keeps the original behavior of
        // opening the folder it was placed in; an installed copy reopens the last library.
        internal static string ResolveRoot(string[] args) => ResolveRoot(args, out _);

        /// <summary>The folder to open. First run: an installed copy that has never scanned a folder (it shows Welcome instead of scanning).</summary>
        internal static string ResolveRoot(string[] args, out bool firstRun)
        {
            firstRun = false;
            if (args.Length > 0 && Directory.Exists(args[0])) return Path.GetFullPath(args[0]);
            string baseDirectory = AppContext.BaseDirectory;
            bool installed = File.Exists(Path.Combine(baseDirectory, "unins000.exe"));
            if (!installed) return baseDirectory;
            string last = AppSettings.LastFolder;
            if (last != null) return last;
            firstRun = true;
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return Directory.Exists(music) ? music : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
