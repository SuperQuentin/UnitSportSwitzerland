using Godot;

namespace UnitSport.Concert;

/// <summary>What a channel of a fixture does: the names a lighting console shows.</summary>
public enum Attr : byte
{
    Pan, PanFine, Tilt, TiltFine, PanTiltSpeed, Dimmer, DimmerFine, Shutter,
    Red, Green, Blue, White, ColorWheel, Cyan, Magenta, Yellow, Cto, Gobo, GoboRotation, Prism,
    Frost, Focus, Zoom, Iris, Control,
    /// <summary>A multi-cell fixture's cells: a 4-lite blinder's lamps, a pixel bar's RGB cells in order.</summary>
    Cell,
    // a strobe
    Duration, Rate, Effect,
    // a video layer on a media server
    Clip, ClipSpeed, Red2, Green2, Blue2,
    // atmospherics
    Pump, Fan,
    // special effects: a safety arm and a fire, as an SFX controller lays them out
    Arm, Fire,
    /// <summary>A channel the game does not drive (an imported fixture's macro, a 16-bit fine it ignores): kept so the offsets stay right.</summary>
    Other,
}

/// <summary>How a fixture looks and moves; each has its own real channel layout.</summary>
public enum FixtureKind : byte { Spot, Beam, Wash, Blinder, Strobe, PixelBar, Screen, Hazer, Flame, Co2, Confetti, Gerb, Airburst, LowFog }

