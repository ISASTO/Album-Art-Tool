using System;
using System.Threading;
using Avalonia;

namespace AlbumArtTool
{
    internal static class Program
    {
        // Held for the app's lifetime so the installer can wait for this instance to exit
        // before it replaces files during an update.
        internal const string RunningMutex = "Muzikette.Running";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--self-test") return SelfTest.Run(args[1], args[2]);
            // --smoke <library> <png>: opens the library, saves a picture of the window once it is scanned, and exits (CI checks a
            // packaged build starts and draws on a real display server).
            if (args.Length == 3 && args[0] == "--smoke") { App.SmokeShot = System.IO.Path.GetFullPath(args[2]); args = new[] { args[1] }; }
            // Moves the pre-4.0 settings folder (Music Library Manager) before anything reads a setting.
            _ = Core.BackupSettings.AppData;
            using (var running = new Mutex(false, RunningMutex))
            {
                App.Arguments = args;
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }

        // On Linux the window class matches the desktop file (StartupWMClass), so docks show the app's own icon and name.
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "Muzikette" }).LogToTrace();
    }
}
