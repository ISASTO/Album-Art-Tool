using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SetClientAnimation(uint action, uint parameter, ref bool enabled, uint flags);
    private static void ThemeInteractionGuiSmoke()
    {
        guiStage = "actual theme button and resize timing"; Exception failure = null; AppTheme.SetDark(true, false);
        string root = NewFolder("theme-interaction");
        string preference = Path.Combine(BackupSettings.AppData, "appearance.txt"); byte[] prior = File.Exists(preference) ? File.ReadAllBytes(preference) : null;
        bool previousMotion = ThemeTransition.MotionAllowed, motion = true; SetClientAnimation(0x1043, 0, ref motion, 0);
        using (var form = new MainForm(root, false, new FakeCoverSearch(Cover)))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    await Task.Delay(50);
                    var toggle = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Toggle light and dark mode");
                    Assert(ThemeTransition.MotionAllowed, "the real-button performance check enables Windows client-area animations");
                    var clock = Stopwatch.StartNew(); toggle.PerformClick(); double coldMs = clock.Elapsed.TotalMilliseconds;
                    Console.WriteLine("ACTUAL TOGGLE cold: click handler=" + clock.Elapsed.TotalMilliseconds.ToString("F1") + "ms; controls=" + Descendants(form).Count() + "; source-hit=" + ThemeTransition.For(form)?.BeforeCached + "; target-hit=" + ThemeTransition.For(form)?.AfterCached + ".");
                    await Task.Delay(280);
                    clock.Restart(); toggle.PerformClick(); double repeatMs = clock.Elapsed.TotalMilliseconds;
                    Console.WriteLine("ACTUAL TOGGLE return: click handler=" + clock.Elapsed.TotalMilliseconds.ToString("F1") + "ms; source-hit=" + ThemeTransition.For(form)?.BeforeCached + "; target-hit=" + ThemeTransition.For(form)?.AfterCached + ".");
                    Assert(ThemeTransition.For(form)?.BeforeCached == true && ThemeTransition.For(form)?.AfterCached == true, "the real theme button reuses both rendered views on a return switch");
                    Assert(coldMs < 250 && repeatMs < 100, "real theme-button handlers stay within cold/warm responsiveness budgets");
                    await Task.Delay(280);
                    double total = 0, worst = 0, layout = 0, painting = 0;
                    var counts = new Dictionary<string, int>(); var times = new Dictionary<string, double>();
                    PaintTrace.Recorded = (name, milliseconds) => { counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1; times[name] = (times.TryGetValue(name, out double value) ? value : 0) + milliseconds; };
                    for (int n = 0; n < 8; n++)
                    {
                        clock.Restart(); form.ClientSize = new Size(1080 + n * 70, 780 + n * 30); double resized = clock.Elapsed.TotalMilliseconds; layout += resized; form.Refresh(); painting += clock.Elapsed.TotalMilliseconds - resized;
                        double elapsed = clock.Elapsed.TotalMilliseconds; total += elapsed; worst = Math.Max(worst, elapsed);
                    }
                    PaintTrace.Recorded = null;
                    foreach (var pair in times.OrderByDescending(p => p.Value)) Console.WriteLine("RESIZE PROFILE " + pair.Key + ": " + pair.Value.ToString("F1") + "ms in " + counts[pair.Key] + " calls.");
                    Console.WriteLine("RESIZE PHASES: layout=" + (layout / 8).ToString("F1") + "ms; painting=" + (painting / 8).ToString("F1") + "ms.");
                    Console.WriteLine("ACTUAL RESIZE 8 painted steps to " + form.ClientSize + ": average=" + (total / 8).ToString("F1") + "ms; worst=" + worst.ToString("F1") + "ms.");
                    Assert(total / 8 < 65 && worst < 100, "full-window resizing stays within the measured repaint responsiveness budget");
                    Assert(AppTheme.IsDark && toggle.Enabled, "real theme clicks and resize retain an interactive main window");
                }
                catch (Exception ex) { failure = ex; }
                finally { PaintTrace.Recorded = null; SetClientAnimation(0x1043, 0, ref previousMotion, 0); form.Close(); AppTheme.SetDark(true, false); if (prior == null) File.Delete(preference); else File.WriteAllBytes(preference, prior); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
