using System.Globalization;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;
using Candidate = UnitSport.Tools.Preprocessor.PoiPick.Candidate;

namespace UnitSport.Tests;

/// <summary>POI ids and <c>pois.bin</c> (#1224, <c>docs/plans/adventure-places.md</c> 1.2, 1.4).</summary>
public class PoiIdTests
{
    [Fact]
    public void The_hash_is_40_bits_of_SHA256_over_the_source_and_the_same_on_every_run()
    {
        ulong a = PoiId.Hash("67e5f2eb-61fc-469b-912e-77a9f3472529");
        Assert.Equal(a, PoiId.Hash("67e5f2eb-61fc-469b-912e-77a9f3472529"));
        Assert.True(a <= PoiId.HashMask);
        Assert.NotEqual(a, PoiId.Hash("67e5f2eb-61fc-469b-912e-77a9f3472520"));
        // SHA-256("abc") starts ba7816bf8f
        Assert.Equal(0xba7816bf8fUL, PoiId.Hash("abc"));
    }

    [Fact]
    public void A_TLM_uuid_is_the_same_source_with_or_without_braces_and_in_any_case()
    {
        Assert.Equal(PoiPick.SourceUuid("{67E5F2EB-61FC-469B-912E-77A9F3472529}"), PoiPick.SourceUuid("67e5f2eb-61fc-469b-912e-77a9f3472529"));
    }

    [Theory]
    [InlineData("Rigi Kulm", "rigi-kulm")]
    [InlineData("Aiguille d'Argentière", "aiguille-d-argentiere")]
    [InlineData("  Château de Chillon!  ", "chateau-de-chillon")]
    [InlineData("Piz Bernina / Piz Bernina Ost (Spitze)", "piz-bernina-piz-bernina-ost-spit")]
    [InlineData("", "unnamed")]
    public void A_slug_is_the_name_in_lower_case_without_accents_cut_at_32(string name, string slug)
    {
        Assert.Equal(slug, PoiId.Slug(name));
        Assert.True(PoiId.Slug(name).Length <= PoiId.SlugMax);
    }

    [Fact]
    public void An_id_parses_back_to_its_kind_and_hash_whatever_the_slug()
    {
        ulong h = PoiId.Hash("x");
        string id = PoiId.Make(PoiKind.Peak, "Rigi Kulm", h);
        Assert.Matches("^peak:rigi-kulm\\.[0-9a-f]{10}$", id);
        Assert.True(PoiId.TryParse(id, out var k, out ulong back));
        Assert.Equal((PoiKind.Peak, h), (k, back));
        Assert.True(PoiId.TryParse(id.Replace("rigi-kulm", "renamed"), out _, out ulong renamed));
        Assert.Equal(h, renamed);
        Assert.False(PoiId.TryParse("peak:rigi-kulm", out _, out _));          // an alias
        Assert.False(PoiId.TryParse("volcano:x.0123456789", out _, out _));    // no such kind
        Assert.False(PoiId.TryParse("peak:x.012345678", out _, out _));        // nine digits
    }

    [Fact]
    public void A_rerun_keeps_ids_and_a_rename_keeps_the_stamp()
    {
        static PoiData Build(string name)
        {
            var d = new PoiData();
            d.Pois.Add(new PoiRecord { Hash = PoiId.Hash("uuid-1"), Kind = PoiKind.Peak, Name = name, E = 2_600_000, N = 1_200_000 });
            return d;
        }
        var before = new PoiCatalogue(Build("Rigi Kulm"));
        var rerun = new PoiCatalogue(PoiFile.Decode(PoiFile.Encode(Build("Rigi Kulm"))));
        var renamed = new PoiCatalogue(Build("Rigi-Kulm (Gipfel)"));
        string saved = before.All[0].Id;
        Assert.Equal(saved, rerun.All[0].Id);
        Assert.NotEqual(saved, renamed.All[0].Id);           // the slug moved...
        Assert.Same(renamed.All[0], renamed.Resolve(saved)); // ...and the saved stamp still finds it
    }

