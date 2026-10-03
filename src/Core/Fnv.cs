namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// FNV-1a over a string's UTF-16 units: the seed every peer derives from a building key. Never
/// <c>string.GetHashCode</c>, which .NET randomises per process. <c>InteriorGenerator.StableHash</c>
/// is this function, so plans, loot and shops all agree.
/// </summary>
public static class Fnv
{
    public static int Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }

    /// <summary>The hash as a fraction in [0, 1): a stable "dice roll" for one question about one key.</summary>
    public static double Unit(string s) => (uint)Hash(s) / 4294967296.0;
}
