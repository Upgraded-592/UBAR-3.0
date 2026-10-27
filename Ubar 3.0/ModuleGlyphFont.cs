using Microsoft.UI.Xaml.Media;

namespace Ubar3;

/// <summary>
/// Picks the right font family for a module glyph. Fluent PUA codepoints
/// (U+E000–U+F8FF) come from Segoe Fluent Icons; everything else (emoji and
/// symbols like 🌡 or ☀) comes from Segoe UI Symbol, which renders them as
/// monochrome code-style icons instead of color emoji.
/// </summary>
public static class ModuleGlyphFont
{
    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");
    private static readonly FontFamily Symbol = new("Segoe UI Symbol");

    public static FontFamily For(string glyph)
    {
        foreach (var ch in glyph)
        {
            if (ch is >= '\uE000' and <= '\uF8FF') return Fluent;
        }
        return Symbol;
    }
}
