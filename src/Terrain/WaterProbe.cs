using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain;

/// <summary>
/// Verification helper for #298's beds: at an LV95 point over water, the still level the runtime
/// reads (<see cref="ChunkManager.TryGetWaterLevel"/>), the terrain height (the bed) and what a ray
/// straight down hits (collision must be the bed, not the surface). Works on real tiles with a
/// <c>.water</c> layer and on generated ones.
///
///   godot --headless --path . -- --chunks DIR --waterprobe lv95E,lv95N,seconds[,minDepth]
///
/// RESULT ok when there is water, its bed is at least <c>minDepth</c> (default 0) below the level,
/// and the ray lands on the bed within 0.5 m.
/// </summary>
public partial class WaterProbe : Node3D
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly double _e, _n, _settle, _minDepth;
    private double _elapsed;
    private bool _done;

    public WaterProbe(ChunkManager chunks, WorldOrigin origin, double e, double n, double settle, double minDepth)
    {
        _chunks = chunks;
        _origin = origin;
        _e = e;
        _n = n;
        _settle = settle;
        _minDepth = minDepth;
    }

    public override void _Ready()
    {
        Position = _origin.ToWorld(_e, _n, 0);
        _chunks.AddAnchor(this, collision: true);
    }

    public override void _ExitTree() => _chunks.RemoveAnchor(this);

    public static double[]? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--waterprobe")
            {
                var parts = args[i + 1].Split(',');
                if (parts.Length is < 3 or > 4) return null;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                return parts.Select(p => double.Parse(p, inv)).ToArray();
            }
        return null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _elapsed += delta;
        if (_elapsed < _settle) return;
        _done = true;

        var at = _origin.ToWorld(_e, _n, 0);
        bool wet = _chunks.TryGetWaterLevel(at, out float level);
        bool ground = _chunks.TryGetHeight(at, out float bed);
        var space = GetViewport().World3D.DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            _origin.ToWorld(_e, _n, 5000), _origin.ToWorld(_e, _n, -500)));
        float? hitY = hit.Count > 0 ? hit["position"].AsVector3().Y : null;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        GD.Print(string.Format(inv, "[waterprobe] at {0:F0},{1:F0}: level {2}, terrain {3}, collision {4}",
            _e, _n, wet ? level.ToString("F2", inv) : "none", ground ? bed.ToString("F2", inv) : "none",
            hitY is { } y ? y.ToString("F2", inv) : "none"));

        string? fail = !wet ? "no water level here"
            : !ground ? "no terrain height here"
            : level - bed < _minDepth ? string.Format(inv, "depth {0:F2} m under {1:F2} m", level - bed, _minDepth)
            : hitY is not { } h ? "the ray hit nothing"
            : Math.Abs(h - bed) > 0.5f ? string.Format(inv, "collision at {0:F2}, not on the bed {1:F2}", h, bed)
            : null;
        GD.Print(fail == null
            ? string.Format(inv, "[waterprobe] RESULT: ok (depth {0:F2} m)", level - bed)
            : $"[waterprobe] RESULT: FAILED {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
