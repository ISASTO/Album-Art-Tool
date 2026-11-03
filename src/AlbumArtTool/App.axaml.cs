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
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    // Mirror a WinForms ThreadException dialog: report and keep the app usable.
                    e.Handled = true;
                    var owner = desktop.MainWindow;
                    if (owner != null && owner.IsVisible) _ = Dialogs.ShowMessage(owner, "Album Art Tool", e.Exception.Message);
                };
                Task.Run(UpdateInstaller.CleanOldWorkspaces);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.Exit += (s, e) => ScanCache.Flush();
                desktop.MainWindow = new MainWindow(ResolveRoot(Arguments));
            }
            base.OnFrameworkInitializationCompleted();
        }

        // A folder on the command line wins. A portable copy keeps the original behavior of
        // opening the folder it was placed in; an installed copy reopens the last library.
        internal static string ResolveRoot(string[] args)
        {
            if (args.Length > 0 && Directory.Exists(args[0])) return Path.GetFullPath(args[0]);
            string baseDirectory = AppContext.BaseDirectory;
            bool installed = File.Exists(Path.Combine(baseDirectory, "unins000.exe"));
            if (!installed) return baseDirectory;
            string last = AppSettings.LastFolder;
            if (last != null) return last;
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return Directory.Exists(music) ? music : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
