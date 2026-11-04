using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    // An empty list says why it is empty and offers the one action that fills it.
    internal sealed class EmptyState : StackPanel
    {
        private readonly PathIcon icon = Kit.Glyph(Icons.Album, 40);
        private readonly TextBlock title = Kit.Label("", 16, true), message = Kit.Label("", Kit.Body, cls: "muted");
        private readonly Button action = Kit.Button("");
        private Action onAction;

        internal EmptyState()
        {
            Spacing = 8; MaxWidth = 380; HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
            icon.Brush(PathIcon.ForegroundProperty, "Muted"); icon.Opacity = 0.7; icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 4);
            foreach (var text in new[] { title, message })
            {
                text.TextAlignment = TextAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Center;
                text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None;
            }
            title.Named("Empty list title"); message.Named("Empty list message");
            action.HorizontalAlignment = HorizontalAlignment.Center; action.Padding = new Thickness(16, 2); action.Height = 34; action.Margin = new Thickness(0, 6, 0, 0);
            action.Named("Empty list action");
            action.Click += (s, e) => onAction?.Invoke();
            Children.Add(icon); Children.Add(title); Children.Add(message); Children.Add(action);
        }

        internal string Title => title.Text;
        internal string Message => message.Text;
        internal Button Action => action;

        internal void Show(string iconData, string heading, string text, string actionText = null, Action onClick = null)
        {
            Kit.SetIcon(icon, iconData);
            title.Text = heading; message.Text = text ?? ""; message.IsVisible = !string.IsNullOrEmpty(text);
            action.Content = actionText; onAction = onClick; action.IsVisible = actionText != null && onClick != null;
        }
    }
}
