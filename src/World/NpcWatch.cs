using System.Globalization;
using Godot;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// <c>--npcwatch prefix[,delay[,seconds]]</c> (windowed client, loopback checks of the NPC handoff,
/// #50/#85): a chase camera on the first race NPC this client sees. From <c>delay</c> s (default 8)
/// after it first moves faster than 5 m/s, for <c>seconds</c> (default 12), it saves a frame every
/// 0.1 s (<c>prefix_NNN.png</c>, 640 px wide) and logs every physics step as <c>[npcwatch]</c>: the
/// step's displacement against its velocity × dt, its simulator, and <c>JUMP</c> where the two part —
/// a handoff that jumps or stutters shows there and in the frames.
/// </summary>
public partial class NpcWatch : Node
{
    private readonly string _prefix;
    private readonly double _delay, _seconds;
    private FootPlayer? _npc;
    private Camera3D? _cam;
    private Vector3 _last, _heading = Vector3.Forward;
    private double _moving = -1, _sinceShot;
    private int _frame, _sim, _jumps;

    private NpcWatch(string prefix, double delay, double seconds)
    {
        Name = "NpcWatch";
        _prefix = prefix;
        _delay = delay;
        _seconds = seconds;
    }

    public static NpcWatch? FromArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--npcwatch");
        if (i < 0 || i + 1 >= args.Length) return null;
        var parts = args[i + 1].Split(',');
        double Num(int k, double fallback) =>
            parts.Length > k && double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        return new NpcWatch(parts[0], Num(1, 8), Num(2, 12));
    }

    private bool Capturing => _moving >= _delay && _moving < _delay + _seconds;

    public override void _PhysicsProcess(double delta)
    {
        if (_npc == null || !IsInstanceValid(_npc))
        {
            _npc = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p.Npc);
            if (_npc == null) return;
            _last = _npc.GlobalPosition;
            _sim = _npc.SimPeer;
            GD.Print($"[npcwatch] watching {_npc.Name}, simulated by {_sim}");
            return;
        }
        var p = _npc.GlobalPosition;
        var vel = _npc.WorldVelocity;
        float dt = (float)delta;
        if (_moving < 0 && vel.Length() > 5f) _moving = 0;
        if (_moving >= 0) _moving += delta;
        float step = (p - _last).Length(), expect = vel.Length() * dt;
        if (_npc.SimPeer != _sim)
        {
            GD.Print($"[npcwatch] t={_moving:F2} HANDOFF simulator {_sim} -> {_npc.SimPeer}{(_npc.IsMultiplayerAuthority() ? " (here)" : "")}, frame {_frame}");
            _sim = _npc.SimPeer;
        }
        // a step more than 0.3 m and half again off what its velocity says
        bool jump = _moving >= 0 && Mathf.Abs(step - expect) > 0.3f + 0.5f * expect;
        if (jump) _jumps++;
        if (Capturing || jump)
            GD.Print($"[npcwatch] t={_moving:F2} step {step:F2} m, v*dt {expect:F2} m, {vel.Length() * 3.6f:F0} km/h, sim {_sim}{(jump ? " JUMP" : "")}");
        _last = p;
        var flat = new Vector3(vel.X, 0, vel.Z);
        if (flat.Length() > 2f) _heading = flat.Normalized();
    }

    public override void _Process(double delta)
    {
        if (_npc == null || !IsInstanceValid(_npc)) return;
        if (_cam == null)
        {
            _cam = new Camera3D { Name = "NpcWatchCam", Fov = 60f, Far = 4000f };
            AddChild(_cam);
        }
        // a chase camera 9 m behind and 3.5 m above, eased so the picture shows the NPC's own motion
        var eye = _npc.GlobalPosition - _heading * 9f + Vector3.Up * 3.5f;
        var at = _cam.GlobalPosition.DistanceTo(eye) > 30f ? eye : _cam.GlobalPosition.Lerp(eye, 1f - Mathf.Exp(-6f * (float)delta));
        _cam.GlobalTransform = new Transform3D(Flyer.Orient(_npc.GlobalPosition + Vector3.Up - at, Vector3.Up, Vector3.Forward), at);
        _cam.MakeCurrent();
        if (!Capturing) return;
        if ((_sinceShot += delta) < 0.1) return;
        _sinceShot = 0;
        var image = GetViewport().GetTexture().GetImage();
        image.Resize(640, 640 * image.GetHeight() / Mathf.Max(image.GetWidth(), 1));
        image.SavePng($"{_prefix}_{_frame++:000}.png");
        if (!Capturing || _frame % 20 == 0) GD.Print($"[npcwatch] {_frame} frames, {_jumps} jumps so far");
    }
}
