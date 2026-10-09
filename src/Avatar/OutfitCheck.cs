using Godot;
using UnitSport.Items;
using FaceGenome = UnitSport.Avatar.Face.FaceGenome;

namespace UnitSport.Avatar;

/// <summary>
/// <c>--outfitcheck</c> (#251): the wardrobe's data holds together (every look has an item, a slot
/// and a code of its own that survives the packing), and every look builds on a figure in every
/// pose: walking, the fixed poses, a ragdoll's free joints, seated — with no vertex off at
/// infinity and every finish readable back out of the vertex alpha. Headless, no scene.
/// </summary>
public static class OutfitCheck
{
    public static int Run()
    {
        int failed = 0;
        void Fail(string why)
        {
            failed++;
            GD.Print($"[outfitcheck] FAIL {why}");
        }

        foreach (var bad in Garments.Validate()) Fail(bad);

        // every slot packs and unpacks on its own, and all ten at once
        var full = Outfit.Empty;
        foreach (var g in Garments.All.GroupBy(g => g.Slot).Select(group => group.Last()))
            full = full.With(g.Slot, g.Code);
        for (var s = WearSlot.Head; s <= WearSlot.Hands; s++)
            if (full[s] is not { } back || back.Slot != s) Fail($"a full outfit lost its {s}");
        if (full.Bits >> (Outfit.SlotCount * 6) != 0) Fail("an outfit spills past its 60 bits");
        if (Outfit.Of(new[] { ItemId.WhiteTee, ItemId.GothicRobe })[WearSlot.Top]?.Item != ItemId.GothicRobe)
            Fail("Outfit.Of: the later look in a slot should win");

        int built = 0;
        var palette = HumanPalette.ForRider(3);
        foreach (var g in Garments.All)
        {
            var dressed = palette with { Outfit = Outfit.Empty.With(g.Slot, g.Code) };
            var meshes = new List<(string Pose, ArrayMesh Mesh)>
            {
                ("standing", HumanMeshBuilder.Build(dressed)),
                ("running", HumanMeshBuilder.Build(dressed, HumanPose.Running)),
                ("tucked", HumanMeshBuilder.Build(dressed, HumanPose.Tucked)),
                ("stride", HumanMeshBuilder.BuildStride(dressed, 3.5f, 0.3f)),
                ("held", HumanMeshBuilder.BuildPosed(dressed, HumanPose.Standing, ItemArmPose.ShoulderAim, 1f)),
                ("ragdoll", HumanMeshBuilder.BuildJoints(dressed, HumanMeshBuilder.PoseJoints(HumanPose.Running))),
                // a gale from ahead and below (riding fast, falling): skirts stream, nothing flies off
                ("windy", HumanMeshBuilder.Build(dressed with { Wind = new Vector3(3f, 8f, -40f) }, HumanPose.Cycling)),
                ("motorbike", MotoRider(dressed with { Wind = new Vector3(0, 0, -30f) })),
            };
            foreach (var (pose, mesh) in meshes)
            {
                built++;
                // under a full-face helmet (#394) nothing on the head is drawn: it would poke through
                var finish = pose == "motorbike" && g.Slot is WearSlot.Head or WearSlot.Eyes or WearSlot.Face or WearSlot.Ears ? Finish.None : g.Finish;
                if (Problem(mesh, finish) is { } why) Fail($"{g.Item} {pose}: {why}");
            }
        }

        // a whole figure dressed head to toe, with a one-piece over a skirt (the dress wins)
        var outfit = Outfit.Of(new[]
        {
            ItemId.CatHeadset, ItemId.HeartShades, ItemId.MaskUwu, ItemId.StarStuds, ItemId.BellCollar,
            ItemId.LolitaDress, ItemId.TartanSkirt, ItemId.BeeStockings, ItemId.MaryJanes, ItemId.PawGloves,
        });
        if (Problem(HumanMeshBuilder.BuildStride(palette with { Outfit = outfit }, 1.4f, 0.7f), Finish.Neon) is { } whole)
            Fail($"a figure dressed head to toe: {whole}");

        // #724: the patterns stay on the cloth. Standing, every vertex rests where it is drawn; at two
        // points of a run each vertex moves a lot but rests nearly where it did (only the cloth over
        // a bending joint stretches)
        var patterned = palette with { Outfit = Outfit.Of(new[] { ItemId.GalaxyHoodie, ItemId.Fishnets, ItemId.CatHeadset }) };
        if (RestDrift(HumanMeshBuilder.Build(patterned), HumanMeshBuilder.Build(patterned), standing: true) is { } still)
            Fail($"rest positions, standing: {still}");
        if (RestDrift(HumanMeshBuilder.BuildStride(patterned, 6f, 0.1f), HumanMeshBuilder.BuildStride(patterned, 6f, 0.6f), standing: false) is { } run)
            Fail($"rest positions, running: {run}");

        // #394: every appearance packs and comes back, and every build wears every hair and face,
        // dressed and not, under a hat and a helmet
        if (Appearance.Unpack(0) != null) Fail("an unset appearance (0) unpacked to a figure");
        for (int b = 0; b < Appearance.Builds; b++)
            for (int h = 0; h < Appearance.HairStyles; h++)
            {
                var a = new Appearance((BodyBuild)b, h % FaceGenome.PresetCount, h % 8, (b + h) % 8, (HairStyle)h, h % 12);
                if (Appearance.Unpack(a.Pack()) != a) Fail($"{a} did not survive packing");
                var who = palette.With(a);
                foreach (var (name, mesh) in new[]
                {
                    ("plain", HumanMeshBuilder.BuildStride(who, 2.5f, 0.4f)),
                    ("dressed", HumanMeshBuilder.Build(who with { Outfit = outfit }, HumanPose.Running)),
                    ("hat", HumanMeshBuilder.Build(who, hat: Headwear.WitchHat)),
                    ("helmet", MotoRider(who)),
                })
                {
                    built++;
                    if (Problem(mesh, Finish.None) is { } why) Fail($"{a.Build} {a.Hair} {name}: {why}");
                }
            }

        GD.Print(failed == 0
            ? $"[outfitcheck] RESULT: ok — {Garments.All.Length} looks, {built} figures built (poses, wind, motorbike)"
            : $"[outfitcheck] RESULT: FAILED — {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static ArrayMesh MotoRider(HumanPalette p)
    {
        var look = Player.MotorbikeCatalog.All[0].Look;
        var s = new MeshScratch();
        HumanMeshBuilder.AppendRider(s, p, look.Seat, look.Grip, look.Peg);
        return s.Build();
    }

    /// <summary>
    /// What is wrong with two builds of one figure's rest positions (<c>CUSTOM0</c>, #724), or null:
    /// <paramref name="standing"/>, rest is where each vertex is drawn; otherwise the drawn positions
    /// differ (5 % of vertices move over 5 cm: what the patterns slid by before) while 95 % of
    /// rest positions stay within 1 cm.
    /// </summary>
    private static string? RestDrift(ArrayMesh a, ArrayMesh b, bool standing)
    {
        var (pa, ra) = (a.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array(), a.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Custom0].AsFloat32Array());
        var (pb, rb) = (b.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array(), b.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Custom0].AsFloat32Array());
        if (ra.Length != pa.Length * 4 || rb.Length != pb.Length * 4) return "no rest position on every vertex";
        if (pa.Length != pb.Length) return $"{pa.Length} and {pb.Length} vertices: not the same figure";
        var moved = new float[pa.Length];
        var drift = new float[pa.Length];
        for (int i = 0; i < pa.Length; i++)
        {
            var restA = new Vector3(ra[i * 4], ra[i * 4 + 1], ra[i * 4 + 2]);
            var restB = new Vector3(rb[i * 4], rb[i * 4 + 1], rb[i * 4 + 2]);
            if (standing && restA.DistanceTo(pa[i]) > 1e-3f) return $"vertex {i} rests at {restA}, drawn at {pa[i]}";
            moved[i] = pa[i].DistanceTo(pb[i]);
            drift[i] = restA.DistanceTo(restB);
        }
        if (standing) return null;
        Array.Sort(moved);
        Array.Sort(drift);
        float swing = moved[moved.Length * 95 / 100], p95 = drift[drift.Length * 95 / 100];
        GD.Print($"[outfitcheck] running: 5 % of vertices moved over {swing * 100f:0.0} cm, 95 % of rest positions within {p95 * 100f:0.00} cm");
        if (swing < 0.05f) return $"the two strides barely differ ({swing:0.000} m)";
        return p95 > 0.01f ? $"95 % of rest positions drift up to {p95:0.000} m" : null;
    }

    /// <summary>What is wrong with a dressed figure's mesh, or null.</summary>
    private static string? Problem(ArrayMesh mesh, Finish finish)
    {
        if (mesh.GetSurfaceCount() == 0) return "no surface";
        var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var colours = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        foreach (var v in vertices)
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || v.Length() > 5f)
                return $"a vertex at {v}";
        bool seen = finish == Finish.None;
        foreach (var c in colours)
        {
            int id = Mathf.RoundToInt((1f - c.A) * 255f);
            if (id < 0 || id > (int)Finish.Camo) return $"alpha {c.A} decodes to no finish ({id})";
            if (id == (int)finish) seen = true;
        }
        return seen ? null : $"its {finish} finish is nowhere in the mesh";
    }
}
