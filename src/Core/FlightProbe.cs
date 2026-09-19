using Godot;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Streaming smoothness check: flies the spectator camera in a straight line at a fixed speed
/// and prints the frame-time distribution, then quits.
///
///   godot --path . -- --fly x,y,z,yawDeg,speedMps,seconds
///
/// <para>
/// The thing this measures cannot be seen in a screenshot and is not what the settle report
/// says either: a world that loads completely can still hitch every time a ring boundary
/// crosses a tile. Frames over 20 and 33 ms are counted separately because a p99 of 12 ms
/// hides a dozen 80 ms stalls that a player feels one by one.
/// </para>
/// </summary>
public partial class FlightProbe : Node
{
    private readonly SpectatorCamera _camera;
    private readonly ChunkManager _chunks;
    private readonly Vector3 _velocity;
    private readonly double _warmup;
    private readonly double _seconds;
    private readonly List<double> _frames = new();
    private double _elapsed;
    private bool _done;

    public FlightProbe(SpectatorCamera camera, ChunkManager chunks, Vector3 start, float yawDeg,
        float speed, double seconds)
    {
        _camera = camera;
        _chunks = chunks;
        _seconds = seconds;
        _warmup = Math.Min(4, seconds / 3);
        camera.Position = start;
        camera.Rotation = new Vector3(Mathf.DegToRad(-6), Mathf.DegToRad(yawDeg), 0);
        var forward = -camera.GlobalTransform.Basis.Z with { Y = 0 };
        _velocity = forward.Normalized() * speed;
    }

    /// <summary>Parses "--fly x,y,z,yaw,speed,seconds".</summary>
    public static string[]? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--fly")
            {
                var parts = args[i + 1].Split(',');
                return parts.Length == 6 ? parts : null;
            }
        return null;
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        _camera.Current = true;
        _elapsed += delta;

        // let the first arrival settle before moving, or the report is the teleport, not the flight
        if (_elapsed < _warmup) return;
        _camera.Position += _velocity * (float)delta;
        _frames.Add(delta * 1000);

        if (_elapsed < _warmup + _seconds) return;
        _done = true;

        _frames.Sort();
        int n = _frames.Count;
        int over20 = _frames.Count(f => f > 20), over33 = _frames.Count(f => f > 33);
        double worst = _frames[^1];
        GD.Print($"[fly] {n} frames over {_seconds:F0} s at {_velocity.Length():F0} m/s: "
            + $"p50 {_frames[n / 2]:F1} ms, p95 {_frames[(int)(n * 0.95)]:F1}, p99 {_frames[(int)(n * 0.99)]:F1}, "
            + $"max {worst:F1}; >20 ms: {over20}, >33 ms: {over33}");
        GD.Print($"[fly] {_chunks.SettleReport(_camera.GlobalPosition)}");
        GD.Print($"[fly] worker time: {_chunks.BuildTimeReport()}");
        GD.Print($"[fly] mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F0}MB "
            + $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}");
        // a stall a player can feel is a failure, not a statistic
        GetTree().Quit(over33 > 0 ? 1 : 0);
    }
}
