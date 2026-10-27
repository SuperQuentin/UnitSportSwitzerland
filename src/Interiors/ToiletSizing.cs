namespace UnitSport.Interiors;

/// <summary>Which toilets a public building has (#1285), from the most space to the least.</summary>
public enum ToiletSet
{
    None,
    /// <summary>Really no space: one shared room with its own door.</summary>
    Single,
    /// <summary>Less space: one anteroom with the basin, a women's and a men's stall off it.</summary>
    Shared,
    /// <summary>Plenty of space: a women's and a men's room each behind its own anteroom, an accessible WC in a big building.</summary>
    Gendered,
}

/// <summary>
/// The sizes and counts of public toilets (#1285), as pure rules the planner (<c>InteriorGenerator.Toilets</c>)
/// reads: which set a strip of a building fits, how long a piece of the host wall it takes, how many
/// stalls, urinals and basins. Numbers and sources in <c>docs/notes/terrain/public-toilets.md</c>. No
/// Godot: tier 0 (<c>ToiletSizingTests</c>).
/// </summary>
public static class ToiletSizing
{
    /// <summary>A stall: its width between the walls' lines (0.88 m clear inside the 6 cm insets), at most, and its depth, m.</summary>
    public const float StallMinW = 1.0f, StallMaxW = 1.4f, StallD = 1.6f;
    /// <summary>A stall's door: an inswing leaf 0.58-0.66 m [S], here wide enough for the player's 0.64 m capsule.</summary>
    public const float StallDoor = 0.7f;
    /// <summary>The way in to a toilet room or an anteroom, m.</summary>
    public const float Door = 0.9f;
    /// <summary>An anteroom: the basins along one wall and a 1.1 m way past them.</summary>
    public const float AnteW = 1.6f;
    /// <summary>The aisle in front of a row of stalls, m.</summary>
    public const float Aisle = 1.2f;
    /// <summary>Urinal centres [S: 0.6 m with screens, 0.7-0.75 comfortable] and where the row starts off the anteroom's wall.</summary>
    public const float UrinalPitch = 0.7f, UrinalStart = 0.25f;
    /// <summary>A basin's share of the counter, m.</summary>
    public const float BasinPitch = 0.65f;
    /// <summary>The accessible WC (SIA 500): 1.65 x 2.30 m with its door swinging in, here 1.8 m wide.</summary>
    public const float AccessW = 1.8f, AccessD = 2.3f;
    /// <summary>The single WC: a bowl, a basin beside it, a 0.8 m leaf swinging in clear of the bowl.</summary>
    public const float SingleW = 1.4f, SingleD = 1.7f;
    /// <summary>The shared set: two stalls of 1.2 m off an anteroom at least 1.3 m deep.</summary>
    public const float SharedW = 2.4f, SharedD = StallD + 1.3f;
    /// <summary>The gendered set needs this depth: two stalls along a side wall, the anteroom's turn.</summary>
    public const float GenderedD = 2.8f;
    /// <summary>The women's washroom: the stalls along its far wall and the aisle before them.</summary>
    public const float WomenWashW = StallD + Aisle;
    /// <summary>A gendered set from this many people [S: Zurich from 50 seats, Fribourg from ~30]; an accessible WC from this many.</summary>
    public const int GenderedFrom = 30, AccessibleFrom = 60;
    /// <summary>A shared anteroom from this many people; fewer have the single WC.</summary>
    public const int SharedFrom = 12;

    /// <summary>The men's washroom for <paramref name="urinals"/>: the stalls, the doorway clear before the first, the row of urinals.</summary>
    public static float MenWashW(int urinals) => StallD + 0.6f + UrinalStart + urinals * UrinalPitch;

    /// <summary>
    /// What a set holds, and how far along the host wall it reaches (<see cref="Length"/>). Stall counts are
    /// what the people want, as many as the strip's depth takes in a row (<see cref="StallRow"/> lays them).
    /// </summary>
    public readonly record struct Plan(ToiletSet Set, float Length, int WomenStalls, int MenStalls, int Urinals, bool Accessible)
    {
        public static readonly Plan None = new(ToiletSet.None, 0, 0, 0, 0, false);
    }

    /// <summary>What the people of a building want [E]: women's stalls, men's stalls, urinals.</summary>
    public static (int Women, int Men, int Urinals) Want(int people) =>
        (Math.Clamp((people + 24) / 25, 2, 8), Math.Clamp((people + 79) / 80, 1, 4), Math.Clamp((people + 39) / 40, 1, 8));

    /// <summary>
    /// The biggest set a strip <paramref name="length"/> m along its host wall and <paramref name="depth"/> m
    /// deep holds, for <paramref name="people"/>: gendered, shared, single, or none.
    /// </summary>
    public static Plan Choose(float length, float depth, int people)
    {
        const float eps = 1e-3f;
        float gendered = 2 * AnteW + WomenWashW + MenWashW(1);
        if (people >= GenderedFrom && depth >= GenderedD - eps && length >= gendered - eps)
        {
            var want = Want(people);
            int fit = (int)((depth + eps) / StallMinW);
            int women = Math.Clamp(want.Women, 1, Math.Max(1, fit)), men = Math.Clamp(want.Men, 1, Math.Max(1, fit));
            bool access = people >= AccessibleFrom && depth >= AccessD - eps && length >= gendered + AccessW - eps;
            float left = length - 2 * AnteW - WomenWashW - MenWashW(0) - (access ? AccessW : 0);
            int urinals = Math.Clamp(want.Urinals, 1, Math.Max(1, (int)((left + eps) / UrinalPitch)));
            return new Plan(ToiletSet.Gendered, 2 * AnteW + WomenWashW + MenWashW(urinals) + (access ? AccessW : 0),
                women, men, urinals, access);
        }
        if (people >= SharedFrom && depth >= SharedD - eps && length >= SharedW - eps)
            return new Plan(ToiletSet.Shared, SharedW, 1, 1, 0, false);
        if (depth >= SingleD - eps && length >= SingleW - eps)
            return new Plan(ToiletSet.Single, SingleW, 0, 0, 0, false);
        return Plan.None;
    }

    /// <summary>
    /// A row of <paramref name="n"/> stalls along a wall <paramref name="row"/> m long: each stall's width (at most
    /// <see cref="StallMaxW"/>, unless the rest is too short to be anything else) and what is left past them, a
    /// cleaner's cupboard when it is a metre or more, 0 when the stalls take it all.
    /// </summary>
    public static (float Width, float Left) StallRow(int n, float row)
    {
        n = Math.Max(1, n);
        float w = Math.Max(StallMinW, Math.Min(row / n, StallMaxW)), rest = row - n * w;
        return rest >= StallMinW ? (w, rest) : (row / n, 0f);
    }

    /// <summary>Basins on an anteroom's wall <paramref name="wall"/> m long, for the stalls and urinals behind it: one per two, at least one.</summary>
    public static int Basins(int fixtures, float wall) => Math.Clamp((fixtures + 1) / 2, 1, Math.Max(1, (int)(wall / BasinPitch)));
}
