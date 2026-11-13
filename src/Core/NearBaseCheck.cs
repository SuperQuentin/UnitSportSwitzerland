using System.Threading.Tasks;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// <c>--frame earth --nearbasecheck --chunks fixture:straight --traffic 0 --horizon 150</c>, windowed (#1811): the root
/// cause of the user's "nothing renders, any camera, low down only". A door lens (<c>DoorPortals.KeepOutOfDoorways</c>)
/// lowers the drawn camera's near plane to 5 mm in <c>_Process</c> and puts it back after the frame is drawn. With the
/// camera more than 1 km from the world's origin, <c>CameraGlobal.Climb</c> (before the draw) raised that 5 mm to its
/// floor (distance / 200,000) and kept the 5 mm as the camera's base; once the floor fell under it (the camera low and
/// near the origin), it wrote the 5 mm back as the base, for good. Under the user's 160 km far plane (horizon 150 km)
/// that is far / near = 3.2e7, past what Godot's float frustum takes (~1.7e7): <c>create_frustum_points</c> fails every
/// frame and nothing draws, until the camera is high enough for the floor to raise it again (Space).
///
/// <para>
/// The check does what a door lens does, for <see cref="DoorFrames"/> frames with the fly camera 3 km up, then leaves
/// the door and brings the camera down to 400 m, and asserts far / near is then within <see cref="GodotLimit"/>. Drawn
/// frames only: headless draws nothing and <c>CameraGlobal</c> runs before each drawn frame. Prints
/// <c>[nearbasecheck] RESULT</c>; the log's <c>create_frustum_points</c> lines after <c>[nearbasecheck] low</c> are the
/// renderer's own word.
/// </para>
/// </summary>
public partial class NearBaseCheck : Node
{
    public static bool Requested => CmdArgs.Has("--nearbasecheck");

    /// <summary>Past this far / near, (far + near) / (far − near) rounds to 1 in floats and the far plane degenerates.</summary>
    private const float GodotLimit = 1.7e7f;
    /// <summary><c>DoorPortals.DoorwayNear</c>.</summary>
    private const float DoorNear = 0.005f;
    private const int DoorFrames = 30;

    private readonly System.Func<Camera3D?> _camera;
    private Camera3D? _door;
    private float _doorRestore;
    private int _failed;
    private float _ownNear;

    public NearBaseCheck(System.Func<Camera3D?> camera)
    {
        Name = "NearBaseCheck";
        _camera = camera;
    }

    public override void _Ready()
    {
        RenderingServer.FramePostDraw += PutBack;
        _ = Run();
    }

    public override void _ExitTree() => RenderingServer.FramePostDraw -= PutBack;

    /// <summary>As a door lens: the near plane lowered for this frame (in _Process, before CameraGlobal's pre-draw).</summary>
    public override void _Process(double delta)
    {
        if (_door == null) return;
        _doorRestore = _door.Near;
        _doorFar = _door.Far;
        Interiors.DoorPortals.TakeLens(_door, _doorRestore);
        _door.Near = DoorNear;
        // DoorPortals cuts the far plane to match (its MaxDepthRatio)
        if (_door.Far > DoorNear * DoorDepth) _door.Far = DoorNear * DoorDepth;
        _held = true;
    }

    /// <summary>As <c>DoorPortals.PutCameraBack</c>: once the frame is drawn, the planes as they were.</summary>
    private void PutBack()
    {
        Interiors.DoorPortals.DropLens();
        if (!_held || _door == null || !IsInstanceValid(_door)) { _held = false; return; }
        _held = false;
        _door.Near = _doorRestore;
        _door.Far = _doorFar;
    }

    /// <summary><c>DoorPortals.MaxDepthRatio</c>.</summary>
    private const float DoorDepth = 2e6f;
    private float _doorFar;
    private bool _held;

    private static void Log(string what) => GD.Print($"[nearbasecheck] {what}");

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Run()
    {
        if (DisplayServer.GetName() == "headless") { Finish("windowed only: headless draws nothing, and CameraGlobal works before each drawn frame"); return; }
        Camera3D? cam = null;
        for (int i = 0; i < 600 && (cam = _camera()) == null; i++) await Frames(1);
        if (cam == null) { Finish("no fly camera"); return; }
        await Frames(120);
        cam.Current = true;
        cam.GlobalPosition = new Vector3(0, 3000, 0);
        await Frames(10);
        Log($"3 km up: near {cam.Near} far {cam.Far}");
        _ownNear = cam.Near;

        _door = cam;
        await Frames(DoorFrames);
        _door = null;
        await Frames(2);
        Log($"out of the door: near {cam.Near} far {cam.Far}");

        cam.GlobalPosition = new Vector3(0, 400, 0);
        Log("low");
        await Frames(60);
        float ratio = cam.Far / cam.Near;
        Log($"400 m up: near {cam.Near} far {cam.Far}, far / near {ratio:E2}");
        if (!(ratio <= GodotLimit)) { _failed++; Log($"FAIL far / near {ratio:E2} past the float frustum's {GodotLimit:E1}: nothing draws"); }
        else Log("ok   far / near within the float frustum");
        // and the camera's own near plane, not the lens's left behind
        if (!Mathf.IsEqualApprox(cam.Near, _ownNear, _ownNear * 0.05f)) { _failed++; Log($"FAIL near {cam.Near} m, not the camera's own {_ownNear} m: the lens's was kept"); }
        else Log($"ok   the camera's own near plane, {cam.Near} m");
        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { _failed++; Log($"FAIL {error}"); }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
