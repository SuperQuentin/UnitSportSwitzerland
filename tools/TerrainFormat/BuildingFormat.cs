namespace UnitSport.Terrain.Format;

/// <summary>
/// Building kind, from swissBUILDINGS3D <c>OBJEKTART</c> combined with the GWR building
/// category (<c>GKAT</c>) where a cadastre match exists.
/// </summary>
public enum BuildingKind : byte
{
    House = 0,        // GKLAS 1110/1121 — one or two dwellings
    Apartment = 1,    // 1122/1130/1275 — three or more dwellings, communal housing
    Commercial = 2,   // 1211-1231 — hotels, offices, retail, restaurants
    Industrial = 3,   // 1241/1251/1252 — works, silos, warehouses
    Agricultural = 4, // 1271/1276/1277/1278 — barns, animal sheds, greenhouses
    Sacral = 5,       // 1272 — churches, chapels
    Civic = 6,        // 1261-1265/1273 — schools, hospitals, sport, monuments
    Annex = 7,        // 1242/1274 — garages and other minor structures
    UnderConstruction = 8,
    Other = 9,
}

/// <summary>
/// One building: a triangle soup in tile-local metres (X east, Y altitude, Z south),
/// plus the cadastre facts we chose to keep. Vertices are not indexed — the source is
/// already a TIN with per-face vertices, so indexing would save little.
/// </summary>
public sealed class Building
{
    public BuildingKind Kind { get; init; }
    public uint Egid { get; init; }        // 0 when no cadastre match
    public ushort YearBuilt { get; init; } // 0 when unknown
    public byte Floors { get; init; }      // 0 when unknown
    public float MinY { get; init; }
    public float MaxY { get; init; }
    public required float[] Triangles { get; init; } // 9 floats per triangle

    public int TriangleCount => Triangles.Length / 9;
}

public sealed class BuildingTile
{
    public TileId Id { get; init; }
    public required List<Building> Buildings { get; init; }
}

public static class BuildingFormat
{
    /// <summary>"USBD" little-endian.</summary>
    public const uint Magic = 0x44425355;

    public const ushort Version = 1;
    public const int HeaderSize = 24;

    public static string FileName(TileId id) => $"buildings_{id.E}_{id.N}.bldg";

    /// <summary>
    /// Classifies a building from the swissBUILDINGS3D object type and the GWR building
    /// *class* (GKLAS). Note GKLAS, not GKAT: the category field only says whether a
    /// building is residential at all, so using it would label every village house as an
    /// apartment block. Dwelling count is the fallback when GKLAS is missing.
    /// </summary>
    public static BuildingKind Classify(string? objektart, int? gklas, int? dwellings)
    {
        switch (objektart)
        {
            case "Im Bau": return BuildingKind.UnderConstruction;
            case "Kapelle" or "Sakrales Gebaeude": return BuildingKind.Sacral;
            case "Lagertank": return BuildingKind.Industrial;
            case "Treibhaus": return BuildingKind.Agricultural;
        }

        return gklas switch
        {
            1110 or 1121 => BuildingKind.House,
            1122 or 1130 or 1275 => BuildingKind.Apartment,
            1211 or 1212 or 1220 or 1230 or 1231 => BuildingKind.Commercial,
            1241 or 1251 or 1252 => BuildingKind.Industrial,
            1261 or 1262 or 1263 or 1264 or 1265 or 1273 => BuildingKind.Civic,
            1271 or 1276 or 1277 or 1278 => BuildingKind.Agricultural,
            1272 => BuildingKind.Sacral,
            1242 or 1274 => BuildingKind.Annex,
            _ => dwellings switch
            {
                > 2 => BuildingKind.Apartment,
                > 0 => BuildingKind.House,
                _ => BuildingKind.Other,
            },
        };
    }
}
