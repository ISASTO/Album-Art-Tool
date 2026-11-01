using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    internal static class NativeEffects
    {
        // Documented Windows 11 DWM attributes. Earlier Windows versions simply reject them.
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void Apply(Form form)
        {
            if (SystemInformation.HighContrast) return;
            try
            {
                int light = 0, rounded = 2, mica = 2;
                DwmSetWindowAttribute(form.Handle, 20, ref light, 4);
                DwmSetWindowAttribute(form.Handle, 33, ref rounded, 4);
                DwmSetWindowAttribute(form.Handle, 38, ref mica, 4);
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
    }
}
