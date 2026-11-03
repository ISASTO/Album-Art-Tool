using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    internal static class Dialogs
    {
        internal static Task ShowMessage(Window owner, string title, string message)
        {
            var window = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var ok = Kit.Button("OK", "primary");
            ok.Width = 96; ok.Height = 32; ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.Click += (s, e) => window.Close();
            var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            var layout = new DockPanel { Margin = new Thickness(20) };
            DockPanel.SetDock(ok, Dock.Bottom);
            layout.Children.Add(ok); layout.Children.Add(text);
            Kit.Prepare(window, layout);
            return owner != null && owner.IsVisible ? window.ShowDialog(owner) : ShowAsync(window);
        }

        private static Task ShowAsync(Window window)
        {
            var done = new TaskCompletionSource<bool>();
            window.Closed += (s, e) => done.TrySetResult(true);
            window.Show();
            return done.Task;
        }

        /// <summary>Scan and update details, readable and copyable.</summary>
        internal static Window Issues(Window owner, IEnumerable<string> issues)
        {
            var window = new Window { Title = "Scan and update details", Width = 850, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var text = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(12),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"), FontSize = 13.33,
                Text = string.Join(Environment.NewLine + Environment.NewLine, issues), VerticalContentAlignment = VerticalAlignment.Top
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            Kit.Prepare(window, text);
            window.Show(owner);
            return window;
        }
    }
}
