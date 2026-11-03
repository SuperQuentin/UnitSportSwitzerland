using Godot;
using UnitSport.Terrain.Format;

// Plain C# over Godot maths: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>The two Swiss breeds the herds are drawn as.</summary>
public enum CowBreed : byte
{
    /// <summary>Braunvieh: grey-brown all over, a pale muzzle, dark hooves.</summary>
    Brown = 0,
    /// <summary>Simmental: red-brown patches on white, a white head.</summary>
    Simmental = 1,
}

/// <summary>
/// One herd (#863): where its cows graze, how many, which breed. <see cref="Spots"/> are pasture
/// points in metres east and north of the anchor (<see cref="E"/>, <see cref="N"/>, LV95), each
/// well inside the field; a cow walks between them (<see cref="CowMotion"/>). <see cref="Tile"/> and
/// <see cref="Index"/> name the herd on every peer (the server's and a client's plans agree, as
/// both build them from the same field tile).
/// </summary>
public sealed record Herd(TileId Tile, int Index, double E, double N, Vector2[] Spots, CowBreed Breed, int Count, uint Seed)
{
    /// <summary>Herds the trailer director stages are not on a field tile: they never cross the wire.</summary>
    public bool Staged => Index < 0;
}

/// <summary>
/// Where the herds stand on one tile (#863): on every grazed pasture of the federal land-use data
/// (<see cref="CropKind.Pasture"/>), and on one meadow in four (a Swiss meadow is grazed after its
/// cuts). Pure and hashed from the field ids and cells, never from a shared random stream, so a
/// tile that unloads and comes back has the same herds and the server's match every client's.
/// </summary>
public static class HerdPlan
{
    /// <summary>A pasture smaller than this (cells of 16 m²) gets no herd: 0.4 ha.</summary>
    public const int MinCells = 250;
    /// <summary>A herd per this many cells of pasture (1.9 ha), at most <see cref="MaxHerds"/> a field.</summary>
    public const int CellsPerHerd = 1200;
    public const int MaxHerds = 4;
    /// <summary>A herd's spots lie within this of its centre, m.</summary>
    public const float Spread = 28f;
    public const int MaxSpots = 32, MinSpots = 6;

    /// <summary>The herds of one tile, in field order. An empty tile gives none.</summary>
    public static List<Herd> For(FieldTile tile)
    {
        var herds = new List<Herd>();
        int fields = tile.Crops.Length;
        if (fields == 0) return herds;

        // the cells of every grazed field, in cell order (one pass over the owner map)
        var cellsOf = new List<int>?[fields];
        for (int f = 0; f < fields; f++)
            if (Grazed(tile.Crops[f], tile.FieldIds[f])) cellsOf[f] = new List<int>();
        var owner = tile.Owner;
        for (int c = 0; c < owner.Length; c++)
            if (owner[c] > 0 && cellsOf[owner[c] - 1] is { } list) list.Add(c);

        for (int f = 0; f < fields; f++)
        {
            if (cellsOf[f] is not { Count: >= MinCells } cells) continue;
            ushort own = (ushort)(f + 1);
            var inner = cells.Where(c => Inside(owner, c, own)).ToList();
            if (inner.Count < MinSpots) continue;
            int count = Math.Clamp(cells.Count / CellsPerHerd, 1, MaxHerds);
            uint fid = tile.FieldIds[f];
            for (int h = 0; h < count; h++)
            {
                uint seed = Hash(fid, (uint)h, 0x48E7);
                int centre = inner[(int)(seed % (uint)inner.Count)];
                var (ce, cn) = FieldFormat.CellCentre(centre);
                var near = new List<Vector2>();
                foreach (int c in inner)
                {
                    var (e, n) = FieldFormat.CellCentre(c);
                    var d = new Vector2(e - ce, n - cn);
                    if (d.LengthSquared() <= Spread * Spread) near.Add(d);
                }
                if (near.Count < MinSpots) continue;
                // thinned evenly when there are many, so the spots spread over the whole patch
                Vector2[] spots;
                if (near.Count > MaxSpots)
                {
                    spots = new Vector2[MaxSpots];
                    float step = near.Count / (float)MaxSpots;
                    for (int i = 0; i < MaxSpots; i++) spots[i] = near[(int)(i * step)];
                }
                else spots = near.ToArray();
                // two herds on one field stay apart: a second centre too close to an earlier one is dropped
                double he = tile.Id.MinE + ce, hn = tile.Id.MinN + cn;
                if (herds.Any(o => o.Tile == tile.Id && Math.Abs(o.E - he) < Spread * 1.5 && Math.Abs(o.N - hn) < Spread * 1.5)) continue;
                int cows = Math.Min(4 + (int)(Unit(seed, 1) * 7f), Math.Max(2, cells.Count / 40));
                var breed = Unit(fid, 2) < 0.6f ? CowBreed.Brown : CowBreed.Simmental;
                herds.Add(new Herd(tile.Id, herds.Count, he, hn, spots, breed, cows, seed));
            }
        }
        return herds;
    }

