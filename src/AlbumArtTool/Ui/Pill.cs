using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    // A status tag: an indicator light and a short legend in capitals ("● NO COVER", "● DONE").
    // Tones keep their meaning everywhere: Warning = red (needs attention), Positive = green (done or fine),
    // Accent = orange (in progress or chosen), Muted = unlit.
    internal sealed class Pill : Border
    {
        private readonly Border led = Kit.Led();
        private readonly CapsText text = CapsText.Create("");
        private string tone;

        internal Pill(string value = "", string tone = "Accent")
        {
            text.FontFamily = Skin.Key; text.FontWeight = FontWeight.SemiBold; text.FontSize = 10.5; text.LetterSpacing = 0.9;
            led.Margin = new Thickness(0, 0, 7, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(led); row.Children.Add(text);
            Child = row; Background = Brushes.Transparent; Padding = new Thickness(0, 2);
            HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Center;
            Set(value, tone);
        }

        internal string Text => text.Source;
        internal TextBlock Label => text;

        /// <summary>Tone is Accent, Warning, Positive or Muted. An empty value hides the tag.</summary>
        internal void Set(string value, string tone = null)
        {
            text.Source = value ?? ""; IsVisible = !string.IsNullOrEmpty(value);
            tone ??= this.tone ?? "Accent";
            if (tone == this.tone) return;
            this.tone = tone;
            Kit.SetLed(led, tone == "Warning" ? "red" : tone == "Positive" ? "green" : tone == "Muted" ? null : "on");
            text.Brush(TextBlock.ForegroundProperty, tone == "Muted" ? "Muted" : "Ink");
        }
    }
}
