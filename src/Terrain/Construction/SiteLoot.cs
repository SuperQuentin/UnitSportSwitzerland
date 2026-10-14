using Godot;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>What a searchable piece of a building site's yard is (#627): it picks the loot table.</summary>
public enum SiteStockKind : byte { Skip = 0, Rebar = 1, Formwork = 2, Rings = 3, Bricks = 4, Cement = 5 }

/// <summary>
/// One container standing in a building site's yard (#627), placed: the site's building key and its
/// furniture index (<see cref="SiteLoot.FirstYardIndex"/> + slot), what it is, the middle of its
/// box in LV95, the box's bottom and top (altitudes), and its half sizes along the site's axes
/// (tile frame: x east, z south). <see cref="Pallet"/>: a pallet a machine with tines lifts (#615),
/// <c>&lt;building&gt;:c&lt;slot&gt;</c>, which holds nothing once moved.
/// </summary>
public readonly record struct YardStock(string Building, int Index, SiteStockKind Kind, double E, double N,
    float Bottom, float Top, Vector2 AxisU, Vector2 AxisV, float HalfU, float HalfV, bool Pallet)
{
    /// <summary>Its slot in the dressing (<see cref="SiteStockSpot.Slot"/>).</summary>
    public int Slot => Index - SiteLoot.FirstYardIndex;
}

/// <summary>
/// The loot containers of a building site's yard (#627): every skip and every piece of the
/// materials (the rebar bundle, the pallets of bricks and cement, the formwork stacks, the concrete
/// rings), as <see cref="SiteDressings.Plan"/> stands them. A pure function of the site, so every
/// peer and the server list the same ones from the tile alone, and nothing is ever sent about them
/// but what was taken.
///
/// <para>
/// <b>Identity.</b> A yard container is named like a piece of furniture: the site's own building key
/// (<c>E_N_I</c>) and an index from <see cref="FirstYardIndex"/> up (1000 + its slot). So the loot
/// epoch, the roll's seed, the <c>user://loot/E_N.json</c> masks, the restock and the
/// <c>Taken</c>/<c>Granted</c> protocol of <c>LootService</c> work unchanged. The site office's
/// interior (#822) is the same building's, with its furniture below 1000.
/// </para>
/// </summary>
public static class SiteLoot
{
    /// <summary>The first furniture index of a yard: below it, the building's interior.</summary>
    public const int FirstYardIndex = 1000;
    /// <summary>The rebar bundle's slot; the materials' row counts 0, 1, 2… below it.</summary>
    public const int RebarSlot = 100;
    /// <summary>The first skip's slot; the next is one more.</summary>
    public const int FirstSkipSlot = 200;

    /// <summary>How far from a piece's edge the player can search it, as a piece of furniture.</summary>
    public const float SearchReach = 1.3f;
    /// <summary>Walking this far from its edge closes the panel.</summary>
    public const float CloseReach = 2.6f;
    /// <summary>What the server allows past <see cref="SearchReach"/>: the body it has is a moment old.</summary>
    public const float ServerSlack = 2.5f;

    public static bool IsYard(int index) => index >= FirstYardIndex;

    /// <summary>A site's yard containers, in the order the dressing stood them.</summary>
    /// <param name="minE">The tile's west edge (<c>TileId.MinE</c>).</param>
    /// <param name="maxN">The tile's north edge (<c>TileId.MaxN</c>).</param>
    public static List<YardStock> Of(ConstructionSite site, SiteDressingPlan plan, double minE, double maxN)
    {
        var list = new List<YardStock>(plan.Stock.Count);
        var u = site.Box.AxisU;
        var v = site.Box.AxisV;
        foreach (var s in plan.Stock)
        {
            float cx = (s.Min.X + s.Max.X) / 2, cz = (s.Min.Z + s.Max.Z) / 2;
            // the site frame to the tile's (x east, z south): as SiteShellBuilder.ToTile
            var at = site.Box.Center + u * cx + v * cz;
            bool pallet = false;
            if (s.Kind is SiteStockKind.Bricks or SiteStockKind.Cement && s.Slot < RebarSlot)
                foreach (var p in plan.Pallets)
                    if (p.Slot == s.Slot) { pallet = true; break; }
            list.Add(new YardStock(site.Key, FirstYardIndex + s.Slot, s.Kind, minE + at.X, maxN - at.Y,
                s.Min.Y, s.Max.Y, u, v, (s.Max.X - s.Min.X) / 2, (s.Max.Z - s.Min.Z) / 2, pallet));
        }
        return list;
    }

