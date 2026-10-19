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

        // the music: a setlist from the library (no personal CD), and a song's sections drive the desk
        var lib = new[]
        {
            new Audio.Cd.CdInfo(3, "a", 200f, 120f, 0.1f, Audio.Cd.MusicStyle.Pop, 0.5f),
            new Audio.Cd.CdInfo(-5, "personal", 200f, 120f, 0.1f, Audio.Cd.MusicStyle.Pop, 0.5f),
            new Audio.Cd.CdInfo(4, "too short", 12f, 120f, 0.1f, Audio.Cd.MusicStyle.Pop, 0.5f),
            new Audio.Cd.CdInfo(9, "b", 180f, 95f, 0.2f, Audio.Cd.MusicStyle.Rock, 0.5f),
        };
        var set = ConcertMusic.SetlistOf(ev, lib);
        Expect(set.Length == 2 && set.All(c => c.Id is 3 or 9), $"the setlist takes the library's songs, not a personal or a 12 s one ({string.Join(",", set.Select(c => c.Id))})");
        desk.Music = new MusicMoment(ShowSection.Peak, 37.5f, 0.9f, 0.8f, 3, true, 0f);
        desk.Write(100);
        Expect(desk.Section == ShowSection.Peak && MathF.Abs(desk.Beat - 37.5f) < 1e-4f && desk.SectionBar == 3 && desk.LastSection,
            $"a song's section and beat drive the desk ({desk.Section}, beat {desk.Beat})");
        desk.HeldSection = ShowSection.Calm;
        desk.Write(100);
        Expect(desk.Section == ShowSection.Calm, $"and an operator's held section wins over the song");
        desk.HeldSection = null;

        // the song's finer bands (CD analysis v2): a build lifts the beams and runs the strobes, the drop
        // hits the blinders and fires the CO2
        Fixture[] Of(RigGroup g) => pl.Patch.Fixtures.Where(f => f.Group == g).ToArray();
        float Mean(RigGroup g, Func<Fixture, float> v) => Of(g) is { Length: > 0 } fs ? fs.Average(v) : 0f;
        float Tilt() => Mean(RigGroup.MidTruss, f => MathF.Abs(pl.Patch.Get01(f, Attr.Tilt) - 0.5f));
        float Strobe() => Mean(RigGroup.Strobes, f => pl.Patch.Get01(f, Attr.Dimmer));
        float Blind() => Mean(RigGroup.Blinders, f => pl.Patch.Get(f, 0) / 255f);
        float Co2() => Mean(RigGroup.Co2, f => pl.Patch.Get01(f, Attr.Fire));
        var groove = new MusicMoment(ShowSection.Groove, 40.1f, 0.3f, 0.7f, 2, false, 0f, HasBands: true);
        desk.Music = groove;
        desk.Write(100);
        float tilt0 = Tilt(), strobe0 = Strobe(), blind0 = Blind();
        desk.Music = groove with { Build = 0.9f };
        desk.Write(100);
        float tilt1 = Tilt(), strobe1 = Strobe();
        desk.Music = groove with { SinceDrop = 0.1f };
        desk.Write(100);
        float blind1 = Blind(), co2 = Co2();
        desk.Music = groove with { Snare = 0.9f };
        desk.Write(100);
        float snare = Strobe();
        bool hasStrobes = Of(RigGroup.Strobes).Length > 0, hasBlinders = Of(RigGroup.Blinders).Length > 0, hasCo2 = Of(RigGroup.Co2).Length > 0;
        Expect(tilt1 > tilt0 + 0.05f && (!hasStrobes || strobe1 > strobe0 + 0.5f),
            $"a build lifts the beams (tilt {tilt0:F2} -> {tilt1:F2}) and runs the strobes ({strobe0:F2} -> {strobe1:F2})");
        Expect((!hasBlinders || blind1 > blind0 + 0.5f) && (!hasCo2 || co2 > 0.5f),
            $"the drop hits the blinders ({blind0:F2} -> {blind1:F2}) and fires the CO2 ({co2:F2})");
        Expect(!hasStrobes || snare > strobe0 + 0.5f, $"a snare hit pops the strobes ({strobe0:F2} -> {snare:F2})");

        // a strobe burst at the end of a build: the strobes at 12 Hz, the heads' shutters strobing with them
        desk.Music = groove with { Build = 0.9f };
        desk.Write(100);
        float rate = Mean(RigGroup.Strobes, f => pl.Patch.Get01(f, Attr.Rate));
        var beam = Of(RigGroup.MidTruss).FirstOrDefault(f => f.Type.Offset(Attr.Shutter) >= 0 && pl.Patch.Get01(f, Attr.Dimmer) > 0.05f);
        int shutter = beam != null ? pl.Patch.Get(beam, beam.Type.Offset(Attr.Shutter)) : -1;
        Expect(desk.Strobing && (!hasStrobes || MathF.Abs(1f + 24f * rate - 12f) < 0.3f) && (beam == null || shutter is > 16 and < 132),
            $"a build's last quarter strobes: 12 Hz ({1f + 24f * rate:F1} Hz), the heads' shutters too ({shutter})");
        desk.Music = groove;
        desk.Write(100);
        Expect(!desk.Strobing, $"and a plain groove does not");
        desk.Music = null;

        GD.Print(failures == 0 ? "[concertcheck] RESULT: ok" : $"[concertcheck] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static string Fmt(Dictionary<ShowSection, float> d) =>
        string.Join(", ", d.Select(kv => FormattableString.Invariant($"{kv.Key} {kv.Value:F1}")));
}
