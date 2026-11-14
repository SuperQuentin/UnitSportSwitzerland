using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

public partial class Globe
{
    /// <summary><c>--nomorph</c>: the geomorph off (#1656), for the climb probe's before.</summary>
    public static bool MorphOff => CmdArgs.Has("--nomorph");

    /// <summary>
    /// The ground the globe draws over a world point as the screen shows it (#1656, the climb probe's read-out, not per
    /// frame): the deepest visible tile holding the point, its vertices morphed as <c>geomorph.gdshaderinc</c> morphs
    /// them for a camera at <paramref name="camera"/>, along the tile's up, or its lake's surface over it. NaN where no
    /// visible tile holds it.
    /// </summary>
    public float DrawnSurfaceAt(Vector3 world, EcefPos p, Vector3 camera, out int level)
    {
        level = -1;
        for (int l = _maxLevel; l >= 0; l--)
        {
            var key = CubeKey.FromEcef(p, l);
            if (!_patches.TryGetValue(key, out var node) || !node.Visible) continue;
            float young = node.GetInstanceShaderParameter(Young).VariantType == Variant.Type.Float ? (float)node.GetInstanceShaderParameter(Young) : 0f;
            if (MorphedTop(node, world, camera, _morph, young) is not { } y) continue;
            level = l;
            // a lake's surface over its sunk ground, where shown
            if (_lakes.TryGetValue(key, out var lake) && lake.Visible && TopOf(lake, world) is { } w && w > y) y = w;
            return y;
        }
        return float.NaN;
    }

    // a probe's copies of the meshes' arrays (SurfaceGetArrays copies them out of the renderer: per call, per frame, it
    // held the climb at 7 fps and its own streaming with it); dropped with the mesh
    private readonly ConditionalWeakTable<Mesh, MeshCopy> _meshCopies = new();

    private sealed record MeshCopy(Vector3[] V, int[] Idx, float[]? Custom);

    // TopOf with each vertex morphed as the shader does (geomorph.gdshaderinc); the skirts (vertical) passed over
    private float? MorphedTop(MeshInstance3D node, Vector3 world, Vector3 camera, Vector2 morph, float young)
    {
        if (node.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() == 0) return null;
        var xf = node.GlobalTransform;
        var q = xf.AffineInverse() * world;
        if (!_meshCopies.TryGetValue(mesh, out var copy))
        {
            var arrays = mesh.SurfaceGetArrays(0);
            copy = new MeshCopy(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(), arrays[(int)Mesh.ArrayType.Index].AsInt32Array(),
                arrays[(int)Mesh.ArrayType.Custom0].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array());
            _meshCopies.Add(mesh, copy);
        }
        var (v, idx, custom) = (copy.V, copy.Idx, copy.Custom);
        Vector3 At(int i)
        {
            if (custom == null || custom[i * 4 + 3] <= 0 || morph.X <= 0 && young <= 0) return v[i];
            float d = (xf * v[i]).DistanceTo(camera), split = custom[i * 4 + 3] * morph.X;
            float k = Mathf.SmoothStep(1.3f * split, 1.9f * split, d);
            if (morph.Y > 0) k *= Mathf.SmoothStep(morph.Y, morph.Y * 1.3f, d);
            return v[i] + new Vector3(custom[i * 4], custom[i * 4 + 1], custom[i * 4 + 2]) * Math.Max(k, young);
        }
        float? best = null;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var a = At(idx[t]); var b = At(idx[t + 1]); var c = At(idx[t + 2]);
            float det = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
            if (Math.Abs(det) < 1e-6f) continue;
            float u = ((q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z)) / det;
            float w = ((b.X - a.X) * (q.Z - a.Z) - (q.X - a.X) * (b.Z - a.Z)) / det;
            if (u < 0 || w < 0 || u + w > 1) continue;
            float y = (xf * (a + u * (b - a) + w * (c - a))).Y;
            if (best is not { } bb || y > bb) best = y;
        }
        return best;
    }
}

/// <summary>
/// Climbs the camera from the ground to orbit and counts the globe's pops (#1656): <c>--globeclimb FROM_M,TO_M,SECONDS</c>
/// (an exponential climb over the start, looking down 60°). Every frame it reads the ground the globe draws
/// (<see cref="Globe.DrawnSurfaceAt"/>, geomorph included) at 25 points fixed on the Earth round the nadir, and turns each
/// change since the frame before into screen pixels at that point's distance: a level's split or merge that moves the
/// ground at once is a pop; the morph moves it a little each frame. Prints the worst step, how many went over
/// <see cref="PopPx"/>, the deepest level drawn under the nadir at each decade of altitude and how many tiles came
/// from the server. <c>--nomorph</c> for the before.
/// </summary>
public partial class GlobeClimb : Node
{
    public const float PopPx = 2f;

    /// <summary>Seconds hovering at the top before the last read-out.</summary>
    public const double HoldS = 10;

    // the grid's point 0.8 altitudes ahead, straight on: past the near leaves round the body, and short of the horizon
    // from orbit (2.5 altitudes ahead at 2,000 km is past it)
    private const int FarMiddle = 2 * 7 + 3;

    public static string[]? ParseArgs() => CmdArgs.Value("--globeclimb")?.Split(',') is { Length: 3 } p ? p : null;

    private readonly Camera3D _camera;
    private readonly WorldOrigin _origin;
    private readonly double _from, _to, _seconds;
    private double _t = -1, _settle = 6;
    private readonly List<EcefPos> _points = new();
    private GlobalPos _nadir;
    private double _gridAlt = double.NaN;
    private bool _placed;
    private float[] _last = [];
    private int[] _lastLevels = [];
    private int _frames, _pops, _lastLevel = -1;
    private float _worstPx, _worstAlt;
    private int _worstLevel;
    private readonly List<string> _levels = new();
    private double _nextDecade;

