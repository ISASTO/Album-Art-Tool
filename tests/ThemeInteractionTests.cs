using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Design;
using AlbumArtTool.Core;

internal static partial class Tests
{
    private static void ThemeInteractionGuiSmoke()
    {
        guiStage = "actual theme button and resize timing"; Exception failure = null; AppTheme.SetDark(true, false);
        string root = NewFolder("theme-interaction");
        string preference = Path.Combine(BackupSettings.AppData, "appearance.txt"); byte[] prior = File.Exists(preference) ? File.ReadAllBytes(preference) : null;
        using (var form = new MainForm(root, false, new FakeCoverSearch(Cover)))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    await Task.Delay(50);
                    var toggle = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Toggle light and dark mode");
                    var clock = Stopwatch.StartNew(); toggle.PerformClick();
                    Console.WriteLine("ACTUAL TOGGLE cold: click handler=" + clock.Elapsed.TotalMilliseconds.ToString("F1") + "ms; controls=" + Descendants(form).Count() + ".");
                    await Task.Delay(280);
                    clock.Restart(); toggle.PerformClick();
                    Console.WriteLine("ACTUAL TOGGLE return: click handler=" + clock.Elapsed.TotalMilliseconds.ToString("F1") + "ms.");
                    await Task.Delay(280);
                    double total = 0, worst = 0;
                    for (int n = 0; n < 8; n++)
                    {
                        clock.Restart(); form.ClientSize = new Size(1080 + n * 70, 780 + n * 30); form.Refresh();
                        double elapsed = clock.Elapsed.TotalMilliseconds; total += elapsed; worst = Math.Max(worst, elapsed);
                    }
                    Console.WriteLine("ACTUAL RESIZE 8 painted steps to " + form.ClientSize + ": average=" + (total / 8).ToString("F1") + "ms; worst=" + worst.ToString("F1") + "ms.");
                    Assert(AppTheme.IsDark && toggle.Enabled, "real theme clicks and resize retain an interactive main window");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); AppTheme.SetDark(true, false); if (prior == null) File.Delete(preference); else File.WriteAllBytes(preference, prior); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
