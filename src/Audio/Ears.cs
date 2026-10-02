using Godot;
using UnitSport.Player;

namespace UnitSport.Audio;

/// <summary>
/// The listener of this machine (#375): an <see cref="AudioListener3D"/> at the local body's
/// head, facing where the body faces, so what reaches the ears is what would reach that body's
/// ears in stereo, whatever the camera does. A third-person or chase camera, a free look, a crash
/// camera: none of them moves a sound, its panning, its distance, the reverb or a wall in the way.
///
/// <para>
/// Without a body (title screen, spectating, a probe with no local player) the camera's frame is
/// used, as Godot does on its own. Every audio system asks here for the ear
/// (<see cref="Position"/>, <see cref="Frame"/>) instead of the viewport camera.
/// </para>
///
/// <para>
/// The body's facing is eased (~0.12 s), so a figure snapping round to its run direction sweeps
/// the panning instead of flipping it. Runs late in the frame (<see cref="Node.ProcessPriority"/>),
/// after the player and its camera have moved.
/// </para>
/// </summary>
public partial class Ears : Node3D, Core.IOriginShiftAware
{
    private const float TurnSeconds = 0.12f;

    public static Ears? Instance { get; private set; }

    private readonly Func<FootPlayer?> _body;
    private AudioListener3D _listener = null!;
    private Basis _basis = Basis.Identity;
    private Quaternion _rotation = Quaternion.Identity;
    private bool _placed;

    /// <summary>The ear point this frame (the camera's when there is no listener yet).</summary>
    public static Vector3 Position => Frame.Origin;

    /// <summary>Where the ears are and which way the head faces.</summary>
    public static Transform3D Frame { get; private set; } = Transform3D.Identity;

    /// <summary>Whether <see cref="Frame"/> has been set at least once.</summary>
    public static bool Ready => Instance is { _placed: true };

    /// <summary>The body the ears belong to, or null when they follow the camera.</summary>
    public static FootPlayer? Body { get; private set; }

    /// <summary>The body whose closed cabin the ears are in, or null (<see cref="FootPlayer.CabinOwner"/>).</summary>
    public static FootPlayer? Cabin { get; private set; }

    /// <summary>0 in the open .. 1 shut in a cabin, eased over ~0.3 s: what the world bus's cabin filter follows.</summary>
    public static float Shut { get; private set; }

    public Ears(Func<FootPlayer?> body) => _body = body;

    public Ears() : this(() => null) { }

    public override void _Ready()
    {
        Name = "Ears";
        Instance = this;
        TopLevel = true;
        ProcessPriority = 1000;
        // the ears move too: a car passing a car pitches by both speeds (the origin shifter resets it)
        _listener = new AudioListener3D { Name = "Listener", DopplerTracking = AudioListener3D.DopplerTrackingEnum.IdleStep };
        AddChild(_listener);
        _listener.MakeCurrent();
    }

    public override void _ExitTree()
    {
        if (Instance != this) return;
        Instance = null;
        Body = Cabin = null;
        Shut = 0;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var body = _body();
        if (body != null && (!IsInstanceValid(body) || !body.IsInsideTree())) body = null;
        Transform3D frame;
        if (body != null) frame = body.EarFrame;
        else if (GetViewport()?.GetCamera3D() is { } cam && cam.IsInsideTree()) frame = cam.GlobalTransform;
        else return;

        // ease the facing; a first frame or a teleport of the head takes it as it is
        // through normalised quaternions: a camera basis carries a hair of scale, and Basis.Slerp
        // throws on anything not exactly a rotation
        var want = frame.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
        _rotation = _placed ? _rotation.Slerp(want, 1f - Mathf.Exp(-dt / TurnSeconds)).Normalized() : want;
        _basis = new Basis(_rotation);
        _placed = true;
        Frame = new Transform3D(_basis, frame.Origin);
        GlobalTransform = Frame;
        if (!_listener.IsCurrent()) _listener.MakeCurrent();

        Body = body;
        Cabin = body?.CabinOwner;
        Shut = Mathf.MoveToward(Shut, Cabin != null ? 1f : 0f, dt / 0.3f);
    }

    /// <summary>
    /// Moves the ears with the world and restarts the listener's Doppler tracker there, or the
    /// shift reads as kilometres in one step: a pitch spike on every sound.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        Frame = shift.Apply(Frame);
        GlobalTransform = Frame;
        var mode = _listener.DopplerTracking;
        _listener.DopplerTracking = AudioListener3D.DopplerTrackingEnum.Disabled;
        _listener.DopplerTracking = mode;
    }

    /// <summary>The ear point, or the camera's for a node with no <see cref="Ears"/> in the scene (probes).</summary>
    public static Vector3? Of(Node node)
    {
        if (Ready) return Position;
        return node.GetViewport()?.GetCamera3D() is { } cam && cam.IsInsideTree() ? cam.GlobalPosition : null;
    }

    /// <summary>The ear frame, or the camera's when there is no <see cref="Ears"/>.</summary>
    public static Transform3D? FrameOf(Node node)
    {
        if (Ready) return Frame;
        return node.GetViewport()?.GetCamera3D() is { } cam && cam.IsInsideTree() ? cam.GlobalTransform : null;
    }
}
