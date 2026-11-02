using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // Keep the two most recent appearances of each live window. Keys describe only
    // what is visible, so large virtual/ordinary lists are never fully traversed.
    // Compare state on demand instead of installing per-item change listeners.
    internal static class ThemeSnapshotCache
    {
        private sealed class Frame : IDisposable
        {
            internal Bitmap Image; internal ulong Key; internal long Age;
            internal long Bytes => Image == null ? 0 : ((Image.Width * 3L + 3) & ~3L) * Image.Height;
            public void Dispose() { Image?.Dispose(); Image = null; }
        }
        private sealed class Pair : IDisposable
        {
            internal Frame Dark, Light;
            public void Dispose() { Dark?.Dispose(); Light?.Dispose(); Dark = Light = null; }
        }
        private const long Budget = 32L * 1024 * 1024;
        private static readonly ConditionalWeakTable<Form, Pair> pairs = new ConditionalWeakTable<Form, Pair>();
        private static readonly List<WeakReference<Pair>> live = new List<WeakReference<Pair>>();
        private static long age;
        private static Pair For(Form form) => pairs.GetValue(form, f =>
        {
            var pair = new Pair(); live.Add(new WeakReference<Pair>(pair)); f.Disposed += (s, e) => pair.Dispose(); return pair;
        });
        internal static Bitmap Get(Form form, ulong key, bool dark)
        {
            var pair = For(form); var frame = dark ? pair.Dark : pair.Light;
            if (frame?.Image == null || frame.Key != key || frame.Image.Size != form.ClientSize) return null;
            frame.Age = ++age; return (Bitmap)frame.Image.Clone();
        }
        internal static void Store(Form form, ulong key, bool dark, Bitmap image)
        {
            if (image.Width * (long)image.Height * 3 > Budget) return;
            var pair = For(form); var frame = new Frame { Image = (Bitmap)image.Clone(), Key = key, Age = ++age };
            if (dark) { pair.Dark?.Dispose(); pair.Dark = frame; } else { pair.Light?.Dispose(); pair.Light = frame; }
            var frames = new List<Frame>();
            for (int n = live.Count - 1; n >= 0; n--)
            {
                if (!live[n].TryGetTarget(out var entry)) { live.RemoveAt(n); continue; }
                if (entry.Dark?.Image != null) frames.Add(entry.Dark);
                if (entry.Light?.Image != null) frames.Add(entry.Light);
            }
            long total = frames.Sum(f => f.Bytes);
            foreach (var old in frames.OrderBy(f => f.Age)) { if (total <= Budget) break; total -= old.Bytes; old.Dispose(); }
        }
        internal static ulong Key(Form form)
        {
            var hash = new SceneHash(); hash.Add(form.ClientSize.Width); hash.Add(form.ClientSize.Height); hash.Add(form.DeviceDpi); Write(form, hash); return hash.Value;
        }
        private static void Write(Control control, SceneHash hash)
        {
            hash.Add(control.GetType().FullName); hash.Add(control.Visible); if (!control.Visible) return;
            hash.Add(control.Left); hash.Add(control.Top); hash.Add(control.Width); hash.Add(control.Height); hash.Add(control.Enabled); hash.Add(control.Focused);
            if (control.AccessibleName != "Toggle light and dark mode") hash.Add(control.Text);
            hash.Add(control.Font.Name); hash.Add((int)(control.Font.SizeInPoints * 1024)); hash.Add((int)control.Font.Style); hash.Add((int)control.RightToLeft);
            if (control is ThemeButton button) { hash.Add(button.Primary); hash.Add(button.Selected); hash.Add(button.Chrome); }
            if (control is CheckBox check) hash.Add((int)check.CheckState);
            if (control is ComboBox combo) { hash.Add(combo.SelectedIndex); hash.Add(combo.DroppedDown); }
            if (control is TextBoxBase text) { hash.Add(text.SelectionStart); hash.Add(text.SelectionLength); hash.Add(text.ReadOnly); }
            if (control is PictureBox picture) { hash.Add(picture.Image == null ? 0 : RuntimeHelpers.GetHashCode(picture.Image)); hash.Add((int)picture.SizeMode); }
            if (control is ProgressBar progress) { hash.Add(progress.Value); hash.Add(progress.Minimum); hash.Add(progress.Maximum); hash.Add((int)progress.Style); }
            if (control is ScrollableControl scroll) { hash.Add(scroll.AutoScrollPosition.X); hash.Add(scroll.AutoScrollPosition.Y); }
            if (control.IsHandleCreated && (control is ListView || control is ListBox || control is TreeView || control is TextBoxBase))
                for (int bar = 0; bar < 2; bar++)
                {
                    var scrollInfo = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), Mask = 4 };
                    hash.Add(GetScrollInfo(control.Handle, bar, ref scrollInfo) ? scrollInfo.Position : 0);
                }
            if (control is ListView list) WriteList(list, hash);
            if (control is ListBox box)
            {
                hash.Add(box.Items.Count); hash.Add(box.TopIndex); hash.Add(box.SelectedIndex);
                int end = Math.Min(box.Items.Count, box.TopIndex + box.ClientSize.Height / Math.Max(1, box.ItemHeight) + 2);
                for (int n = box.TopIndex; n < end; n++) hash.Add(box.Items[n]?.ToString());
            }
            if (control is TreeView tree)
            {
                var node = tree.TopNode; int remaining = tree.ClientSize.Height / Math.Max(1, tree.ItemHeight) + 2;
                while (node != null && remaining-- > 0) { hash.Add(node.Text); hash.Add(node.Level); hash.Add(node.Nodes.Count); hash.Add(node.Checked); hash.Add(node.IsExpanded); hash.Add(node == tree.SelectedNode); node = node.NextVisibleNode; }
            }
            var children = control.Controls.Cast<Control>().Where(c => !(c is ThemeTransition)).ToArray();
            hash.Add(children.Length); foreach (Control child in children) Write(child, hash);
        }
        private static void WriteList(ListView list, SceneHash hash)
        {
            hash.Add(list.Items.Count); hash.Add((int)list.View); hash.Add((int)list.HeaderStyle); hash.Add(list.CheckBoxes);
            foreach (ColumnHeader column in list.Columns) { hash.Add(column.Text); hash.Add(column.Width); hash.Add(column.DisplayIndex); }
            if (list.Items.Count == 0 || !list.IsHandleCreated) return;
            int top = list.TopItem?.Index ?? 0;
            int height = Math.Max(1, list.GetItemRect(top).Height), end = Math.Min(list.Items.Count, top + list.ClientSize.Height / height + 2);
            hash.Add(top);
            for (int n = top; n < end; n++)
            {
                var item = list.Items[n]; hash.Add(item.Selected); hash.Add(item.Checked); hash.Add(AppTheme.IsMuted(item.ForeColor));
                foreach (ListViewItem.ListViewSubItem cell in item.SubItems) hash.Add(cell.Text);
                if (list is AlbumList albums)
                {
                    var album = albums.AlbumAt(n); hash.Add(album.Key); hash.Add(album.Title); hash.Add(album.Artist); hash.Add(album.Coverage);
                    hash.Add(album.Thumbnail == null ? 0 : RuntimeHelpers.GetHashCode(album.Thumbnail)); hash.Add(albums.WorkStatus?.Invoke(album));
                }
            }
        }
        private sealed class SceneHash
        {
            internal ulong Value = 14695981039346656037UL;
            internal void Add(int value) { unchecked { Value = (Value ^ (uint)value) * 1099511628211UL; } }
            internal void Add(bool value) => Add(value ? 1 : 0);
            internal void Add(string value) { if (value == null) { Add(-1); return; } Add(value.Length); foreach (char c in value) Add(c); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo { internal int Size, Mask, Min, Max; internal uint Page; internal int Position, Track; }
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
    }
}
