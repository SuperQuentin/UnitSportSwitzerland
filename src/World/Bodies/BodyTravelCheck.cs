using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--bodytravelcheck</c> (#2039), under <c>--frame earth</c> (<c>--chunks fixture:straight</c> will do: the Earth's places
/// land on the fill): travel between bodies the way the place search does it, through <see cref="Teleporter"/>. In the fly
/// camera: the Earth to Apollo 11, the Moon to Bern, the Earth to Gale crater, Mars to Geneva. On foot: the Earth to
/// Artemis Base, the Moon to Bern. Each time the anchor's body is the place's, the arrival settles on the ground there
/// (220 m over it flying, standing on it walking) and within 0.01° of the place. Then online refuses another body with a
/// reason, and a walker refuses Jupiter.
/// </summary>
public partial class BodyTravelCheck : Node
{
    public static bool Requested => CmdArgs.Has("--bodytravelcheck");

    private readonly WorldOrigin _origin;
    private int _failed;
    private bool _arrived;

    public BodyTravelCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "BodyTravelCheck";
    }

    public override void _Ready()
    {
        Teleporter.Arrived += OnArrived;
        _ = Run();
    }

    public override void _ExitTree() => Teleporter.Arrived -= OnArrived;

    private void OnArrived() => _arrived = true;

    private ClientWorld? World()
    {
        for (Node? n = this; n != null; n = n.GetParent())
            if (n is ClientWorld w) return w;
        return null;
    }

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[bodytravel] {what}"));

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Waits for the arrival on the wall clock (the ground is built on threads), up to <paramref name="seconds"/>.</summary>
    private async Task<bool> Arrival(double seconds)
    {
        double until = RealClock.Now + seconds;
        while (!_arrived && RealClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return _arrived;
    }

    private async Task Run()
    {
        await Wait(3);
        if (Teleporter.Instance is not { } tp || World() is not { } world)
        {
            Finish("no teleporter or world");
            return;
        }
        var apollo = BodyPlaces.For(BodyId.Moon).First(p => p.Name.Contains("Apollo 11"));
        var artemis = BodyPlaces.For(BodyId.Moon).First(p => p.Name == "Artemis Base");
        var gale = BodyPlaces.For(BodyId.Mars).First(p => p.Name.StartsWith("Gale"));
        var (bernE, bernN) = (2600670.0, 1199655.0);
        var (genevaE, genevaN) = (2500300.0, 1118000.0);

        // the fly camera
        await Trip(tp, "Earth -> Apollo 11 (fly)", () => tp.TeleportTo(apollo.Body, apollo.LatDeg, apollo.LonDeg, apollo.Name, (apollo.LandEast, apollo.LandNorth)),
            BodyId.Moon, apollo.LatDeg, apollo.LonDeg, flying: true);
        // --bodytravelcheck shots (windowed): the search open on the Moon, Mars and Io, from the Moon
        if (CmdArgs.Value("--bodytravelcheck") == "shots")
        {
            await Shots(world);
            Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
            return;
        }
        var bern = Terrain.Format.SwissGeodesy.ToLatLon(bernE, bernN);
        await Trip(tp, "Moon -> Bern (fly)", () => tp.TeleportTo(bernE, bernN, "Bern"), BodyId.Earth, bern.LatDeg, bern.LonDeg, flying: true);
        await Trip(tp, "Earth -> Gale crater (fly)", () => tp.TeleportTo(gale.Body, gale.LatDeg, gale.LonDeg, gale.Name), BodyId.Mars, gale.LatDeg, gale.LonDeg, flying: true);
        var geneva = Terrain.Format.SwissGeodesy.ToLatLon(genevaE, genevaN);
        await Trip(tp, "Mars -> Geneva (fly)", () => tp.TeleportTo(genevaE, genevaN, "Geneva"), BodyId.Earth, geneva.LatDeg, geneva.LonDeg, flying: true);

        // on foot
        world.ToggleMode();
        await Wait(3);
        if (tp.ActiveTarget() is not Player.FootPlayer walker) Expect(false, "on foot: no walker took over");
        else
        {
            await Trip(tp, "Earth -> Artemis Base (on foot)", () => tp.TeleportTo(artemis.Body, artemis.LatDeg, artemis.LonDeg, artemis.Name, (artemis.LandEast, artemis.LandNorth)),
                BodyId.Moon, artemis.LatDeg, artemis.LonDeg, flying: false);
            Expect(tp.RefusalFor(BodyId.Jupiter, walker) != null, "a walker is refused Jupiter (no ground)");
            await Trip(tp, "Moon -> Bern (on foot)", () => tp.TeleportTo(bernE, bernN, "Bern"), BodyId.Earth, bern.LatDeg, bern.LonDeg, flying: false);
        }

        // online: another body waits for C3b, with a reason; the Earth from the Earth still goes
        var was = tp.Networked;
        tp.Networked = () => true;
        string? why = tp.RefusalFor(BodyId.Moon, tp.ActiveTarget());
        Expect(why != null && !tp.TeleportTo(BodyId.Moon, 0, 0), $"online, the Moon is refused: {why}");
        Expect(tp.RefusalFor(BodyId.Earth, tp.ActiveTarget()) == null, "online, the Earth from the Earth is not refused");
        tp.Networked = was;
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private async Task Shots(ClientWorld world)
    {
        if (world.FindChild("PlaceSearchUi", true, false) is not PlaceSearchUi search) { Expect(false, "no place search"); return; }
        string dir = ProjectSettings.GlobalizePath("res://test_output/body_travel");
        System.IO.Directory.CreateDirectory(dir);
        search.SetOpen(true);
        foreach (var body in new[] { BodyId.Moon, BodyId.Mars, BodyId.Io, BodyId.Earth })
        {
            search.ShowBody(body);
            await Wait(1.5);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string path = System.IO.Path.Combine(dir, $"search_{body.ToString().ToLowerInvariant()}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            Log($"shot {path}");
        }
        search.SetOpen(false);
    }

    private async Task Trip(Teleporter tp, string what, System.Func<bool> go, BodyId body, double lat, double lon, bool flying)
    {
        _arrived = false;
        double t0 = RealClock.Now;
        if (!go()) { Expect(false, $"{what}: the jump was refused"); return; }
        bool arrived = await Arrival(200);
        await Wait(1);
        var target = tp.ActiveTarget();
        if (!arrived || target == null || _origin.Frame.Axes is not { } axes)
        {
            Expect(false, $"{what}: no arrival in {RealClock.Now - t0:F0} s");
            return;
        }
        var p = target.GlobalPosition;
        var g = BodyHandover.ShapeOf(axes.Body).ToGeodetic(axes.ToEcef(p.X, p.Y, p.Z));
        double dLat = System.Math.Abs(g.LatDeg - lat), dLon = System.Math.Abs(System.Math.IEEERemainder(g.LonDeg - lon, 360)) * System.Math.Cos(lat * System.Math.PI / 180);
        float over = float.NaN;
        if (Terrain.Earth.EarthWorld.OtherBody is { } other && axes.Body != BodyId.Earth && other.TryGetHeight(p, out float gy)) over = p.Y - gy;
        else if (World() is { } w && w.GroundForChecks?.TryGetSurface(p, out float ey) == true) over = p.Y - ey;
        bool height = flying ? over is > 150 and < 260 : over is > -0.6f and < 2.5f;
        Expect(axes.Body == body && dLat < 0.01 && dLon < 0.01 && height,
            string.Create(CultureInfo.InvariantCulture,
                $"{what}: on {axes.Body}, {g.LatDeg:F4}, {g.LonDeg:F4} (asked {lat:F4}, {lon:F4}), {over:F1} m over the ground, after {RealClock.Now - t0:F0} s"));
    }

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[bodytravel] FAIL {what}");
        }
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[bodytravel] RESULT: ok");
        else GD.PrintErr($"[bodytravel] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
