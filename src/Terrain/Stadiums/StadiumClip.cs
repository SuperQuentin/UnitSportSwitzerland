using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// A stadium owns its outline (#885): any data-building triangle whose centre falls inside a
/// catalogued stadium's facade is dropped as the tile is read, so the bowl is never shared with a
/// solid of the data. At Geneva that is a corner of the La Praille mall standing in the north-west
/// concourse; at Wankdorf it will be the whole 246 x 190 m solid the data draws the stadium as.
///
/// <para>
/// Done where a tile's buildings are decoded (<see cref="LocalChunkSource"/>,
/// <see cref="NetworkChunkSource"/>), not where they are drawn, so every consumer (mesh,
/// collision, doors, occluders, perches, interiors, loot) sees the same clipped tile on the server
/// and on every client, and building indices never change: a building wholly inside keeps its
/// place in the list with no triangles. Tiles away from any stadium come back as they were. Any
/// thread.
/// </para>
/// </summary>
public static class StadiumClip
{
    /// <summary>The tile's buildings without what stands inside a stadium; the same tile when nothing does.</summary>
    public static BuildingTile? Clip(BuildingTile? tile)
    {
        if (tile == null) return null;
        List<Building>? clipped = null;
        foreach (var s in StadiumCatalog.All)
        {
            // an open air keeps what stands on its ground (an airfield's hangars, a farm): only parked
            // cars and trees make way (#948)
            if (s.Kind == VenueKind.OpenAir) continue;
            var outline = StadiumBuilder.Outline(s, tile.Id);
            var (min, max) = Bounds(outline);
            // a building is stored by the tile holding its centre and may reach well past it
            const float reach = StadiumCatalog.Reach;
            if (max.X < -reach || min.X > ChunkFormat.TileSizeM + reach
                || max.Y < -reach || min.Y > ChunkFormat.TileSizeM + reach) continue;
            var list = clipped ?? tile.Buildings;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (Clip(b, outline, min, max) is { } kept)
                {
                    clipped ??= new List<Building>(tile.Buildings);
                    clipped[i] = kept;
                    list = clipped;
                }
            }
        }
        return clipped == null ? tile : new BuildingTile { Id = tile.Id, Buildings = clipped };
    }

    /// <summary>The building without its triangles inside <paramref name="outline"/>, or null when it has none there.</summary>
    public static Building? Clip(Building b, Vector2[] outline, Vector2 min, Vector2 max)
    {
        var t = b.Triangles;
        List<float>? kept = null;
        for (int k = 0; k + 8 < t.Length; k += 9)
        {
            var c = new Vector2((t[k] + t[k + 3] + t[k + 6]) / 3f, (t[k + 2] + t[k + 5] + t[k + 8]) / 3f);
            bool inside = c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && Inside(outline, c);
            if (inside)
            {
                if (kept == null)
                {
                    kept = new List<float>(t.Length);
                    for (int j = 0; j < k; j++) kept.Add(t[j]);
                }
            }
            else if (kept != null)
                for (int j = 0; j < 9; j++) kept.Add(t[k + j]);
        }
        if (kept == null) return null;
        return new Building
        {
            Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors,
            MinY = b.MinY, MaxY = b.MaxY, Triangles = kept.ToArray(),
        };
    }

    /// <summary>Every stadium's facade outline in LV95 (east, north), worked out once.</summary>
    private static readonly (Vector2[] Poly, Vector2 Min, Vector2 Max, double E0, double N0)[] Lv95 = StadiumCatalog.All.Select(s =>
    {
        // relative to the centre spot, so a float keeps centimetres
        var owner = StadiumCatalog.Owner(s);
        var poly = StadiumBuilder.Outline(s, owner)
            .Select(p => new Vector2((float)(owner.MinE + p.X - s.E), (float)(owner.MaxN - p.Y - s.N))).ToArray();
        var (min, max) = Bounds(poly);
        return (poly, min, max, s.E, s.N);
    }).ToArray();

    /// <summary>Whether an LV95 point is inside any stadium's facade: no parked vehicle, tree or prop stands there.</summary>
    public static bool InsideAny(double e, double n)
    {
        foreach (var (poly, min, max, e0, n0) in Lv95)
        {
            var p = new Vector2((float)(e - e0), (float)(n - n0));
            if (p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && Inside(poly, p)) return true;
        }
        return false;
    }

    /// <summary>The tile's trees without those inside a stadium (the bowl stands on mapped ground); the same list when none is.</summary>
    public static List<TreeInstance>? Trees(TileId id, List<TreeInstance>? trees)
    {
        if (trees == null || !Lv95.Any(o => Near(id, o.E0 + o.Min.X, o.N0 + o.Min.Y, o.E0 + o.Max.X, o.N0 + o.Max.Y))) return trees;
        trees.RemoveAll(t => InsideAny(id.MinE + t.X, id.MaxN - t.Z));
        return trees;
    }

    private static bool Near(TileId id, double e0, double n0, double e1, double n1) =>
        e1 >= id.MinE && e0 <= id.MinE + ChunkFormat.TileSizeM && n1 >= id.MinN && n0 <= id.MaxN;

    /// <summary>Even-odd test of a point against a closed polygon (x, z).</summary>
    public static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var (a, b) = (poly[i], poly[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static (Vector2 Min, Vector2 Max) Bounds(Vector2[] poly)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var p in poly)
        {
            min = min.Min(p);
            max = max.Max(p);
        }
        return (min, max);
    }
}
