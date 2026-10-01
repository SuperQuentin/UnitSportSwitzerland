using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// <c>godot --headless --path . -- --roadperf DIR[,label]</c>: the per-tile runtime cost of a
/// region's road data, read straight from its files (no streaming): the road blend, the road mesh
/// and the paint mesh (worker side), the bridge/wall/kerb collision faces and the
/// <see cref="ConcavePolygonShape3D"/> made of them (whose commit is on the main thread), with
/// vertex and triangle counts. Each tile is run three times and the last two timed. Writes
/// <c>test_output/roadperf_&lt;label&gt;.csv</c> and prints mean / p95 / max. Run it on two builds
/// of a region (before and after a change) to compare like with like.
/// </summary>
public partial class RoadPerfProbe : Node
{
    public static (string Dir, string Label)? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--roadperf");
        if (i < 0 || i + 1 >= args.Length) return null;
        var p = args[i + 1].Split(',');
        return (p[0], p.Length > 1 ? p[1] : "roads");
    }

    private readonly string _dir, _label;
    public RoadPerfProbe(string dir, string label) { _dir = dir; _label = label; }

    public override void _Ready() => CallDeferred(nameof(Run));

    private void Run()
    {
        var rows = new List<double[]>();
        var csv = new StringBuilder("tile,segments,blend_ms,mesh_ms,mesh_tris,paint_ms,paint_tris,faces_ms,collision_tris,shape_ms\n");
        foreach (var path in Directory.GetFiles(_dir, "roads_*.road").OrderBy(p => p, StringComparer.Ordinal))
        {
            RoadTile tile;
            using (var s = File.OpenRead(path)) tile = RoadCodec.Decode(s);
            ChunkGrid? grid = null;
            string chunk = Path.Combine(_dir, ChunkFormat.ChunkFileName(tile.Id));
            if (File.Exists(chunk)) using (var s = File.OpenRead(chunk)) grid = ChunkCodec.Decode(s);

            double blend = 0, mesh = 0, paint = 0, faces = 0, shape = 0;
            int meshTris = 0, paintTris = 0, collTris = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                var clock = Stopwatch.StartNew();
                TerrainMeshBuilder.ComputeRoadBlend(tile);
                double b = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var m = RoadMeshBuilder.Build(tile, grid);
                double me = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var pm = RoadPaintBuilder.Build(tile);
                double pa = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                Vector3[] f = [.. RoadMeshBuilder.BuildBridgeCollisionFaces(tile), .. RoadWallBuilder.BuildCollisionFaces(tile),
                    .. RoadStreetBuilder.BuildCollisionFaces(tile)];
                double fa = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                if (f.Length > 0)
                {
                    var sh = new ConcavePolygonShape3D { Data = f, BackfaceCollision = true };
                    sh.GetDebugMesh();   // forces the shape to exist on the physics side
                }
                double shp = clock.Elapsed.TotalMilliseconds;
                if (pass == 0) continue;
                blend += b / 2; mesh += me / 2; paint += pa / 2; faces += fa / 2; shape += shp / 2;
                meshTris = (m?.Indices.Length ?? 0) / 3;
                paintTris = (pm?.Indices.Length ?? 0) / 3;
                collTris = f.Length / 3;
            }
            rows.Add([blend, mesh, meshTris, paint, paintTris, faces, collTris, shape]);
            csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{tile.Id.E}_{tile.Id.N},{tile.Segments.Count},{blend:F2},{mesh:F2},{meshTris},{paint:F2},{paintTris},{faces:F2},{collTris},{shape:F2}\n"));
        }
        Directory.CreateDirectory("test_output");
        File.WriteAllText($"test_output/roadperf_{_label}.csv", csv.ToString());
        string[] names = ["blend ms", "road mesh ms", "road mesh tris", "paint ms", "paint tris", "collision faces ms", "collision tris", "shape ms"];
        GD.Print($"[roadperf] {_label}: {rows.Count} tiles from {_dir}");
        for (int k = 0; k < names.Length; k++)
        {
            var v = rows.Select(r => r[k]).OrderBy(x => x).ToList();
            if (v.Count == 0) break;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[roadperf]   {names[k],-20} mean {v.Average(),10:F2}  p95 {v[(int)(v.Count * 0.95)],10:F2}  max {v[^1],10:F2}  total {v.Sum(),12:F1}"));
        }
        GetTree().Quit();
    }
}
