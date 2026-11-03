namespace UnitSport.Avatar;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests).

/// <summary>
/// <c>Appearance</c> as the one int it is saved (<c>GameSettings.AppearanceBits</c>) and replicated
/// (<c>FootPlayer.AppearanceBits</c>) as (#394). Build bits 0-2, face 3-6, eyes 7-9, skin 10-12,
/// hair style 13-16, hair colour 17-20, then the high bits added later (#880, the aliens): face bit
/// 21 (faces 16-31), skin bits 22-23 (tones 8-31); bit 30 marks it chosen. <b>Append only</b>: a
/// value saved or sent before keeps its meaning (its high bits are 0), and an older peer reads a
/// newer value's low bits.
/// </summary>
public readonly record struct AppearanceCode(int Build, int Face, int Eyes, int Skin, int Hair, int HairColour)
{
    /// <summary>Bit 30: a packed value is chosen; 0 (an int's default) is "nobody chose, use the seed".</summary>
    public const int Set = 1 << 30;

    /// <summary>How many faces and skin tones the bits hold (indices past them wrap when packed).</summary>
    public const int Faces = 32, Skins = 32;

    public int Pack() => Set | (Build & 7) | (Face & 15) << 3 | (Eyes & 7) << 7 | (Skin & 7) << 10
        | (Hair & 15) << 13 | (HairColour & 15) << 17 | (Face >> 4 & 1) << 21 | (Skin >> 3 & 3) << 22;

    /// <summary>A packed value back, or null when it was never set.</summary>
    public static AppearanceCode? Unpack(int bits) => (bits & Set) == 0 ? null : new(
        bits & 7, (bits >> 3 & 15) | (bits >> 21 & 1) << 4, bits >> 7 & 7, (bits >> 10 & 7) | (bits >> 22 & 3) << 3,
        bits >> 13 & 15, bits >> 17 & 15);
}
