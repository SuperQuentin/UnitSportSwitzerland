using Godot;
using UnitSport.Combat;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>
/// The sword's whole-body poses (#1371), drawn by the dance layer like a fight pose: <see cref="DanceParams.Move"/> is
/// <see cref="SwordMoves"/>, <see cref="DanceParams.BarPhase"/> how far through the move the swing is (0..1 of its total; for a
/// held charge, its seconds over <see cref="SwordRules.ChargeMax"/>), and <see cref="DanceParams.Bar"/> the code
/// <see cref="SwordCode"/> packs: the move, its variant and its seed. The numbers are the keyframe tables of
/// <see cref="SwordPoses"/> (pure, unit-tested); this maps one pose onto the dance channels: the chest's twist and lean, the hips,
/// the head, both arms (the sword hand's whip and the off hand's counterweight) and both feet. The skinned figure is posed by its
/// bones as for any dance: nothing is rebuilt per frame.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> from here is the sword pose (the details are in <see cref="DanceParams.Bar"/>).</summary>
    public const int SwordMoves = 7000;

    /// <summary>Packs a move, a variant (0..3) and a seed (0..15) into <see cref="DanceParams.Bar"/>.</summary>
    public static int SwordCode(SwordMove move, int variant, int seed, int chain = 0) => (int)move | ((variant & 3) << 4) | ((seed & 15) << 6) | (chain << 10);

    private static void EvalSword(in Beat t, bool mv, ref DanceCh ch)
    {
        int code = t.Bar;
        var move = (SwordMove)(code & 15);
        int variant = (code >> 4) & 3, seed = (code >> 6) & 15;
        // the swing it flows out of (a combo never returns to the stance between swings), in the bits above the move's own
        var pose = SwordPoses.SampleChain(move, variant, seed, t.R, SwordPoses.UnpackChain(code >> 10));
        if (move == SwordMove.Charge) pose.Psi += 0.025f * Mathf.Sin(t.R * 70f);
        ch.Psi = pose.Psi; ch.Theta = pose.Theta; ch.Phi = pose.Phi;
        ch.Py = pose.Py; ch.Px = pose.Px; ch.Pz = pose.Pz;
        ch.ThN = pose.Neck; ch.PhH = pose.HeadYaw;
        Aim(ref ch, 1f, pose.Hx, pose.Hy, pose.Hz, Elbow(1f));
        Aim(ref ch, -1f, pose.Ox, pose.Oy, pose.Oz, Elbow(-1f));
        // a spin slash turns from the feet: the toes lead the body round
        Foot(ref ch, -1f, pose.LeadX, pose.LeadY, pose.LeadZ, 0.1f + 0.5f * Mathf.Sin(pose.Spin), pose.LeadY > AnkleHeight + 0.05f ? 1f : 0f);
        Foot(ref ch, 1f, pose.RearX, pose.RearY, pose.RearZ, 0.5f, pose.RearY > AnkleHeight + 0.05f ? 1f : 0f);
        // on the move the legs keep the gait and the swing is the upper body's
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    /// <summary>One frame of a sword move for the model viewer's contact sheet: <paramref name="progress"/> of the way through it.</summary>
    private static DanceParams SwordDance(SwordMove move, int variant, float progress) =>
        new(Audio.Cd.MusicStyle.Pop, SwordMoves, 0f, Mathf.Min(progress, 0.999f), SwordCode(move, variant, 3), 1f);

    /// <summary>The figure at that frame with the sword in its hand, placed from the same mounts the game's held item uses.</summary>
    private static Node3D SwordFigure(SwordMove move, int variant, float progress)
    {
        var dance = SwordDance(move, variant, progress);
        var node = Figure(BuildStride(HumanPalette.ForRider((int)move), 0f, 0f, dance: dance));
        var mounts = MountsFor(0f, 0f, ItemArmPose.None, 0f, dance, null, null);
        var hand = mounts.HandL.X > mounts.HandR.X ? mounts.HandL : mounts.HandR;
        var basis = mounts.HandBasis;
        var (wp, wy, wr, wf) = SwordPoses.Wrist(move, progress);
        node.AddChild(new MeshInstance3D
        {
            Mesh = Items.ItemDefs.HandMesh(Items.ItemId.Sword), MaterialOverride = Items.ItemDefs.Material,
            Transform = new Transform3D(basis, hand + basis * Items.SwordBlade.SocketOffset)
                * new Transform3D(Basis.FromEuler(new Vector3(wp, wy, wr)), new Vector3(0, 0, wf)),
        });
        return node;
    }

    [Core.Showcase("Sword poses")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseSword()
    {
        var frames = new[] { 0f, 0.2f, 0.4f, 0.55f, 0.7f, 0.9f };
        foreach (var move in new[] { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Charged, SwordMove.Plunge })
            for (int v = 0; v < SwordPoses.Variants; v++)
                if (v == 0 || move is >= SwordMove.Slash1 and <= SwordMove.Slash5)
                    for (int f = 0; f < frames.Length; f++)
                    {
                        float p = frames[f];
                        var mv = move; int vv = v;
                        yield return ($"{mv} set{vv} f{f}", () => SwordFigure(mv, vv, p));
                    }
        for (int v = 0; v < 4; v++)
            for (int f = 0; f < frames.Length; f++)
            {
                float p = frames[f]; int vv = v;
                yield return ($"Dodge v{vv} f{f}", () => SwordFigure(SwordMove.Dodge, vv, p));
            }
        yield return ("Charging", () => Figure(BuildStride(HumanPalette.ForRider(9), 0f, 0f,
            dance: new DanceParams(Audio.Cd.MusicStyle.Pop, SwordMoves, 0f, 0.6f, SwordCode(SwordMove.Charge, 0, 0), 1f))));
    }
}
