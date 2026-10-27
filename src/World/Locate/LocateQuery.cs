using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Locate;

/// <summary>What <c>/locate</c> looks for (#1277): one noun of <see cref="LocateVocabulary"/>.</summary>
public enum LocateTarget : byte
{
    None = 0,
    // places and the survey's points of interest
    Town, Peak, Pass, SummitCross, Waterfall, Lake, Tower, Church, Chapel, Castle, Bridge, Dam, Station, BusStop,
    Harbour, CableCar, Hut,
    // OSM venues
    Cinema, Bowling, Arcade, Restaurant, Cafe, Bar, Pub, Nightclub, BeerGarden, FastFood,
    // hand-written lists and live things
    Stadium, Arena, OpenAir, Ikea, Airport, Steamer,
    // the server's building index
    Building, House, Apartments, Commercial, Industrial, Farm, Civic, Garage, Construction, Bank,
}

/// <summary>Stackable selectors: <c>+ spire</c>, <c>+ parking entrance</c>, <c>tall</c>, <c>old</c>...</summary>
[Flags]
public enum LocateModifier : byte
{
    None = 0,
    /// <summary>A church whose tower rises above its hall (<c>BuildingTypes</c>' tower groups).</summary>
    Spire = 1,
    /// <summary>An underground garage door, as the game builds it (<c>GarageRule</c>, then the real footprint).</summary>
    Parking = 2,
    /// <summary>At least <see cref="LocateVocabulary.TallHeight"/> from base to roof.</summary>
    Tall = 4,
    /// <summary>Built before <see cref="LocateVocabulary.OldBefore"/>.</summary>
    Old = 8,
    /// <summary>Built in <see cref="LocateVocabulary.NewFrom"/> or later.</summary>
    New = 16,
}

/// <summary>A parsed <c>/locate</c>: the noun, its selectors and where to search from.</summary>
public sealed record LocateQuery(LocateTarget Target, LocateModifier Modifiers, int MinFloors = 0, float MinHeight = 0, string? Town = null)
{
    /// <summary>Whether the noun is answered from the building index (and so takes building selectors).</summary>
    public bool OnBuildings => LocateVocabulary.IsBuilding(Target);

    public bool Has(LocateModifier m) => (Modifiers & m) == m;

    /// <summary>"church with a spire, 6+ floors": what was understood, for the answer's heading.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Has(LocateModifier.Tall)) parts.Add("tall");
        if (Has(LocateModifier.Old)) parts.Add("old");
        if (Has(LocateModifier.New)) parts.Add("new");
        parts.Add(LocateVocabulary.Label(Target));
        var with = new List<string>();
        if (Has(LocateModifier.Spire)) with.Add("a spire");
        if (Has(LocateModifier.Parking)) with.Add("a parking entrance");
        if (MinFloors > 0) with.Add($"{MinFloors}+ floors");
        if (MinHeight > 0) with.Add($"{MinHeight.ToString("0", CultureInfo.InvariantCulture)} m+");
        string s = string.Join(' ', parts);
        if (with.Count > 0) s += " with " + string.Join(" and ", with);
        return s;
    }
}

/// <summary>
/// The words <c>/locate</c> understands (#1277, English only, decided), and the parser. Pure C#, tier 0
/// (<c>LocateTests</c>): the completion offers <see cref="Nouns"/> and <see cref="ModifierWords"/>, so a
/// new word goes in the table and shows up in Tab, the pad's buttons and <c>/help locate</c> by itself.
/// </summary>
public static class LocateVocabulary
{
    public const float TallHeight = 30f;
    public const int OldBefore = 1900, NewFrom = 2010;

