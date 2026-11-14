namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (docs/notes/core/earth-stores.md).

/// <summary>
/// Where the stores live under <c>--frame earth</c> (#1726, G2 D2): beside the flat ones, under <c>user://earth/</c>, so a
/// rollback to <c>--frame flat</c> before D3 still finds the flat files untouched, and the next flip reads the earth ones
/// again (the plan's section 2). A store keyed by an LV95 tile or a flat building key is written here in its new keys:
/// converted from the flat file on first load where a player owns it, started empty where it was generated per tile.
/// The game asks through <c>WorldChoice.StorePath</c>, which picks by the frame and maps a test world's saves.
/// </summary>
public static class EarthStores
{
    private const string User = "user://";

    /// <summary>The folder the earth stores sit under, inside <c>user://</c>.</summary>
    public const string Folder = "earth";

    /// <summary>
    /// A store's earth path: <c>user://loot</c> as <c>user://earth/loot</c>. A path outside <c>user://</c> (a check's own
    /// <c>--farmdir</c>) is the caller's and stays as it is.
    /// </summary>
    public static string Earth(string path) =>
        path.StartsWith(User, StringComparison.Ordinal) && !path.StartsWith(User + Folder + "/", StringComparison.Ordinal)
            ? $"{User}{Folder}/{path[User.Length..]}"
            : path;

    /// <summary>The path for the frame being played: <see cref="Earth"/> under earth, the flat one as it was under flat.</summary>
    public static string For(string path, bool earth) => earth ? Earth(path) : path;

    /// <summary>
    /// A building's name in an earth store (#1726): the Earth identity a stand-in key is aliased to (C5's
    /// <c>BuildingId</c>, <see cref="Fnv.Alias"/>), so a store keeps a house's state across a leaf rebuild that renumbers
    /// its buildings; the key itself when no alias is known (a flat building, a match's own key).
    /// </summary>
    public static string Building(string key) => Fnv.NameOf(key) ?? key;
}
