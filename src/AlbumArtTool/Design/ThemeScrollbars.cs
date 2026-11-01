using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // Keep the native scrolling content, but put its nonclient bars outside a clipped
    // viewport. Independent child windows own the visible bars and all their input.
    // Windows/UxTheme can therefore never repaint a stock bar over these controls.
    internal sealed class ThemeScrollbars : Panel
    {
        private static readonly ConditionalWeakTable<Control, ThemeScrollbars> attached = new ConditionalWeakTable<Control, ThemeScrollbars>();
        private readonly Control content;
        private readonly Viewport viewport;
        private readonly RoundedBar vertical, horizontal;
        private readonly ContentWindow window;
        private bool syncing, pending;
        internal Control VerticalBar => vertical;
        internal Control HorizontalBar => horizontal;
        internal Rectangle VisibleContent => viewport.RectangleToScreen(viewport.ClientRectangle);
        internal static ThemeScrollbars For(Control control) => attached.TryGetValue(control, out var host) ? host : null;
        internal static void Apply(Control control)
        {
            if (control.Parent != null && !SystemInformation.HighContrast) attached.GetValue(control, c => new ThemeScrollbars(c));
        }
        private ThemeScrollbars(Control control)
        {
            content = control;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; TabStop = false;
            Dock = control.Dock; Anchor = control.Anchor; Bounds = control.Bounds; Margin = control.Margin; TabIndex = control.TabIndex;
            MinimumSize = control.MinimumSize; MaximumSize = control.MaximumSize;
            viewport = new Viewport(() => Surface) { TabStop = false };
            vertical = new RoundedBar(true, () => Surface, value => ScrollTo(true, value), () => content.Focus());
            horizontal = new RoundedBar(false, () => Surface, value => ScrollTo(false, value), () => content.Focus());
            var parent = control.Parent;
            parent.SuspendLayout();
            try
            {
                if (parent is TableLayoutPanel table)
                {
                    var cell = table.GetCellPosition(control); int rows = table.GetRowSpan(control), columns = table.GetColumnSpan(control);
                    table.Controls.Remove(control); table.Controls.Add(this, cell.Column, cell.Row); table.SetRowSpan(this, rows); table.SetColumnSpan(this, columns);
                }
                else
                {
                    int index = parent.Controls.GetChildIndex(control);
                    parent.Controls.Remove(control); parent.Controls.Add(this); parent.Controls.SetChildIndex(this, index);
                }
                control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left; control.Margin = Padding.Empty;
                Controls.Add(viewport); Controls.Add(vertical); Controls.Add(horizontal); viewport.Controls.Add(control);
            }
            finally { parent.ResumeLayout(true); }
            window = new ContentWindow(control, QueueSync);
            control.SizeChanged += ContentChanged; control.VisibleChanged += ContentChanged; control.FontChanged += ContentChanged;
            control.BackColorChanged += ContentChanged; control.EnabledChanged += ContentChanged;
            if (control is ScrollableControl scrollable) scrollable.Scroll += ContentScrolled;
            QueueSync();
        }
        private Color Surface
        {
            get
            {
                if (content.BackColor.A == 255) return content.BackColor;
                return AppTheme.IsInset(content) ? AppTheme.Inset : AppTheme.Surface;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Surface); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Sync(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); QueueSync(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueSync(); }
        private void ContentChanged(object sender, EventArgs e) => QueueSync();
        private void ContentScrolled(object sender, ScrollEventArgs e) => QueueSync();
        private void QueueSync()
        {
            if (syncing || pending || IsDisposed || Disposing || !IsHandleCreated) return;
            pending = true;
            try { BeginInvoke((Action)(() => { pending = false; Sync(); })); }
            catch (InvalidOperationException) { pending = false; }
        }
        internal void Sync()
        {
            if (syncing || content == null || viewport == null || !content.IsHandleCreated || IsDisposed || Disposing) return;
            syncing = true;
            try
            {
                if (content is ListView && (SendMessage(content.Handle, 0x1037, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0x10000) == 0)
                    SendMessage(content.Handle, 0x1036, (IntPtr)0x10000, (IntPtr)0x10000);
                int gutter = AppTheme.Scale(this, 14);
                bool showVertical = vertical.Visible, showHorizontal = horizontal.Visible;
                // Native bars may appear/disappear as the window is resized. Converge
                // with the content's CLIENT size equal to the clipped viewport size.
                for (int pass = 0; pass < 6; pass++)
                {
                    viewport.Bounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - (showVertical ? gutter : 0)), Math.Max(1, ClientSize.Height - (showHorizontal ? gutter : 0)));
                    int extraWidth = Math.Max(0, content.Width - content.ClientSize.Width), extraHeight = Math.Max(0, content.Height - content.ClientSize.Height);
                    content.Bounds = new Rectangle(0, 0, viewport.Width + extraWidth, viewport.Height + extraHeight);
                    var v = Read(true); var h = Read(false);
                    bool nextV = v.Scrollable && NativeBarVisible(true), nextH = h.Scrollable && NativeBarVisible(false);
                    if (nextV == showVertical && nextH == showHorizontal && content.ClientSize == viewport.Size) break;
                    showVertical = nextV; showHorizontal = nextH;
                }
                vertical.Bounds = new Rectangle(viewport.Right, 0, gutter, viewport.Height);
                horizontal.Bounds = new Rectangle(0, viewport.Bottom, viewport.Width, gutter);
                vertical.Visible = showVertical; horizontal.Visible = showHorizontal;
                vertical.Enabled = horizontal.Enabled = content.Enabled;
                vertical.SetMetrics(Read(true)); horizontal.SetMetrics(Read(false));
                vertical.Invalidate(); horizontal.Invalidate();
            }
            finally { syncing = false; }
        }
        private bool NativeBarVisible(bool isVertical)
        {
            return (GetWindowLong(content.Handle, -16) & (isVertical ? 0x00200000 : 0x00100000)) != 0;
        }
        private ScrollMetrics Read(bool isVertical)
        {
            var info = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), Mask = 7 };
            if (content.IsHandleCreated && GetScrollInfo(content.Handle, isVertical ? 1 : 0, ref info))
                return new ScrollMetrics(info.Min, info.Max, (int)Math.Min(int.MaxValue, info.Page), info.Position);
            return new ScrollMetrics();
        }
        private void ScrollTo(bool isVertical, int value)
        {
            if (!content.Enabled || !content.IsHandleCreated) return;
            var info = Read(isVertical); int target = Math.Max(info.Min, Math.Min(info.Last, value)); int delta = target - info.Position;
            if (delta == 0) return;
            if (content is ListView list)
            {
                // LVM_SCROLL takes 32-bit deltas. WM_VSCROLL thumb messages truncate
                // positions at 65,535, which is not sufficient for large libraries.
                int pixels = delta;
                if (isVertical && list.View == View.Details && list.Items.Count > 0)
                {
                    int index = list.TopItem?.Index ?? 0;
                    pixels = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)delta * Math.Max(1, list.GetItemRect(index).Height)));
                }
                SendMessage(content.Handle, 0x1014, isVertical ? IntPtr.Zero : (IntPtr)pixels, isVertical ? (IntPtr)pixels : IntPtr.Zero);
            }
            else if (content is ScrollableControl panel)
            {
                var position = panel.AutoScrollPosition;
                panel.AutoScrollPosition = isVertical ? new Point(-position.X, target) : new Point(target, -position.Y);
            }
            else if (isVertical && content is ListBox box) box.TopIndex = target;
            else if (isVertical && content is TreeView tree)
            {
                var node = tree.TopNode;
                while (node != null && delta != 0)
                {
                    var next = delta > 0 ? node.NextVisibleNode : node.PrevVisibleNode;
                    if (next == null) break;
                    node = next; delta += delta > 0 ? -1 : 1;
                }
                if (node != null) tree.TopNode = node;
            }
            else if (isVertical && content is TextBox)
                SendMessage(content.Handle, 0x00B6, IntPtr.Zero, (IntPtr)delta); // EM_LINESCROLL, lines
            else ScrollNative(isVertical, target);
            Sync(); content.Invalidate(); content.Update();
        }
        private void ScrollNative(bool isVertical, int target)
        {
            int message = isVertical ? 0x0115 : 0x0114;
            if (target <= ushort.MaxValue)
                SendMessage(content.Handle, message, new IntPtr((target << 16) | 4), IntPtr.Zero);
            else
            {
                // Rare very-wide native text/tree controls: move without packing a
                // truncated position into the 16-bit thumb-message field.
                var info = Read(isVertical);
                while (info.Position != target)
                {
                    int delta = target - info.Position;
                    int command = Math.Abs((long)delta) >= Math.Max(1, info.Page) ? delta > 0 ? 3 : 2 : delta > 0 ? 1 : 0;
                    SendMessage(content.Handle, message, (IntPtr)command, IntPtr.Zero);
                    var next = Read(isVertical); if (next.Position == info.Position || (long)(target - next.Position) * (target - info.Position) < 0) break; info = next;
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                window?.Dispose();
                if (content != null)
                {
                    content.SizeChanged -= ContentChanged; content.VisibleChanged -= ContentChanged; content.FontChanged -= ContentChanged; content.BackColorChanged -= ContentChanged; content.EnabledChanged -= ContentChanged;
                    if (content is ScrollableControl scrollable) scrollable.Scroll -= ContentScrolled;
                }
            }
            base.Dispose(disposing);
        }
        private sealed class Viewport : Panel
        {
            private readonly Func<Color> surface;
            internal Viewport(Func<Color> surface) { this.surface = surface; DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(surface()); }
        }
        private sealed class ContentWindow : NativeWindow, IDisposable
        {
            private readonly Control owner; private readonly Action changed;
            internal ContentWindow(Control owner, Action changed)
            {
                this.owner = owner; this.changed = changed;
                owner.HandleCreated += Created; owner.HandleDestroyed += Destroyed;
                if (owner.IsHandleCreated) Created(owner, EventArgs.Empty);
            }
            private void Created(object sender, EventArgs e)
            {
                AssignHandle(owner.Handle);
                if (owner is ListView) SendMessage(Handle, 0x1036, (IntPtr)0x10000, (IntPtr)0x10000); // LVS_EX_DOUBLEBUFFER
                changed();
            }
            private void Destroyed(object sender, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            protected override void WndProc(ref Message message)
            {
                // WM_PRINT otherwise includes the hidden native nonclient bars in
                // exported control images even though the screen viewport clips them.
                if (message.Msg == 0x0317) message.LParam = new IntPtr(message.LParam.ToInt64() & ~2L);
                base.WndProc(ref message);
                int kind = message.Msg;
                if (kind == 0x000F || kind == 0x0085 || kind == 0x0005 || kind == 0x000B || kind == 0x007D ||
                    kind == 0x0114 || kind == 0x0115 || kind == 0x020A || kind == 0x020E || kind == 0x0100 || kind == 0x0102 ||
                    kind == 0x000C || kind == 0x00B6 || kind >= 0x1000 && kind < 0x1040 && kind != 0x1004 && kind != 0x100C && kind != 0x101F && kind != 0x1027 && kind != 0x1037)
                    changed();
            }
            public void Dispose() { owner.HandleCreated -= Created; owner.HandleDestroyed -= Destroyed; if (Handle != IntPtr.Zero) ReleaseHandle(); }
        }
        internal struct ScrollMetrics
        {
            internal readonly int Min, Max, Page, Position;
            internal int Last => (int)Math.Max(Min, (long)Max - Math.Max(0, Page - 1));
            internal bool Scrollable => Last > Min;
            internal ScrollMetrics(int min, int max, int page, int position) { Min = min; Max = max; Page = page; Position = position; }
        }
        private sealed class RoundedBar : Control
        {
            private readonly bool vertical;
            private readonly Func<Color> surface;
            private readonly Action<int> scroll;
            private readonly Action focusContent;
            private readonly Timer repeat = new Timer();
            private ScrollMetrics metrics;
            private bool hot, dragging;
            private int grab, pageDirection;
            internal RoundedBar(bool vertical, Func<Color> surface, Action<int> scroll, Action focusContent)
            {
                this.vertical = vertical; this.surface = surface; this.scroll = scroll; this.focusContent = focusContent;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false); TabStop = false; Visible = false;
                AccessibleRole = AccessibleRole.ScrollBar; AccessibleName = vertical ? "Vertical scroll" : "Horizontal scroll";
                repeat.Tick += (s, e) => { repeat.Interval = 65; Page(); };
            }
            internal void SetMetrics(ScrollMetrics value) { metrics = value; Invalidate(); }
            private int Length => vertical ? Height : Width;
            private int Axis(Point point) => vertical ? point.Y : point.X;
            private Rectangle Thumb
            {
                get
                {
                    int space = Math.Max(1, Length - 6);
                    int size = Math.Min(space, Math.Max(AppTheme.Scale(this, 24), (int)(space * (double)Math.Max(1, metrics.Page) / Math.Max(1, (long)metrics.Max - metrics.Min + 1))));
                    int start = 3 + (int)Math.Round((space - size) * (double)(metrics.Position - metrics.Min) / Math.Max(1, (long)metrics.Last - metrics.Min));
                    return vertical ? new Rectangle(0, start, Width, size) : new Rectangle(start, 0, size, Height);
                }
            }
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(surface()); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var thumb = Thumb; int thickness = Math.Min(vertical ? Width - 4 : Height - 4, AppTheme.Scale(this, hot || dragging ? 8 : 6));
                if (!metrics.Scrollable || thickness < 1) return;
                var bounds = vertical ? new Rectangle((Width - thickness) / 2, thumb.Top, thickness, thumb.Height) : new Rectangle(thumb.Left, (Height - thickness) / 2, thumb.Width, thickness);
                Color color = hot || dragging ? AppTheme.Accent : AppTheme.Blend(AppTheme.Muted, surface(), .38f);
                if (!Enabled) color = AppTheme.Blend(color, surface(), .5f);
                using (var path = AppTheme.Rounded(bounds, thickness / 2f)) using (var fill = new SolidBrush(color)) e.Graphics.FillPath(fill, path);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !metrics.Scrollable) return;
                focusContent(); Capture = true;
                if (Thumb.Contains(e.Location)) { dragging = true; grab = Axis(e.Location) - (vertical ? Thumb.Top : Thumb.Left); }
                else { pageDirection = Axis(e.Location) < (vertical ? Thumb.Top : Thumb.Left) ? -1 : 1; Page(); repeat.Interval = 400; repeat.Start(); }
                Invalidate();
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); if (!dragging || !Capture) return;
                var thumb = Thumb; int travel = Math.Max(1, Length - 6 - (vertical ? thumb.Height : thumb.Width));
                double fraction = Math.Max(0, Math.Min(1, (Axis(e.Location) - grab - 3) / (double)travel));
                scroll(metrics.Min + (int)Math.Round(fraction * ((long)metrics.Last - metrics.Min)));
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { dragging = false; repeat.Stop(); Capture = false; Invalidate(); } }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; repeat.Stop(); Invalidate(); } }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                int lines = SystemInformation.MouseWheelScrollLines;
                long step = lines < 0 ? metrics.Page : Math.Max(1, lines);
                scroll((int)Math.Max(metrics.Min, Math.Min(metrics.Last, metrics.Position - e.Delta / 120L * step)));
            }
            private void Page()
            {
                var point = PointToClient(Cursor.Position); var thumb = Thumb;
                int direction = Axis(point) < (vertical ? thumb.Top : thumb.Left) ? -1 : Axis(point) >= (vertical ? thumb.Bottom : thumb.Right) ? 1 : 0;
                if (direction == pageDirection) scroll((int)Math.Max(metrics.Min, Math.Min(metrics.Last, (long)metrics.Position + direction * (long)Math.Max(1, metrics.Page))));
            }
            protected override AccessibleObject CreateAccessibilityInstance() => new BarAccessibility(this);
            private sealed class BarAccessibility : ControlAccessibleObject
            {
                private readonly RoundedBar bar;
                internal BarAccessibility(RoundedBar bar) : base(bar) { this.bar = bar; }
                public override string Value { get { var value = bar.metrics; return value.Position.ToString(System.Globalization.CultureInfo.InvariantCulture); } set { if (int.TryParse(value, out int position)) bar.scroll(position); } }
            }
            protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo { internal int Size, Mask, Min, Max; internal uint Page; internal int Position, TrackPosition; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr data);
    }
}
