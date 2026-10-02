namespace UnitSport.Player;

/// <summary>
/// Walking through water (#380), as numbers. Plain C# with no Godot in it, so the unit tests link
/// it (<c>tests/UnitSportSwitzerland.Tests/SwimTests.cs</c>). The depth is the feet's under the
/// moving surface (<c>WaterField</c>), so a swell washing up a beach slows the walk as it comes.
/// <list type="bullet">
/// <item>Ankle deep (to <see cref="Feel"/>) nothing changes. From there the water's drag on the
/// legs grows to waist deep (<see cref="Waist"/>): at the knee (<see cref="Knee"/>) a walk is ~0.84 of
/// its pace and a run ~0.78; at the waist a walk ~0.42 and a run ~0.22 (drag grows with the square
/// of the speed, so a run loses more), about 0.8 and 1.3 m/s in Game.</item>
/// <item>The spray thrown up: from the shins to the knees it splashes most, at the waist the legs
/// push a bow of water instead (less spray), and only while moving.</item>
/// </list>
/// Deeper than <c>SwimEnter</c> (1.35 m) the body swims (<c>FootPlayer.Swim</c>).
/// </summary>
public static class Wading
{
    /// <summary>Feet this far under the surface (m): the water is felt.</summary>
    public const float Feel = 0.2f;
    /// <summary>Knee deep (m), for checks and pictures.</summary>
    public const float Knee = 0.5f;
    /// <summary>Waist deep (m): the slowest a wader goes; deeper, it soon swims.</summary>
    public const float Waist = 1.05f;
    /// <summary>The share of the pace lost at the waist: walking, running.</summary>
    public const float WalkLoss = 0.58f, RunLoss = 0.78f;

    /// <summary>The share of the walking (or running) pace left with the feet <paramref name="depth"/> metres under.</summary>
    public static float SpeedFactor(float depth, bool running)
    {
        if (!(depth > Feel)) return 1f;   // dry, ankle deep, or no water (NaN)
        float s = Smooth((depth - Feel) / (Waist - Feel));
        return 1f - (running ? RunLoss : WalkLoss) * s;
    }

    /// <summary>The spray a wader throws, 0..1, from the depth (m) and the speed through the water (m/s).</summary>
    public static float Spray(float depth, float speed)
    {
        if (!(depth > 0.06f) || !(speed > 0.3f)) return 0f;
        float wet = Smooth((depth - 0.06f) / 0.4f) * (1f - 0.55f * Smooth((depth - 0.8f) / 0.5f));
        return wet * Math.Clamp((speed - 0.3f) / 2.5f, 0f, 1f);
    }

    /// <summary>How loud a wading stride is, 0..1, from the depth (m): a splash in the shallows, a slosh by the knees.</summary>
    public static float StrideVolume(float depth) =>
        depth > 0.06f ? 0.25f + 0.75f * Smooth((depth - 0.06f) / 0.6f) : 0f;

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
