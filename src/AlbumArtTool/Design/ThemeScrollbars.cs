using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // Retain Windows' scroll range, hit testing, dragging, keyboard and accessibility.
    // Only the nonclient scrollbar surfaces are drawn here.
    internal sealed class ThemeScrollbars : NativeWindow, IDisposable
    {
        private static readonly ConditionalWeakTable<Control, ThemeScrollbars> attached = new ConditionalWeakTable<Control, ThemeScrollbars>();
        private readonly Control owner;
        private bool painting;
        internal static void Apply(Control control) => attached.GetValue(control, c => new ThemeScrollbars(c));
        private ThemeScrollbars(Control control)
        {
            owner = control; owner.HandleCreated += Created; owner.HandleDestroyed += Destroyed; owner.Disposed += Disposed;
            if (owner.IsHandleCreated) AssignHandle(owner.Handle);
        }
        private void Created(object sender, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(owner.Handle); }
        private void Destroyed(object sender, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        private void Disposed(object sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            owner.HandleCreated -= Created; owner.HandleDestroyed -= Destroyed; owner.Disposed -= Disposed;
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
        protected override void WndProc(ref Message message)
        {
            int kind = message.Msg;
            int savedDc = kind == 0x0317 && message.WParam != IntPtr.Zero ? SaveDC(message.WParam) : 0;
            try { base.WndProc(ref message); }
            finally { if (savedDc != 0) RestoreDC(message.WParam, savedDc); }
            if (painting || owner.IsDisposed || owner.Disposing || Handle == IntPtr.Zero || SystemInformation.HighContrast) return;
            if (kind == 0x0317 && message.WParam != IntPtr.Zero && (message.LParam.ToInt64() & 2) != 0)
            {
                using (var graphics = Graphics.FromHdc(message.WParam)) PaintBars(graphics);
                return;
            }
            if (kind == 0x00A0)
            {
                var tracking = new MouseTracking { Size = Marshal.SizeOf(typeof(MouseTracking)), Flags = 0x12, Window = Handle };
                TrackMouseEvent(ref tracking);
            }
            if (kind == 0x000F || kind == 0x0085 || kind == 0x0114 || kind == 0x0115 || kind == 0x020A || kind == 0x020E ||
                kind == 0x00A0 || kind == 0x02A2 || kind == 0x0200 || kind == 0x0215 || kind == 0x0005 || kind == 0x0047 || kind == 0x007D || kind == 0x0113)
            {
                IntPtr dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try { using (var graphics = Graphics.FromHdc(dc)) PaintBars(graphics); }
                finally { ReleaseDC(Handle, dc); }
            }
        }
        private void PaintBars(Graphics graphics)
        {
            if (!GetWindowRect(Handle, out var window)) return;
            painting = true;
            try
            {
                var saved = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var vertical = DrawBar(graphics, window, true); var horizontal = DrawBar(graphics, window, false);
                if (!vertical.IsEmpty && !horizontal.IsEmpty)
                    using (var fill = new SolidBrush(Surface)) graphics.FillRectangle(fill, new Rectangle(vertical.Left, horizontal.Top, vertical.Width, horizontal.Height));
                graphics.Restore(saved);
            }
            catch (ExternalException) { } // An invalidated drawing context must not interrupt a library task.
            finally { painting = false; }
        }
        private Color Surface => owner.BackColor.A == 255 ? owner.BackColor : AppTheme.Surface;
        private Rectangle DrawBar(Graphics graphics, NativeRect window, bool vertical)
        {
            var info = new ScrollbarInfo { Size = Marshal.SizeOf(typeof(ScrollbarInfo)), States = new int[6] };
            if (!GetScrollBarInfo(Handle, vertical ? -5 : -6, ref info) || (info.States[0] & 0x18000) != 0) return Rectangle.Empty;
            var bounds = Rectangle.FromLTRB(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top, info.Bounds.Right - window.Left, info.Bounds.Bottom - window.Top);
            if (bounds.Width < 1 || bounds.Height < 1) return Rectangle.Empty;
            // Include the one-pixel system edge so the channel does not retain a box.
            var channel = Rectangle.Intersect(Rectangle.Inflate(bounds, 1, 1), new Rectangle(0, 0, window.Right - window.Left, window.Bottom - window.Top));
            using (var background = new SolidBrush(Surface)) graphics.FillRectangle(background, channel);
            var pointer = Cursor.Position; pointer.Offset(-window.Left, -window.Top);
            bool active = owner.Enabled && (bounds.Contains(pointer) || (info.States[3] & 8) != 0);
            int thickness = Math.Min(vertical ? bounds.Width - 4 : bounds.Height - 4, AppTheme.Scale(owner, active ? 8 : 6));
            int start = Math.Max(info.LineButton, info.ThumbTop), end = Math.Min((vertical ? bounds.Height : bounds.Width) - info.LineButton, info.ThumbBottom);
            if (thickness > 0 && end > start && (info.States[3] & 0x18000) == 0)
            {
                var thumb = vertical ? new Rectangle(bounds.Left + (bounds.Width - thickness) / 2, bounds.Top + start, thickness, end - start) :
                    new Rectangle(bounds.Left + start, bounds.Top + (bounds.Height - thickness) / 2, end - start, thickness);
                Color color = active ? AppTheme.Accent : AppTheme.Blend(AppTheme.Muted, Surface, .38f);
                if (!owner.Enabled) color = AppTheme.Blend(color, Surface, .5f);
                using (var shape = AppTheme.Rounded(thumb, thickness / 2f))
                using (var fill = new SolidBrush(color)) graphics.FillPath(fill, shape);
            }
            // Arrow targets remain native. Reveal quiet chevrons only on hover.
            if (active && info.LineButton > 0)
            {
                float middle = vertical ? bounds.Left + bounds.Width / 2f : bounds.Top + bounds.Height / 2f;
                float first = (vertical ? bounds.Top : bounds.Left) + info.LineButton / 2f;
                float last = (vertical ? bounds.Bottom : bounds.Right) - info.LineButton / 2f;
                float span = AppTheme.Scale(owner, 2);
                using (var pen = new Pen(AppTheme.Muted, AppTheme.Scale(owner, 1) + .3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (vertical)
                    {
                        graphics.DrawLines(pen, new[] { new PointF(middle - span, first + span / 2), new PointF(middle, first - span / 2), new PointF(middle + span, first + span / 2) });
                        graphics.DrawLines(pen, new[] { new PointF(middle - span, last - span / 2), new PointF(middle, last + span / 2), new PointF(middle + span, last - span / 2) });
                    }
                    else
                    {
                        graphics.DrawLines(pen, new[] { new PointF(first + span / 2, middle - span), new PointF(first - span / 2, middle), new PointF(first + span / 2, middle + span) });
                        graphics.DrawLines(pen, new[] { new PointF(last - span / 2, middle - span), new PointF(last + span / 2, middle), new PointF(last - span / 2, middle + span) });
                    }
                }
            }
            return bounds;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ScrollbarInfo
        {
            internal int Size; internal NativeRect Bounds; internal int LineButton, ThumbTop, ThumbBottom, Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal int[] States;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseTracking { internal int Size, Flags; internal IntPtr Window; internal int HoverTime; }
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetScrollBarInfo(IntPtr window, int objectId, ref ScrollbarInfo info);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TrackMouseEvent(ref MouseTracking tracking);
        [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreDC(IntPtr dc, int state);
    }
}
