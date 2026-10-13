using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="SiteLoot"/> (#627): a building site's skips and materials are loot containers outside
/// any interior, named by the site's building key and a furniture index from 1000 up. What matters:
/// every skip and every piece of the materials is one, each has its own index, the same on every
/// peer, the pallets a machine lifts are flagged so a moved one holds nothing, they stand where the
/// dressing drew them, and a player is in reach of one only on foot beside it and facing it.
/// </summary>
public class SiteLootTests
{
    private static Building Box(float w, float d, float h, byte floors, float turn)
    {
        var u = Vector2.FromAngle(turn);
        var v = new Vector2(-u.Y, u.X);
        var c = new Vector2(500, 500);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], 0), P(corners[(i + 1) % 4], h));
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], h), P(corners[i], h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    private const double MinE = 2585000, MaxN = 1115000;

    private static (ConstructionSite Site, SiteDressingPlan Yard, List<YardStock> Stock) Make(string key, float w, float d, float h, byte floors, float turn = 0.3f, float ground = 0f)
    {
        var b = Box(w, d, h, floors, turn);
        var site = ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, new Vector2(500, 560), _ => false)!;
        var yard = SiteDressings.Plan(site, (_, _) => ground);
        return (site, yard, SiteLoot.Of(site, yard, MinE, MaxN));
    }

    public static IEnumerable<object[]> Sites() => new[]
    {
        new object[] { "2585_1114_68", 24f, 15f, 1.2f, (byte)4 },   // foundations
        new object[] { "2586_1115_51", 24f, 15f, 6.3f, (byte)4 },   // a shell
        new object[] { "2580_1112_61", 14f, 11f, 6.3f, (byte)2 },   // a house, topped out
        new object[] { "2582_1102_61", 40f, 18f, 18.4f, (byte)6 },  // a big block, topped out
        new object[] { "2583_1104_12", 30f, 20f, 9.0f, (byte)5 },
        new object[] { "2587_1119_3", 18f, 12f, 3.5f, (byte)3 },
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void Every_skip_and_every_piece_of_the_materials_is_a_container(string key, float w, float d, float h, byte floors)
    {
        var (_, yard, stock) = Make(key, w, d, h, floors);
        int skips = yard.Boxes.Count(b => b.Part == ShellPart.Skip);
        Assert.Equal(skips, stock.Count(s => s.Kind == SiteStockKind.Skip));
        Assert.Equal(yard.Boxes.Count(b => b.Part == ShellPart.Rebar), stock.Count(s => s.Kind == SiteStockKind.Rebar));
        Assert.Equal(yard.Boxes.Count(b => b.Part == ShellPart.Formwork), stock.Count(s => s.Kind == SiteStockKind.Formwork));
        Assert.Equal(yard.Boxes.Count(b => b.Part == ShellPart.FreshConcrete), stock.Count(s => s.Kind == SiteStockKind.Rings));
        // a pallet is a pallet a machine lifts, or one drawn in the mesh where its deck does not fit
        Assert.Equal(yard.Pallets.Count + yard.Boxes.Count(b => b.Part is ShellPart.Brick or ShellPart.Cement),
            stock.Count(s => s.Kind is SiteStockKind.Bricks or SiteStockKind.Cement));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void Each_has_its_own_index_from_1000_on_its_site(string key, float w, float d, float h, byte floors)
    {
        var (site, _, stock) = Make(key, w, d, h, floors);
        Assert.Equal(stock.Count, stock.Select(s => s.Index).Distinct().Count());
        foreach (var s in stock)
        {
            Assert.Equal(site.Key, s.Building);
            Assert.True(SiteLoot.IsYard(s.Index), $"{key}: index {s.Index}");
            Assert.Equal(s.Index - SiteLoot.FirstYardIndex, s.Slot);
            // the row's slots stay under the rebar's, the skips' over it
            if (s.Kind == SiteStockKind.Skip) Assert.True(s.Slot >= SiteLoot.FirstSkipSlot, $"{key}: skip slot {s.Slot}");
            else if (s.Kind == SiteStockKind.Rebar) Assert.Equal(SiteLoot.RebarSlot, s.Slot);
            else Assert.True(s.Slot < SiteLoot.RebarSlot, $"{key}: {s.Kind} slot {s.Slot}");
        }
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void The_pallets_a_machine_lifts_are_flagged_by_their_own_slot(string key, float w, float d, float h, byte floors)
    {
        var (_, yard, stock) = Make(key, w, d, h, floors);
        var lifted = yard.Pallets.Select(p => p.Slot).ToHashSet();
        foreach (var s in stock)
            Assert.Equal(s.Kind is SiteStockKind.Bricks or SiteStockKind.Cement && lifted.Contains(s.Slot), s.Pallet);
        Assert.Equal(lifted.Count, stock.Count(s => s.Pallet));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void Each_stands_where_the_dressing_drew_it_on_the_ground(string key, float w, float d, float h, byte floors)
    {
        var (site, yard, stock) = Make(key, w, d, h, floors, ground: 412.5f);
        for (int i = 0; i < stock.Count; i++)
        {
            var s = stock[i];
            var spot = yard.Stock[i];
            Assert.Equal(412.5f, s.Bottom, 3);
            Assert.True(s.Top > s.Bottom + 0.3f, $"{key}: {s.Kind} {s.Bottom}..{s.Top}");
            // its middle back in the site frame is the spot's middle
            var tile = new Vector2((float)(s.E - MinE), (float)(MaxN - s.N));
            var rel = tile - site.Box.Center;
            Assert.Equal((spot.Min.X + spot.Max.X) / 2, rel.Dot(site.Box.AxisU), 2);
            Assert.Equal((spot.Min.Z + spot.Max.Z) / 2, rel.Dot(site.Box.AxisV), 2);
            Assert.True(site.Area.Contains(tile, 0.6f), $"{key}: {s.Kind} at {tile} outside the site");
            // its middle is inside it
            Assert.Equal(0f, SiteLoot.EdgeDistance(s, s.E, s.N), 3);
        }
    }

    [Fact]
    public void The_same_site_lists_the_same_containers()
    {
        var a = Make("2586_1115_51", 24, 15, 6.3f, 4).Stock;
        var b = Make("2586_1115_51", 24, 15, 6.3f, 4).Stock;
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void Some_sites_have_skips_and_materials()
    {
        var all = Sites().SelectMany(s => Make((string)s[0], (float)s[1], (float)s[2], (float)s[3], (byte)s[4]).Stock).ToList();
        Assert.Contains(all, s => s.Kind == SiteStockKind.Skip);
        Assert.Contains(all, s => s.Kind == SiteStockKind.Rebar);
        Assert.Contains(all, s => s.Kind is SiteStockKind.Bricks or SiteStockKind.Cement);
    }

    [Fact]
    public void A_skip_is_searched_from_beside_it_facing_it_on_its_level()
    {
        var stock = Make("2586_1115_51", 24, 15, 6.3f, 4).Stock;
        var skip = stock.First(s => s.Kind == SiteStockKind.Skip);
        // a metre out from its long side, along its V axis (tile frame), facing it
        var outV = skip.AxisV * (skip.HalfV + 1.0f);
        double e = skip.E + outV.X, n = skip.N - outV.Y;
        var facing = -skip.AxisV;
        Assert.Equal(1.0f, SiteLoot.EdgeDistance(skip, e, n), 3);
        Assert.True(SiteLoot.Score(skip, e, n, skip.Bottom, facing) < float.MaxValue, "beside it, facing it");
        // turned away from it, it is behind you
        Assert.Equal(float.MaxValue, SiteLoot.Score(skip, e, n, skip.Bottom, -facing));
        // 3 m off its edge, out of reach
        var far = skip.AxisV * (skip.HalfV + 3.0f);
        Assert.Equal(float.MaxValue, SiteLoot.Score(skip, skip.E + far.X, skip.N - far.Y, skip.Bottom, facing));
        // a storey up the scaffolding, or 3 m under it, is not at its level
        Assert.Equal(float.MaxValue, SiteLoot.Score(skip, e, n, skip.Top + 2.5f, facing));
        Assert.Equal(float.MaxValue, SiteLoot.Score(skip, e, n, skip.Bottom - 3f, facing));
        // standing on it is
        Assert.True(SiteLoot.Score(skip, skip.E, skip.N, skip.Top, facing) < float.MaxValue);
    }

    [Fact]
    public void The_server_allows_a_moment_of_lag_but_not_a_search_from_afar()
    {
        var skip = Make("2586_1115_51", 24, 15, 6.3f, 4).Stock.First(s => s.Kind == SiteStockKind.Skip);
        var near = skip.AxisV * (skip.HalfV + SiteLoot.SearchReach + 2f);
        var far = skip.AxisV * (skip.HalfV + SiteLoot.SearchReach + SiteLoot.ServerSlack + 1f);
        Assert.True(SiteLoot.InServerReach(skip, skip.E + near.X, skip.N - near.Y, skip.Bottom));
        Assert.False(SiteLoot.InServerReach(skip, skip.E + far.X, skip.N - far.Y, skip.Bottom));
        Assert.False(SiteLoot.InServerReach(skip, skip.E, skip.N, skip.Top + 10));
    }

    [Fact]
    public void A_long_bundle_is_beside_you_not_ahead()
    {
        var rebar = Sites().SelectMany(s => Make((string)s[0], (float)s[1], (float)s[2], (float)s[3], (byte)s[4]).Stock)
            .Where(s => s.Kind == SiteStockKind.Rebar).OrderByDescending(s => s.HalfU).First();
        Assert.True(rebar.HalfU > 2, $"a bundle {rebar.HalfU * 2:F1} m long");
        // near one end, beside it, looking at it across: its middle is far along, but it is in front
        var at = rebar.AxisU * (rebar.HalfU - 0.5f) + rebar.AxisV * (rebar.HalfV + 0.8f);
        Assert.True(SiteLoot.Score(rebar, rebar.E + at.X, rebar.N - at.Y, rebar.Bottom, -rebar.AxisV) < float.MaxValue);
    }
}
