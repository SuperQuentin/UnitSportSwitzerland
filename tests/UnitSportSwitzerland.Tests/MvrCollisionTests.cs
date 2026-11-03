using System.IO;
using Godot;
using UnitSport.Concert.Mvr;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>Imported scene objects' collision (#974): which piece is a box, a hull, a trimesh or nothing; the caps; the crowd's footprints.</summary>
public class MvrCollisionTests
{
    private readonly ITestOutputHelper _out;
    public MvrCollisionTests(ITestOutputHelper output) => _out = output;

    private static readonly int[] BoxFaces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };

    /// <summary>Boxes from their min and max corners into one mesh's positions and indices.</summary>
    private static (List<Vector3>, List<int>) Boxes(params (Vector3 Lo, Vector3 Hi)[] boxes)
    {
        var p = new List<Vector3>();
        var idx = new List<int>();
        foreach (var (lo, hi) in boxes)
        {
            int at = p.Count;
            for (int i = 0; i < 8; i++) p.Add(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i >> 1 & 1) == 0 ? lo.Y : hi.Y, (i >> 2 & 1) == 0 ? lo.Z : hi.Z));
            foreach (int f in BoxFaces) idx.Add(at + f);
        }
        return (p, idx);
    }

    private static RigData.MeshRow Mesh(params (Vector3 Lo, Vector3 Hi)[] boxes)
    {
        var (p, i) = Boxes(boxes);
        return RigData.Quantise(p, i, new Color(0.5f, 0.5f, 0.5f));
    }

    /// <summary>A flight of <paramref name="n"/> steps up +z: each a box from the floor to its tread.</summary>
    private static RigData.MeshRow Stairs(int n, float rise, float run, float width) =>
        Mesh(Enumerable.Range(0, n).Select(k => (new Vector3(0, 0, k * run), new Vector3(width, (k + 1) * rise, (k + 1) * run))).ToArray());

    private static RigData Rig(params (RigData.MeshRow Mesh, Transform3D At)[] pieces)
    {
        var rig = new RigData();
        foreach (var (m, at) in pieces)
        {
            rig.Objects.Add(new RigData.ObjectRow { Mesh = rig.Meshes.Count, Xf = RigData.X(at) });
            rig.Meshes.Add(m);
        }
        return rig;
    }

    private static Transform3D At(float x, float y, float z, float yawDeg = 0f) =>
        new(new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)), new Vector3(x, y, z));

    [Fact]
    public void Each_piece_is_judged_by_its_size_and_place()
    {
        var rig = Rig(
            (Mesh((Vector3.Zero, new Vector3(4, 1, 3))), At(0, 0, 0)),                        // a deck: its hull
            (Stairs(5, 0.2f, 0.3f, 1.2f), At(10, 0, 0)),                                         // stairs: their hull, a ramp
            (Mesh((Vector3.Zero, new Vector3(0.3f, 0.3f, 3))), At(0, 6, 0)),                     // a truss overhead: a box
            (Mesh((Vector3.Zero, new Vector3(0.4f, 4, 0.4f))), At(20, 0, 0)),                     // a tower: a box
            (Mesh((Vector3.Zero, new Vector3(0.1f, 0.2f, 0.15f))), At(0, 1, 0)),                  // a lamp: nothing
            (Mesh((Vector3.Zero, new Vector3(0.03f, 0.03f, 6))), At(0, 0, 5)),                    // a cable: nothing
            (Mesh((Vector3.Zero, new Vector3(6, 0.08f, 4))), At(30, 1, 0)),                       // a plate up a metre: a box (too thin to hull)
            // a set 6 m tall with a doorway: its own triangles, not a hull that would fill the door
            (Mesh((Vector3.Zero, new Vector3(1, 6, 1)), (new Vector3(4, 0, 0), new Vector3(5, 6, 1)), (new Vector3(0, 5, 0), new Vector3(5, 6, 1))), At(40, 0, 0)),
            (Mesh((Vector3.Zero, new Vector3(3, 1, 3))), At(0, 4, 20)),                           // a deck up high: out of reach, a box
            (Mesh((Vector3.Zero, new Vector3(0.6f, 0.02f, 0.6f))), At(50, 0, 0)));                // a focus point's disc on the floor: nothing
        var plan = MvrCollision.Plan(rig);
        Assert.Equal(new[]
        {
            MvrCollision.Kind.Hull, MvrCollision.Kind.Hull, MvrCollision.Kind.Box, MvrCollision.Kind.Box, MvrCollision.Kind.None,
            MvrCollision.Kind.None, MvrCollision.Kind.Box, MvrCollision.Kind.Mesh, MvrCollision.Kind.Box, MvrCollision.Kind.None,
        }, plan.Select(p => p.Kind).ToArray());
        // the faces are the set's own 36 triangles, placed
        var positions = rig.Meshes.Select(RigData.Positions).ToArray();
        var indices = rig.Meshes.Select(RigData.Indices).ToArray();
        var faces = MvrCollision.Faces(rig, plan, positions, indices);
        Assert.Equal(36 * 3, faces.Length);
        Assert.All(faces, f => Assert.InRange(f.X, 39.99f, 45.01f));
        // a plate's box keeps a minimum thickness, so a walker cannot slip through it
        Assert.Equal(MvrCollision.MinBox, plan[6].Size.Y, 3);
    }

    [Fact]
    public void A_box_follows_the_pieces_turn_and_a_hull_its_ramp()
    {
        var rig = Rig((Mesh((Vector3.Zero, new Vector3(4, 0.5f, 0.5f))), At(0, 3, 0, 90f)), (Stairs(5, 0.2f, 0.3f, 1.2f), At(0, 0, 0)));
        var p = MvrCollision.Plan(rig)[0];
        Assert.Equal(MvrCollision.Kind.Box, p.Kind);
        // turned a quarter: its 4 m along −z in the rig's frame, its centre there too
        Assert.True((p.Axes * new Vector3(p.Size.X, 0, 0)).Abs().DistanceTo(new Vector3(0, 0, 4)) < 0.01f, $"{p.Axes} {p.Size}");
        Assert.True(p.Centre.DistanceTo(new Vector3(0.25f, 3.25f, -2f)) < 0.01f, $"{p.Centre}");
        // the stairs' hull: their foot's front edge and their top's back edge, nothing in front of the nosings' line
        var hull = MvrCollision.Hull(RigData.Positions(rig.Meshes[1]), RigData.X(rig.Objects[1].Xf));
        Assert.InRange(hull.Length, 4, 98);
        Assert.Contains(hull, v => v.Y < 0.01f && v.Z < 0.01f);
        Assert.Contains(hull, v => v.Y > 0.99f && v.Z > 1.49f);
        // the ramp through the nosings (z = 0, y = 0.2 .. z = 1.2, y = 1): every hull point on or under it
        Assert.All(hull, v => Assert.True(v.Y <= 0.2f + (v.Z) * (0.8f / 1.2f) + 0.01f || v.Z > 1.19f, $"{v}"));
    }

    [Fact]
    public void Caps_fall_back_to_boxes_then_nothing_the_biggest_kept()
    {
        var pieces = new List<(RigData.MeshRow, Transform3D)>();
        for (int i = 0; i < MvrCollision.MaxBoxes + 10; i++)
            pieces.Add((Mesh((Vector3.Zero, new Vector3(0.4f, 0.4f, 1f + i * 0.001f))), At(i, 6, 0)));
        for (int i = 0; i < MvrCollision.MaxHulls + 5; i++)
            pieces.Add((Mesh((Vector3.Zero, new Vector3(2, 1, 2))), At(i * 3, 0, 50)));
        var plan = MvrCollision.Plan(Rig(pieces.ToArray()));
        Assert.Equal(MvrCollision.MaxHulls, plan.Count(p => p.Kind == MvrCollision.Kind.Hull));
        Assert.Equal(MvrCollision.MaxBoxes, plan.Count(p => p.Kind == MvrCollision.Kind.Box));
        // the hulls past their cap are boxes, which push the smallest truss pieces out
        Assert.Equal(MvrCollision.Kind.None, plan[0].Kind);
        Assert.Equal(MvrCollision.Kind.Box, plan[MvrCollision.MaxBoxes + 9].Kind);
    }

    [Fact]
    public void The_crowd_keeps_off_what_stands_on_the_floor_not_off_what_hangs_or_lies_flat()
    {
        var rig = Rig(
            (Mesh((Vector3.Zero, new Vector3(4, 1, 3))), At(0, 0, 0)),
            (Mesh((Vector3.Zero, new Vector3(0.3f, 0.3f, 3))), At(10, 6, 0)),
            (Mesh((Vector3.Zero, new Vector3(6, 0.08f, 4))), At(20, 0, 0)),
            (Mesh((Vector3.Zero, new Vector3(0.1f, 0.2f, 0.15f))), At(30, 0, 0)));
        var feet = MvrCollision.Footprints(MvrCollision.Plan(rig));
        Assert.Single(feet);
        Assert.True(MvrCollision.Blocks(feet, new Vector2(2, 1.5f), 0f));
        Assert.False(MvrCollision.Blocks(feet, new Vector2(5, 1.5f), 0.5f));
        Assert.True(MvrCollision.Blocks(feet, new Vector2(5, 1.5f), 1.2f));
        Assert.False(MvrCollision.Blocks(feet, new Vector2(10, 1.5f), 0f));   // under the truss
        Assert.False(MvrCollision.Blocks(feet, new Vector2(22, 1.5f), 0f));   // on the plate
    }

    [Fact]
    public void Real_files_when_present()
    {
        string? dir = System.Environment.GetEnvironmentVariable("MVR_SAMPLES");
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.mvr"))
        {
            var import = MvrFile.Read(File.ReadAllBytes(file), Path.GetFileName(file), out _);
            if (import == null || import.Rig.Objects.Count == 0) continue;
            var rig = import.Rig;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = MvrCollision.Plan(rig);
            var positions = rig.Meshes.Select(RigData.Positions).ToArray();
            var indices = rig.Meshes.Select(RigData.Indices).ToArray();
            int faces = MvrCollision.Faces(rig, plan, positions, indices).Length / 3;
            int hullPoints = plan.Where(p => p.Kind == MvrCollision.Kind.Hull)
                .Sum(p => MvrCollision.Hull(positions[rig.Objects[p.Object].Mesh], RigData.X(rig.Objects[p.Object].Xf)).Length);
            _out.WriteLine($"{Path.GetFileName(file)}: {plan.Length} pieces: {plan.Count(p => p.Kind == MvrCollision.Kind.None)} none, " +
                $"{plan.Count(p => p.Kind == MvrCollision.Kind.Box)} boxes, {plan.Count(p => p.Kind == MvrCollision.Kind.Hull)} hulls ({hullPoints} points), " +
                $"{plan.Count(p => p.Kind == MvrCollision.Kind.Mesh)} trimesh pieces ({faces} triangles); {MvrCollision.Footprints(plan).Count} footprints; {sw.ElapsedMilliseconds} ms");
        }
    }
}
