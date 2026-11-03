using System;
using System.Threading;
using Avalonia;

namespace AlbumArtTool
{
    internal static class Program
    {
        // Held for the app's lifetime so the installer can wait for this instance to exit
        // before it replaces files during an update.
        internal const string RunningMutex = "AlbumArtTool.Running";

        [STAThread]
        private static int Main(string[] args)
        {
            using (var running = new Mutex(false, RunningMutex))
            {
                App.Arguments = args;
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }
}
