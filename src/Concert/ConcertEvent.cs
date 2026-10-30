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
/// <remarks>Jazz (#1394) is appended, never drawn by <see cref="ConcertPlanner.Plan"/>: only a venue's festival forces it (<see cref="ConcertPlanner.Jazz"/>).</remarks>
public enum Genre { Rock, Pop, Electronic, HipHop, Ballad, Jazz }

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

    /// <summary>A jazz night (#1394): songs in a set before the call for the bis (<see cref="JazzSet"/>), and whether it is the closing night's finale set (the confetti cannons fire).</summary>
    public int SetSongs { get; init; }
    public bool FinaleNight { get; init; }

    /// <summary>The game hours the show plays, [start, end); the stage stands from <see cref="BuildHour"/>.</summary>
    public float StartHour { get; init; } = 20.5f;
    public float EndHour { get; init; } = 23.5f;

    /// <summary>When the stage is up by in the morning (#956): built for the day, struck at <see cref="EndHour"/>.</summary>
    public const float BuildHour = 8f;

    /// <summary>Whether the stage stands at this hour: from the morning's build to the show's end; else the stadium is empty.</summary>
    public bool Standing(double hour) => hour >= BuildHour && hour < EndHour;

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

    /// <summary>Whether the venue has a stage on this date: every night, except a venue with a season (<see cref="StadiumSpec.Season"/>, #1394) outside it.</summary>
    public static bool Open(StadiumSpec stadium, DateTime date) => stadium.InSeason(DateOnly.FromDateTime(date));

    /// <summary>
    /// Tonight's event in a stadium: seeded by its name and the date; a venue with a season (Montreux, #1394) plays its jazz night inside it
    /// (<see cref="Jazz"/>), and an ordinary night outside it (it has no stage then: <see cref="Open"/>).
    /// </summary>
    public static ConcertEvent Tonight(StadiumSpec stadium, DateTime date) =>
        stadium.SeasonAt(DateOnly.FromDateTime(date)) is { } at ? Jazz(stadium, at.Year, at.Day) : Plan(stadium, Seed(stadium.Name, date));

    private static readonly string[] JazzFirst =
    {
        "Clara", "Anselm", "Odile", "Basile", "Ruth", "Gaspard", "Mireille", "Paulo", "Edda", "Lucien", "Noemi", "Victor", "Hanne", "Armand",
    };
    private static readonly string[] JazzLast =
    {
        "Delacroix", "Lindner", "Perrin", "Baumann", "Valentin", "Roche", "Aebi", "Cattin", "Morand", "Zbinden", "Girard", "Sutter",
    };
    private static readonly string[] JazzGroup =
    {
        "Quartet", "Four", "Sextet", "Lakeside Jazz", "Blue Hour Quartet", "Quai Collective", "Night Train Four", "Trio and Friends",
    };
    private static readonly string[] JazzTour =
    {
        "Montreux Sessions", "Lakeside Set", "Blue Hour", "Quai Nights", "Late Set", "Standards and Strangers",
    };

    /// <summary>The set's songs on a festival night: two on an ordinary one, four on the Jazz Night, six on the closing one (<see cref="MontreuxJazzRules.SetSongs"/>).</summary>
    private static int SongsOn(int night) => Occasions.Festivals.MontreuxJazz.MontreuxJazzRules.SetSongs(night);

    /// <summary>
    /// The jazz quartet's night at a venue with a season (#1394): night <paramref name="night"/> (1-based) of the festival that opened in <paramref name="year"/>.
    /// Forced by the festival, never drawn by <see cref="Plan"/>, so the other venues' seeds and the 300-seed check do not move. A small end stage
    /// (a 12 x 7 m deck under a 4.5 m roof, no screens), about 30 fixtures (spots, washes, floor beams, hazers; no strobes, blinders or pyro), amber, deep
    /// blue and magenta, 80 to 150 BPM; the closing night adds two confetti cannons.
    /// </summary>
    public static ConcertEvent Jazz(StadiumSpec stadium, int year, int night)
    {
        int seed = Seed($"{stadium.Name}|montreuxjazz|{year}", new DateTime(2000, 1, 1).AddDays(night));
        var rng = new Random(seed);
        T Pick<T>(T[] from) => from[rng.Next(from.Length)];
        float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        string artist = rng.Next(3) switch
        {
            0 => $"{Pick(JazzFirst)} {Pick(JazzLast)} {Pick(JazzGroup)}",
            1 => $"The {Pick(JazzLast)} {Pick(JazzGroup)}",
            _ => $"{Pick(JazzFirst)} {Pick(JazzLast)} Trio",
        };
        float hue = (float)rng.NextDouble();
        bool closing = night == Occasions.Festivals.MontreuxJazz.MontreuxJazzRules.ClosingNight;
        float across = 2 * stadium.FrontHalfU;
        return new ConcertEvent
        {
            Seed = seed,
            Stadium = stadium,
            Artist = artist,
            Tour = Pick(JazzTour),
            Genre = Genre.Jazz,
            Layout = StageLayout.EndNorth,
            Screens = ScreenSet.None,
            // amber on the soloist, deep blue and a magenta accent over it
            Palette = new[] { new Color(1f, 0.72f, 0.32f), new Color(0.12f, 0.2f, 0.7f), Color.FromHsv(0.86f + 0.04f * hue, 0.75f, 1f) },
            Bpm = Range(80f, 150f),
            Width = Math.Min(12f, across * 0.72f),
            Depth = Occasions.Festivals.MontreuxJazz.MontreuxJazzRules.DeckDepth,
            DeckHeight = Range(1.0f, 1.3f),
            RoofHeight = 4.5f,
            Spots = 6, Beams = 0, Washes = 6, FloorBeams = 4, Blinders = 0, Strobes = 0, PixelBars = 0,
            Flames = 0, Co2Jets = 0, Gerbs = 0, Airbursts = 0,
            ConfettiCannons = closing ? 2 : 0,
            SetSongs = SongsOn(night),
            FinaleNight = closing,
        };
    }

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
        // a venue that always builds its stage in one place (an open air's ground, #948)
        if (stadium.StageAt is { } at)
            layout = at switch
            {
                StadiumSide.North => StageLayout.EndNorth,
                StadiumSide.East => StageLayout.SideEast,
                StadiumSide.West => StageLayout.SideWest,
                _ => StageLayout.EndSouth,
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
        bool round = layout == StageLayout.Center;
        // the stage fits its venue (#948): its width across the floor, its depth a share of the floor's
        // length, its roof under an arena's ceiling; a smaller stage flies a smaller rig
        bool side = layout is StageLayout.SideEast or StageLayout.SideWest;
        float floorW = 2 * stadium.FrontHalfU, floorL = 2 * stadium.FrontHalfV;
        float across = side ? floorL : floorW, along = side ? floorW : floorL;
        // room for the wings and the PA beside it: never more than about seven tenths of the floor
        float width = round ? Math.Min(Range(20f, 26f), Math.Min(floorW, floorL) * 0.45f) : Math.Min(Range(40f, 54f), across * 0.72f);
        float depth = round ? 0f : Math.Min(Range(15f, 20f), along * 0.22f);
        float roof = Range(16f, 22f);
        // under an arena's ceiling, its canopy (2.8 m over the grid) included
        if (stadium.Kind == VenueKind.Arena) roof = Math.Max(4.5f, Math.Min(roof, stadium.RoofInner - stadium.RoofThickness - 4.3f));
        // side screens only where there is room beside the stage for them
        if (!round && across / 2 - width / 2 < 14f)
            screens = screens switch { ScreenSet.Sides => ScreenSet.None, ScreenSet.CenterAndSides => ScreenSet.Center, _ => screens };
        float size = Range(0.8f, 1.2f) * Math.Clamp(width / (round ? 23f : 47f), 0.45f, 1f);
        int N(float n) => Math.Max(2, (int)MathF.Round(n * size / 2) * 2);

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
            Width = width,
            Depth = depth,
            DeckHeight = Range(1.8f, 2.4f),
            RoofHeight = roof,
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
