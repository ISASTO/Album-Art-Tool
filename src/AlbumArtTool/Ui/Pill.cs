using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    // A small rounded status label: "No embedded art", "All good", "Ready to apply".
    // The tone picks a theme pair (badge background and text color) so it reads in light and dark.
    internal sealed class Pill : Border
    {
        private readonly TextBlock text = new TextBlock { FontSize = 11.33, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private string tone;

        internal Pill(string value = "", string tone = "Accent")
        {
            Child = text; CornerRadius = new CornerRadius(9); Padding = new Thickness(8, 2, 8, 3);
            HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Center;
            Set(value, tone);
        }

        internal string Text => text.Text;
        internal TextBlock Label => text;

        /// <summary>Tone is Accent, Warning, Positive or Muted. An empty value hides the pill.</summary>
        internal void Set(string value, string tone = null)
        {
            text.Text = value ?? ""; IsVisible = !string.IsNullOrEmpty(value);
            tone ??= this.tone ?? "Accent";
            if (tone == this.tone) return;
            this.tone = tone;
            this.Brush(BackgroundProperty, tone == "Warning" ? "BadgeWarning" : tone == "Positive" ? "BadgePositive" : tone == "Muted" ? "Input" : "BadgeAccent");
            text.Brush(TextBlock.ForegroundProperty, tone == "Muted" ? "Muted" : tone);
        }
    }
}
