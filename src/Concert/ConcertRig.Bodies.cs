using System.Xml.Linq;
using Godot;
using UnitSport.Concert.Mvr;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.Concert;

/// <summary>
/// Imported fixtures drawn with their GDTF's own bodies (#961): one MultiMesh per body mesh (a type's
/// base, yoke or head; types sharing a part share it), the generic box hidden for them. The base is
/// placed once; each frame the yoke turns with the head's pan about its pivot and the head with its
/// tilt (<see cref="GdtfBody.Pose"/>), written in place like the rest of the rig, and the beam and its
/// flare leave from the head's beam point.
/// </summary>
public partial class ConcertRig
{
    /// <summary>Per head: whether it has a body, its frame (its GDTF axes at its place), its moving parts' slots, pivots and beam point.</summary>
    private bool[] _bodied = Array.Empty<bool>();
    private Transform3D[] _frame = Array.Empty<Transform3D>();
    private MultiMesh?[] _yokeMm = Array.Empty<MultiMesh?>(), _headMm = Array.Empty<MultiMesh?>();
    private int[] _yokeSlot = Array.Empty<int>(), _headSlot = Array.Empty<int>();
    private Vector3[] _yokePivot = Array.Empty<Vector3>(), _headPivot = Array.Empty<Vector3>(), _beamAt = Array.Empty<Vector3>();

    /// <summary>How many fixtures are drawn with their own body (for the checks).</summary>
    public int BodiesDrawn { get; private set; }

