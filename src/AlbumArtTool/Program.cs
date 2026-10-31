using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Threading.Tasks;
using AlbumArtTool.Updates;

namespace AlbumArtTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--apply-update") return UpdateInstaller.RunHelper();
            // Register before the JIT loads any method that refers to TagLibSharp.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                if (new AssemblyName(e.Name).Name != "TagLibSharp") return null;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlbumArtTool.TagLibSharp.dll"))
                {
                    if (stream == null) return null;
                    using (var buffer = new MemoryStream())
                    { stream.CopyTo(buffer); return Assembly.Load(buffer.ToArray()); }
                }
            };
            Start(args);
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Start(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.Message,
                "Album Art Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            string root = args.Length > 0 && Directory.Exists(args[0])
                ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            Task.Run(() => UpdateInstaller.CleanOldWorkspaces());
            using (var form = new MainForm(root))
            {
                form.Shown += (s, e) => UpdateInstaller.SignalStarted(args);
                Application.Run(form);
            }
        }
    }
}
