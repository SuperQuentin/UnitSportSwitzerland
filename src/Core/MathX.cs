using Godot;

namespace UnitSport.Core;

/// <summary>
/// The small maths helpers every system used to write again (#221): the ground plane, frame-rate
/// independent easing, an angle brought into ±π. Pure functions, no engine calls: tier-0 tested
/// (tests/UnitSportSwitzerland.Tests/MathXTests.cs). A smoothstep is <see cref="Mathf.SmoothStep"/>.
/// </summary>
public static class MathX
{
    /// <summary><paramref name="v"/> on the ground plane: y dropped.</summary>
    public static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>The length of <paramref name="v"/> on the ground plane.</summary>
    public static float FlatLength(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>The distance between <paramref name="a"/> and <paramref name="b"/> on the ground plane.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// The weight that eases a value toward its target at <paramref name="rate"/> per second over
    /// <paramref name="dt"/>, the same at any frame rate: <c>x = Lerp(x, target, Damp(rate, dt))</c>.
    /// </summary>
    public static float Damp(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);

    /// <summary><paramref name="radians"/> brought into [−π, π).</summary>
    public static float WrapAngle(float radians) => Mathf.Wrap(radians, -Mathf.Pi, Mathf.Pi);
}
