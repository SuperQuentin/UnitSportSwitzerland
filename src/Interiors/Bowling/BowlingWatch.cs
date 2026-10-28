using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// After your release, when not in VR (#1336, the owner's: no separate replay): the bowling camera stays over the lane
/// while the ball rolls, then, as the lane's own screen starts its slow-motion replay (<see cref="LaneRig.ReplayLane"/>),
/// glides up close to that screen and holds there while it plays, the roll's word sweeping over it at the end; then the
/// player's own camera is back. A roll with no replay (a gutter ball) gives it back after a while. Moving, Jump or
/// Cancel gives it back at once.
/// </summary>
public partial class BowlingWatch : Node
{
    /// <summary>The longest a roll is watched without a replay starting, s; the glide to the screen, s; how far in front of it, m.</summary>
    private const double NoReplay = 9, Glide = 0.9;
    private const float Back = 1.05f;

    private Camera3D _own = null!, _cam = null!;
    private LaneRig _rig = null!;
    private int _lane;
    private double _age, _onScreen = -1, _after = -1;
    private Transform3D _from;

    /// <summary>Watches lane <paramref name="lane"/> of <paramref name="rig"/> from <paramref name="start"/> (the bowling camera's place).</summary>
    public static void Begin(Node parent, Camera3D own, LaneRig rig, int lane, Transform3D start)
    {
        if (XR.XrSession.Active || !IsInstanceValid(own)) return;
        var w = new BowlingWatch { Name = "BowlingWatch", _own = own, _rig = rig, _lane = lane };
        w._cam = new Camera3D { Name = "WatchCamera", Fov = 55f, Near = 0.05f, Far = own.Far, Environment = own.Environment, Attributes = own.Attributes, CullMask = own.CullMask };
        parent.AddChild(w);
        w.AddChild(w._cam);
        w._cam.GlobalTransform = start;
        w._cam.MakeCurrent();
    }

    public override void _ExitTree()
    {
        if (IsInstanceValid(_own)) _own.MakeCurrent();
    }

    public override void _Process(double delta)
    {
        _age += delta;
        if (!IsInstanceValid(_rig) || !IsInstanceValid(_own) || Interrupted()) { QueueFree(); return; }
        bool playing = _rig.ReplayLane == _lane;
        if (playing && _onScreen < 0) { _onScreen = 0; _from = _cam.GlobalTransform; }
        if (_onScreen >= 0)
        {
            _onScreen += delta;
            var g = _rig.Geometry;
            // the screen faces the settees (−Z of the pair): stand a little in front of its middle, level with it
            var face = new Vector3(g.LaneX(_lane), g.ScreenY + 0.26f, g.ScreenZ);
            var eye = _rig.GlobalTransform * (face + new Vector3(0, -0.05f, -Back));
            var want = new Transform3D(Basis.Identity, eye).LookingAt(_rig.GlobalTransform * face, Vector3.Up);
            float u = (float)Mathf.Clamp(_onScreen / Glide, 0, 1);
            _cam.GlobalTransform = _from.InterpolateWith(want, u * u * (3 - 2 * u));
            if (!playing && _after < 0) _after = 0;
        }
        if (_after >= 0 && (_after += delta) > 0.6) { QueueFree(); return; }
        if (_onScreen < 0 && _age > NoReplay) QueueFree();
    }

    /// <summary>The player wants their camera back: moving, Jump, or Cancel.</summary>
    private static bool Interrupted() =>
        Input.IsActionJustPressed(PlayerInput.Jump) || Input.IsActionJustPressed("ui_cancel")
        || Input.GetVector(PlayerInput.MoveLeft, PlayerInput.MoveRight, PlayerInput.MoveForward, PlayerInput.MoveBack).LengthSquared() > 0.2f;
}
