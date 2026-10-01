using System.Globalization;
using Godot;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// <c>--npcwatch prefix[,before[,after]]</c> (windowed client, loopback checks of the NPC handoff,
/// #50/#85): a chase camera on the first race NPC this client sees, a frame every 0.1 s kept in
/// memory for the last <c>before</c> s (default 4); when its simulator changes it saves those and
/// <c>after</c> s more (default 4) as <c>prefix_NNN.png</c> (640 px wide; frame <c>before×10</c> is
/// the handoff). Every drawn frame whose displacement parts from its velocity × dt is logged as
/// <c>[npcwatch] ... JUMP</c> — a handoff that jumps or stutters shows there and in the frames.
/// </summary>
public partial class NpcWatch : Node
{
    private readonly string _prefix;
    private readonly double _before, _after;
    private readonly System.Collections.Generic.Queue<Image> _ring = new();
    private double _sinceHandoff = -1;
    private FootPlayer? _npc;
    private Camera3D? _cam;
    private Vector3 _last, _heading = Vector3.Forward;
    private double _moving = -1, _sinceShot;
    private int _frame, _sim, _jumps;
    private readonly bool _headless = DisplayServer.GetName() == "headless";

    private NpcWatch(string prefix, double before, double after)
    {
        Name = "NpcWatch";
        ProcessPriority = 1000;   // after the player's own camera, which makes itself current every frame
        _prefix = prefix;
        _before = before;
        _after = after;
    }

    public static NpcWatch? FromArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--npcwatch");
        if (i < 0 || i + 1 >= args.Length) return null;
        var parts = args[i + 1].Split(',');
        double Num(int k, double fallback) =>
            parts.Length > k && double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        return new NpcWatch(parts[0], Num(1, 4), Num(2, 4));
    }

    private bool Capturing => _sinceHandoff >= 0 && _sinceHandoff < _after;

    public override void _PhysicsProcess(double delta)
    {
        if (_npc == null || !IsInstanceValid(_npc))
        {
            _npc = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p.Npc);
            if (_npc == null) return;
            _last = _npc.GlobalPosition;
            _sim = _npc.SimPeer;
            GD.Print($"[npcwatch] watching {_npc.Name}, simulated by {_sim}");
        }
    }

    /// <summary>Measured per drawn frame, not per physics step: a remote body moves where it is drawn
    /// (its interpolator), and per physics step it read 0, 0.2, 0, 0.2 m — a "jump" every other step.</summary>
    private void Measure(double delta)
    {
        var p = _npc!.GlobalPosition;
        var vel = _npc.WorldVelocity;
        float dt = (float)delta;
        if (_moving < 0 && vel.Length() > 5f) _moving = 0;
        if (_moving >= 0) _moving += delta;
        float step = (p - _last).Length(), expect = vel.Length() * dt;
        if (_npc.SimPeer != _sim)
        {
            GD.Print($"[npcwatch] t={_moving:F2} HANDOFF simulator {_sim} -> {_npc.SimPeer}{(_npc.IsMultiplayerAuthority() ? " (here)" : "")}, frame {_frame}");
            _sim = _npc.SimPeer;
            _sinceHandoff = 0;
            foreach (var img in _ring) img.SavePng($"{_prefix}_{_frame++:000}.png");
            _ring.Clear();
        }
        if (_sinceHandoff >= 0) _sinceHandoff += delta;
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
        Measure(delta);
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
        // headless: no picture, the log lines (speed per drawn frame around the handoff) still come
        if (_headless || _sinceHandoff >= _after || (_sinceShot += delta) < 0.1) return;
        _sinceShot = 0;
        var image = GetViewport().GetTexture().GetImage();
        image.Resize(640, 640 * image.GetHeight() / Mathf.Max(image.GetWidth(), 1));
        if (!Capturing)
        {
            _ring.Enqueue(image);
            while (_ring.Count > _before * 10) _ring.Dequeue();
            return;
        }
        image.SavePng($"{_prefix}_{_frame++:000}.png");
        if (_sinceHandoff + 0.1 >= _after) GD.Print($"[npcwatch] {_frame} frames saved, {_jumps} jumps");
    }
}