    [Fact]
    public void A_region_build_gives_the_ids_of_the_nationwide_build()
    {
        // ids come from the source alone, before the region filter
        var all = Enumerable.Range(0, 50).Select(i => new Candidate { Kind = PoiKind.BusStop, SourceId = $"uuid-{i}", Name = $"Stop {i}", E = 2_500_000 + i * 700, N = 1_100_000 }).ToList();
        var region = all.Where(c => c.E < 2_510_000).ToList();
        foreach (var c in region)
            Assert.Equal(PoiId.Hash(all.Single(a => a.SourceId == c.SourceId).SourceId), PoiId.Hash(c.SourceId));
    }

    [Fact]
    public void Aliases_resolve_to_the_canonical_poi_and_unknown_ones_are_reported()
    {
        var c = PoiCatalogue.Fixture();
        var peak = c.OfKind(PoiKind.Peak)[0];
        var missing = c.AddAliases(new[] { ("peak:fixture", peak.Id), ("tower:nowhere", "tower:nowhere.0000000000") });
        Assert.Equal(new[] { "tower:nowhere" }, missing);
        Assert.Same(peak, c.Resolve("peak:fixture"));
        Assert.Null(c.Resolve("tower:nowhere"));
        Assert.Null(c.Resolve(""));
    }

    [Fact]
    public void Two_sources_with_one_hash_in_a_kind_fail_the_build_but_not_across_kinds()
    {
        var rows = new[]
        {
            new Candidate { Kind = PoiKind.Peak, SourceId = "a" },
            new Candidate { Kind = PoiKind.Peak, SourceId = "b" },
            new Candidate { Kind = PoiKind.Pass, SourceId = "c" },
        };
        Assert.Single(PoiPick.Collisions(rows, _ => 7));
        Assert.Empty(PoiPick.Collisions(rows.Where(r => r.SourceId != "b"), _ => 7));
        Assert.Empty(PoiPick.Collisions(rows));
    }
}

public class PoiFileTests
{
    private static PoiData Sample()
    {
        var d = new PoiData();
        d.Cantons.Add(new PoiCanton("VS", 1, 2, 300));
        d.Cantons.Add(new PoiCanton("VD", 0, 1, 120));
        d.Pois.Add(new PoiRecord
        {
            Hash = 0xfedcba9876, Kind = PoiKind.Peak, Flags = PoiFlags.Named | PoiFlags.Cross | PoiFlags.Viewpoint, Tier = 1,
            Canton = "VS", DiscoverRadius = 40, RevealRadius = 7000, E = 2_617_543.27, N = 1_091_234.91, Elevation = 4478,
            Name = "Matterhorn",
        });
        d.Pois.Add(new PoiRecord
        {
            Hash = 1, Kind = PoiKind.CableCarStation, Flags = PoiFlags.Top, Tier = 2, Canton = "VD", DiscoverRadius = 60,
            E = 2_570_000, N = 1_130_000, Elevation = -12, Name = "Glacier 3000", LineKey = 25_700_001_130_000L,
        });
        d.Pois.Add(new PoiRecord
        {
            Hash = 2, Kind = PoiKind.Lake, Tier = 2, Canton = "", DiscoverRadius = 60, E = 2_550_000, N = 1_140_000,
            Name = "Lac de Test", Outline = new[] { 2_549_000.0, 1_139_000.0, 2_551_000.1, 1_139_000.0, 2_551_000.1, 1_141_000.9, 2_549_000.0, 1_139_000.0 },
        });
        d.Pois.Add(new PoiRecord { Hash = 3, Kind = PoiKind.BusStop, Tier = 3, Canton = "VS", E = 2_600_000, N = 1_100_000, Name = "Matterhorn" });
        return d;
    }