/// <summary>
/// A fixture type, as its manufacturer's manual lays out its DMX channels in one mode: modelled on
/// the classes of head every stadium rig flies (a profile spot, a sharp beam, an RGBW zoom wash, a
/// four-lite blinder, an LED strobe, an 8-cell pixel bar, a media-server layer, a hazer). Pan is
/// 540°, tilt 270°, both 16-bit, as on real moving heads.
/// </summary>
public sealed record FixtureType(string Name, FixtureKind Kind, Attr[] Channels, float PanRange = 540f, float TiltRange = 270f,
    float BeamMin = 4f, float BeamMax = 40f)
{
    public int Footprint => Channels.Length;

    private int[]? _cells;

    /// <summary>The offsets of the <see cref="Attr.Cell"/> channels, in order: a blinder's lamps, a pixel bar's R, G, B per cell.</summary>
    public int[] CellOffsets => _cells ??= Enumerable.Range(0, Channels.Length).Where(i => Channels[i] == Attr.Cell).ToArray();

    /// <summary>How many cells it lights: a pixel bar's RGB triples, a blinder's lamps, else one.</summary>
    public int Cells => Kind switch
    {
        FixtureKind.PixelBar => Math.Max(1, CellOffsets.Length / 3),
        FixtureKind.Blinder => Math.Max(1, CellOffsets.Length),
        _ => 1,
    };

    /// <summary>The offset of an attribute in the layout, or -1 (the first, for <see cref="Attr.Cell"/>).</summary>
    public int Offset(Attr a) => Array.IndexOf(Channels, a);

    public static readonly FixtureType Spot = new("Profile spot 18ch", FixtureKind.Spot, new[]
    {
        Attr.Pan, Attr.PanFine, Attr.Tilt, Attr.TiltFine, Attr.PanTiltSpeed, Attr.Dimmer, Attr.DimmerFine, Attr.Shutter,
        Attr.Cyan, Attr.Magenta, Attr.Yellow, Attr.ColorWheel, Attr.Cto, Attr.Gobo, Attr.GoboRotation, Attr.Prism, Attr.Focus, Attr.Zoom,
    }, BeamMin: 6f, BeamMax: 45f);

    public static readonly FixtureType Beam = new("Beam 14ch", FixtureKind.Beam, new[]
    {
        Attr.Pan, Attr.PanFine, Attr.Tilt, Attr.TiltFine, Attr.PanTiltSpeed, Attr.Shutter, Attr.Dimmer, Attr.ColorWheel,
        Attr.Gobo, Attr.Prism, Attr.Frost, Attr.Focus, Attr.Zoom, Attr.Control,
    }, BeamMin: 2f, BeamMax: 6f);

    public static readonly FixtureType Wash = new("RGBW zoom wash 16ch", FixtureKind.Wash, new[]
    {
        Attr.Pan, Attr.PanFine, Attr.Tilt, Attr.TiltFine, Attr.PanTiltSpeed, Attr.Dimmer, Attr.DimmerFine, Attr.Shutter,
        Attr.Red, Attr.Green, Attr.Blue, Attr.White, Attr.Cto, Attr.Zoom, Attr.Effect, Attr.Rate,
    }, BeamMin: 8f, BeamMax: 60f);

    public static readonly FixtureType Blinder = new("4-lite blinder 4ch", FixtureKind.Blinder,
        new[] { Attr.Cell, Attr.Cell, Attr.Cell, Attr.Cell }, PanRange: 0, TiltRange: 0, BeamMin: 50f, BeamMax: 50f);

    public static readonly FixtureType Strobe = new("LED strobe 4ch", FixtureKind.Strobe,
        new[] { Attr.Dimmer, Attr.Duration, Attr.Rate, Attr.Effect }, PanRange: 0, TiltRange: 0, BeamMin: 90f, BeamMax: 90f);

    /// <summary>Eight RGB cells, 24 channels.</summary>
    public static readonly FixtureType PixelBar = new("Pixel bar 8x RGB", FixtureKind.PixelBar,
        Enumerable.Repeat(Attr.Cell, 24).ToArray(), PanRange: 0, TiltRange: 0, BeamMin: 15f, BeamMax: 15f);

    public static readonly FixtureType Screen = new("Media server layer 9ch", FixtureKind.Screen, new[]
    {
        Attr.Dimmer, Attr.Clip, Attr.ClipSpeed, Attr.Red, Attr.Green, Attr.Blue, Attr.Red2, Attr.Green2, Attr.Blue2,
    }, PanRange: 0, TiltRange: 0);

    public static readonly FixtureType Hazer = new("Hazer 2ch", FixtureKind.Hazer, new[] { Attr.Pump, Attr.Fan }, PanRange: 0, TiltRange: 0);

    // special effects (#885): a channel to arm and one to fire, both over half to go, as on a real
    // SFX controller; nothing fires disarmed. Flames and CO2 fire for as long as they are held,
    // confetti, gerbs and airbursts once per rising edge.
    private static FixtureType Sfx(string name, FixtureKind kind) => new(name, kind, new[] { Attr.Arm, Attr.Fire }, PanRange: 0, TiltRange: 0);
    public static readonly FixtureType Flame = Sfx("Flame projector 2ch", FixtureKind.Flame);
    public static readonly FixtureType Co2 = Sfx("CO2 jet 2ch", FixtureKind.Co2);
    public static readonly FixtureType Confetti = Sfx("Confetti cannon 2ch", FixtureKind.Confetti);
    public static readonly FixtureType Gerb = Sfx("Pyro gerb 2ch", FixtureKind.Gerb);
    public static readonly FixtureType Airburst = Sfx("Pyro airburst 2ch", FixtureKind.Airburst);
    public static readonly FixtureType LowFog = new("Low fog 2ch", FixtureKind.LowFog, new[] { Attr.Pump, Attr.Fan }, PanRange: 0, TiltRange: 0);

    /// <summary>A spot's or a beam's colour wheel, slot by slot (each slot is 8 DMX values wide).</summary>
    public static readonly Color[] Wheel =
    {
        new(1f, 1f, 1f), new(1f, 0.1f, 0.05f), new(1f, 0.45f, 0.05f), new(1f, 0.9f, 0.1f), new(0.1f, 1f, 0.2f),
        new(0.1f, 0.9f, 1f), new(0.1f, 0.25f, 1f), new(0.6f, 0.15f, 1f), new(1f, 0.15f, 0.7f), new(1f, 0.75f, 0.5f),
    };

    public static int WheelSlot(Color c)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < Wheel.Length; i++)
        {
            float d = (Wheel[i].R - c.R) * (Wheel[i].R - c.R) + (Wheel[i].G - c.G) * (Wheel[i].G - c.G) + (Wheel[i].B - c.B) * (Wheel[i].B - c.B);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}

/// <summary>Where a fixture hangs: which position of the rig it belongs to (a console's group).</summary>
public enum RigGroup : byte { FrontTruss, MidTruss, BackTruss, Floor, Towers, Blinders, Strobes, PixelBars, Screens, Haze, Flames, Co2, Confetti, Gerbs, Airbursts, LowFog }

