using Godot;
using UnitSport.Combat;
using UnitSport.Core;

namespace UnitSport.Avatar;

// The figure's entries in the model viewer (--models, docs/notes/avatar/model-viewer.md): one per
// enum value or table row, so a new pose, emote, fight move, hair style or hat shows without
// touching this file.
public static partial class HumanMeshBuilder
{
    private static MeshInstance3D Figure(ArrayMesh mesh) => ModelViewer.Shaded(mesh, figure: true);

    private static IEnumerable<(string, Func<Node3D>)> Each<T>(IEnumerable<T> values, Func<T, int, ArrayMesh> build) =>
        values.Select((v, i) => (v!.ToString()!, (Func<Node3D>)(() => Figure(build(v, i)))));

    [Showcase("Figures", "Pose")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcasePoses() =>
        Each(Enum.GetValues<HumanPose>(), (pose, i) => Build(HumanPalette.ForRider(i), pose));

    [Showcase("Figures", "Holding")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseArmPoses() =>
        Each(Enum.GetValues<ItemArmPose>().Where(arm => arm != ItemArmPose.None),
            (arm, i) => BuildPosed(HumanPalette.ForRider(i), HumanPose.Standing, arm, 1f));

    // #1202: skinned figures (built once, posed by their bones on the GPU), to set beside the rebuilt ones
    [Showcase("Figures", "Skinned")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseSkinned()
    {
        var plain = HumanPalette.ForRider(2);
        HumanPalette Wearing(GarmentShape shape, int rider) =>
            Garments.All.First(g => g.Shape == shape) is var g ? HumanPalette.ForRider(rider) with { Outfit = Outfit.Empty.With(g.Slot, g.Code) } : plain;
        var kart = KartMeshBuilder.SeatFor(1.05f);
        yield return ("Walking", () => Skinned(plain, f => PoseStride(f, 1.6f, 0.3f)));
        yield return ("Sprinting", () => Skinned(plain, f => PoseStride(f, 7f, 0.2f)));
        yield return ("Dancing", () => Skinned(plain, f => PoseStride(f, 0f, 0f, dance: new DanceParams(Audio.Cd.MusicStyle.HipHop, 10, 0.55f, 0.3f, 1, 1f))));
        yield return ("Aiming", () => Skinned(plain, f => PoseStride(f, 1.6f, 0.7f, ItemArmPose.ShoulderAim, 1f)));
        yield return ("Driving", () => Skinned(plain, f => PoseDriver(f, kart, 1.2f, 0.5f, 0f)));
        yield return ("Swimming", () => Skinned(plain, f => PoseSwim(f, SwimStyle.Crawl, 0.3f)));
        yield return ("Wall climbing", () => Skinned(plain, f => PoseWallClimb(f, 0.05f, 0f)));
        yield return ("Climb-jump", () => Skinned(plain, f => PoseWallClimb(f, 0.3f, 1f)));
        yield return ("Robe, running", () => Skinned(Wearing(GarmentShape.Robe, 4), f => PoseStride(f, 4.6f, 0.3f)));
        yield return ("Dress, seated", () => Skinned(Wearing(GarmentShape.Dress, 5), f => PoseDriver(f, kart, 0f, 0.3f, 0f)));
        yield return ("Ruffle, running", () => Skinned(Wearing(GarmentShape.RuffleMini, 6), f => PoseStride(f, 4.6f, 0.6f)));
    }

    private static Node3D Skinned(HumanPalette palette, Action<SkinnedFigure> pose)
    {
        var figure = new SkinnedFigure { MaterialOverride = FigureMaterial() };
        figure.Show(new FigureLook(palette));
        pose(figure);
        return figure;
    }

    [Showcase("Figures", "Build")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseBuilds() =>
        Each(Enum.GetValues<BodyBuild>(),
            (build, i) => Build(HumanPalette.ForRider(i).With(Appearance.Default with { Build = build })));

    // every garment of the wardrobe on a figure that keeps the rest plain, so a new look shows by itself (#716)
    [Showcase("Clothes")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseClothes() =>
        Garments.All.Select((g, i) => (g.Name, (Func<Node3D>)(() =>
            Figure(Build(HumanPalette.ForRider(i) with { Outfit = Outfit.Empty.With(g.Slot, g.Code) })))));

    [Showcase("Figures", "Hair")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseHair() =>
        Each(Enum.GetValues<HairStyle>(),
            (hair, i) => Build(HumanPalette.ForRider(i).With(Appearance.Default with { Hair = hair, HairColour = i })));

    [Showcase("Figures", "Hat")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseHats() =>
        Each(Enum.GetValues<Headwear>().Where(hat => hat != Headwear.None),
            (hat, i) => Build(HumanPalette.ForRider(i + 1), hat: hat));

    // the procedural faces (#657): bald, so nothing hides them
    [Showcase("Faces", "Preset")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseFaces() =>
        Enumerable.Range(0, Face.FaceGenome.PresetCount).Select(f => (Face.FaceGenome.PresetName(f), (Func<Node3D>)(() =>
            Figure(Build(HumanPalette.ForRider(f).With(Appearance.Default with { Face = f, Hair = HairStyle.None }))))));

    [Showcase("Faces", "Expression")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseExpressions() =>
        Enum.GetValues<Face.FaceExpression>().Select(e => (e.ToString(), (Func<Node3D>)(() =>
        {
            var node = Figure(Build(HumanPalette.ForRider(3).With(Appearance.Default with { Hair = HairStyle.None })));
            Face.FaceAnimator.Apply(node, Face.FaceExpressions.Of(e));
            return node;
        })));

    [Showcase("Faces", "Seeded")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseSeededFaces() =>
        Enumerable.Range(0, 8).Select(i => ($"seed {i}", (Func<Node3D>)(() =>
            Figure(BuildBody(BodyLook.Of(Appearance.ForSeed(i) with { Hair = HairStyle.None })
                with { Genome = Face.FaceGenome.ForSeed((uint)i * 7919u + 13u) })))));

    // #880: the aliens, the named presets dressed, dancing, waving, and their faces' moods
    private static HumanPalette Dressed(AlienLook alien, int rider) =>
        HumanPalette.ForRider(rider).With(alien.Looks) with { Outfit = alien.Outfit };

    [Showcase("Aliens")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseAliens()
    {
        for (int i = 0; i < Aliens.Crew.Length; i++)
        {
            var alien = Aliens.Crew[i];
            int rider = i;
            yield return (alien.Name, () => Figure(Build(Dressed(alien, rider))));
        }
        yield return ("Zorp doing the robot", () => Figure(BuildStride(Dressed(Aliens.Zorp, 1), 0f, 0f,
            dance: new DanceParams(Audio.Cd.MusicStyle.Pop, EmoteMoves + 16, 0.25f, 0.25f, 0, 1f))));
        yield return ("Zib waving", () => Figure(BuildStride(Dressed(Aliens.Zib, 2), 0f, 0f,
            dance: new DanceParams(Audio.Cd.MusicStyle.Pop, EmoteMoves, 0.25f, 0.25f, 0, 1f))));
        foreach (var e in new[] { Face.FaceExpression.Happy, Face.FaceExpression.Surprised, Face.FaceExpression.Sad })
            yield return ($"Gleeb {e}", () =>
            {
                var node = Figure(Build(Dressed(Aliens.Gleeb, 3)));
                Face.FaceAnimator.Apply(node, Face.FaceExpressions.Of(e));
                return node;
            });
        for (int skin = Appearance.HumanSkins; skin < Appearance.SkinTones.Length; skin++)
        {
            var bare = new AlienLook(Appearance.SkinNames[skin], Aliens.Look(skin), []);
            yield return (bare.Name, () => Figure(Build(HumanPalette.ForRider(0).With(bare.Looks))));
        }
    }

    [Showcase("Figures")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseMisc()
    {
        // built around the hip: raised by the hip's height so it sits on the floor
        yield return ("Seated passenger", () =>
        {
            var node = Figure(SeatedFigure.Build(HumanPalette.ForRider(5), new SeatAnchor(0, new Vector3(0, 0.45f, 0), 0.2f, 0f)));
            node.Position = new Vector3(0, 0.45f, 0);
            return node;
        });
        yield return ("Helmet", () => Figure(Build(HumanPalette.ForRider(4), helmet: true)));
        yield return ("Walking", () => Figure(BuildStride(HumanPalette.ForRider(1), 1.4f, 0.25f)));
        yield return ("Running", () => Figure(BuildStride(HumanPalette.ForRider(2), 5f, 0.25f)));
    }

    // laid the way FootPlayer.Swim lays the swimmer: face down for the crawl, nearly upright treading
    [Showcase("Figures", "Swim")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseSwim() =>
        Enum.GetValues<SwimStyle>().Select((style, i) => (style.ToString(), (Func<Node3D>)(() =>
        {
            var node = Figure(BuildSwim(HumanPalette.ForRider(i + 3), style, 0.3f));
            node.Rotation = new Vector3(style switch { SwimStyle.Crawl => -1.45f, SwimStyle.Under => -1.57f, _ => -0.12f }, 0, 0);
            return node;
        })));

    // a frame a quarter into the move's bar: the arms are up, not at rest
    private static ArrayMesh Danced(int move, int rider, float barPhase = 0.25f) =>
        BuildStride(HumanPalette.ForRider(rider), 0f, 0f,
            dance: new DanceParams(Audio.Cd.MusicStyle.Pop, move, 0.25f, barPhase, 0, 1f));

    [Showcase("Emotes")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseEmotes() =>
        Enumerable.Range(0, EmoteCount).Select(i => (EmoteName(i), (Func<Node3D>)(() => Figure(Danced(EmoteMoves + i, i)))));

    [Showcase("Fights")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseFights() =>
        Enum.GetValues<FightStance>().Where(s => s is not FightStance.None and < FightStance.MoveBase)
            .Select(s => ($"Stance - {s}", (Func<Node3D>)(() => Figure(Danced(FightMoves + (int)s, (int)s, 0.5f)))))
            .Concat(Enum.GetValues<FightMove>().Where(m => m != FightMove.None)
                .Select(m => ($"Move - {m}", (Func<Node3D>)(() => Figure(Danced(FightMoves + FightRules.PoseOf(m), (int)m, 0.4f))))));

    // the aura battle (#907): its stances, then each move most of the way through (the pose held)
    [Showcase("Aura")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseAura() =>
        Enum.GetValues<AuraStance>().Where(s => s is not AuraStance.None and < AuraStance.MoveBase)
            .Select(s => ($"Stance - {s}", (Func<Node3D>)(() => Figure(Danced(AuraMoves + (int)s, (int)s, 0.5f)))))
            .Concat(AuraRules.Moves
                .Select(m => ($"Move - {AuraRules.Name(m)}", (Func<Node3D>)(() => Figure(Danced(AuraMoves + AuraRules.PoseOf(m), (int)m, 0.7f))))));
}
