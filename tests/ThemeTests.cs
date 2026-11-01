using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Design;

internal static partial class Tests
{
    private static void ThemePreferenceTests()
    {
        string path = Path.Combine(NewFolder("appearance-settings"), "appearance.txt");
        Assert(ThemeSettings.ReadDark(path), "a first run defaults to dark mode");
        Assert(ThemeSettings.SaveDark(false, path) && !ThemeSettings.ReadDark(path), "light mode persists and reloads through the startup preference reader");
        Assert(ThemeSettings.SaveDark(true, path) && ThemeSettings.ReadDark(path), "dark mode can be saved over an existing light preference");
        File.WriteAllText(path, "invalid preference"); Assert(ThemeSettings.ReadDark(path), "an invalid preference falls back to dark mode");
        File.Delete(path); Directory.CreateDirectory(path);
        Assert(!ThemeSettings.SaveDark(false, path), "an unwritable preference reports a save failure instead of crashing the app");
        AppTheme.SetDark(true, false);
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr data);
    private static void ThemeGuiSmoke(string output)
    {
        string root = NewFolder("appearance-gui"), album = Path.Combine(root, "Morning Miles"); Directory.CreateDirectory(album);
        CopyTrack(album, "one.mp3", "Morning Miles", "Atlas Sessions");
        string image = Path.Combine(workspace, "appearance-cover.jpg"); File.WriteAllBytes(image, Cover);
        string preference = Path.Combine(BackupSettings.AppData, "appearance.txt"); byte[] prior = File.Exists(preference) ? File.ReadAllBytes(preference) : null;
        Exception failure = null;
        using (var form = new MainForm(root, false, new FakeCoverSearch(Cover)))
        {
            form.Shown += async (s, e) =>
            {
                try
                {
                    form.Size = form.MinimumSize;
                    await form.ScanAsync(); var list = Descendants(form).OfType<AlbumList>().Single();
                    var panel = Descendants(form).OfType<CoverSearchPanel>().Single(); await panel.CurrentSearch; await Task.Delay(40);
                    var toggle = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Toggle light and dark mode");
                    Assert(AppTheme.IsDark && toggle.Text == "Light mode", "the main window starts dark and offers a visible light-mode toggle");
                    Assert(toggle.Parent.ClientRectangle.Contains(toggle.Bounds), "the appearance toggle fits in the header at the minimum window size");
                    var sort = Descendants(form).OfType<ComboBox>().Single(c => c.AccessibleName == "Album sort order");
                    Assert(sort is ThemeComboBox && sort.AccessibleName.Length > 0, "the dropdown preserves its native accessible ComboBox control");
                    sort.Focus(); sort.SelectedIndex = 0;
                    SendMessage(sort.Handle, 0x0100, (IntPtr)Keys.Down, IntPtr.Zero);
                    Assert(sort.SelectedIndex == 1, "the restyled dropdown retains native arrow-key selection");
                    sort.DroppedDown = true; await Task.Delay(40);
                    Assert(sort.DroppedDown, "the native dropdown opens with its themed result list");
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
                        bitmap.Save(Path.Combine(output, "dark-dropdown-open.png"), ImageFormat.Png);
                    }
                    SendMessage(sort.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero); await Task.Delay(25);
                    Assert(!sort.DroppedDown, "Escape closes the native dropdown without changing other controls");
                    form.StageImage(image); string selected = list.SelectedAlbum.Key;
                    var query = Descendants(form).OfType<TextBox>().Single(t => t.AccessibleName == "Online artwork search query"); query.Text = "An edited artwork search";
                    Descendants(form).OfType<Button>().Single(b => b.Text == "Manage library…").PerformClick();
                    var manager = form.OwnedForms.OfType<LibraryManagerWindow>().Single();
                    var rename = Descendants(manager).OfType<CheckBox>().Single(c => c.AccessibleName == "Rename song files from Title tags"); rename.Checked = true;
                    toggle.PerformClick(); await Task.Delay(40);
                    Assert(!AppTheme.IsDark && !ThemeSettings.ReadDark() && toggle.Text == "Dark mode", "the toggle switches and saves light mode immediately");
                    Assert(manager.BackColor == AppTheme.Canvas && rename.Checked && query.Text == "An edited artwork search" && list.SelectedAlbum.Key == selected && !form.CanInstallUpdate,
                        "theme changes update existing tool windows while preserving their options, selection, query and staged cover");
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "light-mode.png"), ImageFormat.Png); }
                    manager.Close();
                    toggle.PerformClick(); await Task.Delay(40);
                    Assert(AppTheme.IsDark && ThemeSettings.ReadDark() && toggle.Text == "Light mode", "the toggle switches back to dark mode and saves it");
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "dark-mode.png"), ImageFormat.Png); }
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        try { if (prior == null) File.Delete(preference); else File.WriteAllBytes(preference, prior); } finally { AppTheme.SetDark(true, false); }
        if (failure != null) throw failure;
    }
}
