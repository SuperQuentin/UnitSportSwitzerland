namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A 5 x 7 pixel font for the backboxes (#1325): digits, capitals and a little punctuation, each glyph
/// five columns of seven bits (bit 0 the top row). The dot-matrix, the alphanumeric and the LCD displays
/// draw their text with it; pure, so its layout is testable.
/// </summary>
public static class PinballFont
{
    public const int W = 5, H = 7;

    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = new byte[] { 0x3E, 0x51, 0x49, 0x45, 0x3E }, ['1'] = new byte[] { 0x00, 0x42, 0x7F, 0x40, 0x00 },
        ['2'] = new byte[] { 0x42, 0x61, 0x51, 0x49, 0x46 }, ['3'] = new byte[] { 0x21, 0x41, 0x45, 0x4B, 0x31 },
        ['4'] = new byte[] { 0x18, 0x14, 0x12, 0x7F, 0x10 }, ['5'] = new byte[] { 0x27, 0x45, 0x45, 0x45, 0x39 },
        ['6'] = new byte[] { 0x3C, 0x4A, 0x49, 0x49, 0x30 }, ['7'] = new byte[] { 0x01, 0x71, 0x09, 0x05, 0x03 },
        ['8'] = new byte[] { 0x36, 0x49, 0x49, 0x49, 0x36 }, ['9'] = new byte[] { 0x06, 0x49, 0x49, 0x29, 0x1E },
        ['A'] = new byte[] { 0x7E, 0x11, 0x11, 0x11, 0x7E }, ['B'] = new byte[] { 0x7F, 0x49, 0x49, 0x49, 0x36 },
        ['C'] = new byte[] { 0x3E, 0x41, 0x41, 0x41, 0x22 }, ['D'] = new byte[] { 0x7F, 0x41, 0x41, 0x22, 0x1C },
        ['E'] = new byte[] { 0x7F, 0x49, 0x49, 0x49, 0x41 }, ['F'] = new byte[] { 0x7F, 0x09, 0x09, 0x09, 0x01 },
        ['G'] = new byte[] { 0x3E, 0x41, 0x49, 0x49, 0x7A }, ['H'] = new byte[] { 0x7F, 0x08, 0x08, 0x08, 0x7F },
        ['I'] = new byte[] { 0x00, 0x41, 0x7F, 0x41, 0x00 }, ['J'] = new byte[] { 0x20, 0x40, 0x41, 0x3F, 0x01 },
        ['K'] = new byte[] { 0x7F, 0x08, 0x14, 0x22, 0x41 }, ['L'] = new byte[] { 0x7F, 0x40, 0x40, 0x40, 0x40 },
        ['M'] = new byte[] { 0x7F, 0x02, 0x0C, 0x02, 0x7F }, ['N'] = new byte[] { 0x7F, 0x04, 0x08, 0x10, 0x7F },
        ['O'] = new byte[] { 0x3E, 0x41, 0x41, 0x41, 0x3E }, ['P'] = new byte[] { 0x7F, 0x09, 0x09, 0x09, 0x06 },
        ['Q'] = new byte[] { 0x3E, 0x41, 0x51, 0x21, 0x5E }, ['R'] = new byte[] { 0x7F, 0x09, 0x19, 0x29, 0x46 },
        ['S'] = new byte[] { 0x46, 0x49, 0x49, 0x49, 0x31 }, ['T'] = new byte[] { 0x01, 0x01, 0x7F, 0x01, 0x01 },
        ['U'] = new byte[] { 0x3F, 0x40, 0x40, 0x40, 0x3F }, ['V'] = new byte[] { 0x1F, 0x20, 0x40, 0x20, 0x1F },
        ['W'] = new byte[] { 0x3F, 0x40, 0x38, 0x40, 0x3F }, ['X'] = new byte[] { 0x63, 0x14, 0x08, 0x14, 0x63 },
        ['Y'] = new byte[] { 0x07, 0x08, 0x70, 0x08, 0x07 }, ['Z'] = new byte[] { 0x61, 0x51, 0x49, 0x45, 0x43 },
        [' '] = new byte[] { 0, 0, 0, 0, 0 }, ['-'] = new byte[] { 0x08, 0x08, 0x08, 0x08, 0x08 },
        [','] = new byte[] { 0x00, 0x50, 0x30, 0x00, 0x00 }, ['.'] = new byte[] { 0x00, 0x60, 0x60, 0x00, 0x00 },
        [':'] = new byte[] { 0x00, 0x36, 0x36, 0x00, 0x00 }, ['!'] = new byte[] { 0x00, 0x00, 0x5F, 0x00, 0x00 },
        ['/'] = new byte[] { 0x20, 0x10, 0x08, 0x04, 0x02 }, ['\''] = new byte[] { 0x00, 0x05, 0x03, 0x00, 0x00 },
        ['%'] = new byte[] { 0x23, 0x13, 0x08, 0x64, 0x62 }, ['>'] = new byte[] { 0x41, 0x22, 0x14, 0x08, 0x00 },
        ['<'] = new byte[] { 0x00, 0x08, 0x14, 0x22, 0x41 }, ['+'] = new byte[] { 0x08, 0x08, 0x3E, 0x08, 0x08 },
    };

    /// <summary>Whether pixel (<paramref name="col"/>, <paramref name="row"/>) of <paramref name="c"/> is on (lower case reads as capitals; unknown as blank).</summary>
    public static bool On(char c, int col, int row)
    {
        if (col < 0 || col >= W || row < 0 || row >= H) return false;
        return Glyphs.TryGetValue(char.ToUpperInvariant(c), out var g) && (g[col] >> row & 1) != 0;
    }

    /// <summary>Width of <paramref name="text"/> in pixels at <paramref name="scale"/>, one column between glyphs.</summary>
    public static int Width(string text, int scale = 1) => text.Length == 0 ? 0 : (text.Length * (W + 1) - 1) * scale;

    /// <summary>Calls <paramref name="set"/> for every lit pixel of <paramref name="text"/> drawn from (x, y), top left.</summary>
    public static void Draw(string text, int x, int y, int scale, Action<int, int> set)
    {
        for (int i = 0; i < text.Length; i++)
            for (int col = 0; col < W; col++)
                for (int row = 0; row < H; row++)
                    if (On(text[i], col, row))
                        for (int sx = 0; sx < scale; sx++)
                            for (int sy = 0; sy < scale; sy++)
                                set(x + (i * (W + 1) + col) * scale + sx, y + row * scale + sy);
    }

    /// <summary>
    /// The seven segments of a digit (a top, b top right, c bottom right, d bottom, e bottom left, f top left,
    /// g middle) as bits 0-6, for the 7-segment displays.
    /// </summary>
    public static int Segments(char c) => c switch
    {
        '0' => 0b0111111, '1' => 0b0000110, '2' => 0b1011011, '3' => 0b1001111, '4' => 0b1100110,
        '5' => 0b1101101, '6' => 0b1111101, '7' => 0b0000111, '8' => 0b1111111, '9' => 0b1101111,
        '-' => 0b1000000, _ => 0,
    };
}