    /// <summary>Every noun: its words (the first is the one offered), what it finds, how one is called.</summary>
    private static readonly (string[] Words, LocateTarget Target, string Label)[] Table =
    [
        (["town", "village", "city", "place"], LocateTarget.Town, "town"),
        (["peak", "summit", "mountain"], LocateTarget.Peak, "peak"),
        (["pass", "mountain pass"], LocateTarget.Pass, "pass"),
        (["summit cross", "cross"], LocateTarget.SummitCross, "summit cross"),
        (["waterfall", "falls"], LocateTarget.Waterfall, "waterfall"),
        (["lake", "pond"], LocateTarget.Lake, "lake"),
        (["tower", "lookout tower", "lookout"], LocateTarget.Tower, "tower"),
        (["church"], LocateTarget.Church, "church"),
        (["chapel"], LocateTarget.Chapel, "chapel"),
        (["castle", "chateau", "fortress", "manor", "ruin"], LocateTarget.Castle, "castle"),
        (["covered bridge", "bridge"], LocateTarget.Bridge, "covered bridge"),
        (["dam"], LocateTarget.Dam, "dam"),
        (["station", "train station", "railway station"], LocateTarget.Station, "station"),
        (["bus stop", "stop"], LocateTarget.BusStop, "bus stop"),
        (["harbour", "harbor", "port", "pier", "jetty", "landing", "boat landing"], LocateTarget.Harbour, "harbour"),
        (["cable car", "gondola", "aerial tramway"], LocateTarget.CableCar, "cable car"),
        (["hut", "mountain hut", "cabin"], LocateTarget.Hut, "hut"),
        (["cinema", "movie theater", "movie theatre", "movies"], LocateTarget.Cinema, "cinema"),
        (["bowling", "bowling alley", "bowling centre", "bowling center"], LocateTarget.Bowling, "bowling centre"),
        (["arcade", "amusement arcade", "game arcade"], LocateTarget.Arcade, "arcade"),
        (["restaurant"], LocateTarget.Restaurant, "restaurant"),
        (["cafe", "coffee", "tea room"], LocateTarget.Cafe, "café"),
        (["bar"], LocateTarget.Bar, "bar"),
        (["pub"], LocateTarget.Pub, "pub"),
        (["nightclub", "club", "disco"], LocateTarget.Nightclub, "nightclub"),
        (["beer garden", "biergarten"], LocateTarget.BeerGarden, "beer garden"),
        (["fast food", "kebab", "burger", "takeaway"], LocateTarget.FastFood, "fast food"),
        (["stadium"], LocateTarget.Stadium, "stadium"),
        (["arena"], LocateTarget.Arena, "arena"),
        (["open air", "openair", "festival"], LocateTarget.OpenAir, "open air"),
        (["ikea"], LocateTarget.Ikea, "IKEA"),
        (["airport", "airfield"], LocateTarget.Airport, "airport"),
        (["steamer", "paddle steamer", "steamboat"], LocateTarget.Steamer, "steamer"),
        (["building"], LocateTarget.Building, "building"),
        (["house", "home"], LocateTarget.House, "house"),
        (["apartment building", "apartment block", "apartments", "apartment", "block of flats", "flats", "appartement building"],
            LocateTarget.Apartments, "apartment building"),
        (["commercial building", "office", "offices", "shop", "store", "hotel"], LocateTarget.Commercial, "commercial building"),
        (["factory", "warehouse", "industrial building", "works"], LocateTarget.Industrial, "industrial building"),
        (["farm", "barn", "farmhouse"], LocateTarget.Farm, "farm building"),
        (["school", "hospital", "public building", "civic building"], LocateTarget.Civic, "public building"),
        (["garage"], LocateTarget.Garage, "garage"),
        (["construction site", "construction zone", "building site", "construction"], LocateTarget.Construction, "construction site"),
        (["bank"], LocateTarget.Bank, "bank"),
    ];

    private static readonly (string[] Words, LocateModifier Modifier)[] Modifiers =
    [
        (["spire", "steeple", "bell tower", "tower"], LocateModifier.Spire),
        (["parking entrance", "garage entrance", "underground garage", "car park", "parking"], LocateModifier.Parking),
        (["tall", "high", "highrise", "high rise", "tower block"], LocateModifier.Tall),
        (["old", "historic"], LocateModifier.Old),
        (["new", "modern"], LocateModifier.New),
    ];

    /// <summary>Words joining selectors: dropped wherever they stand.</summary>
    private static readonly HashSet<string> Fillers = ["+", "with", "and", "having", "a", "an", "the", "nearest", "closest", "near", "me"];

    /// <summary>The nouns offered by completion, in table order.</summary>
    public static IEnumerable<string> Nouns => Table.Select(t => t.Words[0]);

    /// <summary>The selectors offered after a noun, as typed after a <c>+</c>.</summary>
    public static readonly string[] ModifierWords = ["spire", "parking entrance", "tall", "old", "new", "6 floors", "in"];

    public static string Label(LocateTarget t) => Table.FirstOrDefault(r => r.Target == t).Label ?? t.ToString().ToLowerInvariant();

    public static bool IsBuilding(LocateTarget t) => t >= LocateTarget.Building;

    /// <summary>Which index kind a building noun stands for; null for "any building" and the nouns that are not kinds.</summary>
    public static BuildingKind? KindOf(LocateTarget t) => t switch
    {
        LocateTarget.House => BuildingKind.House,
        LocateTarget.Apartments => BuildingKind.Apartment,
        LocateTarget.Commercial => BuildingKind.Commercial,
        LocateTarget.Industrial => BuildingKind.Industrial,
        LocateTarget.Farm => BuildingKind.Agricultural,
        LocateTarget.Civic => BuildingKind.Civic,
        LocateTarget.Garage => BuildingKind.Garage,
        LocateTarget.Construction => BuildingKind.UnderConstruction,
        _ => null,
    };

