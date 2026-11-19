using System;
using Avalonia.Input;

namespace AlbumArtTool.Ui
{
    // Shortcuts use Ctrl on Windows and Linux and ⌘ (Command) on a Mac, and are written the way each system writes them.
    internal static class Shortcut
    {
        /// <summary>Whether shortcuts use ⌘ (tests switch it on to check the Mac wording).</summary>
        internal static bool Mac = OperatingSystem.IsMacOS();

        /// <summary>The modifier that goes with letter and number shortcuts: ⌘ on a Mac, Ctrl elsewhere.</summary>
        internal static KeyModifiers Command => Mac ? KeyModifiers.Meta : KeyModifiers.Control;
        internal static bool Pressed(KeyModifiers modifiers) => (modifiers & Command) != 0;

        /// <summary>A tooltip or label as it reads on this system: on a Mac "Undo (Ctrl+Z)" becomes "Undo (⌘Z)".</summary>
        internal static string Text(string text)
        {
            if (!Mac || string.IsNullOrEmpty(text)) return text;
            return text.Replace("Ctrl + mouse wheel", "pinching on the trackpad").Replace("Ctrl + minus", "⌘ −").Replace("Ctrl + plus", "⌘ +")
                .Replace("Ctrl + 0", "⌘0").Replace("Shift+F5", "⇧⌘R").Replace("F5", "⌘R").Replace("F1", "⌘?").Replace("Ctrl+", "⌘");
        }
    }
}