    [Fact]
    public void A_file_round_trips_to_the_decimetre_with_outlines_cantons_and_shared_names()
    {
        var src = Sample();
        var bytes = PoiFile.Encode(src);
        var back = PoiFile.Decode(bytes);
        Assert.Equal(src.Cantons, back.Cantons);
        Assert.Equal(src.Pois.Count, back.Pois.Count);
        for (int i = 0; i < src.Pois.Count; i++)
        {
            var (a, b) = (src.Pois[i], back.Pois[i]);
            Assert.Equal((a.Hash, a.Kind, a.Flags, a.Tier, a.Canton, a.Elevation, a.Name, a.LineKey), (b.Hash, b.Kind, b.Flags, b.Tier, b.Canton, b.Elevation, b.Name, b.LineKey));
            Assert.Equal(a.DiscoverRadius, b.DiscoverRadius);
            Assert.Equal(a.RevealRadius, b.RevealRadius);
            Assert.InRange(Math.Abs(a.E - b.E), 0, 0.051);
            Assert.InRange(Math.Abs(a.N - b.N), 0, 0.051);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Outline == null, b.Outline == null);
            if (a.Outline != null)
                for (int k = 0; k < a.Outline.Length; k++) Assert.InRange(Math.Abs(a.Outline[k] - b.Outline![k]), 0, 0.051);
        }
        // "Matterhorn" twice, stored once: header + 2 strings... the size is records + table, not per-record names
        int strings = 3 * 2 + "Matterhorn".Length + "Glacier 3000".Length + "Lac de Test".Length;
        int outline = 4 + 8 + 3 * 8;
        Assert.Equal(PoiFile.HeaderSize + strings + 2 * 14 + 4 * PoiFile.RecordSize + outline, bytes.Length);
    }

    [Fact]
    public void A_bad_magic_a_newer_version_and_a_truncated_file_are_refused()
    {
        var bytes = PoiFile.Encode(Sample());
        var bad = (byte[])bytes.Clone();
        bad[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => PoiFile.Decode(bad));
        var newer = (byte[])bytes.Clone();
        newer[4] = 99;
        Assert.Throws<InvalidDataException>(() => PoiFile.Decode(newer));
        Assert.Throws<InvalidDataException>(() => PoiFile.Decode(bytes.AsSpan(0, bytes.Length - 3)));
    }

    [Fact]
    public void A_canton_missing_from_the_table_fails_the_build()
    {
        var d = Sample();
        d.Pois[0].Canton = "UR";
        Assert.Throws<InvalidDataException>(() => PoiFile.Encode(d));
    }
}

/// <summary><c>PoiStage</c>'s rules on hand-made rows.</summary>
public class PoiPickTests
{
    private static Candidate C(PoiKind kind, string id, string name, double e, double n, PoiFlags flags = PoiFlags.Named) =>
        new() { Kind = kind, SourceId = id, Name = name, E = e, N = n, Flags = flags };

    [Fact]
    public void A_cross_within_150_m_flags_its_peak_and_the_rest_stand_alone_named_after_the_nearest_peak()
    {
        var peaks = new List<Candidate> { C(PoiKind.Peak, "p1", "Pointe", 0, 0), C(PoiKind.Peak, "p2", "Dent", 1000, 0) };
        var crosses = new[] { C(PoiKind.SummitCross, "c1", "", 100, 100, PoiFlags.None), C(PoiKind.SummitCross, "c2", "", 1300, 0, PoiFlags.None) };
        var alone = PoiPick.JoinCrosses(peaks, crosses);
        Assert.True(peaks[0].Flags.HasFlag(PoiFlags.Cross | PoiFlags.Viewpoint));
        Assert.False(peaks[1].Flags.HasFlag(PoiFlags.Cross));
        var c2 = Assert.Single(alone);
        Assert.Equal("c2", c2.SourceId);
        Assert.Equal("Summit cross near Dent", c2.Name);
        Assert.True(c2.Flags.HasFlag(PoiFlags.Viewpoint));
        Assert.False(c2.Flags.HasFlag(PoiFlags.Named));
    }

