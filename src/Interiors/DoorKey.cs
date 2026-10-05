using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>A building, named the way every peer can agree on: its tile and its index in that tile's <c>.bldg</c>.</summary>
public readonly record struct BuildingKey(int TileE, int TileN, int Index)
{
    public TileId Tile => new(TileE, TileN);
    public override string ToString() => $"{TileE}_{TileN}_{Index}";

    public static bool TryParse(string s, out BuildingKey key)
    {
        key = default;
        var p = s.Split('_');
        if (p.Length != 3 || !int.TryParse(p[0], out int e) || !int.TryParse(p[1], out int n)
            || !int.TryParse(p[2], out int i)) return false;
        key = new BuildingKey(e, n, i);
        return true;
    }
}

/// <summary>
/// One door of one building, named the way every peer can agree on (#498): its building plus
/// which of that building's doors it is. <see cref="Slot"/> 0 is the main door, and its name is
/// the plain <see cref="BuildingKey"/> text, so a building with one door is named exactly as it
/// was before buildings had several — on the wire, in stored plans and in saved loot.
/// </summary>
public readonly record struct DoorKey(int TileE, int TileN, int Index, int Slot = 0)
{
    public DoorKey(BuildingKey building, int slot = 0) : this(building.TileE, building.TileN, building.Index, slot) { }

    public TileId Tile => new(TileE, TileN);

    /// <summary>The building the door is on: what a plan, a space and a tile of loot are keyed by.</summary>
    public BuildingKey Building => new(TileE, TileN, Index);

    public override string ToString() => Slot == 0 ? Building.ToString() : $"{TileE}_{TileN}_{Index}_{Slot}";

    public static bool TryParse(string s, out DoorKey key)
    {
        key = default;
        var p = s.Split('_');
        if (p.Length is not (3 or 4) || !int.TryParse(p[0], out int e) || !int.TryParse(p[1], out int n)
            || !int.TryParse(p[2], out int i)) return false;
        int slot = 0;
        if (p.Length == 4 && !int.TryParse(p[3], out slot)) return false;
        key = new DoorKey(e, n, i, slot);
        return true;
    }
}
