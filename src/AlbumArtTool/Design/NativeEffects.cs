using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    internal static class NativeEffects
    {
        // Documented Windows 11 DWM attributes. Earlier Windows versions simply reject them.
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private sealed class State { internal IntPtr Handle; internal bool? Dark; }
        private static readonly ConditionalWeakTable<Form, State> states = new ConditionalWeakTable<Form, State>();
        internal static void Apply(Form form)
        {
            if (SystemInformation.HighContrast) return;
            try
            {
                int dark = AppTheme.IsDark ? 1 : 0, rounded = 2, mica = 2;
                var state = states.GetValue(form, _ => new State());
                if (state.Handle != form.Handle)
                {
                    state.Handle = form.Handle; state.Dark = null;
                    // These are lifetime settings. Resetting the system backdrop
                    // on each toggle unnecessarily rebuilds DWM composition.
                    DwmSetWindowAttribute(form.Handle, 33, ref rounded, 4);
                    DwmSetWindowAttribute(form.Handle, 38, ref mica, 4);
                }
                if (state.Dark == AppTheme.IsDark) return;
                DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
                int caption = AppTheme.Canvas.R | AppTheme.Canvas.G << 8 | AppTheme.Canvas.B << 16;
                int text = AppTheme.Ink.R | AppTheme.Ink.G << 8 | AppTheme.Ink.B << 16;
                DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
                state.Dark = AppTheme.IsDark;
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
    }
}
