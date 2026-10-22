namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// What the inference rule knows of a lit approach (#1110, <c>docs/notes/tools/direction-signs.md</c>): where its stop line's
/// middle is (LV95) and the way its traffic drives (degrees from north, clockwise), its car lanes at the line and its street's own
/// lanes, how many different lane moves it has, how important its road is (TLM <c>verkehrsbedeutung</c> level, owner 0 commune,
/// 1 canton, 2 confederation; the class) and how far the nearest motorway, expressway or ramp is (m).
/// </summary>
public sealed record GantryFacts(double E, double N, double Bearing, int Lanes, int StreetLanes, int TurnSets, int Importance, int Owner,
    RoadClass Class, double MotorwayM)
{
    /// <summary>A main road, signed blue (SSV Art. 51: reached mainly by main roads): of some importance, or the canton's or the confederation's.</summary>
    public bool MainRoad => Importance > 0 || Owner > 0 || Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp;

    public const string Header = "e\tn\tbearing\tlanes\tstreet_lanes\tturn_sets\timportance\towner\tclass\tmotorway_m";

    public string ToTsv() => string.Create(CultureInfo.InvariantCulture,
        $"{E:F1}\t{N:F1}\t{Bearing:F0}\t{Lanes}\t{StreetLanes}\t{TurnSets}\t{Importance}\t{Owner}\t{Class}\t{MotorwayM:F0}");

    public static GantryFacts? Parse(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 10 || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double e)) return null;
        double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
        int I(int i) => int.Parse(f[i], CultureInfo.InvariantCulture);
        return new GantryFacts(e, D(1), D(2), I(3), I(4), I(5), I(6), I(7), Enum.Parse<RoadClass>(f[8]), D(9));
    }
}

/// <summary>
/// Where a lit approach gets a gantry with lane signs and a horizontal head in each lane's band (#1110), where no data says (the
/// rest of Switzerland; Geneva's SITG data has them): the user's plan (Oct 22 2026), tuned on Geneva against SITG's real
/// gantries (<c>--gantry-benchmark</c>). An approach of <see cref="MinLanes"/> car lanes or more at its line, or of
/// <see cref="MinTurnLanes"/> or more whose lanes go <see cref="MinTurnSets"/> different ways or more; from a street of
/// <see cref="MinStreetLanes"/> lanes of its own or more; on a main road (signed blue) or within <see cref="MotorwayReach"/> m
/// of a motorway, an expressway or a ramp (where <see cref="NeedsBoth"/>: on a main road and that close).
/// The defaults are the benchmark's best (Oct 22 2026, 109 Geneva tiles, 1,153 lit approaches, 15 with SITG lane signs within
/// 200 m before their line): two lanes or more, from a street of two lanes, on a main road within 300 m of a motorway, expressway
/// or ramp; 4 hits, 11 misses, 8 false placements (F1 0.30), where the user's plan (three lanes, or two going two ways, on a main
/// road or within 1 km of a motorway) placed 220 for 2 hits. The motorway's nearness is what tells them apart: 14 of the 15
/// within 600 m, against 12 % of all lit approaches (the user: "really only used near highway intersections").
/// </summary>
public sealed record GantryRule(int MinLanes = 2, int MinTurnLanes = 99, int MinTurnSets = 2, int MinStreetLanes = 2,
    double MotorwayReach = 300, bool NeedsBoth = true)
{
    public static readonly GantryRule Default = new();

    public bool Wants(GantryFacts f)
    {
        bool lanes = f.Lanes >= MinLanes || f.Lanes >= MinTurnLanes && f.TurnSets >= MinTurnSets;
        if (!lanes || f.StreetLanes < MinStreetLanes) return false;
        bool near = f.MotorwayM <= MotorwayReach;
        return NeedsBoth ? near && f.MainRoad : near || f.MainRoad;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"lanes>={MinLanes} or (>={MinTurnLanes} going {MinTurnSets}+ ways), street lanes>={MinStreetLanes}, {(NeedsBoth ? "main road and" : "main road or")} motorway within {MotorwayReach:F0} m");
}

/// <summary>
/// The text of a Geneva direction sign (#1110, SITG <c>CONTENU</c>): destinations as written, <c>#</c> between the fields of a sign
/// that has several; pictograms named in capitals (<c>AUTOROUTE</c>, <c>AVION</c>, <c>TRAIN</c>) or as SSV numbers
/// (<c>OSR 4.01</c> the motorway, <c>OSR 5.50</c> the airport). Pure.
/// </summary>
public static class SignText
{
    /// <summary>Articles and prepositions that keep a destination going ("Route du lac", "Les Tuileries"), as lower-case words do.</summary>
    private static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
        { "de", "du", "des", "la", "le", "les", "l'", "d'", "sur", "sous", "en", "et", "am", "an", "bei", "im" };

