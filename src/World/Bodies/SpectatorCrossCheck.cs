using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--spectatorcrosscheck [all|moonback|moonahead|earthback|earthahead]</c> (#1988, offline, no body data needed): the
/// fly camera from 10 km over one body to 100 km over the other, between the Moon and the Earth both ways, looking back at
/// the body it leaves or ahead at the one it goes to, through the sphere-of-influence handover and every re-anchor on
/// the way. The look is set once at the start and never touched; the camera is moved by the check along the straight
/// line to the other body's centre at the fly camera's own top speed there (<see cref="Player.SpectatorSpeed.SurfaceCap"/>),
/// from a start 20° off the line between the centres (so the path is not radial and the view's level-keeping works).
///
/// <para>
/// Every frame it reads the angle between the view's middle and the watched body's centre: roll never changes it,
/// parallax only smoothly, a snap of the view at once. It fails on a jump in that angle's change from one frame to the
/// next past <see cref="JumpDeg"/>, and on the camera's place (counted from the frame's body, through the frame tree at
/// each frame's sky moment) moving more than its own step in a frame. <c>--fixed-fps 60</c> or 30.
/// </para>
/// </summary>
public partial class SpectatorCrossCheck : Node
{
    public static bool Requested => CmdArgs.Has("--spectatorcrosscheck");

    /// <summary>A change in the watched body's angle from the view's middle, frame to frame, past this is a jump, degrees.</summary>
    public const double JumpDeg = 0.05;

    private readonly WorldOrigin _origin;
    private int _failed;

    private sealed record Leg(string Name, BodyId From, BodyId To, bool Back);

    private static readonly Leg[] Legs =
    [
        new("moonback", BodyId.Moon, BodyId.Earth, true),
        new("moonahead", BodyId.Moon, BodyId.Earth, false),
        new("earthback", BodyId.Earth, BodyId.Moon, true),
        new("earthahead", BodyId.Earth, BodyId.Moon, false),
    ];

    public SpectatorCrossCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "SpectatorCrossCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[spectatorcross] {what}"));

    private void Expect(bool ok, string what)
    {
        Log((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Run()
    {
        double until = GameClock.Now + 3;
        while (GameClock.Now < until) await Frame();
        if (GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) is not ChatManager chat) { Finish("no chat"); return; }
        string which = CmdArgs.Value("--spectatorcrosscheck") is { Length: > 0 } v && !v.StartsWith("--", StringComparison.Ordinal) ? v : "all";
        foreach (var leg in Legs)
            if (which == "all" || which == leg.Name) await Fly(chat, leg);
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private Player.SpectatorCamera? Camera() => GetViewport()?.GetCamera3D() as Player.SpectatorCamera;

    /// <summary>A body's centre in world space now (through the frame tree from the anchor's body).</summary>
    private Vector3 Centre(BodyId body) => _origin.ToWorld(GlobalPos.On(body, new EcefPos(0, 0, 0)));

    private async Task Fly(ChatManager chat, Leg leg)
    {
        var from = CelestialBody.Of(leg.From);
        var to = CelestialBody.Of(leg.To);
        // 20° of latitude off the point under the other body, 10 km up
        var dir = Frames.Convert(leg.To, new DVec3(0, 0, 0), leg.From, SkyClock.Now);
        var g = from.Shape.ToGeodetic(new EcefPos(dir.X, dir.Y, dir.Z));
        double lat = g.LatDeg > 0 ? g.LatDeg - 20 : g.LatDeg + 20;
        chat.Send(string.Create(CultureInfo.InvariantCulture, $"/goto {from.Name.ToLowerInvariant()} {lat:F3} {g.LonDeg:F3} 10"));
        for (int i = 0; i < 10; i++) await Frame();
        if (Camera() is not { } cam || _origin.Body != leg.From) { Expect(false, $"{leg.Name}: no fly camera on {from.Name}"); return; }
        var watched = leg.Back ? leg.From : leg.To;
        var look = (Centre(watched) - cam.GlobalPosition).Normalized();
        var hint = Mathf.Abs(look.Dot(Vector3.Up)) > 0.9f ? Vector3.Forward : Vector3.Up;
        cam.GlobalBasis = Basis.LookingAt(look, hint);
        await Frame();

        double worstJump = 0, maxRate = 0, lastAngle = double.NaN, lastRate = double.NaN, worstPop = 0;
        int crossings = 0, frames = 0;
        string worstAt = "";
        var bodyWas = _origin.Body;
        (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? last = null;
        double t0 = GameClock.Now;
        while (true)
        {
            float dt = (float)GetProcessDeltaTime();
            if (_origin.Frame.Axes is not { } axes) { Expect(false, $"{leg.Name}: no anchor frame"); return; }
            if (_origin.Body != bodyWas) { crossings++; Log($"{leg.Name}: the anchor counts from {_origin.Body} now, {GameClock.Now - t0:F1} s in"); bodyWas = _origin.Body; }

            // the watched body's angle from the view's middle: its change frame to frame, and that change's own jump
            var toWatched = (Centre(watched) - cam.GlobalPosition).Normalized();
            double angle = Mathf.RadToDeg((-cam.GlobalBasis.Z).AngleTo(toWatched));
            if (!double.IsNaN(lastAngle))
            {
                double rate = angle - lastAngle;
                maxRate = Math.Max(maxRate, Math.Abs(rate));
                // from the third frame of motion: the first ones start the parallax from rest
                if (!double.IsNaN(lastRate) && frames > 2 && Math.Abs(rate - lastRate) > worstJump)
                {
                    worstJump = Math.Abs(rate - lastRate);
                    worstAt = string.Create(CultureInfo.InvariantCulture, $"{GameClock.Now - t0:F2} s in, the anchor on {_origin.Body}, {rate:F4}° after {lastRate:F4}°");
                }
                lastRate = rate;
            }
            lastAngle = angle;

            // the camera's place, continuous through the handover (counted through the frame tree)
            var e = axes.ToEcef(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z);
            var here = new DVec3(e.X, e.Y, e.Z);
            var sky = SkyClock.Now;
            if (last is { } l)
            {
                var before = Frames.Convert(l.Body, l.P, axes.Body, l.Sky);
                double pop = (here - before).Length - (l.Step * 1.05 + 1);
                worstPop = Math.Max(worstPop, pop);
            }

            // on toward the other body's centre at the fly camera's top speed by the nearest surface
            double surface = Player.SurfaceReach.Of(_origin.Frame, cam.GlobalPosition, _origin.AltitudeOf(cam.GlobalPosition));
            var toward = Centre(leg.To) - cam.GlobalPosition;
            double over = toward.Length() - to.Shape.A;
            if (over < 100_000) break;
            double step = Math.Min(Player.SpectatorSpeed.SurfaceCap(surface) * dt, over - 99_000);
            cam.GlobalPosition += toward.Normalized() * (float)step;
            last = (axes.Body, here, sky, step);
            frames++;
            if (GameClock.Now - t0 > 600) { Expect(false, $"{leg.Name}: still {over / 1000:F0} km from {to.Name} after 600 s"); return; }
            await Frame();
        }
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{leg.Name}: {from.Name} → {to.Name} watching {watched} in {GameClock.Now - t0:F1} s ({frames} frames, {crossings} handover(s)): on screen at most {maxRate:F3}°/frame, the worst jump {worstJump:F4}° ({worstAt}), the camera's place at worst {Math.Max(worstPop, 0):F2} m past its step"));
        Expect(crossings == 1, $"{leg.Name}: one handover ({crossings})");
        Expect(worstJump <= JumpDeg, string.Create(CultureInfo.InvariantCulture, $"{leg.Name}: the watched body never jumps on screen ({worstJump:F4}° of {JumpDeg}°)"));
        Expect(worstPop <= 1, string.Create(CultureInfo.InvariantCulture, $"{leg.Name}: no pop in the camera's place ({Math.Max(worstPop, 0):F2} m)"));
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[spectatorcross] RESULT: ok");
        else GD.PrintErr($"[spectatorcross] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
