namespace UnitSport.Avatar.Face;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests).

/// <summary>The eye's outline (<c>shaders/body/face.gdshaderinc</c>, <c>FACE_EYES</c>).</summary>
public enum EyeShape { Round, Almond, Droopy, Sharp, Button, Anime, Sleepy, Wide }

/// <summary>The brows: none, a thin arch, straight, or angled down to the nose.</summary>
public enum BrowShape { None, Arch, Straight, Angled }

/// <summary>The mouth at rest; <see cref="FaceState"/> bends and opens it from there.</summary>
public enum MouthShape { Line, Smile, Cat, Grin, Oh, Smirk, Frown, Beam }

/// <summary>The nose: none, a dot, a hook beside the middle, two nostrils.</summary>
public enum NoseShape { None, Dot, Hook, Nostrils }

/// <summary>
/// A procedural pixel face (#657): what it is made of, fixed for a figure, drawn by
/// <c>shaders/body/face.gdshaderinc</c> and animated there by a <see cref="FaceState"/>. Sizes are
/// steps (0..3), not lengths, so a face packs into <see cref="Code"/>: two whole numbers below 2^24
/// that the face band carries as its second UV (<c>MeshScratch.FaceBand</c>). The bit layout is
/// the shader's: change both together.
/// </summary>
public readonly record struct FaceGenome(
    EyeShape Eyes,
    int EyeSize,
    int EyeSpacing,
    int EyeHeight,
    int Iris,
    bool Lashes,
    bool Shine,
    BrowShape Brow,
    bool ThickBrow,
    int BrowHeight,
    MouthShape Mouth,
    int MouthWidth,
    int MouthHeight,
    NoseShape Nose,
    bool Blush,
    bool Freckles,
    bool Stubble,
    int Resolution,
    int Seed)
{
    /// <summary>The pixel grids a face can be drawn on, by <see cref="Resolution"/>: 16 is the old atlas's.</summary>
    public static readonly int[] Grids = { 16, 24, 32, 40 };

    /// <summary>The figures' grid: twice the old atlas's pixels across.</summary>
    public const int FigureResolution = 2;

    /// <summary>The pixels across this face's grid.</summary>
    public int Pixels => Grids[Step(Resolution)];

    /// <summary>
    /// The two halves of the face, each below 2^24. Low: eyes 0-2, eye size 3-4, spacing 5-6,
    /// height 7-8, iris 9-10, lashes 11, shine 12, brow 13-14, thick brow 15, mouth 16-18, mouth
    /// width 19-20, nose 21-22, blush 23. High: freckles 0, stubble 1, grid 2-3, seed 4-11, brow
    /// height 12-13, mouth height 14-15.
    /// </summary>
    public (int Low, int High) Code
    {
        get
        {
            int low = ((int)Eyes & 7)
                | Step(EyeSize) << 3 | Step(EyeSpacing) << 5 | Step(EyeHeight) << 7 | Step(Iris) << 9
                | Bit(Lashes) << 11 | Bit(Shine) << 12 | ((int)Brow & 3) << 13 | Bit(ThickBrow) << 15
                | ((int)Mouth & 7) << 16 | Step(MouthWidth) << 19 | ((int)Nose & 3) << 21 | Bit(Blush) << 23;
            int high = Bit(Freckles) | Bit(Stubble) << 1 | Step(Resolution) << 2 | (Seed & 0xFF) << 4
                | Step(BrowHeight) << 12 | Step(MouthHeight) << 14;
            return (low, high);
        }
    }

    /// <summary>The face <see cref="Code"/> packed.</summary>
    public static FaceGenome FromCode(int low, int high) => new(
        (EyeShape)(low & 7), low >> 3 & 3, low >> 5 & 3, low >> 7 & 3, low >> 9 & 3,
        (low >> 11 & 1) != 0, (low >> 12 & 1) != 0, (BrowShape)(low >> 13 & 3), (low >> 15 & 1) != 0,
        high >> 12 & 3, (MouthShape)(low >> 16 & 7), low >> 19 & 3, high >> 14 & 3, (NoseShape)(low >> 21 & 3),
        (low >> 23 & 1) != 0, (high & 1) != 0, (high >> 1 & 1) != 0, high >> 2 & 3, high >> 4 & 0xFF);

    /// <summary>This face with its blink and glances started at <paramref name="seed"/> (low 8 bits).</summary>
    public FaceGenome WithSeed(int seed) => this with { Seed = seed & 0xFF };

    private static int Step(int v) => v < 0 ? 0 : v > 3 ? 3 : v;

    private static int Bit(bool b) => b ? 1 : 0;

    // ---- the presets: Appearance.Face, 4 bits on the wire ------------------------------------

    private static FaceGenome F(EyeShape eyes, int size, int spacing, int height, int iris, bool lashes, bool shine,
        BrowShape brow, bool thick, int browHeight, MouthShape mouth, int width, NoseShape nose,
        bool blush = false, bool freckles = false, bool stubble = false) =>
        new(eyes, size, spacing, height, iris, lashes, shine, brow, thick, browHeight, mouth, width, 1, nose,
            blush, freckles, stubble, FigureResolution, 0);

    /// <summary>
    /// The faces a figure picks from (<c>Appearance.Face</c>). <b>Append only</b>: the index is
    /// replicated and saved. 0-7 are the #394 atlas's faces, drawn again procedurally; 8-15 new.
    /// </summary>
    private static readonly (string Name, FaceGenome Face)[] Presets =
    {
        ("anime", F(EyeShape.Anime, 1, 1, 1, 3, true, true, BrowShape.Arch, false, 1, MouthShape.Smile, 0, NoseShape.None, blush: true)),
        ("calm", F(EyeShape.Almond, 1, 1, 1, 2, false, false, BrowShape.Arch, false, 2, MouthShape.Line, 0, NoseShape.None)),
        ("sharp", F(EyeShape.Sharp, 1, 1, 1, 2, true, false, BrowShape.Angled, false, 1, MouthShape.Line, 1, NoseShape.None)),
        ("cute", F(EyeShape.Round, 1, 2, 2, 3, false, true, BrowShape.None, false, 1, MouthShape.Cat, 0, NoseShape.None, blush: true)),
        ("freckles", F(EyeShape.Round, 0, 1, 1, 2, false, true, BrowShape.Arch, false, 1, MouthShape.Smile, 1, NoseShape.Dot, freckles: true)),
        // #394 feedback: masculine faces, heavier brows and smaller eyes
        ("stern", F(EyeShape.Almond, 0, 1, 1, 2, false, false, BrowShape.Angled, true, 0, MouthShape.Line, 1, NoseShape.Hook)),
        ("grin", F(EyeShape.Almond, 0, 1, 1, 2, false, true, BrowShape.Straight, true, 1, MouthShape.Smirk, 2, NoseShape.Dot)),
        ("stubble", F(EyeShape.Button, 1, 1, 1, 2, false, false, BrowShape.Straight, true, 1, MouthShape.Line, 1, NoseShape.Hook, stubble: true)),
        // #657: the procedural faces
        ("sparkle", F(EyeShape.Anime, 2, 1, 1, 3, true, true, BrowShape.Arch, false, 2, MouthShape.Oh, 0, NoseShape.None, blush: true)),
        ("sleepy", F(EyeShape.Sleepy, 1, 1, 1, 2, false, false, BrowShape.Arch, false, 1, MouthShape.Smile, 0, NoseShape.Dot)),
        ("button", F(EyeShape.Button, 2, 1, 2, 2, false, true, BrowShape.None, false, 1, MouthShape.Cat, 1, NoseShape.None, blush: true)),
        ("gloomy", F(EyeShape.Droopy, 1, 1, 1, 2, false, false, BrowShape.Arch, false, 3, MouthShape.Frown, 0, NoseShape.Dot)),
        ("cheeky", F(EyeShape.Round, 1, 1, 1, 2, false, true, BrowShape.Arch, false, 2, MouthShape.Beam, 1, NoseShape.Dot, freckles: true)),
        ("startled", F(EyeShape.Wide, 1, 1, 1, 0, false, true, BrowShape.Straight, false, 3, MouthShape.Oh, 0, NoseShape.Nostrils)),
        ("dreamy", F(EyeShape.Almond, 2, 1, 1, 3, true, true, BrowShape.Arch, false, 2, MouthShape.Smile, 0, NoseShape.None, blush: true)),
        ("bold", F(EyeShape.Sharp, 1, 1, 1, 2, false, false, BrowShape.Angled, true, 0, MouthShape.Smirk, 2, NoseShape.Hook, stubble: true)),
    };

    /// <summary>How many faces a figure can pick (at most 16: <c>Appearance.Face</c> is 4 bits).</summary>
    public static int PresetCount => Presets.Length;

    /// <summary>Face <paramref name="index"/> (wrapped), seed 0.</summary>
    public static FaceGenome Preset(int index) => Presets[Wrap(index)].Face;

    public static string PresetName(int index) => Presets[Wrap(index)].Name;

    private static int Wrap(int index) => ((index % Presets.Length) + Presets.Length) % Presets.Length;

    /// <summary>
    /// A face made up from <paramref name="seed"/>, the same on every peer: for creatures and
    /// crowds that pick no preset (#658). Its blink seed is the seed's low byte.
    /// </summary>
    public static FaceGenome ForSeed(uint seed, int resolution = FigureResolution)
    {
        uint h = seed * 0x9E3779B9u ^ 0x85EBCA6Bu;
        int Next(int n)
        {
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (int)(h % (uint)n);
        }
        var eyes = (EyeShape)Next(8);
        var brow = (BrowShape)Next(4);
        var mouth = (MouthShape)Next(8);
        return new FaceGenome(eyes, Next(4), Next(4), Next(3), Next(4), Next(3) == 0, Next(2) == 0,
            brow, Next(3) == 0, Next(4), mouth, Next(3), Next(3), (NoseShape)Next(4),
            Next(3) == 0, Next(5) == 0, Next(6) == 0, resolution, (int)(seed & 0xFF));
    }
}
