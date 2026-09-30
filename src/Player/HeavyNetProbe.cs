using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--heavynet a|b [password]</c> on clients connected to a loopback server (a with the server's
/// <c>--admin-password</c>, so it may leave what it conjured in the world): trucks and buses (#70)
/// seen from the OTHER peer.
///
/// <list type="bullet">
/// <item><b>a</b> takes the Scania, couples a curtainsider from the picker's path, drives it
/// through a bend, uncouples (the trailer is left in the world), couples again from where it
/// stands, gets out (the whole train is parked), gets back in (the trailer still on), then takes
/// the articulated bus and works its doors, kneel and destination;</item>
/// <item><b>b</b> watches: a's ride, trailer code, joint angles and lamp/door bits as replicated,
/// the section bodies its copy of a built and where they stand, and every parked vehicle with its
/// trailer and angles. Screenshots into <c>test_output/</c>.</item>
/// </list>
/// Read the <c>[heavynet]</c> lines; the timeline is a's, in seconds from standing on the ground.
/// </summary>
public partial class HeavyNetProbe : Node
{
    public static string? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--heavynet");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? Password
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--heavynet");
            return i >= 0 && i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : null;
        }
    }

    private readonly string _role;
    private readonly System.Func<FootPlayer?> _local;
    private double _t = -1, _snap, _clock;
    private string _last = "";
    private Camera3D? _eye;
    private readonly HashSet<string> _shot = new();

    public HeavyNetProbe(string role, System.Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "HeavyNetProbe";
    }

    private void Log(string what) => GD.Print($"[heavynet] {_role} t={_t,5:F1} {what}");

    private static RideKind Tractor => HeavyCatalog.All[0].Kind;
    private static RideKind Articulated => HeavyCatalog.All[3].Kind;

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_clock > 200) { GD.Print($"[heavynet] {_role} RESULT: done (timeout)"); GetTree().Quit(); return; }
        var me = _local();
        if (me == null) return;
        if (_t < 0)
        {
            if (!me.IsOnFloor()) return;
            _t = 0;
            Log($"on the ground as {me.Name}");
        }
        double before = _t;
        _t += delta;
        bool At(double s) => before < s && _t >= s;
        if (_role == "a") Act(me, At);
        else Watch(me);
    }

    /// <summary>A key press as the player's own input handler sees it.</summary>
    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private static string Train(FootPlayer p) => p.Heavy is { } t
        ? $"trailer {t.TrailerCode} joints {string.Join(" ", t.Articulation.Take(t.SectionCount - 1).Select(a => $"{Mathf.RadToDeg(a):F0}°"))} flags {t.PackFlags()}"
        : "no truck";

    private void Act(FootPlayer me, System.Func<double, bool> at)
    {
        if (at(0.5) && Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
            chat.Send($"/login {pw}");
        if (at(1.8)) Log($"admin: {Permissions.IsAdmin}");
        if (at(2)) { me.NextLoad = 1f; Log($"SetRide {Tractor}: {me.SetRide(Tractor)}"); }
        if (at(3)) Log($"curtainsider from the picker: {me.SpawnTrailer(0, 1f)} -> {Train(me)}");
        // a slow bend to the right, so the trailer swings
        if (at(5)) { Input.ActionPress(PlayerInput.Throttle, 0.5f); Input.ActionPress(PlayerInput.MoveRight); }
        if (at(9)) Log($"in the bend at {me.GroundSpeed * 3.6f:F0} km/h: {Train(me)}");
        if (at(11)) { Input.ActionRelease(PlayerInput.Throttle); Input.ActionRelease(PlayerInput.MoveRight); Input.ActionPress(PlayerInput.Brake); }
        if (at(16)) { Input.ActionRelease(PlayerInput.Brake); Log($"stopped at {me.GroundSpeed * 3.6f:F1} km/h: {Train(me)}"); }
        if (at(17)) { Press(PlayerInput.Couple); }
        if (at(18))
            Log($"uncoupled: {Train(me)}; lone trailers in the world: {VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Count(v => v.Trailer != null)}");
        if (at(24)) { Press(PlayerInput.Couple); }
        if (at(26)) Log($"coupled again from where it stood: {Train(me)}");
        if (at(30)) { me.ExitVehicle(); Log($"got out: ride {me.Ride} trailer {me.TrailerCode}"); }
        if (at(34))
        {
            var v = VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(x => x.Kind == Tractor);
            Log($"the parked train: {v?.Name} train {(v?.Ride as Truck)?.TrailerCode} boxes {v?.GetChildren().OfType<CollisionShape3D>().Count()}");
            if (v != null) me.GlobalPosition = v.ToGlobal(v.Ride.EntryPoint) + Vector3.Up * 0.3f;
        }
        if (at(36)) Log($"get back in: {me.TryInteract()}");
        if (at(40)) Log($"in again: {Train(me)}");
        if (at(44)) { me.ExitVehicle(); Log("got out again"); }
        if (at(46)) { me.GlobalPosition += me.GlobalTransform.Basis.X * -6f + Vector3.Up; me.NextLoad = 0.5f; }
        if (at(48)) Log($"SetRide {Articulated}: {me.SetRide(Articulated)} -> sections {me.Heavy?.SectionCount}");
        if (at(50)) { Press(PlayerInput.CarDoor); Log($"doors: {Train(me)} kneeling {me.Heavy?.Kneeling}"); }
        if (at(52)) { Press(PlayerInput.Destination); Press(PlayerInput.Destination); Log($"destination: {me.Heavy?.DestinationText}"); }
        if (at(60)) { Press(PlayerInput.CarDoor); Log($"doors shut: {Train(me)}"); }
        if (at(62)) { Input.ActionPress(PlayerInput.Throttle, 0.6f); Input.ActionPress(PlayerInput.MoveLeft); }
        if (at(68)) { Log($"bus in a bend: {Train(me)}"); Input.ActionRelease(PlayerInput.Throttle); Input.ActionRelease(PlayerInput.MoveLeft); Input.ActionPress(PlayerInput.Brake); }
        if (at(72)) Input.ActionRelease(PlayerInput.Brake);
        if (at(80)) { GD.Print("[heavynet] a RESULT: done"); GetTree().Quit(); }
        if (Mathf.PosMod(_t, 1.0) < GetPhysicsProcessDeltaTime() && me.Heavy is { } h)
            Log($"  drive: {me.GroundSpeed * 3.6f:F1} km/h floor {me.IsOnFloor()} {h.GearLabel} {h.Rpm:F0} rpm clutch {h.Box.Clutch:F2}{(h.Box.Locked ? " locked" : "")}"
                + $" thr {h.Throttle:F2} in ({me.LastRideInput.Throttle:F2},{me.LastRideInput.Brake:F2},{me.LastRideInput.Steer:F2}) springs {h.Box.SpringBrakes} hits {me.SectionHits} at {me.GlobalPosition.Round()}");
    }

    private void Watch(FootPlayer me)
    {
        _snap += GetPhysicsProcessDeltaTime();
        if (_snap < 0.25) return;
        _snap = 0;

        var lines = new List<string>();
        foreach (var p in GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>())
        {
            if (p == me) continue;
            var sections = p.GetChildren().OfType<CharacterBody3D>().Where(s => s.Name.ToString().StartsWith("Section") && !s.IsQueuedForDeletion()).ToList();
            string where = string.Join(" ", sections.Select(s =>
            {
                var local = p.ToLocal(s.GlobalPosition);
                float yaw = Mathf.RadToDeg(Mathf.Wrap(s.GlobalRotation.Y - p.GlobalRotation.Y, -Mathf.Pi, Mathf.Pi));
                return $"{s.Name}@({local.X:F1},{local.Z:F1}) yaw {yaw:F0}°";
            }));
            lines.Add($"player {p.Name}: {p.Ride} trailer {p.TrailerCode} pose ({Mathf.RadToDeg(p.TrainPose.X):F0}°,{Mathf.RadToDeg(p.TrainPose.Y):F0}°) flags {Mathf.RoundToInt(p.Anim.W)} sections [{where}]");
            if (Mathf.Abs(p.TrainPose.X) > 0.12f) Shoot(p, "remote_bend");
            if (p.Ride == Articulated && (Mathf.RoundToInt(p.Anim.W) & 0xF0) != 0) Shoot(p, "remote_bus_doors");
        }
        foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
        {
            string ride = v.Ride switch
            {
                Truck t => $"truck trailer {t.TrailerCode} angles {Mathf.RadToDeg(t.Articulation[0]):F0}°",
                ParkedTrailer lone => $"lone {lone.Spec.Label}",
                _ => v.Kind.ToString(),
            };
            lines.Add($"parked {v.Name}: {ride} boxes {v.GetChildren().OfType<CollisionShape3D>().Count()}");
            if (v.Ride is Truck { TrailerCode: not 0 }) Shoot(v, "parked_train");
        }
        string now = string.Join(" | ", lines);
        if (now != _last) { Log(now); _last = now; }
    }

    private Node3D? _shootAt;
    private string _shootName = "";
    private double _shootIn;

    private void Shoot(Node3D target, string name)
    {
        if (DisplayServer.GetName() == "headless" || !_shot.Add(name)) return;
        _eye ??= new Camera3D { Name = "HeavyNetEye", Far = 3000f };
        if (_eye.GetParent() == null) GetTree().Root.AddChild(_eye);
        _shootAt = target;
        _shootName = name;
        _shootIn = 0.6;
    }

    public override void _Process(double delta)
    {
        if (_shootAt == null || _eye == null) return;
        if (!IsInstanceValid(_shootAt)) { _shootAt = null; return; }
        var t = _shootAt.GlobalTransform;
        _eye.LookAtFromPosition(t.Origin + t.Basis.Z * 14f + t.Basis.X * 12f + Vector3.Up * 8f, t.Origin + t.Basis.Z * 6f + Vector3.Up, Vector3.Up);
        _eye.Current = true;
        if ((_shootIn -= delta) > 0) return;
        string path = ProjectSettings.GlobalizePath($"res://test_output/heavynet_{_shootName}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[heavynet] {_role} screenshot {path}");
        _shootAt = null;
        _eye.Current = false;
    }
}
