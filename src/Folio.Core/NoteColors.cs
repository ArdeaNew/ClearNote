namespace Folio.Core;

/// <summary>Portable sRGB text colors. Null means automatic text or no highlight.</summary>
public static class NoteColors
{
    public static bool IsValid(string? color) => color is null ||
        color.Length == 7 && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static string? Normalize(string? color)
    {
        if (!IsValid(color)) throw new ArgumentException("Text colors must use #RRGGBB.", nameof(color));
        return color?.ToUpperInvariant();
    }
}