    /// <summary>
    /// Parses what follows <c>/locate</c>: one noun, any selectors before or after it (<c>+</c>, <c>with</c>
    /// and <c>and</c> join them), an optional <c>in &lt;town&gt;</c> at the end. Null with a reason when it
    /// cannot be read.
    /// </summary>
    public static LocateQuery? Parse(string text, out string error)
    {
        error = "";
        string norm = PlaceIndex.Normalize(text).Replace("+", " + ").Replace(",", " ");
        string? town = null;
        // "in <town>" ends the line: everything after it is the town's name, spaces and all
        var all = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int inAt = all.IndexOf("in");
        if (inAt >= 0)
        {
            town = string.Join(' ', all.Skip(inAt + 1));
            all = all.Take(inAt).ToList();
            if (town.Length == 0) { error = "'in' needs a town: /locate castle in Gruyeres"; return null; }
        }
        var words = all.Where(w => !Fillers.Contains(w)).ToList();
        if (words.Count == 0) { error = Usage; return null; }

        var target = LocateTarget.None;
        var mods = LocateModifier.None;
        int floors = 0;
        float height = 0;
        var unknown = new List<string>();
        for (int i = 0; i < words.Count;)
        {
            // "<n> floors", "<n>+ floors", "<n> storeys"; "<n> m", "<n>m"
            if (TryNumber(words[i], out int n, out bool metres))
            {
                if (metres) { height = n; i++; continue; }
                if (i + 1 < words.Count && words[i + 1] is "floors" or "floor" or "storeys" or "stories" or "storey" or "levels")
                {
                    floors = n;
                    i += 2;
                    continue;
                }
                if (i + 1 < words.Count && words[i + 1] == "m")
                {
                    height = n;
                    i += 2;
                    continue;
                }
                unknown.Add(words[i]);
                i++;
                continue;
            }

            // the longest phrase first: "bell tower" before "tower", "apartment building" before "building"
            int took = 0;
            LocateTarget noun = LocateTarget.None;
            LocateModifier mod = LocateModifier.None;
            for (int len = Math.Min(3, words.Count - i); len >= 1 && took == 0; len--)
            {
                string phrase = string.Join(' ', words.Skip(i).Take(len));
                // a noun's word while there is no noun yet; a selector's once there is one ("church + tower")
                LocateTarget n2 = NounOf(phrase);
                LocateModifier m2 = ModifierOf(phrase);
                if (n2 != LocateTarget.None && (target == LocateTarget.None || m2 == LocateModifier.None)) { noun = n2; took = len; }
                else if (m2 != LocateModifier.None) { mod = m2; took = len; }
            }
            if (took == 0) { unknown.Add(words[i]); i++; continue; }
            i += took;
            if (mod != LocateModifier.None) { mods |= mod; continue; }
            if (target != LocateTarget.None && target != noun)
            {
                // "church building": the building noun only says it is a building
                if (noun == LocateTarget.Building && IsBuilding(target)) continue;
                if (target == LocateTarget.Building && IsBuilding(noun)) { target = noun; continue; }
                error = $"One thing at a time: '{Label(target)}' or '{Label(noun)}'?";
                return null;
            }
            target = noun;
        }

        if (unknown.Count > 0)
        {
            error = $"Don't know '{string.Join(' ', unknown)}'. Try: {string.Join(", ", Nouns.Take(12))}... (Tab lists them all)";
            return null;
        }
        // selectors with no noun are about buildings: "/locate tall" or "/locate + parking entrance"
        if (target == LocateTarget.None)
        {
            if (mods == LocateModifier.None && floors == 0 && height == 0) { error = Usage; return null; }
            target = (mods & LocateModifier.Spire) != 0 ? LocateTarget.Church : LocateTarget.Building;
        }
        if (mods.HasFlag(LocateModifier.Old) && mods.HasFlag(LocateModifier.New)) { error = "Old or new, not both."; return null; }
        bool buildingMods = mods != LocateModifier.None || floors > 0 || height > 0;
        if (buildingMods && !IsBuilding(target) && target != LocateTarget.Church)
        {
            error = $"Selectors like spire, parking entrance, tall, old or floors are for buildings, not a {Label(target)}.";
            return null;
        }
        if (mods.HasFlag(LocateModifier.Spire) && target is not (LocateTarget.Church or LocateTarget.Building))
        {
            error = "Only churches have spires here: /locate church + spire";
            return null;
        }
        return new LocateQuery(target, mods, floors, height, town);
    }

    public const string Usage = "Usage: /locate <thing> [+ selector ...] [in <town>], e.g. /locate church + spire, /locate apartment building + parking entrance, /locate castle in Gruyeres";

    private static LocateTarget NounOf(string phrase)
    {
        foreach (var (words, target, _) in Table)
            foreach (string w in words)
                if (w == phrase || w + "s" == phrase || w + "es" == phrase || (w.EndsWith('y') && w[..^1] + "ies" == phrase))
                    return target;
        return LocateTarget.None;
    }

    private static LocateModifier ModifierOf(string phrase)
    {
        foreach (var (words, mod) in Modifiers)
            foreach (string w in words)
                if (w == phrase || w + "s" == phrase) return mod;
        return LocateModifier.None;
    }

    /// <summary>"6", "6+", "40m": a whole number, and whether it was metres.</summary>
    private static bool TryNumber(string word, out int n, out bool metres)
    {
        metres = word.EndsWith('m');
        string digits = word.TrimEnd('m', '+');
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0 && n < 1000;
    }
}
