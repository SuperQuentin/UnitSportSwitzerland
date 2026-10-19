using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Occasions;

/// <summary>
/// <c>--sightingshots &lt;dir&gt;</c> (windowed, offline, #863): stages each kind of sighting near the
/// player through the director's own call (<see cref="UfoSightings.Stage"/>), the abduction over the
/// nearest cow, and photographs it from a camera of its own at the moments that matter: the saucer
/// coming in, the cow rising in the beam, held high; the landing with its ramp down (then the parked
/// UFO standing in for it); a flyover and the mothership overhead. Checks the cow rose and came
/// down, and the landed saucer became a parked UFO. Run it at night in Cartoon:
/// <c>--chunks fixture:flat --style cartoon --time 22 --occasion ufo --sightingshots test_output/863</c>
/// (or anywhere on the real map with a herd within 600 m, <c>--at E,N</c>).
/// </summary>
public partial class SightingShots : Node
{
    public static string? Dir => CmdArgs.Value("--sightingshots");

    private readonly WorldOrigin _origin;
    private readonly Terrain.ChunkManager? _chunks;
    private Camera3D _cam = null!;
    private bool _own;
    private int _failures;

    private readonly Func<FootPlayer?> _player;

    public SightingShots(Terrain.ChunkManager? chunks, WorldOrigin origin, Func<FootPlayer?> player)
    {
        Name = "SightingShots";
        _player = player;
        _chunks = chunks;
        _origin = origin;
    }

    public SightingShots() : this(null, WorldOrigin.SwissDefault(), () => null) { }