    public GlobeClimb(Camera3D camera, WorldOrigin origin, double fromM, double toM, double seconds)
    {
        (_camera, _origin, _from, _to, _seconds) = (camera, origin, fromM, toM, seconds);
        _nextDecade = fromM;
    }

    public override void _Process(double delta)
    {
        if (Globe.Current is not { } globe) return;
        _camera.Current = true;
        if (_t < 0)
        {
            // settle at the start: the near ground and the first levels in
            Place(_from);
            _settle -= delta;
            if (_settle > 0) return;
            _t = 0;
            return;
        }
        _t += delta;
        double alt = _from * Math.Pow(_to / _from, Math.Min(_t / _seconds, 1));
        Place(alt);
        // the points across the view (7 x 7, out to 2.5 altitudes ahead), laid again every 30 % of climb: a point is only
        // compared with itself in the frame before
        if (!(alt < _gridAlt * 1.3))
        {
            _gridAlt = alt;
            _points.Clear();
            double span = Math.Min(alt, 2_000_000);
            for (int j = 0; j < 7; j++)
                for (int i = -3; i <= 3; i++)
                    _points.Add(_nadir.Moved(i * span / 2, j * span * 2.5 / 6, 0).Ecef);
            _last = new float[_points.Count];
            _lastLevels = new int[_points.Count];
            Array.Fill(_last, float.NaN);
        }
        var cam = _camera.GlobalPosition;
        float focal = GetViewport().GetVisibleRect().Size.Y / (2 * Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2));
        int deepest = -1;
        for (int k = 0; k < _points.Count; k++)
        {
            var g = GlobalPos.FromEcef(_points[k]);
            var world = _origin.ToWorld(g);
            float wy = globe.DrawnSurfaceAt(world, _points[k], cam, out int level);
            // the middle of the view 0.8 altitudes ahead: under the nadir the near leaves round the body draw
            if (k == FarMiddle) deepest = globe.DrawnLevelAt(_points[k]);
            // its height over the ellipsoid: the same whatever the frame (a re-anchor turns world y)
            float y = float.IsNaN(wy) ? float.NaN : (float)_origin.ToGlobal(world with { Y = wy }).Geodetic.H;
            // a change within one tile is the morph's own motion or a lake's surface shown or hidden (15 km): a pop is a
            // tile replaced by another level
            if (!float.IsNaN(y) && !float.IsNaN(_last[k]) && level != _lastLevels[k])
            {
                float d = (world with { Y = wy }).DistanceTo(cam);
                // the step seen at the point: its height change across the view (looking down, most of it)
                float px = Math.Abs(y - _last[k]) * focal / Math.Max(d, 1f);
                // the camera's own climb moves nothing on the ground: only the ground's own change counts
                if (px > _worstPx) (_worstPx, _worstAlt, _worstLevel) = (px, (float)alt, level);
                if (px > PopPx)
                {
                    _pops++;
                    if (_pops <= 20)
                        GD.Print($"[globeclimb] step {px:F1} px at {alt / 1000:F2} km, point {k} ({d / 1000:F1} km off): {_last[k]:F1} m (level {_lastLevels[k]}) to {y:F1} m (level {level})");
                }
            }
            _last[k] = y;
            _lastLevels[k] = level;
        }
        _frames++;
        if (alt >= _nextDecade)
        {
            _levels.Add($"{alt / 1000:F0} km: level {deepest}");
            _nextDecade *= 3;
        }
        _lastLevel = deepest;
        // then a hover at the top (HoldS): what the climb streamed too late to draw on the way has come by its end
        if (_t < _seconds + HoldS) return;
        var pyramid = Globe.DataSource;
        int streamed = pyramid?.Keys.Count(pyramid.IsStreamed) ?? 0;
        GD.Print($"[globeclimb] {_frames} frames from {_from / 1000:F1} to {_to / 1000:F0} km in {_seconds:F0} s and {HoldS:F0} s at the top{(Globe.MorphOff ? ", geomorph off" : "")}: "
            + $"worst ground step {_worstPx:F1} px (at {_worstAlt / 1000:F1} km, level {_worstLevel}), {_pops} steps over {PopPx} px");
        GD.Print($"[globeclimb] deepest level ahead: {string.Join(", ", _levels)}; at the top after {HoldS:F0} s: level {deepest}; "
            + $"the pyramid: {pyramid?.Describe()}, {streamed} tiles best from the server");
        GD.Print($"RESULT {(_frames > 0 ? "PASS" : "FAIL")} globeclimb: {_pops} pops over {PopPx} px, worst {_worstPx:F1} px");
        GetTree().Quit(_frames > 0 ? 0 : 1);
    }

    /// <summary>The camera over the start's nadir at an altitude, looking down 60° northward (the frame's up and north there).</summary>
    private void Place(double alt)
    {
        if (!_placed)
        {
            _nadir = _origin.ToGlobal(_camera.GlobalPosition).WithAlt(0);
            _placed = true;
        }
        var at = _origin.ToWorld(_nadir.WithAlt(alt));
        var ahead = _origin.ToWorld(_nadir.Moved(0, 1000, 0).WithAlt(alt));
        var up = (_origin.ToWorld(_nadir.WithAlt(alt + 1000)) - at).Normalized();
        var north = (ahead - at).Normalized();
        var look = (north * Mathf.Cos(Mathf.DegToRad(60)) - up * Mathf.Sin(Mathf.DegToRad(60))).Normalized();
        _camera.GlobalPosition = at;
        _camera.LookAt(at + look, up);
    }
}
