using Godot;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.Concert;

/// <summary>
/// An imported rig's trusses and scene objects (#958), drawn once in the rig's frame with the stage's
/// flat prop material, no collision (a truss is walked under, not on). A mesh placed many times is
/// one MultiMesh; the rest (Capture writes every object as its own file) are baked into one mesh, so
/// a show of nine hundred one-off files is one draw call, not nine hundred.
/// </summary>
public partial class ConcertRig
{
    /// <summary>From this many pieces a mesh is instanced; fewer are baked into the shared mesh.</summary>
    private const int InstancedFrom = 4;

    /// <summary>How many object pieces are drawn (for the checks).</summary>
    public int ObjectsDrawn { get; private set; }

    private void BuildObjects()
    {
        if (Imported is not { Objects.Count: > 0 } rig) return;
        var material = StyleKit.Material(MaterialRole.Prop);
        var root = new Node3D { Name = "MvrObjects" };
        var uses = new int[rig.Meshes.Count];
        foreach (var o in rig.Objects) uses[o.Mesh]++;

        var positions = new Vector3[rig.Meshes.Count][];
        var indices = new int[rig.Meshes.Count][];
        var colours = new Color[rig.Meshes.Count];
        for (int m = 0; m < rig.Meshes.Count; m++)
        {
            positions[m] = Mvr.RigData.Positions(rig.Meshes[m]);
            indices[m] = Mvr.RigData.Indices(rig.Meshes[m]);
            colours[m] = Mvr.RigData.ColorOf(rig.Meshes[m]).SrgbToLinear() with { A = 0f };
        }

        // the one-offs, baked
        var verts = new List<Vector3>();
        var cols = new List<Color>();
        var idx = new List<int>();
        foreach (var o in rig.Objects)
        {
            if (uses[o.Mesh] >= InstancedFrom) continue;
            var xf = Mvr.RigData.X(o.Xf);
            int at = verts.Count;
            foreach (var p in positions[o.Mesh]) { verts.Add(xf * p); cols.Add(colours[o.Mesh]); }
            foreach (int i in indices[o.Mesh]) idx.Add(at + i);
        }
        if (idx.Count > 0)
            root.AddChild(new MeshInstance3D
            {
                Name = "Baked",
                Mesh = ChunkNode.ToPropMesh(new PierMeshBuilder.MeshData(verts.ToArray(), cols.ToArray(), idx.ToArray()), material),
            });

        // the repeated, instanced
        var multi = new MultiMesh?[rig.Meshes.Count];
        for (int m = 0; m < rig.Meshes.Count; m++)
        {
            if (uses[m] < InstancedFrom) continue;
            var c = new Color[positions[m].Length];
            Array.Fill(c, colours[m]);
            multi[m] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = ChunkNode.ToPropMesh(new PierMeshBuilder.MeshData(positions[m], c, indices[m]), material),
                InstanceCount = uses[m],
            };
            root.AddChild(new MultiMeshInstance3D { Name = $"Mesh{m}", Multimesh = multi[m] });
        }
        var next = new int[rig.Meshes.Count];
        foreach (var o in rig.Objects)
            multi[o.Mesh]?.SetInstanceTransform(next[o.Mesh]++, Mvr.RigData.X(o.Xf));
        AddChild(root);
        ObjectsDrawn = rig.Objects.Count;
    }
}