/// <summary>
/// One fixture of the rig: its type, its group, where it hangs (stadium frame, metres over the pitch)
/// and which way its home points (the beam with pan and tilt at centre), and its DMX address.
/// <paramref name="Up"/> is the yoke's axis: down for a hung head, up for one on the floor.
/// </summary>
public sealed record Fixture(int Id, FixtureType Type, RigGroup Group, Vector3 Position, Vector3 Home, Vector3 Up, int Universe, int Address)
{
    /// <summary>For a screen: its centre, size and facing (stadium frame), and its pixels.</summary>
    public Vector3 ScreenSize { get; init; }
    public int PixelsX { get; init; }
    public int PixelsY { get; init; }
    /// <summary>
    /// Where the beam points at pan and tilt centre, as a pan from <see cref="Home"/> and a tilt from
    /// <see cref="Up"/> (degrees): an imported plot's focus point (#959); zero for ours.
    /// </summary>
    public float HomePan { get; init; }
    public float HomeTilt { get; init; }
    /// <summary>The fixture's pan or tilt runs the other way for the same DMX (an MVR's DMXInvertPan/Tilt, #959).</summary>
    public bool InvertPan { get; init; }
    public bool InvertTilt { get; init; }
}

/// <summary>
/// A rig's DMX: universes of 512 channels, fixtures addressed into them in order as a lighting
/// tech patches a show (a fixture never straddles two universes), the same bytes Art-Net or sACN
/// would carry. Whoever drives the rig writes here; the rig only reads.
/// </summary>
public sealed class DmxPatch
{
    public const int Channels = 512;

    public readonly List<Fixture> Fixtures = new();
    public readonly List<byte[]> Universes = new();

    /// <summary>Patches a fixture at the next free address; returns it.</summary>
    public Fixture Add(FixtureType type, RigGroup group, Vector3 position, Vector3 home, Vector3 up)
    {
        int u = Universes.Count - 1;
        int next = _next;
        if (u < 0 || next + type.Footprint > Channels)
        {
            Universes.Add(new byte[Channels]);
            u = Universes.Count - 1;
            next = 0;
        }
        var f = new Fixture(Fixtures.Count, type, group, position, home.Normalized(), up.Normalized(), u, next);
        _next = next + type.Footprint;
        Fixtures.Add(f);
        return f;
    }

    /// <summary>
    /// Patches a fixture at a given universe and address (0-based), as an imported rig lays them out;
    /// the universes grow to reach it, and later <see cref="Add"/>s go after the highest channel used.
    /// </summary>
    public Fixture AddAt(FixtureType type, RigGroup group, Vector3 position, Vector3 home, Vector3 up, int universe, int address)
    {
        while (Universes.Count <= universe) Universes.Add(new byte[Channels]);
        var f = new Fixture(Fixtures.Count, type, group, position, home.Normalized(), up.Normalized(), universe, address);
        Fixtures.Add(f);
        if (universe == Universes.Count - 1) _next = Math.Max(_next, address + type.Footprint);
        return f;
    }

    /// <summary>Unpatches everything (an imported rig takes the generated one's place).</summary>
    public void Clear()
    {
        Fixtures.Clear();
        Universes.Clear();
        _next = 0;
    }

    /// <summary>Patches a screen (a media-server layer driving its pixels).</summary>
    public Fixture AddScreen(Vector3 centre, Vector3 facing, Vector3 size, int px, int py)
    {
        var f = Add(FixtureType.Screen, RigGroup.Screens, centre, facing, Vector3.Up);
        var s = f with { ScreenSize = size, PixelsX = px, PixelsY = py };
        Fixtures[^1] = s;
        return s;
    }

    private int _next;

    public byte Get(Fixture f, int offset) => Universes[f.Universe][f.Address + offset];
    public void Set(Fixture f, int offset, byte v) => Universes[f.Universe][f.Address + offset] = v;

    public float Get01(Fixture f, Attr a) => f.Type.Offset(a) is int o and >= 0 ? Get(f, o) / 255f : 0f;
    public void Set01(Fixture f, Attr a, float v)
    {
        if (f.Type.Offset(a) is int o and >= 0) Set(f, o, (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255));
    }

    /// <summary>A 16-bit attribute (pan, tilt) from its coarse and fine channels, 0..1.</summary>
    public float Get16(Fixture f, Attr coarse, Attr fine)
    {
        int c = f.Type.Offset(coarse), fi = f.Type.Offset(fine);
        if (c < 0) return 0.5f;
        int v = Get(f, c) << 8 | (fi >= 0 ? Get(f, fi) : 0);
        return v / 65535f;
    }

    public void Set16(Fixture f, Attr coarse, Attr fine, float v)
    {
        int x = Math.Clamp((int)MathF.Round(v * 65535f), 0, 65535);
        if (f.Type.Offset(coarse) is int c and >= 0) Set(f, c, (byte)(x >> 8));
        if (f.Type.Offset(fine) is int fi and >= 0) Set(f, fi, (byte)(x & 0xff));
    }

    public int ChannelsUsed => Universes.Count == 0 ? 0 : (Universes.Count - 1) * Channels + _next;
}
