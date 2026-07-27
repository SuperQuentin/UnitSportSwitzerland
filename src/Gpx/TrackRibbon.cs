using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// Draws the route as a raised 3D path following the terrain: a tread surface with kerbs
/// down each side that bed into the ground, rather than a flat decal.
///
/// It is rebuilt lazily rather than once at load: the terrain streams, so heights for
/// distant parts of the track are not known until those tiles arrive.
/// </summary>
public partial class TrackRibbon : MeshInstance3D
{
    private GpxTrack _track = null!;
    public GpxTrack Track => _track;
    private ChunkManager _chunks = null!;
    private WorldOrigin _origin = null!;
    private double _sinceRebuild = double.MaxValue;
    private int _lastResolved = -1;

    /// <summary>
    /// Sampling step along the track. Short enough that the path follows terrain
    /// contours instead of cutting through bumps between samples — the same failure the
    /// road ribbons had before they were densified.
    /// </summary>
    private const double StepM = 3.0;

    private const float HalfWidth = 0.8f;
    private const float TreadLift = 0.28f;   // tread height above ground
    private const float KerbDepth = 0.34f;   // how far the sides sink in

    public static TrackRibbon Create(GpxTrack track, ChunkManager chunks, WorldOrigin origin) => new()
    {
        Name = "TrackRibbon",
        _track = track,
        _chunks = chunks,
        _origin = origin,
        MaterialOverride = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_path.gdshader"),
        },
    };

    public override void _Process(double delta)
    {
        _sinceRebuild += delta;
        if (_sinceRebuild < 1.5) return;
        _sinceRebuild = 0;
        Rebuild();
    }

    private void Rebuild()
    {
        int steps = Math.Max(2, (int)(_track.Length / StepM));
        var centres = new List<Vector3>(steps);
        var sides = new List<Vector3>(steps);
        var breaks = new List<bool>(steps);   // true where terrain was missing before this point
        int resolved = 0;

        Vector3? prev = null;
        for (int i = 0; i <= steps; i++)
        {
            double seconds = _track.Duration * i / steps;
            var (e, n, ele, _, _) = _track.Sample(seconds);
            var p = _origin.ToWorld(e, n, ele);

            // only include stretches whose terrain has streamed in
            if (!_chunks.TryGetHeight(p, out float ground))
            {
                prev = null;
                continue;
            }
            resolved++;
            p.Y = ground;

            var dir = prev.HasValue ? p - prev.Value : Vector3.Forward;
            dir.Y = 0;
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.Forward;
            dir = dir.Normalized();

            centres.Add(p);
            sides.Add(new Vector3(-dir.Z, 0, dir.X) * HalfWidth);
            breaks.Add(!prev.HasValue);
            prev = p;
        }

        if (resolved == _lastResolved || centres.Count < 2) return;
        _lastResolved = resolved;

        var verts = new List<Vector3>(centres.Count * 8);
        var up = new Vector3(0, TreadLift, 0);
        var down = new Vector3(0, -KerbDepth, 0);

        for (int i = 0; i < centres.Count - 1; i++)
        {
            // a gap means terrain was missing in between; do not bridge it
            if (breaks[i + 1]) continue;
            if (centres[i].DistanceTo(centres[i + 1]) > StepM * 6) continue;

            Vector3 aL = centres[i] - sides[i] + up, aR = centres[i] + sides[i] + up;
            Vector3 bL = centres[i + 1] - sides[i + 1] + up, bR = centres[i + 1] + sides[i + 1] + up;
            Vector3 aLd = aL + down, aRd = aR + down;
            Vector3 bLd = bL + down, bRd = bR + down;

            AddQuad(verts, aL, aR, bL, bR);       // tread
            AddQuad(verts, aLd, aL, bLd, bL);     // left kerb
            AddQuad(verts, aR, aRd, bR, bRd);     // right kerb
        }

        if (verts.Count == 0) return;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
    }

    /// <summary>Two triangles from four corners; unindexed so each face gets a flat normal.</summary>
    private static void AddQuad(List<Vector3> v, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        v.Add(a); v.Add(b); v.Add(c);
        v.Add(b); v.Add(d); v.Add(c);
    }
}
