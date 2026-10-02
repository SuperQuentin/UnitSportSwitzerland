using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--crashnet a|b [password]</c> on clients connected to a loopback server (a with the server's
/// <c>--admin-password</c>, so it may take a car online): the crash ragdoll (#214) seen from the
/// OTHER peer.
///
/// <list type="bullet">
/// <item><b>a</b> takes a car, waits, then drives flat out into a wall 70 m ahead and is thrown
/// through the windscreen; it logs its own ragdoll from the throw to the rest.</item>
/// <item><b>b</b> puts the same wall where a's car stood (walls are local: it is a test prop) and
/// watches a's copy: whether its ragdoll starts, how far its hips are from a's replicated position,
/// whether it ends when a gets up. Windowed, it takes screenshots into <c>test_output/</c>.</item>
/// </list>
/// Read the <c>[crashnet]</c> lines.
/// </summary>
public partial class CrashNetProbe : Node
{
    public static string? ParseArgs() => CmdArgs.Value("--crashnet");

    private static string? Password => CmdArgs.Value("--crashnet", 2, notFlag: true);

    private const float WallAhead = 70f;
    private readonly string _role;
    private readonly System.Func<FootPlayer?> _local;
    private double _t = -1, _clock, _snap, _limpSince = -1;
    private bool _wall, _wasLimp, _sawLimp, _sawEnd;
    private float _worstGap, _gapSum;
    private int _gapCount;
    private Camera3D? _eye;
    private readonly HashSet<double> _shots = new();

    public CrashNetProbe(string role, System.Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "CrashNetProbe";
    }

    private void Log(string what) => GD.Print($"[crashnet] {_role} t={_t,5:F1} {what}");

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_clock > 150) { Finish("timeout"); return; }
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

    private void Act(FootPlayer me, System.Func<double, bool> at)
    {
        if (at(0.5) && Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
            chat.Send($"/login {pw}");
        if (at(2)) Log($"admin {Permissions.IsAdmin}, SetRide car: {me.SetRide((RideKind)CarCatalog.First)}");
        // standing still a while, so the watcher places its wall from where the car stands
        if (at(4)) { SpawnWall(me.GlobalPosition, -me.GlobalBasis.Z); Input.ActionPress(PlayerInput.Throttle); }
        bool limp = me.Ragdolled;
        if (limp && !_wasLimp) { _sawLimp = true; Log($"THROWN at {me.GlobalPosition}"); Input.ActionRelease(PlayerInput.Throttle); }
        if (!limp && _wasLimp) { _sawEnd = true; Log($"at rest at {me.GlobalPosition}, {me.CrashBones} bones, pose {me.PoseKind}"); }
        _wasLimp = limp;
        _snap += GetPhysicsProcessDeltaTime();
        if (limp && _snap >= 0.25) { _snap = 0; Log($"  limp: hips {me.GlobalPosition}"); }
        if (at(35)) Finish(_sawLimp && _sawEnd ? "ok" : $"FAILED thrown {_sawLimp} rested {_sawEnd}");
    }

    private void Watch(FootPlayer me)
    {
        var a = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p != me);
        if (a == null) return;
        if (!_wall && a.Ride != RideKind.OnFoot && a.Ride != RideKind.RoadBike && _t > 3)
        {
            SpawnWall(a.GlobalPosition, -a.GlobalBasis.Z);
            Log($"a's car at {a.GlobalPosition}: wall placed");
        }
        bool limp = a.Ragdolled;
        if (limp && !_wasLimp) { _sawLimp = true; _limpSince = _t; Log($"a's copy THROWN (pose {a.PoseKind}, launch {a.Anim})"); }
        if (!limp && _wasLimp) { _sawEnd = true; Log($"a's copy at rest (pose {a.PoseKind}) after {_t - _limpSince:F1} s"); }
        _wasLimp = limp;
        if (limp && a.RagdollPelvis is { } hips)
        {
            float gap = hips.DistanceTo(a.GlobalPosition);
            _worstGap = Mathf.Max(_worstGap, gap);
            _gapSum += gap;
            _gapCount++;
            _snap += GetPhysicsProcessDeltaTime();
            if (_snap >= 0.25) { _snap = 0; Log($"  copy's hips {hips}, a's replicated position {a.GlobalPosition}, gap {gap:F2} m"); }
            foreach (double s in new[] { 0.4, 1.0, 2.0, 3.5 })
                if (_t - _limpSince >= s && _shots.Add(s)) Shoot(a, $"remote_{s:0.0}".Replace(',', '.'));
        }
        if (_sawEnd && _t - _limpSince > 1.5 && _t > 0 && !_shots.Contains(-1))
        {
            _shots.Add(-1);
            Finish(_sawLimp ? $"ok, gap mean {_gapSum / Mathf.Max(1, _gapCount):F2} m worst {_worstGap:F2} m" : "FAILED never saw the ragdoll");
        }
    }

    private void Finish(string result)
    {
        GD.Print($"[crashnet] {_role} RESULT: {result}");
        Input.ActionRelease(PlayerInput.Throttle);
        GetTree().Quit();
    }

    /// <summary>The same wall on both peers, from where a's car stood when it set off.</summary>
    private void SpawnWall(Vector3 car, Vector3 forward)
    {
        if (_wall) return;
        _wall = true;
        var fwd = (forward with { Y = 0 }).Normalized();
        var at = car + fwd * WallAhead;
        var size = new Vector3(30f, 4f, 1f);
        var wall = new StaticBody3D { Name = "CrashWall" };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size } });
        GetTree().Root.AddChild(wall);
        float ground = _local()?.Terrain is { } t && t.TryGetHeight(at, out float g) ? g : car.Y;
        wall.GlobalTransform = new Transform3D(Basis.LookingAt(fwd, Vector3.Up), new Vector3(at.X, ground + size.Y * 0.5f - 0.3f, at.Z));
    }

    private void Shoot(FootPlayer target, string name)
    {
        if (DisplayServer.GetName() == "headless" || target.RagdollPelvis is not { } hips) return;
        _eye ??= new Camera3D { Name = "CrashNetEye", Far = 3000f };
        if (_eye.GetParent() == null) GetTree().Root.AddChild(_eye);
        var side = target.GlobalBasis.X;
        _eye.LookAtFromPosition(hips + side * 7f + Vector3.Up * 2.5f, hips, Vector3.Up);
        _eye.Current = true;
        _pending = name;
    }

    private string? _pending;
    private int _wait;

    public override void _Process(double delta)
    {
        if (_pending == null) return;
        // a frame for the eye to render from its new place
        if (++_wait < 2) return;
        _wait = 0;
        string path = ProjectSettings.GlobalizePath($"res://test_output/crashnet_{_pending}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[crashnet] {_role} screenshot {path}");
        _pending = null;
    }
}