    [Fact]
    public void The_village_stop_is_the_nearest_stop_named_after_the_town_else_the_nearest_within_400_m()
    {
        var stops = new List<Candidate>
        {
            C(PoiKind.BusStop, "s1", "Bifurcation", 50, 0),
            C(PoiKind.BusStop, "s2", "Riddes, gare", 900, 0),
            C(PoiKind.BusStop, "s3", "Isérables, village", 5000, 5000),
            C(PoiKind.BusStop, "s4", "Les Prés", 5300, 5000),
        };
        int picked = PoiPick.PickVillageStops(stops, new[] { ("Riddes", 0.0, 0.0), ("Auddes", 5000.0, 5100.0), ("Nowhere", 9000.0, 9000.0) });
        Assert.Equal(2, picked);
        Assert.Equal(new[] { "s2", "s3" }, stops.Where(s => s.Flags.HasFlag(PoiFlags.VillageStop)).Select(s => s.SourceId));
    }

    private static PoiPick.Segment Seg(string id, string name, params double[] e) =>
        new(id, name, e, new double[e.Length], new double[e.Length]);

    [Fact]
    public void Covered_bridge_segments_touching_end_to_end_are_one_bridge_named_and_identified_by_the_longest()
    {
        var bridges = PoiPick.MergeSegments(new[]
        {
            Seg("a", "", 0, 10), Seg("b", "Kapellbrücke", 10, 40), Seg("c", "", 40, 45),   // 45 m, one bridge
            Seg("d", "", 1000, 1012),                                                       // 12 m, too short
        }, PoiKind.Bridge, PoiPick.BridgeMinLength);
        var b = Assert.Single(bridges);
        Assert.Equal(("b", "Kapellbrücke", 45.0), (b.SourceId, b.Name, b.Size));
        Assert.Equal(25, b.E, 6);
    }

    [Fact]
    public void Two_footprints_of_one_name_within_100_m_are_one_place_whatever_order_they_come_in()
    {
        var rows = new[]
        {
            C(PoiKind.Church, "z", "Kirche St. Martin", 0, 0), C(PoiKind.Church, "a", "Kirche St. Martin", 40, 0),
            C(PoiKind.Church, "m", "Kirche St. Martin", 5000, 0), C(PoiKind.Church, "q", "Église", 20, 0),
        };
        var kept = PoiPick.Dedup(rows, PoiPick.DedupReach).Select(c => c.SourceId).Order().ToArray();
        Assert.Equal(new[] { "a", "m", "q" }, kept);
        Assert.Equal(kept, PoiPick.Dedup(rows.Reverse(), PoiPick.DedupReach).Select(c => c.SourceId).Order().ToArray());
    }

    [Fact]
    public void Lake_shores_join_into_a_ring_and_simplify_to_at_most_128_points()
    {
        // a 2 km circle of 720 points, cut into three shore lines, one reversed
        var pts = Enumerable.Range(0, 721).SelectMany(i => new[] { 1000 * Math.Cos(i * Math.PI / 360), 1000 * Math.Sin(i * Math.PI / 360) }).ToArray();
        double[] Part(int from, int to) => pts[(from * 2)..(to * 2 + 2)];
        var second = Part(200, 500);
        var reversed = new double[second.Length];
        for (int i = 0; i < second.Length; i += 2) { reversed[i] = second[second.Length - 2 - i]; reversed[i + 1] = second[second.Length - 1 - i]; }
        var rings = PoiPick.Rings(new[] { Part(0, 200), reversed, Part(500, 720), new[] { 5000.0, 5000, 5100, 5000 } });
        var ring = Assert.Single(rings);
        Assert.InRange(PoiPick.Area(ring), Math.PI * 1e6 * 0.999, Math.PI * 1e6);
        var simple = PoiPick.Simplify(ring);
        Assert.InRange(simple.Length / 2, 4, PoiPick.OutlineMaxPoints);
        Assert.Equal(simple[0], simple[^2]);
        Assert.InRange(PoiPick.Area(simple), Math.PI * 1e6 * 0.9, Math.PI * 1e6);
        Assert.True(PoiPick.Inside(simple, 0, 0));
        Assert.False(PoiPick.Inside(simple, 1100, 0));
        // a big lake's 5 000 points still come down to 128
        var big = Enumerable.Range(0, 5001).SelectMany(i => new[] { 20000 * Math.Cos(i * Math.PI / 2500) + 30 * Math.Sin(i * 0.7), 9000 * Math.Sin(i * Math.PI / 2500) }).ToArray();
        Assert.InRange(PoiPick.Simplify(big).Length / 2, 4, PoiPick.OutlineMaxPoints);
    }

