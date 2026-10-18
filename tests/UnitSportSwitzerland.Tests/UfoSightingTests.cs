using Godot;
using UnitSport.Farming;
using UnitSport.Occasions;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// UFO sightings and the cows they beam up (#863): the first-Friday schedule, the herds on a
/// pasture, a cow's walk, the saucers' paths and the planner's rolls (src/Occasions/UfoSighting.cs,
/// OccasionWeekdays.cs, src/Farming/HerdPlan.cs, CowMotion.cs; linked in).
/// </summary>
public class UfoSightingTests
{
    // ---- the schedule ---------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 10, 2)]   // October 2026 starts on a Thursday
    [InlineData(2026, 11, 6)]
    [InlineData(2026, 5, 1)]    // May 2026 starts on a Friday
    public void First_friday_is_found(int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), OccasionWeekdays.Nth(y, m, DayOfWeek.Friday, 1));

    [Fact]
    public void Last_saturday_counts_from_the_end() =>
        Assert.Equal(new DateOnly(2026, 10, 31), OccasionWeekdays.Nth(2026, 10, DayOfWeek.Saturday, -1));

    [Fact]
    public void Window_holds_the_friday_and_its_morning_after()
    {
        Assert.True(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 6), out int y));
        Assert.Equal(2026, y);
        Assert.True(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 7), out _));
        Assert.False(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 8), out _));
        Assert.False(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 13), out _));
        Assert.False(OccasionWeekdays.Contains("fri#1", "fri#1", new DateOnly(2026, 11, 7), out _));
    }

    [Fact]
    public void A_tail_spills_over_the_month_and_the_year()
    {
        // 2027-01-01 is a Friday; a window opening on the last Thursday of December carries over
        Assert.True(OccasionWeekdays.Contains("thu#-1", "+2", new DateOnly(2027, 1, 1), out int y));
        Assert.Equal(2026, y);
    }

    [Fact]
    public void Date_windows_are_not_weekday_tokens()
    {
        Assert.False(OccasionWeekdays.IsToken("09-01"));
        Assert.False(OccasionWeekdays.IsToken("2026-02-06"));
        Assert.True(OccasionWeekdays.IsToken("FRI#1"));
        Assert.False(OccasionWeekdays.IsToken("fri#0"));
    }

    // ---- herds ----------------------------------------------------------------------------------

    private static FieldTile Pasture(uint id = 77, CropKind crop = CropKind.Pasture, float size = 200f) =>
        FieldTile.Build(new TileId(2600, 1200), new[]
        {
            new FieldPolygon(id, crop, FieldSource.Lwb, 0, new[] { new float[] { 100, 100, 100 + size, 100, 100 + size, 100 + size, 100, 100 + size } }),
            new FieldPolygon(id + 1, CropKind.Wheat, FieldSource.Lwb, 0, new[] { new float[] { 500, 500, 700, 500, 700, 700, 500, 700 } }),
        });

    [Fact]
    public void A_pasture_gets_herds_inside_it_and_wheat_none()
    {
        var tile = Pasture();
        var herds = HerdPlan.For(tile);
        Assert.InRange(herds.Count, 1, HerdPlan.MaxHerds);
        foreach (var h in herds)
        {
            Assert.InRange(h.Count, 2, 10);
            foreach (var s in h.Spots)
            {
                double e = h.E + s.X - tile.Id.MinE, n = h.N + s.Y - tile.Id.MinN;
                Assert.InRange(e, 100, 300);
                Assert.InRange(n, 100, 300);
            }
        }
    }

    [Fact]
    public void Herds_are_the_same_every_time()
    {
        var a = HerdPlan.For(Pasture());
        var b = HerdPlan.For(Pasture());
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].E, b[i].E);
            Assert.Equal(a[i].Count, b[i].Count);
            Assert.Equal(a[i].Breed, b[i].Breed);
        }
    }

    [Fact]
    public void A_small_pasture_gets_none() => Assert.Empty(HerdPlan.For(Pasture(size: 40f)));

    // ---- a cow ----------------------------------------------------------------------------------

    private static Herd Herd() => HerdPlan.For(Pasture())[0];

    [Fact]
    public void A_cow_walks_slowly_and_stays_on_its_pasture()
    {
        var h = Herd();
        var was = CowMotion.At(h, 0, 1000);
        bool walked = false, grazed = false;
        for (double t = 1000; t < 1600; t += 0.25)
        {
            var p = CowMotion.At(h, 0, t);
            // never faster than a brisk walk, never off the herd's patch (spread + the cow's nudge)
            Assert.True((p.Offset - was.Offset).Length() / 0.25f < 3.5f, $"jump at {t}: {was} -> {p}");
            Assert.True(p.Offset.Length() < HerdPlan.Spread * 2 + 3f);
            walked |= p.Gait > 0.5f;
            grazed |= p.Head > 0.5f;
            was = p;
        }
        Assert.True(walked && grazed);
    }

    [Fact]
    public void Cows_of_one_herd_do_not_move_in_step()
    {
        var h = Herd();
        var a = CowMotion.At(h, 0, 5000);
        var b = CowMotion.At(h, 1, 5000);
        Assert.NotEqual(a.Offset, b.Offset);
    }

    [Fact]
    public void Yaw_faces_the_walk()
    {
        Assert.Equal(0f, CowMotion.YawOf(new Vector2(0, 1)), 3);          // north: -Z, yaw 0
        Assert.Equal(-Mathf.Pi / 2, CowMotion.YawOf(new Vector2(1, 0)), 3); // east: +X
    }

    // ---- sightings ------------------------------------------------------------------------------

    private static Sighting Plan(SightingKind kind, int count = 1) =>
        new(1, kind, 100, SightingPath.LengthOf(kind), 2600500, 1200500, 500, 1.0f, count, 1234);

    [Fact]
    public void Nothing_is_shown_before_or_after()
    {
        var s = Plan(SightingKind.Flyover, 3);
        Assert.False(SightingPath.Pose(s, 0, 99).Shown);
        Assert.False(SightingPath.Pose(s, 0, s.End + 1).Shown);
        Assert.True(SightingPath.Pose(s, 2, 130).Shown);
        Assert.False(SightingPath.Pose(s, 3, 130).Shown);
    }

    [Fact]
    public void An_abduction_beams_over_its_anchor_within_reach()
    {
        var s = Plan(SightingKind.Abduction, 2);
        var p = SightingPath.Pose(s, 0, s.Start + (SightingPath.AbductBeamOn + SightingPath.AbductBeamOff) / 2);
        Assert.True(p.Beam);
        Assert.True(new Vector2(p.Offset.X, p.Offset.Z).Length() < 1.5f);
        Assert.InRange(p.Offset.Y, 20f, UnitSport.Player.TractorBeamMath.Reach - 10f);
        Assert.False(SightingPath.Pose(s, 0, s.Start + SightingPath.AbductBeamOff + 1).Beam);
        Assert.False(SightingPath.Pose(s, 1, s.Start + 30).Beam);   // the escort never beams
        Assert.True(SightingPath.Beaming(s, s.Start + 30));
    }

    [Fact]
    public void A_landing_stands_on_its_legs_with_the_ramp_down_while_boardable()
    {
        var s = Plan(SightingKind.Landing);
        for (float tau = SightingPath.BoardFrom; tau <= SightingPath.BoardTo; tau += 2f)
        {
            var p = SightingPath.Pose(s, 0, s.Start + tau);
            Assert.Equal(Vector3.Zero, p.Offset);
            Assert.Equal(1f, p.Legs);
            Assert.True(p.Ramp > 0.99f);
            Assert.True(SightingPath.Boardable(s, s.Start + tau));
        }
        Assert.False(SightingPath.Boardable(s, s.Start + SightingPath.LandLift));
        Assert.True(SightingPath.Pose(s, 0, s.Start + 5).Offset.Y > 100f);
        Assert.True(SightingPath.Pose(s, 0, s.End - 1).Offset.Y > 200f);
    }

    [Fact]
    public void Paths_are_smooth()
    {
        foreach (var kind in Enum.GetValues<SightingKind>())
        {
            var s = Plan(kind, SightingPlanner.CountOf(kind, 99) + 1);
            for (int c = 0; c < s.Count; c++)
            {
                var was = SightingPath.Pose(s, c, s.Start);
                for (double t = s.Start + 0.1; t < s.End; t += 0.1)
                {
                    var p = SightingPath.Pose(s, c, t);
                    float speed = (p.Offset - was.Offset).Length() / 0.1f;
                    Assert.True(speed < 250f, $"{kind} craft {c} at {t - s.Start:F1}: {speed:F0} m/s");
                    was = p;
                }
            }
        }
    }

    [Fact]
    public void A_flyover_crosses_over_the_anchor()
    {
        var s = Plan(SightingKind.Flyover, 2);
        var mid = SightingPath.Pose(s, 0, s.Start + s.Length / 2);
        Assert.True(new Vector2(mid.Offset.X, mid.Offset.Z).Length() < 80f);
        Assert.True(mid.Offset.Y > 150f);
        var start = SightingPath.Pose(s, 0, s.Start + 0.5);
        // came in along its heading: from the opposite side
        Assert.True(start.Offset.Dot(SightingPath.Dir(s.Heading)) < -900f);
    }

    [Fact]
    public void Planner_rolls_are_stable_and_varied()
    {
        var kinds = new HashSet<SightingKind?>();
        for (long slot = 0; slot < 200; slot++)
        {
            var a = SightingPlanner.Roll(1300, 600, slot, out uint sa);
            var b = SightingPlanner.Roll(1300, 600, slot, out uint sb);
            Assert.Equal(a, b);
            Assert.Equal(sa, sb);
            kinds.Add(a);
        }
        Assert.Equal(4, kinds.Count);   // nothing, and all three kinds
        Assert.True(SightingPlanner.Night(22) && SightingPlanner.Night(3) && !SightingPlanner.Night(12) && !SightingPlanner.Night(20.9));
    }
}
