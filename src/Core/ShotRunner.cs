using Godot;

namespace UnitSport.Core;

/// <summary>
/// Headless-ish verification helper: places the camera, waits for streaming to settle,
/// writes a PNG and quits. Lets screenshots be captured from the command line without
/// an editor attached.
///
///   godot --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png
/// </summary>
public partial class ShotRunner : Node
{
    private readonly Camera3D _camera;
    private readonly string _outPath;
    private readonly double _settleSeconds;
    private double _elapsed;
    private bool _done;

    public ShotRunner(Camera3D camera, Vector3 position, float pitchDeg, float yawDeg,
        double settleSeconds, string outPath)
    {
        _camera = camera;
        _outPath = outPath;
        _settleSeconds = settleSeconds;
        camera.Position = position;
        camera.Rotation = new Vector3(Mathf.DegToRad(pitchDeg), Mathf.DegToRad(yawDeg), 0);
    }

    /// <summary>Parses "--shot x,y,z,pitch,yaw,seconds,path" from the command line.</summary>
    public static string[]? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot")
            {
                var parts = args[i + 1].Split(',');
                return parts.Length == 7 ? parts : null;
            }
        return null;
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        // A mode may make its own camera current — GPX playback does, from a deferred call
        // that lands after this node is constructed. The shot asked for one exact
        // transform, so take it back every frame until the picture is written.
        _camera.Current = true;
        _elapsed += delta;
        if (_elapsed < _settleSeconds) return;
        _done = true;

        var image = GetViewport().GetTexture().GetImage();
        var err = image.SavePng(_outPath);
        GD.Print(err == Error.Ok
            ? $"[shot] wrote {_outPath} ({image.GetWidth()}x{image.GetHeight()}) at {_camera.Position}"
            : $"[shot] FAILED to write {_outPath}: {err}");
        GD.Print($"[shot] fps={Engine.GetFramesPerSecond()} " +
                 $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)} " +
                 $"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} " +
                 $"mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F0}MB");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