    /// <summary>After the generic boxes are placed: the bodied fixtures' parts, their boxes hidden.</summary>
    private void BuildBodies()
    {
        int n = _heads.Length;
        _bodied = new bool[n];
        _frame = new Transform3D[n];
        _yokeMm = new MultiMesh?[n];
        _headMm = new MultiMesh?[n];
        _yokeSlot = new int[n];
        _headSlot = new int[n];
        _yokePivot = new Vector3[n];
        _headPivot = new Vector3[n];
        _beamAt = new Vector3[n];
        if (Imported is not { Bodies.Count: > 0 } rig) return;
        var byName = new Dictionary<string, RigData.BodyRow>();
        foreach (var t in rig.Types)
            if (t.Body != null) byName.TryAdd(t.Name, t.Body);
        RigData.BodyRow? BodyOf(Fixture f) => byName.GetValueOrDefault(f.Type.Name);

        var all = _heads.Concat(_cells).ToArray();
        var uses = new int[rig.Bodies.Count];
        foreach (var f in all)
            if (BodyOf(f) is { } b)
                foreach (int m in new[] { b.Base, b.Yoke, b.Head })
                    if (m >= 0) uses[m]++;
        var material = StyleKit.Material(MaterialRole.Prop);
        var root = new Node3D { Name = "Bodies" };
        var multi = new MultiMesh?[rig.Bodies.Count];
        for (int m = 0; m < multi.Length; m++)
        {
            if (uses[m] == 0) continue;
            var row = rig.Bodies[m];
            var pos = RigData.Positions(row);
            var cols = new Color[pos.Length];
            Array.Fill(cols, RigData.ColorOf(row).SrgbToLinear() with { A = 0f });
            multi[m] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = ChunkNode.ToPropMesh(new PierMeshBuilder.MeshData(pos, cols, RigData.Indices(row)), material),
                InstanceCount = uses[m],
            };
            root.AddChild(new MultiMeshInstance3D { Name = $"Body{m}", Multimesh = multi[m], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        AddChild(root);

        var next = new int[multi.Length];
        var hidden = new Transform3D(Basis.FromScale(Vector3.Zero), Vector3.Zero);
        int Slot(int m, Transform3D at)
        {
            int s = next[m]++;
            multi[m]!.SetInstanceTransform(s, at);
            return s;
        }
        for (int i = 0; i < all.Length; i++)
        {
            var f = all[i];
            if (BodyOf(f) is not { } b) continue;
            var frame = new Transform3D(MvrFile.Axes(f.Up, f.Home), f.Position);
            if (b.Base >= 0) Slot(b.Base, frame);
            _bodies.SetInstanceTransform(i, hidden);
            BodiesDrawn++;
            if (i >= n)
            {
                // a cell's body stands still
                if (b.Yoke >= 0) Slot(b.Yoke, frame);
                if (b.Head >= 0) Slot(b.Head, frame);
                continue;
            }
            _bodied[i] = true;
            _frame[i] = frame;
            _yokePivot[i] = RigData.V(b.YokePivot);
            _headPivot[i] = RigData.V(b.HeadPivot);
            _beamAt[i] = RigData.V(b.BeamAt);
            if (b.Yoke >= 0) { _yokeMm[i] = multi[b.Yoke]; _yokeSlot[i] = Slot(b.Yoke, frame); }
            if (b.Head >= 0) { _headMm[i] = multi[b.Head]; _headSlot[i] = Slot(b.Head, frame); }
        }
    }

    /// <summary>A bodied head at its pan and tilt now: its yoke and head written in place; where its beam leaves.</summary>
    private Vector3 PoseBody(int i)
    {
        var (yoke, head) = GdtfBody.Pose(_frame[i], _yokePivot[i], _headPivot[i], Mathf.DegToRad(_pan[i]), Mathf.DegToRad(_tilt[i]));
        _yokeMm[i]?.SetInstanceTransform(_yokeSlot[i], yoke);
        _headMm[i]?.SetInstanceTransform(_headSlot[i], head);
        return head * _beamAt[i];
    }

    /// <summary>GDTF's primitives as bodies (BlenderDMX's profiles are made of them): a moving head panned and tilted, a par, the plain shapes.</summary>
    [Showcase("Concert", "GDTF bodies")]
    private static IEnumerable<(string, Func<Node3D>)> BodyShowcase()
    {
        const string head = """
            <Model Name="Base" Length="0.3" Width="0.3" Height="0.13" PrimitiveType="Base1_1"/>
            <Model Name="Yoke" Length="0.353" Width="0.116" Height="0.3" PrimitiveType="Yoke"/>
            <Model Name="Head" Length="0.224" Width="0.186" Height="0.244" PrimitiveType="Head"/>
            """;
        const string headTree = """
            <Geometry Name="Base" Model="Base"><Axis Name="Yoke" Model="Yoke" Position="{1,0,0,0}{0,1,0,0}{0,0,1,-0.28}{0,0,0,1}">
            <Axis Name="Head" Model="Head" Position="{1,0,0,0}{0,1,0,0}{0,0,1,-0.1}{0,0,0,1}"><Beam Name="Beam" Position="{1,0,0,0}{0,1,0,0}{0,0,1,-0.13}{0,0,0,1}"/></Axis></Axis></Geometry>
            """;
        yield return ("Moving head (Base1_1, Yoke, Head), pan 30, tilt 50", () => Show(head, headTree, "Yoke", "Head", 30f, 50f));
        foreach (var p in new[] { "Conventional1_1", "Cube", "Cylinder", "Sphere", "Scanner" })
            yield return (p, () => Show($"""<Model Name="Body" Length="0.26" Width="0.26" Height="0.4" PrimitiveType="{p}"/>""",
                """<Geometry Name="Body" Model="Body"/>""", null, null, 0f, 0f));
    }

    private static Node3D Show(string models, string tree, string? pan, string? tilt, float panDeg, float tiltDeg)
    {
        var ft = XElement.Parse($"<FixtureType><Models>{models}</Models><Geometries>{tree}</Geometries></FixtureType>");
        var body = GdtfBody.Read(ft, null, pan, tilt, _ => null)!;
        var node = new Node3D { Name = "GdtfBody" };
        // hung from a bar: the yoke axis down, the base on top; 4x so it reads in the viewer
        var frame = new Transform3D(MvrFile.Axes(Vector3.Down, Vector3.Back).Scaled(Vector3.One * 4f), Vector3.Up * 2f);
        var (yoke, headXf) = GdtfBody.Pose(frame, body.YokePivot, body.HeadPivot, Mathf.DegToRad(panDeg), Mathf.DegToRad(tiltDeg));
        var material = StyleKit.Material(MaterialRole.Prop);
        foreach (var (mesh, xf) in new[] { (body.Base, frame), (body.Yoke, yoke), (body.Head, headXf) })
        {
            if (mesh == null) continue;
            var cols = new Color[mesh.Positions.Length];
            Array.Fill(cols, mesh.Color.SrgbToLinear() with { A = 0f });
            node.AddChild(new MeshInstance3D { Mesh = ChunkNode.ToPropMesh(new PierMeshBuilder.MeshData(mesh.Positions, cols, mesh.Indices), material), Transform = xf });
        }
        return node;
    }
}
