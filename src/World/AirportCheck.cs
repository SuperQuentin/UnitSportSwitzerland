using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// <c>--airportcheck [shots] --chunks fixture:airport</c> (#422), offline or as a client of a server on
/// the same course (<c>tools/airportnetcheck.sh</c>): the aircraft at the stands
/// (<see cref="AirportStands"/>).
/// <list type="bullet">
/// <item>the fixture airport's six A320s, each with its two airstairs docked (L1, L2), the AN-124 and
/// the military freighter, standing on the ground at their stands;</item>
/// <item>one A320 taken: its stand empties and is not refilled with the player near;</item>
/// <item>the player 1.4 km away: the stand is refilled, the aircraft with its two stairs docked again
/// (the old ones cleared, not doubled).</item>
/// </list>
/// The refill wait is <see cref="AirportStands.RespawnSeconds"/>, 4 s here offline (the server's
/// <c>--standrespawn</c> online). On real tiles (<c>--at</c> an airport) it only counts what stands
/// there. <c>shots</c> windowed: the apron, the cargo apron and a runway into
/// <c>test_output/airports/</c> (<c>--style cartoon</c> for the progress pictures). RESULT line at the end.
/// </summary>
public partial class AirportCheck : Node
{
    public static bool Requested => CmdArgs.Has("--airportcheck");
    private static bool Shots => CmdArgs.Value("--airportcheck") == "shots";
    private static bool Fixture => CmdArgs.Value("--chunks") == "fixture:airport";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures;

