using Godot;
using UnitSport.Player;

namespace UnitSport.Ui;

/// <summary>
/// The spectator's camera (#1268, taken out of the Battle Royale's): a <see cref="Camera3D"/> that follows a body
/// from behind, added as a chunk anchor so the ground streams round it, ← / → (<c>ui_left</c> / <c>ui_right</c>,
/// the D-pad, a VR stick) stepping through who may be followed. The owner (a Battle Royale, a Versteckis game)
/// hands it that list with <see cref="SetCandidates"/> when its state changes, never per frame; the body followed
/// is looked up only when the target changes (<c>general/perf-no-per-frame-allocations</c>).
/// Docs: <c>docs/notes/ui/follow-cam.md</c>.
/// </summary>
public partial class FollowCam : Node
{
    private static readonly StringName Left = "ui_left", Right = "ui_right";

    /// <summary>The local player: where the camera starts, and whose camera comes back when it stops.</summary>
    public Func<FootPlayer?> LocalPlayer { get; set; } = () => null;
    /// <summary>A peer's body on this client, or null while it is not streamed here.</summary>
    public Func<long, FootPlayer?> BodyOf { get; set; } = _ => null;
    public Action<Node3D>? AddAnchor { get; set; }
    public Action<Node3D>? RemoveAnchor { get; set; }

    private Camera3D? _camera;
    private bool _anchored;
    private readonly List<long> _who = new();
    private int _index;
    private long _watching;
    private FootPlayer? _body;
    private long _bodyOf;

    /// <summary>The camera is the view now.</summary>
    public bool Active => _camera is { Current: true };

    /// <summary>The peer followed while <see cref="Active"/>, else 0.</summary>
    public long Watching => Active ? _watching : 0;

    /// <summary>The peer it will follow (set before it starts: the killer, a probe's choice), 0 for the first candidate.</summary>
    public long Target => _watching;

    /// <summary>Who may be followed, in the order ← / → steps through them (the owner's state changed).</summary>
    public void SetCandidates(IEnumerable<long> who)
    {
        _who.Clear();
        _who.AddRange(who);
    }

    /// <summary>Follow this peer next (a killer, a team-mate); it stays only while it is a candidate.</summary>
    public void Prefer(long peer) => _watching = peer;

    /// <summary>Every frame while spectating: the camera eases in behind the body followed.</summary>
    public void Follow(double delta)
    {
        if (_who.Count == 0) return;
        if (!_who.Contains(_watching))
        {
            _index = ((_index % _who.Count) + _who.Count) % _who.Count;
            _watching = _who[_index];
        }
        if (_bodyOf != _watching || _body == null || !IsInstanceValid(_body))
        {
            _bodyOf = _watching;
            _body = BodyOf(_watching);
        }
        if (_camera == null)
        {
            _camera = new Camera3D { Name = "Spectator", Fov = 70, Far = 20000 };
            AddChild(_camera);
        }
        if (!_camera.Current)
        {
            _camera.GlobalPosition = (LocalPlayer()?.GlobalPosition ?? Vector3.Zero) + Vector3.Up * 4f;
            _camera.Current = true;
        }
        if (!_anchored && AddAnchor != null)
        {
            AddAnchor(_camera);
            _anchored = true;
        }
        if (_body == null) return;   // not streamed here yet: hold the view until it arrives
        var target = _body;
        var behind = new Vector3(Mathf.Sin(target.NetYaw), 0, Mathf.Cos(target.NetYaw)) * 6f + Vector3.Up * 2.8f;
        var want = target.GlobalPosition + behind;
        float k = 1f - Mathf.Exp(-6f * (float)delta);
        _camera.GlobalPosition = _camera.GlobalPosition.DistanceTo(want) > 200f ? want : _camera.GlobalPosition.Lerp(want, k);
        var look = target.GlobalPosition + Vector3.Up * 1.5f;
        if (!_camera.GlobalPosition.IsEqualApprox(look)) _camera.LookAt(look, Vector3.Up);
    }

    /// <summary>Back to the player's own camera.</summary>
    public void Stop()
    {
        if (_camera == null) return;
        if (_anchored) RemoveAnchor?.Invoke(_camera);
        _anchored = false;
        _camera.Current = false;
        if (LocalPlayer() is { } me) me.Camera.Current = true;
        _watching = 0;
        _body = null;
        _bodyOf = 0;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Active || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(Left)) _index--;
        else if (e.IsActionPressed(Right)) _index++;
        else return;
        _watching = 0;
        GetViewport().SetInputAsHandled();
    }
}
