using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Generated dance moves (#2055): <see cref="DanceGen"/> draws a fixed pool of 32 moves per style
/// from stable hashes; this lays one instant of one onto a <see cref="DanceCh"/>. The move number
/// is <see cref="GeneratedMoves"/> + the pool index, so a peer rebuilds the same pose from the same
/// number and the shared clock, and the crossfade between two generated moves works like any other.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> values from here on are generated moves: <c>GeneratedMoves</c> + the index into the style's pool.</summary>
    public const int GeneratedMoves = 8000;

    /// <summary>How many generated moves a style has (none for the rat dance).</summary>
    public static int GeneratedCount(Audio.Cd.MusicStyle style) => DanceGen.Count(DanceStyleIndex(style));

    /// <summary>The energy class of each generated move of a style, for <see cref="DancePick"/>.</summary>
    public static System.ReadOnlySpan<byte> GeneratedEnergy(Audio.Cd.MusicStyle style) => DanceGen.Energy(DanceStyleIndex(style));

    private static void EvalGenerated(Audio.Cd.MusicStyle style, int index, in Beat t, bool mv, ref DanceCh ch)
    {
        var g = DanceGen.Get(DanceStyleIndex(style), index);
        if (g == null) return;
        var f = default(GenFrame);
        DanceGen.Sample(g, t.Par * 4f + t.C, ref f);
        ch.Px = f.Px; ch.Py = f.Py; ch.Pz = f.Pz; ch.Tilt = f.Tilt;
        ch.Theta = f.Theta; ch.Phi = f.Phi; ch.Psi = f.Psi; ch.ThN = f.ThN; ch.PhH = f.PhH;
        ch.ShL = f.ShL; ch.ShR = f.ShR;
        SetArm(ref ch, -1f, new Vector3(f.ArmL.X, f.ArmL.Y, f.ArmL.Z), new Vector3(f.ArmL.HX, f.ArmL.HY, f.ArmL.HZ));
        SetArm(ref ch, 1f, new Vector3(f.ArmR.X, f.ArmR.Y, f.ArmR.Z), new Vector3(f.ArmR.HX, f.ArmR.HY, f.ArmR.HZ));
        GenFoot(ref ch, -1f, f.FootL);
        GenFoot(ref ch, 1f, f.FootR);
        if (mv) MovingScale(ref ch, g.KMov, g.ArmBlend);
    }

    private static void GenFoot(ref DanceCh ch, float s, in GenFoot f)
    {
        Foot(ref ch, s, f.X, AnkleHeight + f.Y, f.Z, f.Turn, f.Lift);
        HeelUp(ref ch, s, f.Heel);
    }
}
