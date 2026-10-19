using Godot;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertcheck</c> (#885): the concert generator without a world. The same seed makes the same
/// event; a few hundred seeds reach every layout, screen set and genre; every rig patches without a
/// fixture straddling a universe; every stage stands inside the stadium's seating front and clear of
/// the stands; the desk's DMX decodes back to what it wrote (16-bit pan and tilt, colour), and each
/// section of a song lights something (or nothing, the blackout). Prints the first seeds' events, so
/// a seed can be picked for <c>--concert N</c>. RESULT line last.
/// </summary>
public static class ConcertCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--concertcheck") >= 0;

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, FormattableString what)
        {
            if (!ok) failures++;
            GD.Print($"[concertcheck] {(ok ? "ok  " : "FAIL")} {FormattableString.Invariant(what)}");
        }

        var s = StadiumCatalog.Geneva;
        Expect(ConcertPlanner.Plan(s, 42).ToString() == ConcertPlanner.Plan(s, 42).ToString(), $"seed 42 makes the same event twice");
        Expect(ConcertPlanner.Seed(s.Name, new DateTime(2026, 10, 19)) == ConcertPlanner.Seed(s.Name, new DateTime(2026, 10, 19))
            && ConcertPlanner.Seed(s.Name, new DateTime(2026, 10, 19)) != ConcertPlanner.Seed(s.Name, new DateTime(2026, 10, 20)),
            $"tonight's seed is stable through the day and changes the next");

        var layouts = new HashSet<StageLayout>();
        var screens = new HashSet<ScreenSet>();
        var genres = new HashSet<Genre>();
        int worstChannels = 0, worstFixtures = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            var e = ConcertPlanner.Plan(s, seed);
            layouts.Add(e.Layout); screens.Add(e.Screens); genres.Add(e.Genre);
            var plan = new StagePlan(e);
            var patch = plan.Patch;
            worstChannels = Math.Max(worstChannels, patch.ChannelsUsed);
            worstFixtures = Math.Max(worstFixtures, patch.Fixtures.Count);
            if (seed <= 24) GD.Print($"[concertcheck] seed {seed}: {e}");
            foreach (var f in patch.Fixtures)
                if (f.Address + f.Type.Footprint > DmxPatch.Channels)
                    Expect(false, $"seed {seed}: fixture {f.Id} straddles universe {f.Universe}");
            // the stage on the pitch: inside the seating front (a little run-off left), under the roof
            foreach (var b in plan.Boxes)
            {
                float c = MathF.Abs(MathF.Cos(b.Yaw)), sn = MathF.Abs(MathF.Sin(b.Yaw));
                float ex = c * b.Size.X / 2 + sn * b.Size.Z / 2, ez = sn * b.Size.X / 2 + c * b.Size.Z / 2;
                if (MathF.Abs(b.Centre.X) + ex > s.FrontHalfU + 0.5f || MathF.Abs(b.Centre.Z) + ez > s.FrontHalfV + 0.5f
                    || b.Centre.Y + b.Size.Y / 2 > s.RoofInner - 1f)
                {
                    Expect(false, $"seed {seed} ({e.Layout}): a box at {b.Centre} size {b.Size} leaves the pitch or reaches the roof");
                    break;
                }
            }
        }
        Expect(layouts.Count == Enum.GetValues<StageLayout>().Length, $"300 seeds reach every layout ({layouts.Count})");
        Expect(screens.Count == Enum.GetValues<ScreenSet>().Length, $"and every screen set ({screens.Count})");
        Expect(genres.Count == Enum.GetValues<Genre>().Length, $"and every genre ({genres.Count})");
        Expect(worstChannels <= 4 * DmxPatch.Channels, $"the biggest rig: {worstFixtures} fixtures on {worstChannels} channels");

        // the desk: DMX written decodes back, and the sections light the rig as they should
        var ev = ConcertPlanner.Plan(s, 7);
        var pl = new StagePlan(ev);
        var desk = new LightDesk(pl);
        var head = pl.Patch.Fixtures.First(f => f.Type.Kind == FixtureKind.Beam);
        pl.Patch.Set16(head, Attr.Pan, Attr.PanFine, 0.73215f);
        Expect(MathF.Abs(pl.Patch.Get16(head, Attr.Pan, Attr.PanFine) - 0.73215f) < 1f / 65535f, $"16-bit pan round-trips");
        var lit = new Dictionary<ShowSection, float>();
        for (double t = -10; t < 900; t += 0.5)
        {
            desk.Write(t);
            float sum = 0;
            foreach (var f in pl.Patch.Fixtures)
                if (f.Type.Kind is FixtureKind.Spot or FixtureKind.Beam or FixtureKind.Wash) sum += pl.Patch.Get01(f, Attr.Dimmer);
            lit[desk.Section] = Math.Max(lit.GetValueOrDefault(desk.Section), sum);
        }
        Expect(lit.GetValueOrDefault(ShowSection.Blackout, -1f) == 0f, $"the blackout between songs is dark");
        Expect(lit.GetValueOrDefault(ShowSection.Chorus) > lit.GetValueOrDefault(ShowSection.Calm) && lit.GetValueOrDefault(ShowSection.Calm) > 0,
            $"a chorus lights more than a calm verse ({Fmt(lit)})");
        Expect(lit.ContainsKey(ShowSection.WalkIn) && lit.ContainsKey(ShowSection.Peak) && lit.ContainsKey(ShowSection.Groove),
            $"walk-in, groove and peak all play");

        GD.Print(failures == 0 ? "[concertcheck] RESULT: ok" : $"[concertcheck] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static string Fmt(Dictionary<ShowSection, float> d) =>
        string.Join(", ", d.Select(kv => FormattableString.Invariant($"{kv.Key} {kv.Value:F1}")));
}
