using Godot;
using UnitSport.Audio;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The sound of a building site (#617): <see cref="SiteSfx"/> synthesises it, <see cref="SiteSoundPlan"/>
/// decides which sound, where and when. Nobody can hear a unit test, so the clips are judged by
/// numbers (bounded, not silent, the pitch they are meant to have, the beeper's rhythm) and the
/// plan by what it must never do: make a sound outside working hours, or put one off the site.
/// </summary>
public class SiteSoundTests
{
    private static readonly SiteSoundKind[] Kinds = Enum.GetValues<SiteSoundKind>();

    private static float[] Direct(SiteSoundKind kind, int v) => kind switch
    {
        SiteSoundKind.Hammer => SiteSfx.Hammer(v),
        SiteSoundKind.Grinder => SiteSfx.Grinder(v),
        SiteSoundKind.Vibrator => SiteSfx.Vibrator(v),
        SiteSoundKind.Beeper => SiteSfx.Beeper(3 + v, v),
        SiteSoundKind.Radio => SiteSfx.Radio(v),
        _ => SiteSfx.CraneMotor(v),
    };

    public static IEnumerable<object[]> EveryClip() =>
        Kinds.SelectMany(k => Enumerable.Range(0, SiteSfx.Variants(k)).Select(v => new object[] { k, v }));

    private static double Rms(float[] s, int from = 0, int to = -1)
    {
        if (to < 0) to = s.Length;
        double sum = 0;
        for (int i = from; i < to; i++) sum += s[i] * s[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>The signal's energy at one frequency (Goertzel), over a window.</summary>
    private static double Power(float[] s, double hz, int from, int to)
    {
        double w = 2 * Math.PI * hz / SiteSfx.Rate, c = 2 * Math.Cos(w), a = 0, b = 0;
        for (int i = from; i < to; i++)
        {
            double n = s[i] + c * a - b;
            b = a;
            a = n;
        }
        return a * a + b * b - c * a * b;
    }

    [Theory]
    [MemberData(nameof(EveryClip))]
    public void Every_clip_is_made_bounded_and_not_silent(SiteSoundKind kind, int variant)
    {
        var s = Direct(kind, variant);
        Assert.NotEmpty(s);
        foreach (float v in s)
        {
            Assert.False(float.IsNaN(v) || float.IsInfinity(v));
            Assert.InRange(v, -1f, 1f);
        }
        Assert.True(Rms(s) > 0.02, $"{kind} {variant} is nearly silent: rms {Rms(s):F4}");
        Assert.True(s.Max(Math.Abs) > 0.5f, $"{kind} {variant} never gets loud");
    }

    [Theory]
    [MemberData(nameof(EveryClip))]
    public void A_clip_is_the_same_every_time(SiteSoundKind kind, int variant) =>
        Assert.Equal(Direct(kind, variant), Direct(kind, variant));

    [Fact]
    public void The_baked_clips_are_the_ones_the_generators_make()
    {
        foreach (var kind in Kinds)
        {
            var clips = SiteSfx.Clips(kind);
            Assert.Equal(SiteSfx.Variants(kind), clips.Length);
            for (int v = 0; v < clips.Length; v++) Assert.Equal(Direct(kind, v), clips[v]);
            Assert.Same(clips, SiteSfx.Clips(kind));   // baked once
        }
    }

    [Fact]
    public void The_variants_of_a_sound_differ()
    {
        foreach (var kind in Kinds)
            for (int v = 1; v < SiteSfx.Variants(kind); v++)
                Assert.NotEqual(Direct(kind, 0), Direct(kind, v));
    }

    [Fact]
    public void The_clips_are_as_long_as_the_sound_they_are()
    {
        for (int v = 0; v < SiteSfx.GrinderVariants; v++)
            Assert.InRange(SiteSfx.Grinder(v).Length / (double)SiteSfx.Rate, 2.0, 4.0);
        for (int v = 0; v < SiteSfx.VibratorVariants; v++)
            Assert.InRange(SiteSfx.Vibrator(v).Length / (double)SiteSfx.Rate, 3.0, 6.0);
        for (int v = 0; v < SiteSfx.HammerVariants; v++)
            Assert.InRange(SiteSfx.Hammer(v).Length / (double)SiteSfx.Rate, 0.2, 0.6);
    }

    [Fact]
    public void The_vibrator_hums_at_about_150_hertz()
    {
        var s = SiteSfx.Vibrator(0);
        int from = SiteSfx.Rate, to = SiteSfx.Rate * 2;
        double best = 0, bestHz = 0;
        for (double hz = 100; hz <= 220; hz += 2)
        {
            double p = Power(s, hz, from, to);
            if (p > best) { best = p; bestHz = hz; }
        }
        Assert.InRange(bestHz, 135, 165);
        Assert.True(best > 8 * Power(s, 420, from, to), "the buzz should stand clear of the band above it");
    }

    [Fact]
    public void The_beeper_beeps_a_second_on_a_second_off_at_about_a_kilohertz()
    {
        const int beeps = 4;
        var s = SiteSfx.Beeper(beeps, 0);
        int rate = SiteSfx.Rate;
        Assert.Equal((beeps - 1) * rate + rate / 2, s.Length);
        for (int b = 0; b < beeps; b++)
        {
            int start = b * rate;
            Assert.True(Rms(s, start + rate / 10, start + rate * 4 / 10) > 0.3, $"beep {b} is on");
            if (b < beeps - 1) Assert.True(Rms(s, start + rate * 6 / 10, start + rate * 9 / 10) < 1e-4, $"after beep {b} it is quiet");
        }
        double near = Power(s, 1000, rate / 10, rate * 4 / 10);
        Assert.True(near > 20 * Power(s, 500, rate / 10, rate * 4 / 10));
        Assert.True(near > 20 * Power(s, 1500, rate / 10, rate * 4 / 10));
    }

    [Fact]
    public void The_radio_is_band_limited()
    {
        var s = SiteSfx.Radio(0);
        int n = s.Length;
        // a squeezed-in tin-can sound: little under 150 Hz and almost nothing above 5 kHz
        double low = 0, mid = 0, high = 0;
        for (double hz = 40; hz < 150; hz += 10) low += Power(s, hz, 0, n);
        for (double hz = 400; hz < 3000; hz += 100) mid += Power(s, hz, 0, n);
        for (double hz = 5200; hz < 9000; hz += 200) high += Power(s, hz, 0, n);
        Assert.True(mid / 26 > 5 * (low / 11), "most of a radio's energy is in the middle");
        Assert.True(mid / 26 > 3 * (high / 19), "and not up in the hiss");
    }

    // ---- the plan ---------------------------------------------------------------------------

    private static Building Box(float cx, float cz, float w, float d, float h, byte floors)
    {
        var c = new Vector2(cx, cz);
        var corners = new[] { c + new Vector2(-w / 2, -d / 2), c + new Vector2(w / 2, -d / 2), c + new Vector2(w / 2, d / 2), c + new Vector2(-w / 2, d / 2) };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(P(a, 0), P(b, 0), P(b, h));
            Tri(P(a, 0), P(b, h), P(a, h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    /// <summary>Sites of every phase: a foundations slab, a shell three storeys up, a topped-out block, a house plot.</summary>
    private static List<ConstructionSite> Sites()
    {
        var sites = new List<ConstructionSite>();
        (float W, float D, float H, byte Floors)[] shapes = { (34, 22, 1.3f, 6), (34, 22, 9.5f, 6), (34, 22, 18f, 6), (12, 10, 2f, 2), (60, 20, 12f, 5) };
        for (int i = 0; i < 24; i++)
        {
            var (w, d, h, f) = shapes[i % shapes.Length];
            var b = Box(250, 300, w, d, h, f);
            var site = ConstructionSites.Plan($"2537_1152_{i}", b, PlanBox.Of(b)!.Value, new Vector2(250, 340), _ => false);
            if (site != null) sites.Add(site);
        }
        return sites;
    }

    private static readonly List<ConstructionSite> All = Sites();

    [Fact]
    public void The_test_sites_cover_every_phase_and_have_offices_and_cranes()
    {
        Assert.Contains(All, s => s.Phase == SitePhase.Foundations);
        Assert.Contains(All, s => s.Phase == SitePhase.Shell);
        Assert.Contains(All, s => s.Phase == SitePhase.ToppedOut);
        Assert.Contains(All, s => s.Cranes.Count > 0 && s.Zones.Any(z => z.Kind == SiteZoneKind.Office));
    }

    [Theory]
    [InlineData(0.0, 2L)]
    [InlineData(6.99, 2L)]
    [InlineData(12.0, 2L)]
    [InlineData(12.99, 2L)]
    [InlineData(17.0, 2L)]
    [InlineData(23.5, 2L)]
    [InlineData(10.0, 6L)]    // Sunday
    [InlineData(10.0, 13L)]
    [InlineData(10.0, -1L)]   // the day before the clock's first is a Sunday too
    public void A_site_makes_no_sound_outside_working_hours_and_on_sundays(double hour, long day)
    {
        Assert.False(SiteSoundPlan.Audible(hour, day));
        foreach (var site in All)
            for (long step = 0; step < 60; step++)
                Assert.Null(SiteSoundPlan.Next(site, step, hour, day));
    }

    [Theory]
    [InlineData(7.0, 0L)]
    [InlineData(11.99, 3L)]
    [InlineData(13.0, 4L)]
    [InlineData(16.99, 5L)]
    public void A_site_always_has_a_sound_to_make_in_working_hours(double hour, long day)
    {
        Assert.True(SiteSoundPlan.Audible(hour, day));
        foreach (var site in All)
            for (long step = 0; step < 60; step++)
                Assert.NotNull(SiteSoundPlan.Next(site, step, hour, day));
    }

    [Fact]
    public void The_working_hours_are_the_cranes()
    {
        for (long day = -8; day < 14; day++)
            for (double hour = 0; hour < 24; hour += 0.25)
                Assert.Equal(CraneMotion.Working(hour, day), SiteSoundPlan.Audible(hour, day));
    }

    [Fact]
    public void Every_sound_is_on_the_site_and_above_the_ground()
    {
        foreach (var site in All)
            for (long step = 0; step < 200; step++)
            {
                var ev = SiteSoundPlan.Next(site, step, 9.0, 1)!.Value;
                var plan = new Vector2(ev.At.X, ev.At.Z);
                Assert.True(site.Area.Contains(plan, 0.5f), $"{site.Key} step {step}: {ev.Kind} at {plan} is off the site");
                Assert.True(ev.At.Y >= site.Base, $"{ev.Kind} is under the ground");
                Assert.True(ev.At.Y <= site.Base + site.TargetHeight + 25f, $"{ev.Kind} is above everything");
            }
    }

    [Fact]
    public void Each_sound_is_where_it_is_made()
    {
        foreach (var site in All)
            for (long step = 0; step < 200; step++)
            {
                var ev = SiteSoundPlan.Next(site, step, 9.0, 1)!.Value;
                var plan = new Vector2(ev.At.X, ev.At.Z);
                switch (ev.Kind)
                {
                    case SiteSoundKind.Hammer or SiteSoundKind.Grinder or SiteSoundKind.Vibrator:
                        Assert.True(new SiteRect(site.Box.Center, site.Box.AxisU, site.Box.Width, site.Box.Depth).Contains(plan, 0.1f), "on the building");
                        break;
                    case SiteSoundKind.Radio:
                        Assert.True(site.Zones.Any(z => z.Kind == SiteZoneKind.Office && z.Rect.Contains(plan, 0.1f)), "at the office");
                        break;
                    case SiteSoundKind.CraneMotor:
                        Assert.Contains(site.Cranes, c => c.Base.DistanceTo(plan) < 0.1f);
                        break;
                    case SiteSoundKind.Beeper:
                        Assert.False(new SiteRect(site.Box.Center, site.Box.AxisU, site.Box.Width, site.Box.Depth).Contains(plan), "in the yard, not in the building");
                        break;
                }
            }
    }

    [Fact]
    public void A_site_never_offers_what_it_has_no_place_for()
    {
        foreach (var site in All)
        {
            bool office = site.Zones.Any(z => z.Kind == SiteZoneKind.Office), crane = site.Cranes.Count > 0;
            for (long step = 0; step < 300; step++)
            {
                var kind = SiteSoundPlan.Next(site, step, 14.0, 2)!.Value.Kind;
                if (!office) Assert.NotEqual(SiteSoundKind.Radio, kind);
                if (!crane) Assert.NotEqual(SiteSoundKind.CraneMotor, kind);
            }
        }
        Assert.Contains(All, s => s.Cranes.Count == 0);
    }

    [Fact]
    public void The_same_inputs_give_the_same_sound()
    {
        foreach (var site in All)
            for (long step = 0; step < 50; step++)
                Assert.Equal(SiteSoundPlan.Next(site, step, 9.5, 1), SiteSoundPlan.Next(site, step, 9.5, 1));
    }

    [Fact]
    public void A_site_is_not_always_the_same_sound_and_its_phase_shows()
    {
        var full = All.First(s => s.Cranes.Count > 0 && s.Zones.Any(z => z.Kind == SiteZoneKind.Office) && s.Phase == SitePhase.Shell);
        var kinds = Enumerable.Range(0, 300).Select(i => SiteSoundPlan.Next(full, i, 9.0, 1)!.Value.Kind).ToHashSet();
        Assert.Equal(Kinds.Length, kinds.Count);

        int Count(SitePhase phase, SiteSoundKind kind) => All.Where(s => s.Phase == phase).Sum(s =>
            Enumerable.Range(0, 400).Count(i => SiteSoundPlan.Next(s, i, 9.0, 1)!.Value.Kind == kind));
        // pouring concrete is a foundations job, cutting and grinding the finishing of a topped-out one
        Assert.True(Count(SitePhase.Foundations, SiteSoundKind.Vibrator) > 3 * Count(SitePhase.ToppedOut, SiteSoundKind.Vibrator));
        Assert.True(Count(SitePhase.ToppedOut, SiteSoundKind.Grinder) > Count(SitePhase.Foundations, SiteSoundKind.Grinder));
    }

    [Fact]
    public void A_sound_waits_for_itself_to_finish()
    {
        foreach (var site in All)
            for (long step = 0; step < 100; step++)
            {
                var ev = SiteSoundPlan.Next(site, step, 9.0, 1)!.Value;
                Assert.InRange(ev.Strikes, 1, 9);
                Assert.True(ev.Wait > 3f);
                // a hammer burst's blows are over before the site is asked again
                Assert.True(ev.Wait > (ev.Strikes - 1) * ev.StrikeGap * 1.15f);
                if (ev.Kind != SiteSoundKind.Hammer) Assert.Equal(1, ev.Strikes);
                Assert.True(ev.Db is < 0f and > -20f);
            }
    }

    [Fact]
    public void The_distance_to_a_site_is_the_distance_to_its_fence()
    {
        var site = All[0];
        Assert.Equal(0f, SiteSoundPlan.Distance(site, site.Area.Center));
        Assert.Equal(0f, SiteSoundPlan.Distance(site, site.Box.Center));
        var out40 = site.Area.Center + site.Area.AxisU * (site.Area.Width / 2 + 40f);
        Assert.Equal(40f, SiteSoundPlan.Distance(site, out40), 0.01f);
        var diagonal = site.Area.Center + site.Area.AxisU * (site.Area.Width / 2 + 30f) + site.Area.AxisV * (site.Area.Depth / 2 + 40f);
        Assert.Equal(50f, SiteSoundPlan.Distance(site, diagonal), 0.01f);
    }
}