    /// <summary>A pasture always; a meadow one time in four (by its id).</summary>
    public static bool Grazed(CropKind crop, uint fieldId) =>
        crop == CropKind.Pasture || crop == CropKind.Meadow && Unit(fieldId, 7) < 0.25f;

    /// <summary>The cell and the cells two away in the four directions are the same field: a cow there stands well inside it.</summary>
    private static bool Inside(ushort[] owner, int cell, ushort own)
    {
        const int Side = FieldFormat.CellsPerSide, Gap = 2;
        int col = cell % Side, row = cell / Side;
        if (col < Gap || row < Gap || col >= Side - Gap || row >= Side - Gap) return false;
        return owner[cell - Gap] == own && owner[cell + Gap] == own
            && owner[cell - Gap * Side] == own && owner[cell + Gap * Side] == own;
    }

    // ---- no field data: open land ---------------------------------------------------------------

    /// <summary>The fallback's grid of 250 m cells, each with at most one herd, a herd in this share of them.</summary>
    public const int LandCell = 250;
    public const float LandShare = 0.4f;

    /// <summary>
    /// The herds of a tile with no field data (the map built without the farm layer, the generated
    /// fill): from the land cover instead. Each 250 m cell of the tile may hold one herd (two in
    /// five do, by hash), on the first of a hashed lattice of candidates that stands on open land
    /// (<see cref="CoverClass.Open"/> all round within 28 m), 50 m from any building, 20 m from any
    /// road or path, on ground no steeper than 4 m in 20. Pure and hashed like <see cref="For"/>:
    /// the server's and every client's agree, as they read the same tile files.
    /// </summary>
    /// <param name="cover">The tile's cover raster (<see cref="CoverFormat"/>: 1 m, rows south from the north edge).</param>
    public static List<Herd> FromLand(TileId id, byte[] cover, BuildingTile? buildings, RoadTile? roads, ChunkGrid? grid)
    {
        var herds = new List<Herd>();
        const int Side = ChunkFormat.GridSize, Block = 10, Blocks = (Side + Block - 1) / Block;
        if (cover.Length < Side * Side) return herds;

        // 10 m blocks near a building (50 m) or a road (20 m): no herd there
        var blocked = new bool[Blocks * Blocks];
        void Mark(float x, float z, int r)
        {
            int bx = (int)(x / Block), bz = (int)(z / Block);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int cx = bx + dx, cz = bz + dz;
                    if ((uint)cx < Blocks && (uint)cz < Blocks) blocked[cz * Blocks + cx] = true;
                }
        }
        if (buildings != null)
            foreach (var b in buildings.Buildings)
                for (int o = 0; o + 2 < b.Triangles.Length; o += 9) Mark(b.Triangles[o], b.Triangles[o + 2], 5);
        if (roads != null)
            foreach (var r in roads.Segments)
                for (int k = 0; k + 5 < r.Points.Length; k += 3)
                {
                    float ax = r.Points[k], az = r.Points[k + 2], bx = r.Points[k + 3], bz = r.Points[k + 5];
                    int steps = Math.Max(1, (int)(MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)) / 5f));
                    for (int t = 0; t <= steps; t++) Mark(ax + (bx - ax) * t / steps, az + (bz - az) * t / steps, 2);
                }

        bool Open(int x, int z) => (uint)x < Side && (uint)z < Side && (CoverClass)cover[z * Side + x] == CoverClass.Open;
        float Height(int x, int z) => grid == null ? 0f : (float)grid.HeightMetersAt(Math.Clamp(x, 0, Side - 1), Math.Clamp(z, 0, Side - 1));

        uint tileSeed = Hash((uint)id.E, (uint)id.N, 0x1A4D);
        int cells = (Side - 1) / LandCell;
        for (int cz = 0; cz < cells; cz++)
            for (int cx = 0; cx < cells; cx++)
            {
                uint cellSeed = Hash(tileSeed, (uint)(cz * cells + cx), 0x2B);
                if (Unit(cellSeed, 1) >= LandShare) continue;
                // a 5 x 5 lattice 50 m apart inside the cell, tried in a hashed order
                for (int tries = 0; tries < 25; tries++)
                {
                    int pick = (int)((Hash(cellSeed, 3) + (uint)tries * 7u) % 25u);
                    int x = cx * LandCell + 25 + pick % 5 * 50, z = cz * LandCell + 25 + pick / 5 * 50;
                    if (blocked[z / Block * Blocks + x / Block]) continue;
                    bool open = true;
                    for (int dz = -28; dz <= 28 && open; dz += 7)
                        for (int dx = -28; dx <= 28 && open; dx += 7)
                            if (dx * dx + dz * dz <= 28 * 28 && !Open(x + dx, z + dz)) open = false;
                    if (!open) continue;
                    if (grid != null)
                    {
                        float h0 = Height(x - 10, z), h1 = Height(x + 10, z), h2 = Height(x, z - 10), h3 = Height(x, z + 10);
                        if (Math.Abs(h1 - h0) > 4f || Math.Abs(h3 - h2) > 4f) continue;
                    }
                    var spots = new List<Vector2>();
                    for (int dz = -24; dz <= 24; dz += 6)
                        for (int dx = -24; dx <= 24; dx += 6)
                            if (dx * dx + dz * dz <= 24 * 24 && Open(x + dx, z + dz) && !blocked[(z + dz) / Block * Blocks + (x + dx) / Block])
                                spots.Add(new Vector2(dx, -dz));
                    if (spots.Count < MinSpots) continue;
                    int cows = 3 + (int)(Unit(cellSeed, 4) * 6f);
                    var breed = Unit(cellSeed, 5) < 0.6f ? CowBreed.Brown : CowBreed.Simmental;
                    herds.Add(new Herd(id, herds.Count, id.MinE + x, id.MaxN - z, spots.ToArray(), breed, cows, cellSeed));
                    break;
                }
            }
        return herds;
    }

    /// <summary>A herd the trailer director stages at an LV95 point: <paramref name="count"/> cows on open ground round it.</summary>
    public static Herd Staged(int index, double e, double n, int count, CowBreed breed, float radius = 18f, uint seed = 1)
    {
        var spots = new Vector2[16];
        for (int i = 0; i < spots.Length; i++)
        {
            float a = Unit(seed, (uint)(i * 2 + 11)) * Mathf.Tau, r = radius * Mathf.Sqrt(Unit(seed, (uint)(i * 2 + 12)));
            spots[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        return new Herd(TileId.FromLv95(e, n), -1 - index, e, n, spots, breed, count, seed);
    }

    // ---- hashing (no Godot, no shared stream) ---------------------------------------------------

    public static uint Hash(uint a, uint b, uint salt = 0)
    {
        unchecked
        {
            uint h = salt * 0x9E3779B9u ^ a * 0x85EBCA6Bu;
            h ^= b * 0xC2B2AE35u; h = (h << 13) | (h >> 19);
            h *= 0x165667B1u; h ^= h >> 15; h *= 0x85EBCA77u; h ^= h >> 13; h *= 0xC2B2AE3Du; h ^= h >> 16;
            return h;
        }
    }

    /// <summary>0..1 from two numbers.</summary>
    public static float Unit(uint a, uint b, uint salt = 0) => (Hash(a, b, salt) & 0xFFFFFF) / 16777216f;
}
