using Godot;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>Where the stage stands in the bowl.</summary>
public enum StageLayout
{
    /// <summary>Across the south end, facing north: the classic stadium end stage.</summary>
    EndSouth,
    EndNorth,
    /// <summary>On a long side, facing across the pitch: half the bowl sold.</summary>
    SideEast,
    SideWest,
    /// <summary>In the round on the centre spot, every seat sold.</summary>
    Center,
    /// <summary>An end stage with a catwalk down the pitch to a B-stage.</summary>
    Thrust,
}

/// <summary>Video on the stage.</summary>
public enum ScreenSet { None, Center, Sides, CenterAndSides }

/// <summary>What kind of show: it sizes the rig and picks its looks.</summary>
public enum Genre { Rock, Pop, Electronic, HipHop, Ballad }

/// <summary>
/// One night's concert in a stadium (#885): who plays, on what stage, under what rig. A pure
/// function of its seed (<see cref="ConcertPlanner"/>), so every peer that knows the seed builds the
/// same stage and runs the same show on the shared clock; nothing else is sent.
/// </summary>
public sealed record ConcertEvent
{
    public required int Seed { get; init; }
    public required StadiumSpec Stadium { get; init; }
    public required string Artist { get; init; }
    public required string Tour { get; init; }
    public required Genre Genre { get; init; }
    public required StageLayout Layout { get; init; }
    public required ScreenSet Screens { get; init; }
    /// <summary>The show's colours, sRGB: the first is the artist's, the rest its accents.</summary>
    public required Color[] Palette { get; init; }
    /// <summary>The tempo the desk runs its effects at when no music is playing.</summary>
    public required float Bpm { get; init; }

    // the stage's size, metres
    public required float Width { get; init; }
    public required float Depth { get; init; }
    public required float DeckHeight { get; init; }
    public required float RoofHeight { get; init; }

    // the rig: fixtures per position
    public required int Spots { get; init; }
    public required int Beams { get; init; }
    public required int Washes { get; init; }
    public required int FloorBeams { get; init; }
    public required int Blinders { get; init; }
    public required int Strobes { get; init; }
    public required int PixelBars { get; init; }

    // the effects: flame projectors, CO2 jets, confetti cannons, gerbs, airbursts
    public int Flames { get; init; }
    public int Co2Jets { get; init; }
    public int ConfettiCannons { get; init; }
    public int Gerbs { get; init; }
    public int Airbursts { get; init; }

    /// <summary>The game hours the show plays, [start, end); the stage stands all day.</summary>
    public float StartHour { get; init; } = 20.5f;
    public float EndHour { get; init; } = 23.5f;

    public bool HasCenterScreen => Screens is ScreenSet.Center or ScreenSet.CenterAndSides;
    public bool HasSideScreens => Screens is ScreenSet.Sides or ScreenSet.CenterAndSides;

    public override string ToString() =>
        $"{Artist} — {Tour} ({Genre}, {Layout}, screens {Screens}, {Spots}+{Beams}+{Washes}+{FloorBeams} heads, {Bpm:F0} bpm)";
}

/// <summary>
/// Makes a night's <see cref="ConcertEvent"/> from a seed: tonight's for a stadium is seeded by its
/// name and the date (<see cref="Tonight"/>), so the city gets a different show every day, the same
/// on every peer. Pure; no Godot node.
/// </summary>
public static class ConcertPlanner
{
    private static readonly string[] First =
    {
        "Luna", "Nova", "Jules", "Mika", "Zoé", "Aria", "Léon", "Kai", "Noor", "Elio", "Sasha", "Remy",
        "Alba", "Theo", "Yara", "Nico", "Lior", "Ines", "Milo", "Vera",
    };
    private static readonly string[] Last =
    {
        "Moreau", "Keller", "Rossi", "Blanc", "Weber", "Fontaine", "Steiner", "Lambert", "Ferrari", "Meier",
        "Duval", "Brunner", "Marchand", "Huber", "Colombo",
    };
    private static readonly string[] BandAdj =
    {
        "Electric", "Silent", "Neon", "Velvet", "Golden", "Crimson", "Midnight", "Glass", "Wild", "Paper",
        "Northern", "Burning", "Hollow", "Lunar", "Savage",
    };
    private static readonly string[] BandNoun =
    {
        "Glaciers", "Wolves", "Avalanche", "Pilots", "Echoes", "Satellites", "Foxes", "Lanterns", "Rivers",
        "Machines", "Ghosts", "Summits", "Comets", "Kings", "Tides",
    };
    private static readonly string[] TourWord =
    {
        "World Tour", "Stadium Tour", "Live", "Tour 2026", "Arena Nights", "The Summit Tour", "Night Shift",
        "Afterglow Tour", "Gravity Tour", "Horizon Tour",
    };

    /// <summary>Tonight's event in a stadium: seeded by its name and the date.</summary>
    public static ConcertEvent Tonight(StadiumSpec stadium, DateTime date) =>
        Plan(stadium, Seed(stadium.Name, date));

