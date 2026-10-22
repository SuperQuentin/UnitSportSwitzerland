using System.Linq;
using System.Text;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Which parts of the scene the triangles come from (#1112): <c>--rendercensus [seconds]</c> walks the tree
/// once that long into the run and prints the visible meshes' triangles summed by the first two names of
/// their path under the root, biggest first. Rough on purpose: whole meshes, ignoring culling and LOD
/// ranges, which is enough to say whether the terrain, the horizon or the props are the bulk.
/// </summary>
public static class RenderCensus
{
    public static void Install(Node root)
    {
        double? at = CmdArgs.Double("--rendercensus");
        if (at == null && !CmdArgs.Has("--rendercensus")) return;
        root.GetTree().CreateTimer(at ?? 30).Timeout += () => Print(root.GetTree().Root);
    }

    private static void Print(Node root)
    {
        var sums = new Dictionary<string, (long Tris, int Nodes)>();
        Walk(root, root, sums);
        var sb = new StringBuilder("[census] triangles of visible meshes, by branch:\n");
        foreach (var (key, v) in sums.OrderByDescending(kv => kv.Value.Tris).Take(25))
            sb.Append(System.FormattableString.Invariant($"  {v.Tris / 1000.0,9:F0}k  {v.Nodes,6} nodes  {key}\n"));
        sb.Append(System.FormattableString.Invariant($"  total {sums.Values.Sum(v => v.Tris) / 1e6:F2} M"));
        GD.Print(sb.ToString());
    }

    private static void Walk(Node node, Node root, Dictionary<string, (long, int)> sums)
    {
        if (node is Node3D n3 && !n3.Visible) return;
        if (node is CanvasItem) return;
        long tris = node switch
        {
            MultiMeshInstance3D mm when mm.Multimesh is { Mesh: { } m } multi =>
                Triangles(m) * (multi.VisibleInstanceCount >= 0 ? multi.VisibleInstanceCount : multi.InstanceCount),
            MeshInstance3D mi when mi.Mesh is { } m => Triangles(m),
            _ => 0,
        };
        if (tris > 0)
        {
            var path = root.GetPathTo(node).ToString().Split('/');
            string key = string.Join('/', path.Take(Math.Min(3, path.Length - 1)));
            sums.TryGetValue(key, out var v);
            sums[key] = (v.Item1 + tris, v.Item2 + 1);
        }
        foreach (var c in node.GetChildren()) Walk(c, root, sums);
    }

    private static long Triangles(Mesh mesh)
    {
        long t = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh is ArrayMesh am)
            {
                int idx = am.SurfaceGetArrayIndexLen(s);
                t += (idx > 0 ? idx : am.SurfaceGetArrayLen(s)) / 3;
            }
            else t += mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].As<Vector3[]>().Length / 3;
        }
        return t;
    }
}