    /// <summary>The motorway symbol: the word or the SSV number.</summary>
    public static bool IsMotorway(string token) => token is "AUTOROUTE" or "4.01" or "4.02";

    /// <summary>
    /// Its destinations, at most <paramref name="max"/> (a lane sign carries three lines at most, SSV Art. 51), and whether it shows
    /// the motorway symbol. A new destination starts at a capitalised word, unless a joiner or a lower-case word comes before it
    /// in the same name; pictograms, SSV numbers and distances ("450 m") are left out.
    /// </summary>
    public static (bool Motorway, List<string> Names) Parse(string? contenu, int max = 3)
    {
        var names = new List<string>();
        bool motorway = false;
        foreach (var field in (contenu ?? "").Split('#'))
        {
            var tokens = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new List<string>();
            void Flush() { if (current.Count > 0) names.Add(string.Join(' ', current)); current.Clear(); }
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t == "OSR" && i + 1 < tokens.Length)
                {
                    if (IsMotorway(tokens[i + 1])) motorway = true;
                    i++;
                    Flush();
                    continue;
                }
                if (IsMotorway(t)) { motorway = true; Flush(); continue; }
                // a pictogram (AVION, TRAIN, BUS, INSCRIPTION LUMINEUSE), a plus between symbols, a number or a distance
                bool pictogram = t.Length >= 3 && t.Any(char.IsLetter) && t.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
                if (pictogram || t == "+" || char.IsDigit(t[0]) || t is "m" or "km")
                {
                    Flush();
                    continue;
                }
                bool capital = char.IsUpper(t[0]);
                // after an article or a preposition, also one ending a hyphenated word ("Les Tuileries", "Genève-La Praille")
                bool joined = current.Count > 0 && (Joiners.Contains(current[^1][(current[^1].LastIndexOf('-') + 1)..]) || current[^1].EndsWith('\''));
                if (capital && !joined) Flush();
                current.Add(t);
            }
            Flush();
        }
        // a destination written twice (a sign naming it on two fields) once
        names = names.Distinct(StringComparer.Ordinal).Take(max).ToList();
        return (motorway, names);
    }
}

/// <summary>
/// How a gantry's signs share its lanes (#1110): a sign over each run of lanes going the same way (the user's photos: a green
/// sign over the left lane to the motorway, a blue one over the two lanes straight on), each lane keeping its own arrow (and
/// head) in the band under it. Pure.
/// </summary>
public static class GantryLayout
{
    /// <summary>A sign is this much narrower than its lanes (m): room between two signs, and past the outer ones.</summary>
    public const double SignMargin = 0.3;
    /// <summary>The widest a lane sign is drawn (m): Geneva's run to 2.5-2.8 m (SITG <c>FORMAT</c>), over two lanes their width but a margin.</summary>
    public const double MaxWidth = 5.6;

    /// <summary>The runs of lanes (left to right, each its middle, width and moves) going the same way: their first and last index.</summary>
    public static List<(int From, int To)> Runs(IReadOnlyList<(double Mid, double Width, SignalMoves Moves)> lanes)
    {
        var runs = new List<(int, int)>();
        for (int i = 0; i < lanes.Count; i++)
        {
            if (runs.Count > 0 && lanes[runs[^1].Item2].Moves == lanes[i].Moves) runs[^1] = (runs[^1].Item1, i);
            else runs.Add((i, i));
        }
        return runs;
    }

    /// <summary>A run's sign: its middle across the arm and its width.</summary>
    public static (double Mid, double Width) Sign(IReadOnlyList<(double Mid, double Width, SignalMoves Moves)> lanes, (int From, int To) run)
    {
        double left = lanes[run.From].Mid - lanes[run.From].Width * 0.5, right = lanes[run.To].Mid + lanes[run.To].Width * 0.5;
        return ((left + right) * 0.5, Math.Min(right - left - SignMargin, MaxWidth));
    }

    /// <summary>The sign's colour from where its lanes lead (SSV Art. 51): green to a motorway, blue along main roads, white otherwise.</summary>
    public static SignColour Colour(bool motorway, bool mainRoad) => motorway ? SignColour.Green : mainRoad ? SignColour.Blue : SignColour.White;
}