    [Theory]
    [InlineData(PoiKind.LookoutTower, 900, 3000)]
    [InlineData(PoiKind.Pass, 2000, 3000)]
    [InlineData(PoiKind.SummitCross, 400, 2000)]
    [InlineData(PoiKind.Peak, 1797, 3945.5)]
    [InlineData(PoiKind.Peak, 4478, 7000)]
    [InlineData(PoiKind.CableCarStation, 2971, 5706.5)]
    public void A_viewpoint_reveals_3_km_or_more_the_higher_it_stands(PoiKind kind, double elevation, double radius)
    {
        Assert.Equal(radius, PoiPick.RevealRadius(kind, PoiFlags.Viewpoint, elevation), 1);
        Assert.Equal(0, PoiPick.RevealRadius(kind, PoiFlags.Named, elevation));
    }

    [Fact]
    public void Tiers_follow_the_plan()
    {
        Assert.Equal(2, PoiPick.Tier(new Candidate { Kind = PoiKind.Peak, Sort = "Hauptgipfel" }));
        Assert.Equal(3, PoiPick.Tier(new Candidate { Kind = PoiKind.Peak, Sort = "Huegel" }));
        Assert.Equal(2, PoiPick.Tier(new Candidate { Kind = PoiKind.Pass, Sort = "Strassenpass" }));
        Assert.Equal(3, PoiPick.Tier(new Candidate { Kind = PoiKind.Pass, Sort = "Pass" }));
        Assert.Equal(2, PoiPick.Tier(new Candidate { Kind = PoiKind.Lake, Size = 150_000 }));
        Assert.Equal(3, PoiPick.Tier(new Candidate { Kind = PoiKind.Lake, Size = 50_000 }));
        Assert.Equal(2, PoiPick.Tier(new Candidate { Kind = PoiKind.CableCarStation, Flags = PoiFlags.Top }));
        Assert.Equal(3, PoiPick.Tier(new Candidate { Kind = PoiKind.CableCarStation }));
        Assert.Equal(3, PoiPick.Tier(new Candidate { Kind = PoiKind.BusStop, Flags = PoiFlags.VillageStop }));
        Assert.Equal(2, PoiPick.Tier(new Candidate { Kind = PoiKind.Castle }));
    }

    [Theory]
    [InlineData("Cabane de Moiry", true)]
    [InlineData("Monte-Rosa-Hütte", true)]
    [InlineData("Capanna Regina Margherita", true)]
    [InlineData("Chamanna da Boval", true)]
    [InlineData("Kirche St. Martin", false)]
    public void A_hut_is_known_by_its_name(string name, bool hut) => Assert.Equal(hut, PoiPick.IsHut(name));
}

public class PoiCatalogueTests
{
    /// <summary>One POI on each side of the tile seam at E 2 600 000, and a lake over it.</summary>
    private static PoiCatalogue Seam()
    {
        var d = new PoiData();
        d.Cantons.Add(new PoiCanton("BE", 0, 3, 64));
        d.Pois.Add(new PoiRecord { Hash = 1, Kind = PoiKind.Peak, Canton = "BE", Tier = 2, E = 2_599_990, N = 1_200_500, Name = "West" });
        d.Pois.Add(new PoiRecord { Hash = 2, Kind = PoiKind.Peak, Canton = "BE", Tier = 2, E = 2_600_010, N = 1_200_500, Name = "East" });
        d.Pois.Add(new PoiRecord { Hash = 3, Kind = PoiKind.Chapel, Canton = "BE", E = 2_600_150, N = 1_200_500, Name = "Far" });
        d.Pois.Add(new PoiRecord
        {
            Hash = 4, Kind = PoiKind.Lake, Canton = "BE", Tier = 2, E = 2_603_000, N = 1_200_500, Name = "Lake",
            Outline = new[] { 2_600_100.0, 1_200_000, 2_606_000, 1_200_000, 2_606_000, 1_201_000, 2_600_100, 1_201_000, 2_600_100, 1_200_000 },
        });
        return new PoiCatalogue(d);
    }