    /// <summary>The point's offset from the container's middle, in the tile frame (x east, z south).</summary>
    private static Vector2 Rel(in YardStock c, double e, double n) => new((float)(e - c.E), (float)(c.N - n));

    /// <summary>The horizontal distance from a point (LV95) to the container's box, 0 inside it.</summary>
    public static float EdgeDistance(in YardStock c, double e, double n)
    {
        var rel = Rel(c, e, n);
        float du = Math.Max(0, Math.Abs(rel.Dot(c.AxisU)) - c.HalfU), dv = Math.Max(0, Math.Abs(rel.Dot(c.AxisV)) - c.HalfV);
        return MathF.Sqrt(du * du + dv * dv);
    }

    /// <summary>
    /// Whether feet at <paramref name="alt"/> are at the container's level: on the ground beside it
    /// (a slope lets the ground fall away a little), or standing on it.
    /// </summary>
    public static bool AtLevel(in YardStock c, float alt) => alt > c.Bottom - 1.5f && alt < c.Top + 0.6f;

    /// <summary>
    /// How well a player at <paramref name="e"/>, <paramref name="n"/> (feet at <paramref name="alt"/>)
    /// looking along <paramref name="look"/> (tile frame, x east, z south; need not be unit) faces the
    /// container, lower is better, as <c>LootService.NearestOf</c> scores furniture: the distance to
    /// its edge less what is in front. <see cref="float.MaxValue"/> when out of reach, off its level
    /// or behind the player.
    /// </summary>
    public static float Score(in YardStock c, double e, double n, float alt, Vector2 look, float reach = SearchReach)
    {
        if (!AtLevel(c, alt)) return float.MaxValue;
        var rel = Rel(c, e, n);
        float lu = rel.Dot(c.AxisU), lv = rel.Dot(c.AxisV);
        float du = Math.Max(0, Math.Abs(lu) - c.HalfU), dv = Math.Max(0, Math.Abs(lv) - c.HalfV);
        float dist = MathF.Sqrt(du * du + dv * dv);
        if (dist > reach) return float.MaxValue;
        // toward the box's nearest point, not its middle: a 14 m bundle is beside you, not ahead
        var near = c.AxisU * Math.Clamp(lu, -c.HalfU, c.HalfU) + c.AxisV * Math.Clamp(lv, -c.HalfV, c.HalfV);
        var to = near - rel;
        float front = 1f;
        if (to.LengthSquared() > 1e-4f && look.LengthSquared() > 1e-6f) front = look.Normalized().Dot(to.Normalized());
        if (front < -0.2f) return float.MaxValue;
        return dist - front * 0.8f;
    }

    /// <summary>
    /// Server: whether a body at <paramref name="e"/>, <paramref name="n"/>, <paramref name="alt"/> may
    /// take from the container: within <see cref="SearchReach"/> + <see cref="ServerSlack"/> of its
    /// edge, and within 4 m of its height.
    /// </summary>
    public static bool InServerReach(in YardStock c, double e, double n, double alt) =>
        EdgeDistance(c, e, n) <= SearchReach + ServerSlack && alt > c.Bottom - 4 && alt < c.Top + 4;

    /// <summary>A name for the prompt and the panel: "Search the skip".</summary>
    public static string Describe(SiteStockKind kind) => kind switch
    {
        SiteStockKind.Skip => "skip",
        SiteStockKind.Rebar => "rebar bundle",
        SiteStockKind.Formwork => "formwork stack",
        SiteStockKind.Rings => "concrete rings",
        SiteStockKind.Bricks => "pallet of bricks",
        _ => "pallet of cement",
    };
}