    public AirportCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[airportcheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        ulong end = Time.GetTicksMsec() + (ulong)(seconds * 1000);
        while (!condition())
        {
            if (Time.GetTicksMsec() > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private static IEnumerable<VehicleBody> Vehicles() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Where(v => !v.IsQueuedForDeletion()) ?? Enumerable.Empty<VehicleBody>();

    private static VehicleBody? Body(string name) => Vehicles().FirstOrDefault(v => v.Name == name);

    private static List<VehicleBody> Planes(RideKind kind) =>
        Vehicles().Where(v => v.Kind == kind && v.Name.ToString().StartsWith(AirportStands.Prefix, System.StringComparison.Ordinal)).OrderBy(v => v.Name.ToString()).ToList();

    /// <summary>The plane's two stairs, docked at a door of it.</summary>
    private static int Docked(VehicleBody plane) =>
        Vehicles().Count(v => v.Name.ToString().StartsWith(plane.Name + "_", System.StringComparison.Ordinal) && v.StairsDockedAt is { } s && s.Host == plane);

    private static int StairsOf(string name) => Vehicles().Count(v => v.Name.ToString().StartsWith(name + "_", System.StringComparison.Ordinal));

    public override async void _Ready()
    {
        if (Fixture && (Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer)) AirportStands.RespawnSeconds = 4;
        await Seconds(2);
        ulong deadline = Time.GetTicksMsec() + 60_000;
        for (int i = 0; Time.GetTicksMsec() < deadline && (_player() is not { } ready || !ready.IsOnFloor()); i++)
        {
            // a run that starts in the vehicle or free camera mode: on foot
            if (_player() == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor() || VehicleManager.Instance is not { } vehicles) { Finish("no player"); return; }

        // ---- the stands filled -------------------------------------------------------------------
        int want = Fixture ? 6 : 1;
        bool filled = await Until(() => Planes(RideKind.A320).Count >= want && Planes(RideKind.An124).Count >= 1 && Planes(RideKind.Freighter).Count >= 1
            && Planes(RideKind.A320).All(p => Docked(p) == 2), 90);
        var a320s = Planes(RideKind.A320);
        Expect(filled && a320s.All(p => (p.DoorsOpen & 5) == 5), $"parked at the stands: {a320s.Count} A320s ({a320s.Sum(Docked)} airstairs docked), {Planes(RideKind.An124).Count} AN-124, {Planes(RideKind.Freighter).Count} freighter");
        if (Fixture)
        {
            Expect(a320s.Count == 6 && a320s.All(p => Docked(p) == 2), "the fixture's six A320s, two stairs docked at each");
            foreach (var v in Planes(RideKind.A320).Concat(Planes(RideKind.An124)).Concat(Planes(RideKind.Freighter)))
                GD.Print($"[airportcheck] {v.Name} {v.Kind} at {v.GlobalPosition} yaw {Mathf.RadToDeg(v.Rotation.Y):F0} doors {v.DoorsOpen} posed {v.Posed}");
        }
        foreach (var p in a320s.Take(2))
            if (Body(p.Name + "_0") is { Ride: Airstairs s0 } st)
                GD.Print($"[airportcheck] {p.Name}_0 platform {s0.Height:F2} m, docked at door {st.StairsDockedAt?.Door}");

        if (!filled)
            foreach (var v in Vehicles().Where(v => v.Name.ToString().StartsWith(AirportStands.Prefix, System.StringComparison.Ordinal)).Take(4))
            {
                var sills = new List<AirstairsDock.Sill>();
                AirstairsDock.SillsNear(GetTree(), v.GlobalPosition, 12f, sills);
                GD.Print($"[airportcheck] {v.Name} at {v.GlobalPosition} yaw {Mathf.RadToDeg(v.GlobalRotation.Y):F1} posed {v.Posed} lip {AirstairsDock.Lip(v.GlobalTransform)} sills {string.Join(" ", sills.Select(x => $"{x.Door}:{x.Edge}"))}");
            }
        if (Shots) await TakeShots(me);

        if (Fixture && a320s.Count > 0)
        {
            // ---- one taken: the stand stays empty with the player near ---------------------------
            var plane = a320s[0];
            string name = plane.Name;
            vehicles.Claim(plane, _ => { });
            Expect(await Until(() => Body(name) == null, 10), $"{name} taken: its stand is empty");
            await Seconds(10);
            Expect(Body(name) == null, "not refilled with a player within 300 m");
            // ---- the player far away: refilled, its stairs docked again -------------------------
            var home = me.GlobalPosition;
            me.PlaceAt(home + new Vector3(1500f, 0f, 300f), 0f);
            Expect(await Until(() => Body(name) != null, 40), $"refilled once nobody is near: {name} back");
            // back to look: the stairs dock to it again (each peer docks them where they stand)
            me.PlaceAt(home, 0f);
            bool docked = await Until(() => Body(name) is { } again && Docked(again) == 2, 20);
            Expect(docked, $"{name} with {(Body(name) is { } b ? Docked(b) : 0)} stairs docked, L1 and L2 open ({Body(name)?.DoorsOpen}), {StairsOf(name)} stairs in all");
            Expect(StairsOf(name) == 2, "its old stairs cleared, not doubled");
        }
        Finish(null);
    }

    private async Task TakeShots(FootPlayer me)
    {
        if (DisplayServer.GetName() == "headless") return;
        string dir = ProjectSettings.GlobalizePath("res://test_output/airports");
        System.IO.Directory.CreateDirectory(dir);
        string style = CmdArgs.Value("--style") ?? "ps1";
        var a320s = Planes(RideKind.A320);
        if (a320s.Count > 0)
        {
            var mid = a320s.Aggregate(Vector3.Zero, (s, p) => s + p.GlobalPosition) / a320s.Count;
            var nose = -a320s[0].GlobalTransform.Basis.Z with { Y = 0 };
            var side = nose.Cross(Vector3.Up).Normalized();
            // from ahead and to the left of the row, up a little: noses, L1 doors and stairs
            await Shot($"{dir}/apron-{style}.png", mid + nose.Normalized() * 70f - side * 90f + Vector3.Up * 22f, mid + Vector3.Up * 3f);
        }
        // the cargo pair, each from behind and to one side (the taxilane side: open), well up; they
        // may stand a kilometre apart
        foreach (var (kind, tag, off) in new[] { (RideKind.An124, "an124", -120f), (RideKind.Freighter, "freighter", -70f) })
            if (Planes(kind).FirstOrDefault() is { } heavy)
            {
                var nose = (-heavy.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                var side = nose.Cross(Vector3.Up).Normalized();
                await Shot($"{dir}/{tag}-{style}.png", heavy.GlobalPosition + nose * off + side * off * 0.6f + Vector3.Up * Mathf.Abs(off) * 0.35f, heavy.GlobalPosition + Vector3.Up * 4f);
            }
        if (AirportStands.Instance?.Index is { } index && VehicleManager.Instance?.Origin is { } origin)
        {
            var at = me.GlobalPosition;
            var runway = index.Airports.SelectMany(a => a.Runways).Where(r => r.Usable)
                .OrderBy(r => origin.ToWorld(r.E1, r.N1, 0).DistanceTo(at with { Y = 0 })).FirstOrDefault();
            if (runway != null)
            {
                var a = origin.ToWorld(runway.E1, runway.N1, 0);
                var b = origin.ToWorld(runway.E2, runway.N2, 0);
                float ground = VehicleManager.Instance.Terrain is { } t && t.TryGetHeight(a, out float g) ? g : me.GlobalPosition.Y;
                var along = (b - a).Normalized();
                await Shot($"{dir}/runway-{style}.png", a - along * 60f + Vector3.Up * (ground + 14f), a + along * 600f + Vector3.Up * ground);
            }
        }
    }

    /// <summary>A picture from <paramref name="eye"/> looking at <paramref name="at"/>, by a camera of its own (the tiles there given time to build).</summary>
    private async Task Shot(string path, Vector3 eye, Vector3 at)
    {
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 60f, Near = 0.1f, Far = 20000f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        await Seconds(4);
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[airportcheck] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        GD.Print(_failures == 0 ? "[airportcheck] RESULT: ok" : $"[airportcheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