    [Fact]
    public void Near_finds_points_across_a_tile_seam_and_lakes_by_their_box()
    {
        var c = Seam();
        var into = new List<Poi>();
        c.Near(2_600_000, 1_200_500, 50, into);
        Assert.Equal(new[] { "East", "West" }, into.Select(p => p.Name).Order());
        c.Near(2_600_000, 1_200_500, 120, into);
        Assert.Equal(new[] { "East", "Lake", "West" }, into.Select(p => p.Name).Order());
        c.Near(2_600_000, 1_200_500, 200, into, PoiKind.Chapel);
        Assert.Equal("Far", Assert.Single(into).Name);
        c.Near(2_600_000, 1_200_500, 200, into, PoiKind.Lake);
        Assert.Equal("Lake", Assert.Single(into).Name);
        c.Near(2_500_000, 1_100_000, 200, into);
        Assert.Empty(into);
    }

    [Fact]
    public void Near_allocates_nothing_into_a_reused_list()
    {
        var c = PoiCatalogue.Fixture();
        var into = new List<Poi>(32);
        c.Near(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN, 200, into);   // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) c.Near(PoiCatalogue.FixtureE + i % 7, PoiCatalogue.FixtureN, 200, into);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(4, into.Count);
    }

    [Fact]
    public void InTile_InCanton_OfKind_and_Totals_answer_from_the_file()
    {
        var c = Seam();
        Assert.Equal(new[] { "East", "Far" }, c.InTile(new TileId(2600, 1200)).ToArray().Select(p => p.Name).Order());
        Assert.Equal("West", c.InTile(new TileId(2599, 1200))[0].Name);
        Assert.Equal(0, c.InTile(new TileId(1, 1)).Length);
        Assert.Equal(4, c.InCanton("BE").Count);
        Assert.Empty(c.InCanton("UR"));
        Assert.Equal(2, c.OfKind(PoiKind.Peak).Count);
        Assert.Equal(new CantonTotals(0, 3, 64), c.Totals("BE"));
        Assert.Equal(3, c.Totals("BE").Landmarks);
        Assert.Equal(default, c.Totals("UR"));
    }

    [Fact]
    public void InLake_is_inside_the_outline_or_within_the_buffer_of_its_shore()
    {
        var lake = Seam().OfKind(PoiKind.Lake)[0];
        Assert.True(PoiCatalogue.InLake(lake, 2_603_000, 1_200_500, 0));
        Assert.False(PoiCatalogue.InLake(lake, 2_600_050, 1_200_500, 30));
        Assert.True(PoiCatalogue.InLake(lake, 2_600_050, 1_200_500, 60));
        Assert.False(PoiCatalogue.InLake(lake, 2_603_000, 1_202_000, 60));
        Assert.False(PoiCatalogue.InLake(Seam().OfKind(PoiKind.Peak)[0], 2_599_990, 1_200_500, 100));
    }

    [Fact]
    public void The_empty_catalogue_answers_nothing_and_the_fixture_is_a_round_trip_of_its_file()
    {
        Assert.Equal(0, PoiCatalogue.Empty.Count);
        Assert.Null(PoiCatalogue.Empty.Resolve("peak:x.0000000001"));
        var fixture = PoiCatalogue.Fixture();
        var file = PoiCatalogue.Load(PoiFile.Encode(PoiCatalogue.FixtureData()));
        Assert.Equal(fixture.All.Select(p => p.Id), file.All.Select(p => p.Id));
        Assert.Equal(fixture.Totals("VS"), file.Totals("VS"));
        Assert.Contains(fixture.All, p => p.Kind == PoiKind.Lake && p.Outline != null);
    }

    [Fact]
    public void Ids_and_slugs_do_not_depend_on_the_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-CH");
            Assert.Equal("peak:rigi-kulm.00000000ff", PoiId.Make(PoiKind.Peak, "Rigi Kulm", 255));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
