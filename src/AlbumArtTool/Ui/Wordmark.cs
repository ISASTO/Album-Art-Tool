using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    // Muzikette's wordmark: "muzikette" in lower-case Archivo Expanded Bold, drawn as outlines, with the orange power
    // light as the dot of the i. The letters take the finish's ink colour, so it reads on Silver and on Black.
    internal static class Wordmark
    {
        // The letters at 100 px type, in a 573.8 × 77.2 box (top-left at 0, 0), without the dot of the i.
        private const string Letters =
            "M0 76V23.3H12.6L13.7 32.2H14.6Q17.4 28.4 20.9 26.2Q24.5 24 28.6 23Q32.6 22.1 36.5 22.1Q43.9 22.1 48.9 24.5Q53.8 27 56 32.2H57Q59.8 28.4 63.4 26.2Q67 24 71 23Q75.1 22.1 79.1 22.1Q86.2 22.1 91.1 24.4Q95.9 26.8 98.3 31.6Q100.7 36.4 100.7 43.7V76H85.1V46.2Q85.1 43.1 84.2 40.8Q83.4 38.6 81.8 37.2Q80.3 35.8 78.2 35.1Q76 34.5 73.3 34.5Q69.3 34.5 65.8 36.2Q62.4 38 60.2 41Q58.1 44.1 58.1 48.2V76H42.5V46.2Q42.5 43.1 41.7 40.8Q40.9 38.6 39.4 37.2Q37.8 35.8 35.7 35.1Q33.5 34.5 30.7 34.5Q26.7 34.5 23.3 36.2Q19.8 38 17.7 41Q15.6 44.1 15.6 48.2V76Z " +
            "M134.9 77.2Q123.8 77.2 118 71.8Q112.3 66.5 112.3 55.6V23.3H128V53.1Q128 56.2 128.8 58.4Q129.7 60.7 131.3 62.1Q133 63.5 135.3 64.1Q137.7 64.7 140.6 64.7Q144.8 64.7 148.4 63Q152 61.3 154.2 58.2Q156.4 55.1 156.4 51.1V23.3H172.1V76H159.5L158.4 67.1H157.5Q154.7 70.9 150.9 73.1Q147.2 75.3 143.1 76.2Q139 77.2 134.9 77.2Z " +
            "M180.5 76V70L212.6 34.7H182.6V23.3H237.6V29L205.1 64.6H238.8V76Z M247.2 76V23.3H262.8V76Z M275.1 76V3.6H290.7V46.2L317 23.3H336.4L314 44.2L337.2 76H318.6L302.1 53.3L290.7 61.7V76Z " +
            "M375.9 77.2Q365.4 77.2 357.8 74.2Q350.2 71.3 346 65.2Q341.9 59.1 341.9 49.7Q341.9 40.5 345.9 34.3Q350 28.2 357.5 25.1Q365.1 22.1 375.3 22.1Q386 22.1 393.4 25.1Q400.8 28.1 404.6 34Q408.4 39.9 408.4 48.6V53.2H358.3Q358.6 57.8 360.8 60.6Q363 63.5 366.9 64.8Q370.9 66.1 376.2 66.1Q378.6 66.1 381.2 65.5Q383.9 65 386.2 64Q388.6 63 390.2 61.4Q391.8 59.9 392 57.7H408Q408 61.9 405.8 65.5Q403.6 69.1 399.4 71.7Q395.2 74.3 389.2 75.7Q383.3 77.2 375.9 77.2ZM358.4 43.7H391.7Q391.7 41.1 390.5 39.1Q389.3 37.2 387.1 35.8Q384.8 34.5 381.9 33.8Q379 33.2 375.6 33.2Q370.7 33.2 366.9 34.4Q363.2 35.7 361 38Q358.9 40.4 358.4 43.7Z " +
            "M442 77.2Q435.9 77.2 432 75.6Q428 74.1 426.1 70.3Q424.2 66.6 424.2 60.1V34.7H414V23.3H424.7L427.8 7.7H439.8V23.3H454.3V34.7H439.8V58.1Q439.8 61.9 441.2 63.8Q442.7 65.8 447.5 65.8H454.3V75.5Q452.9 76 450.7 76.4Q448.4 76.8 446.1 77Q443.7 77.2 442 77.2Z " +
            "M487.5 77.2Q481.4 77.2 477.5 75.6Q473.5 74.1 471.6 70.3Q469.7 66.6 469.7 60.1V34.7H459.5V23.3H470.2L473.3 7.7H485.3V23.3H499.8V34.7H485.3V58.1Q485.3 61.9 486.8 63.8Q488.2 65.8 493 65.8H499.8V75.5Q498.4 76 496.2 76.4Q493.9 76.8 491.6 77Q489.2 77.2 487.5 77.2Z " +
            "M541.3 77.2Q530.8 77.2 523.2 74.2Q515.6 71.3 511.5 65.2Q507.3 59.1 507.3 49.7Q507.3 40.5 511.4 34.3Q515.4 28.2 523 25.1Q530.5 22.1 540.7 22.1Q551.4 22.1 558.8 25.1Q566.2 28.1 570 34Q573.8 39.9 573.8 48.6V53.2H523.7Q524 57.8 526.2 60.6Q528.4 63.5 532.4 64.8Q536.3 66.1 541.6 66.1Q544 66.1 546.7 65.5Q549.3 65 551.7 64Q554 63 555.6 61.4Q557.2 59.9 557.4 57.7H573.4Q573.4 61.9 571.2 65.5Q569 69.1 564.8 71.7Q560.6 74.3 554.7 75.7Q548.7 77.2 541.3 77.2ZM523.8 43.7H557.1Q557.1 41.1 555.9 39.1Q554.7 37.2 552.5 35.8Q550.2 34.5 547.3 33.8Q544.4 33.2 541 33.2Q536.1 33.2 532.4 34.4Q528.6 35.7 526.5 38Q524.3 40.4 523.8 43.7Z";
        internal const double Width = 573.8, Height = 77.2;
        private const double LightX = 255.0, LightY = 9.4, LightRadius = 9.4;

        /// <summary>The wordmark, <paramref name="height"/> pixels tall (the light's top to the baseline).</summary>
        internal static Control Create(double height)
        {
            var letters = new Path { Data = Geometry.Parse(Letters) }.Brush(Shape.FillProperty, "Ink");
            var light = new Border
            {
                Width = LightRadius * 2, Height = LightRadius * 2, CornerRadius = new CornerRadius(LightRadius),
                Background = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#FFB08A"), 0), new GradientStop(Color.Parse("#FF5B14"), 0.6), new GradientStop(Color.Parse("#E84E0C"), 1) }
                },
                BoxShadow = BoxShadows.Parse("0 0 10 1 #B3FF5B14")
            };
            Canvas.SetLeft(light, LightX - LightRadius); Canvas.SetTop(light, LightY - LightRadius);
            var canvas = new Canvas { Width = Width, Height = Height, Children = { letters, light } };
            var mark = new Viewbox { Child = canvas, Height = height, Width = height * Width / Height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            Avalonia.Automation.AutomationProperties.SetName(mark, "Muzikette");
            return mark;
        }
    }
}
