using Godot;
using UnitSport.Items;

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

        // #394: every appearance packs and comes back, and every build wears every hair and face,
        // dressed and not, under a hat and a helmet
        if (Appearance.Unpack(0) != null) Fail("an unset appearance (0) unpacked to a figure");
        for (int b = 0; b < Appearance.Builds; b++)
            for (int h = 0; h < Appearance.HairStyles; h++)
            {
                var a = new Appearance((BodyBuild)b, h % FaceAtlas.Count, h % 8, (b + h) % 8, (HairStyle)h, h % 12);
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
            if (id < 0 || id > (int)Finish.Studs) return $"alpha {c.A} decodes to no finish ({id})";
            if (id == (int)finish) seen = true;
        }
        return seen ? null : $"its {finish} finish is nowhere in the mesh";
    }
}
