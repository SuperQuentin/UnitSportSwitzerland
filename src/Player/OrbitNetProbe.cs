using System;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>--orbitnetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/orbitnetcheck.sh</c>, #1430 H5): two
/// saucers put on orbit 400 km up by an admin's <c>/orbit</c> (the server's check, then <c>OrbitForced</c> to
/// each pilot's client), side by side, everything checked on the remote peer.
/// <list type="bullet">
/// <item>both take a saucer on the ground, 16 m apart, and ask for <c>/orbit 400</c> together; B then joins A's orbit,
/// 16 m beside A's copy (each orbit starts when its own reply comes back, which may be a second apart);</item>
/// <item>each flies its own orbit in doubles on its own client and sees the other's copy beside it,
/// at 7.7 km/s, drawn without a jump for a minute;</item>
/// <item>A thrusts down out of orbit: B sees it fall away; A comes down into the hover below 50 km.</item>
/// </list>
/// Both log in as admin (<c>--admin-password test</c>): <c>/orbit</c> is an admin's online.
/// </summary>
public partial class OrbitNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--orbitnetcheck");

    public OrbitNetProbe(ItemController items) : base(items, "orbitnet", "OR") { }
    public OrbitNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private float _down;
    private float _lastApart;
    private double _logAt;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Other() != null))
        {
            await Finish(0);
            return;
        }
        await Run(Me!);
        await Finish(2);
    }

    /// <summary>How far the other saucer is drawn from this one, m; infinite when it is not drawn here (out of view).</summary>
    private float Apart(FootPlayer me) =>
        Other() is { } o && IsInstanceValid(o) && o.Ride == RideKind.Ufo ? o.GlobalPosition.DistanceTo(me.GlobalPosition) : float.PositiveInfinity;

    /// <summary>
    /// Puts <paramref name="me"/>'s saucer on the orbit of <paramref name="other"/>'s copy: this saucer's own orbit (the same
    /// /orbit, from the same spot, the same heading) turned about its pole by the distance along the track to where the copy
    /// is drawn here (its sender's present, #1869). Not from the copy's velocity: a published velocity carries up to ~1 % of
    /// the frame's share of the Earth's turn, and a saucer set from it flew off at 110 m/s. The set orbit takes the body there.
    /// </summary>
    private static void JoinOrbit(FootPlayer me, Ufo ufo, FootPlayer other)
    {
        var own = ufo.Orbit!.Value;
        var ahead = me.Velocity.Normalized();
        double along = (other.GlobalPosition - me.GlobalPosition).Dot(ahead);
        double theta = along / own.R.Length;
        var k = own.R.Cross(own.V).Normalized();
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        DVec3 Turn(DVec3 v) => v * cos + k.Cross(v) * sin + k * (k.Dot(v) * (1 - cos));
        ufo.SetOrbit(new OrbitState(Turn(own.R), Turn(own.V)));
        GD.Print(FormattableString.Invariant($"[orbitnet B] joined A's orbit {along:F0} m along the track"));
    }

    /// <summary>How far <paramref name="velocity"/> is from the average velocity of the move <paramref name="from"/> → <paramref name="to"/> over <paramref name="seconds"/>, m/s.</summary>
    private static double VelocityError(EcefPos to, EcefPos from, double seconds, EcefPos velocity)
    {
        var d = to - from;
        double ex = d.X / seconds - velocity.X, ey = d.Y / seconds - velocity.Y, ez = d.Z / seconds - velocity.Z;
        return Math.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    private static double Median(System.Collections.Generic.List<double> sorted) => sorted.Count == 0 ? double.NaN : sorted[sorted.Count / 2];

    /// <summary>The other client's player, as drawn here.</summary>
    private FootPlayer? Other()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    private async Task Run(FootPlayer me)
    {
        if (!await LogIn("test")) return;
        string other = _role == "A" ? "B" : "A";
        if (_role == "B")
        {
            // said until A answers: A listens only once it has joined and seen B
            bool answered = false;
            for (int i = 0; i < 30 && !answered; i++)
            {
                Say("ready");
                answered = await Heard("A", "here", 2);
            }
            if (!answered) { Fail("A never got ready"); return; }
            // B stands 16 m east of A: the two saucers side by side
            me.PlaceAt(Other()!.GlobalPosition + new Vector3(16f, 0.5f, 0f), 0f);
        }
        else
        {
            if (!await Heard("B", "ready", 60)) { Fail("B never got ready"); return; }
            me.PlaceAt(me.GlobalPosition + Vector3.Up * 0.5f, 0f);
            Say("here");
        }
        // both face north: /orbit flies the way the saucer heads, so the two orbits are one
        if (!await Until(() => me.IsOnFloor(), 10)) Fail("never stood on the ground");
        Expect(me.SetRide(RideKind.Ufo), "takes a saucer");
        if (me.Saucer is not { } ufo) { Fail("not a saucer"); return; }
        me.FlyControls = () => new FlightInput(Vector2.Zero, 0f, _down, 0f, 0f, false, false, 0f, Boost: _down > 0f);
        if (!await Until(() => ufo.State.Landed, 15)) Fail("never landed on its legs");
        Say("landed");
        if (!await Heard(other, "landed", 60)) { Fail($"{other} never took a saucer"); return; }

        // both at once, on the same broadcast line: an admin's /orbit, through the server to this client
        if (_role == "A") Say("go");
        if (!await Heard("A", "go", 30)) { Fail("A never said go"); return; }
        Chat!.Send("/orbit 400");
        Expect(await Until(() => ufo.Orbit != null, 10), "the server's /orbit put this saucer in orbit");
        if (ufo.Orbit is not { } start) return;
        Expect(Math.Abs(start.V.Length - CelestialBody.Earth.CircularSpeed(start.R.Length)) < 1, $"circular: {start.V.Length:F0} m/s");
        Say("orbit");
        if (!await Heard(other, "orbit", 30)) { Fail($"{other} never got to orbit"); return; }

        // each client builds its orbit when its own /orbit comes back, over where it is then: two replies a second apart
        // (a loaded server) are two saucers 7.7 km apart on one track, which is no test of the copy (#1869). So B joins A's
        // orbit, 16 m to the side of A's copy as B draws it now: from then on the two fly together in truth
        if (_role == "B")
        {
            if (!await Until(() => Other() is { } a && IsInstanceValid(a) && a.Ride == RideKind.Ufo
                    && a.WorldVelocity.LengthSquared() > Net.RemoteInterpolator.FastSpeed * Net.RemoteInterpolator.FastSpeed, 20))
            {
                Fail($"A's copy never showed in orbit ({Apart(me):F0} m)");
                return;
            }
            JoinOrbit(me, ufo, Other()!);
            Say("joined");
        }
        else if (!await Heard("B", "joined", 40)) { Fail("B never joined A's orbit"); return; }

        // a minute side by side: the other's copy beside this one, moving with it, never jumping
        if (!await Until(() => Apart(me) < 5_000, 15))
        {
            Fail($"the other saucer is not beside this one ({Apart(me):F0} m)");
            return;
        }
        double t0 = GameClock.Now;
        float near = Apart(me), far = near;
        // the other's copy on its own track, in ECEF against the wall clock it is drawn by: an orbit drawn
        // smoothly moves at its own 7.7 km/s from frame to frame. (Against this body, it wobbles by the
        // gap between this body's physics time and the wall clock on a loaded headless client.)
        var speeds = new System.Collections.Generic.List<double>();
        EcefPos? lastAt = null;
        double lastWall = 0;
        // how far each velocity is from how its body moved, m/s (#1869): the copy's published one against its motion on the
        // wall clock it is drawn by, this body's own against its motion on the physics clock it moves by
        var copyVelErr = new System.Collections.Generic.List<double>();
        var ownVelErr = new System.Collections.Generic.List<double>();
        EcefPos? ownAt = null;
        double ownT = 0;
        while (GameClock.Now - t0 < 60)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            float d = Apart(me);
            if (float.IsInfinity(d) != float.IsInfinity(_lastApart))
            {
                var gone = Other();
                GD.Print(FormattableString.Invariant($"{Log} t {GameClock.Now - t0:F1} s: the other's copy {(float.IsInfinity(d) ? "lost" : "back")}: {(gone == null ? "no copy" : !IsInstanceValid(gone) ? "freed" : $"ride {gone.Ride}, {gone.InterpState}")}"));
            }
            _lastApart = d;
            near = Mathf.Min(near, d);
            far = Mathf.Max(far, d);
            if (GameClock.Now - t0 >= _logAt)
            {
                _logAt += 10;
                var seen = Other();
                string why = seen == null ? "no copy" : !IsInstanceValid(seen) ? "freed" : $"ride {seen.Ride}, visible {seen.Visible}, {seen.InterpState}";
                GD.Print(FormattableString.Invariant($"{Log} t {GameClock.Now - t0:F0} s: the other {d:F0} m away ({why}; own frame {me.GetProcessDeltaTime() * 1000:F0} ms)"));
            }
            if (me.Origin is { } own)
            {
                var mine = own.Frame.ToEcef(me.GlobalPosition);
                if (ownAt is { } was && GameClock.Now - ownT >= 0.1)
                {
                    ownVelErr.Add(VelocityError(mine, was, GameClock.Now - ownT, own.Frame.EcefDirection(me.GlobalPosition, me.Velocity)));
                    ownAt = mine;
                    ownT = GameClock.Now;
                }
                else if (ownAt == null) { ownAt = mine; ownT = GameClock.Now; }
            }
            if (Other() is not { } o || !IsInstanceValid(o) || me.Origin is not { } origin) { lastAt = null; continue; }
            var at = origin.Frame.ToEcef(o.GlobalPosition);
            double wall = Time.GetTicksUsec() / 1e6;
            // over a tenth of a second: a stall or a jump of the copy shows, a millisecond of difference
            // between when it was placed and when this reads the clock (7.7 m) does not
            if (lastAt is not { } before) { lastAt = at; lastWall = wall; }
            else if (wall - lastWall >= 0.1)
            {
                speeds.Add(Math.Abs(at.DistanceTo(before) / (wall - lastWall) / start.V.Length - 1));
                copyVelErr.Add(VelocityError(at, before, wall - lastWall, origin.Frame.EcefDirection(o.GlobalPosition, o.WorldVelocity)));
                lastAt = at;
                lastWall = wall;
            }
        }
        double alt = me.Origin!.AltitudeOf(me.GlobalPosition);
        copyVelErr.Sort();
        ownVelErr.Sort();
        GD.Print(FormattableString.Invariant(
            $"{Log} velocity against motion, median: the copy's {Median(copyVelErr):F1} m/s, this body's {Median(ownVelErr):F1} m/s"));
        speeds.Sort();
        double p95 = speeds.Count > 0 ? speeds[(int)(speeds.Count * 0.95)] : 1;
        double worst = speeds.Count > 0 ? speeds[^1] : 1;
        GD.Print(FormattableString.Invariant(
            $"{Log} a minute in orbit at {alt / 1000:F0} km: the other {near:F0} to {far:F0} m away; its copy's speed over each tenth of a second within {p95 * 100:F1} % at the 95th percentile, {worst * 100:F0} % at worst, over {speeds.Count} tenths; {ufo.Contacts} contacts"));
        Expect(ufo.Orbit != null, "still in orbit after a minute");
        // drawn all along, beside this one: a copy out of view or a teleport reads as infinite or kilometres
        Expect(far < 5_000, $"the other's copy stays beside this one ({far:F0} m)");
        // the existing bar for a remote body is a step at most 1.5 x its own (RemoteInterpolator.SelfCheck)
        Expect(p95 < 0.2, $"the other's copy flies smoothly (speed within {p95 * 100:F1} % at the 95th percentile)");
        Expect(worst < 1.5, $"the other's copy never stalls or leaps ({worst * 100:F0} % at worst)");
        Expect(ufo.Contacts == 0, $"no contact moved the saucer off its orbit ({ufo.Contacts})");
        Say("watched");
        if (!await Heard(other, "watched", 60)) { Fail($"{other} never finished watching"); return; }

        // A goes down: B sees it fall away; A's hover takes over below 50 km
        if (_role == "A")
        {
            _down = 1f;
            Expect(await Until(() => ufo.Orbit == null, 600), "thrusting down takes A out of orbit");
            _down = 0f;
            Expect(await Until(() => Mathf.Abs(me.Velocity.Y) < 0.5f, 60), $"A hovers below the Space regime ({me.Velocity.Y:F1} m/s)");
            Say("down");
        }
        else
        {
            Expect(await Until(() => Apart(me) > 20_000, 120), "B sees A fall away (or leave its view)");
            if (!await Heard("A", "down", 660)) Fail("A never came down");
        }
        me.FlyControls = null;
        Say("done");
    }
}