    /// <summary>A stable seed from a stadium and a day (not <c>string.GetHashCode</c>, which is randomised per run).</summary>
    public static int Seed(string stadium, DateTime date)
    {
        uint h = 2166136261;
        foreach (char ch in $"{stadium}|{date:yyyy-MM-dd}") h = (h ^ ch) * 16777619;
        return (int)(h & 0x7fffffff);
    }

    public static ConcertEvent Plan(StadiumSpec stadium, int seed)
    {
        var rng = new Random(seed);
        T Pick<T>(T[] from) => from[rng.Next(from.Length)];
        float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        var genre = (Genre)rng.Next(5);
        // the layouts by how often stadiums are really set up: mostly end stages
        var layout = rng.Next(100) switch
        {
            < 34 => StageLayout.EndSouth,
            < 50 => StageLayout.EndNorth,
            < 66 => StageLayout.Thrust,
            < 80 => StageLayout.Center,
            < 90 => StageLayout.SideEast,
            _ => StageLayout.SideWest,
        };
        var screens = (ScreenSet)rng.Next(4);
        // an in-the-round show's screen is the cube over it, or nothing
        if (layout == StageLayout.Center && screens == ScreenSet.Sides) screens = ScreenSet.Center;

        string artist = rng.Next(3) switch
        {
            0 => $"{Pick(First)} {Pick(Last)}",
            1 => $"The {Pick(BandAdj)} {Pick(BandNoun)}",
            _ => genre == Genre.Electronic ? $"DJ {Pick(First)}" : $"{Pick(BandAdj)} {Pick(BandNoun)}",
        };

        // the palette: the artist's hue and its accents, by genre
        float hue = (float)rng.NextDouble();
        Color[] palette = genre switch
        {
            Genre.Rock => new[] { Color.FromHsv(hue, 0.9f, 1f), new Color(1f, 0.55f, 0.15f), new Color(1f, 0.95f, 0.85f) },
            Genre.Pop => new[] { Color.FromHsv(hue, 0.7f, 1f), Color.FromHsv(hue + 0.33f, 0.7f, 1f), Color.FromHsv(hue + 0.66f, 0.7f, 1f) },
            Genre.Electronic => new[] { Color.FromHsv(hue, 1f, 1f), Color.FromHsv(hue + 0.5f, 1f, 1f), new Color(1f, 1f, 1f) },
            Genre.HipHop => new[] { Color.FromHsv(hue, 0.85f, 1f), new Color(1f, 0.8f, 0.3f), new Color(0.6f, 0.2f, 1f) },
            _ => new[] { new Color(1f, 0.75f, 0.45f), Color.FromHsv(hue, 0.5f, 1f), new Color(0.35f, 0.45f, 1f) },
        };
        float bpm = genre switch
        {
            Genre.Electronic => Range(124, 130),
            Genre.HipHop => Range(86, 98),
            Genre.Ballad => Range(68, 80),
            Genre.Rock => Range(110, 140),
            _ => Range(100, 124),
        };
        // the rig: an EDM show is beams and strobes, a ballad washes and spots
        float size = Range(0.8f, 1.2f);
        int N(float n) => Math.Max(2, (int)MathF.Round(n * size / 2) * 2);
        bool round = layout == StageLayout.Center;

        return new ConcertEvent
        {
            Seed = seed,
            Stadium = stadium,
            Artist = artist,
            Tour = Pick(TourWord),
            Genre = genre,
            Layout = layout,
            Screens = screens,
            Palette = palette,
            Bpm = bpm,
            Width = round ? Range(20f, 26f) : Range(40f, 54f),
            Depth = round ? 0f : Range(15f, 20f),
            DeckHeight = Range(1.8f, 2.4f),
            RoofHeight = Range(16f, 22f),
            Spots = N(genre is Genre.Ballad or Genre.Rock ? 20 : 14),
            Beams = N(genre is Genre.Electronic or Genre.HipHop ? 28 : 18),
            Washes = N(genre is Genre.Ballad or Genre.Pop ? 20 : 14),
            FloorBeams = N(genre == Genre.Electronic ? 16 : 10),
            Blinders = N(genre is Genre.Rock or Genre.Pop ? 10 : 6),
            Strobes = N(genre == Genre.Electronic ? 12 : 6),
            PixelBars = N(genre is Genre.Electronic or Genre.Pop ? 12 : 6),
            // a rock or EDM show burns, a ballad sparkles and rains confetti
            Flames = genre is Genre.Rock or Genre.Electronic or Genre.HipHop ? N(8) : 0,
            Co2Jets = genre is Genre.Ballad ? 0 : N(4),
            ConfettiCannons = 2 + 2 * rng.Next(2),
            Gerbs = genre is Genre.Ballad or Genre.Pop or Genre.Rock ? N(8) : N(4),
            Airbursts = 3,
        };
    }
}