    public override async void _Ready()
    {
        _cam = new Camera3D { Name = "SightingCam", Fov = 60f, Far = 20000f };
        AddChild(_cam);
        string dir = ProjectSettings.GlobalizePath(Dir!.StartsWith("res://") || Dir.StartsWith("/") ? Dir : "res://" + Dir);
        System.IO.Directory.CreateDirectory(dir);
        try { await Run(dir); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? "[sightingshots] RESULT: ok" : $"[sightingshots] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        if (_own && !_cam.Current) _cam.MakeCurrent();
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[sightingshots] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private FootPlayer? Me => _player();

    private float Ground(Vector3 at) => _chunks != null && _chunks.TryGetSurface(at, out float g) ? g : at.Y;

    /// <summary>The camera <paramref name="back"/> m from <paramref name="at"/> on the bearing <paramref name="from"/>, <paramref name="up"/> over the ground, looking at <paramref name="look"/>.</summary>
    private void Frame(Vector3 at, float from, float back, float up, Vector3 look)
    {
        var eye = at + new Vector3(Mathf.Sin(from), 0f, -Mathf.Cos(from)) * back;
        eye.Y = Ground(eye) + up;
        _own = true;
        _cam.GlobalPosition = eye;
        _cam.LookAt(look, Vector3.Up);
        _cam.MakeCurrent();
    }

    private async Task Shot(string dir, string name)
    {
        await Seconds(0.3);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = System.IO.Path.Combine(dir, $"sighting_{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[sightingshots] shot {path}");
    }

    private async Task Run(string dir)
    {
        // the player if there is one, else the command line's fly camera: the place it stands over
        if (!await Until(() => (Me != null || GetViewport().GetCamera3D() != null) && UfoSightings.Instance != null && Farming.Herds.Instance != null, 120))
        {
            Expect(false, $"a player or a camera, sightings ({UfoSightings.Instance != null}) and herds ({Farming.Herds.Instance != null})");
            return;
        }
        var sightings = UfoSightings.Instance!;
        var herds = Farming.Herds.Instance!;
        var start = Me?.GlobalPosition ?? GetViewport().GetCamera3D()!.GlobalPosition;
        await Until(() => _chunks == null || _chunks.TryGetSurface(start, out _), 60);
        var home = start with { Y = Ground(start) };
        await Seconds(3);
        var (pe, pn) = _origin.ToLv95(home);

        // ---- the abduction, over the nearest cow ----
        (double E, double N, float Ground)? cow = null;
        await Until(() => (cow = herds.CowNear(pe, pn, 600f, UfoSightings.Now + 4)) != null, 30);
        Expect(cow != null, "a cow within 600 m to beam up");
        if (cow is { } c)
        {
            float heading = 1.1f;
            // its beam comes on 4 s from now, over where that cow will be then
            int id = sightings.Stage(SightingKind.Abduction, c.E, c.N, c.Ground, heading, 2, -(SightingPath.AbductBeamOn - 4), 7);
            var anchor = _origin.ToWorld(c.E, c.N, c.Ground);
            float side = heading + 1.9f;
            Frame(anchor, side, 34f, 3f, anchor + Vector3.Up * 14f);
            await Seconds(1.5);
            await Shot(dir, "abduction_coming");
            float best = 0f;
            for (int i = 0; i < 16; i++)
            {
                await Seconds(1);
                if (herds.Lifted() is { } l) best = Mathf.Max(best, l.Lift);
                if (i == 5) await Shot(dir, "abduction_rising");
                if (i % 4 == 1)
                    foreach (var b in TractorBeams.Active(GetTree()))
                        GD.Print($"[sightingshots] beam apex {b.Apex}: {herds.Describe(b.Apex)}");
            }
            Expect(best > 6f, $"the cow rose in the beam ({best:F1} m)");
            Frame(anchor, side, 26f, 2f, anchor + Vector3.Up * 16f);
            await Shot(dir, "abduction_held");
            await Until(() => herds.InAir == 0, 25);
            Expect(herds.InAir == 0, "the cow came back down");
            sightings.Unstage(id);
        }

        // ---- a landing, on open ground 40 m from the player ----
        {
            var at = home + new Vector3(40f, 0f, -10f);
            at.Y = Ground(at);
            var (le, ln) = _origin.ToLv95(at);
            float heading = 4.0f;
            int id = sightings.Stage(SightingKind.Landing, le, ln, at.Y, heading, 1, -12, 3);
            Frame(at, heading + 2.4f, 30f, 2.5f, at + Vector3.Up * 8f);
            await Seconds(4);
            await Shot(dir, "landing_down");
            await Seconds(SightingPath.LandRampOpen - 16 + 4);
            Frame(at, heading + 0.3f, 17f, 2.2f, at + Vector3.Up * 2.5f);
            await Shot(dir, "landing_ramp");
            string name = UfoSightings.ParkedName(id);
            bool parked = await Until(() => Vehicles.VehicleManager.Instance?.GetNodeOrNull(name) != null, 10);
            Expect(parked, "the landed saucer is a parked UFO to board");
            if (!parked && Vehicles.VehicleManager.Instance is { } vm)
                GD.Print($"[sightingshots] vehicles: {string.Join(", ", vm.GetChildren().Select(c => c.Name.ToString()))}; taken {sightings.IsTaken(id)}; drawn {sightings.DrawnSaucers}");
            await Seconds(1);
            await Shot(dir, "landing_parked");
            sightings.Unstage(id);
            Vehicles.VehicleManager.Instance?.GetNodeOrNull(name)?.QueueFree();
        }

        // ---- a flyover and the mothership, from the player ----
        {
            var at = home;
            var (fe, fn) = _origin.ToLv95(at);
            float g = Ground(at);
            int fly = sightings.Stage(SightingKind.Flyover, fe, fn, g, 0.6f, 3, -26, 5);
            Frame(at, 3.4f, 60f, 2f, at + new Vector3(0, 170f, 0) + SightingPath.Dir(0.6f) * -60f);
            await Seconds(1.5);
            await Shot(dir, "flyover");
            sightings.Unstage(fly);
            var mat = at + SightingPath.Dir(2.2f) * 1100f;
            var (me2, mn2) = _origin.ToLv95(mat);
            int mother = sightings.Stage(SightingKind.Mothership, me2, mn2, Ground(mat), 2.2f, 1, -45, 9);
            Frame(at, 2.2f + Mathf.Pi, 10f, 2f, mat + Vector3.Up * 350f);
            await Seconds(2);
            await Shot(dir, "mothership");
            Expect(sightings.DrawnSaucers >= 0, "drawn");
            sightings.Unstage(mother);
        }
    }
}
