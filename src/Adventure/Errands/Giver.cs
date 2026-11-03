using System;
using System.Collections.Generic;
using System.Text;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>Who asks for errands (#1267 part 1). The byte is never stored in a save, but the order is kept (the givers file uses it).</summary>
public enum GiverKind : byte { HutWarden = 0, StationMaster = 1, ShipMaster = 2, Farmer = 3, Senn = 4, Dairy = 5, Postmaster = 6, Host = 7 }

/// <summary>
/// A giver: a server-known figure at a real place. <see cref="E"/>/<see cref="N"/> is where it stands (LV95 metres, the
/// outside point of its door), <see cref="Heading"/> the way it faces (radians, 0 = north, clockwise).
/// </summary>
public sealed record Giver(string Id, GiverKind Kind, double E, double N, float Heading, string Canton, float Elevation, string Name, string Source)
{
    /// <summary>The 5 km cell holding it.</summary>
    public (int E, int N) Cell => Postkarte.CellOf(E, N);
}

/// <summary>A stable string hash (FNV-1a, 64 bits) and the unit floats made from it: the same on every peer and every run.</summary>
public static class ErrandHash
{
    public static ulong Of(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return Mix(h);
    }

    public static ulong Mix(ulong h)
    {
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return h;
    }

    public static ulong Of(params long[] parts)
    {
        ulong h = 0x9E3779B97F4A7C15UL;
        foreach (long p in parts) h = Mix(h ^ (ulong)p) + 0x9E3779B97F4A7C15UL;
        return Mix(h);
    }

    /// <summary>0 &lt;= x &lt; 1.</summary>
    public static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

    public static string Hex(ulong h) => (h & 0xFFFFFFFFFFUL).ToString("x10");
}

/// <summary>A giver's name by language region (de, fr, it, rm): the same id always gives the same person.</summary>
public static class GiverNames
{
    private static readonly string[] De = { "Brügger", "Müller", "Gerber", "Zbinden", "Imhof", "Steiner", "Baumann", "Frei", "Wyss", "Inäbnit", "Lüthi", "Kaufmann" };
    private static readonly string[] Fr = { "Rossier", "Dubois", "Favre", "Pittet", "Berset", "Monnard", "Genoud", "Bovet", "Overney", "Crettaz", "Vuilleumier", "Python" };
    private static readonly string[] It = { "Bernasconi", "Ferrari", "Rezzonico", "Pedrazzini", "Zanetti", "Lepori", "Gianini", "Delcò", "Pellegrini", "Tognola" };
    private static readonly string[] Rm = { "Cathomen", "Caduff", "Decurtins", "Janett", "Condrau", "Tuor", "Deplazes", "Cadonau" };

    /// <summary>"de", "fr", "it" or "rm" for a canton (a few Valais and Fribourg givers speak the other language, by id).</summary>
    public static string Language(string canton, ulong hash) => canton switch
    {
        "GE" or "VD" or "NE" or "JU" => "fr",
        "FR" or "VS" => hash % 3 == 0 ? "de" : "fr",
        "TI" => "it",
        "GR" => hash % 4 == 0 ? "rm" : "de",
        _ => "de",
    };

    public static string Surname(string id, string canton)
    {
        ulong h = ErrandHash.Of(id);
        var list = Language(canton, h) switch { "fr" => Fr, "it" => It, "rm" => Rm, _ => De };
        return list[(int)((h >> 8) % (ulong)list.Length)];
    }

    public static string Role(GiverKind kind, string language) => (kind, language) switch
    {
        (GiverKind.HutWarden, "fr") => "gardienne de cabane",
        (GiverKind.HutWarden, "it") => "custode",
        (GiverKind.HutWarden, _) => "hut warden",
        (GiverKind.StationMaster, "fr") => "chef de gare",
        (GiverKind.StationMaster, "it") => "capostazione",
        (GiverKind.StationMaster, _) => "station master",
        (GiverKind.ShipMaster, "fr") => "capitaine",
        (GiverKind.ShipMaster, "it") => "capitano",
        (GiverKind.ShipMaster, _) => "ship master",
        (GiverKind.Farmer, "fr") => "paysan",
        (GiverKind.Farmer, "it") => "contadino",
        (GiverKind.Farmer, _) => "farmer",
        (GiverKind.Senn, "fr") => "fromager d'alpage",
        (GiverKind.Senn, "it") => "casaro",
        (GiverKind.Senn, _) => "Senn",
        (GiverKind.Dairy, "fr") => "fromager",
        (GiverKind.Dairy, "it") => "casaro",
        (GiverKind.Dairy, _) => "dairyman",
        (GiverKind.Postmaster, "fr") => "buraliste",
        (GiverKind.Postmaster, "it") => "ufficio postale",
        (GiverKind.Postmaster, _) => "postmaster",
        (GiverKind.Host, "fr") => "patron",
        (GiverKind.Host, "it") => "oste",
        _ => "host",
    };

    /// <summary>"Frau Brügger, hut warden" / "M. Rossier, fromager".</summary>
    public static string For(string id, GiverKind kind, string canton)
    {
        ulong h = ErrandHash.Of(id);
        string lang = Language(canton, h);
        string title = lang switch { "fr" => (h & 1) == 0 ? "M." : "Mme", "it" => (h & 1) == 0 ? "Sig." : "Sig.ra", _ => (h & 1) == 0 ? "Herr" : "Frau" };
        return new StringBuilder(title).Append(' ').Append(Surname(id, canton)).Append(", ").Append(Role(kind, lang)).ToString();
    }

    /// <summary>The farewell word in the giver's language.</summary>
    public static string Farewell(string canton, string id) => Language(canton, ErrandHash.Of(id)) switch
    {
        "fr" => "Au revoir", "it" => "Arrivederci", "rm" => "A revair", _ => "Uf Widerluege",
    };

    public static string Greeting(string canton, string id) => Language(canton, ErrandHash.Of(id)) switch
    {
        "fr" => "Bonjour", "it" => "Buongiorno", "rm" => "Bun di", _ => "Grüezi",
    };
}
