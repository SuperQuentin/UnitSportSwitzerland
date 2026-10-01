namespace UnitSport.Core;

/// <summary>
/// A position that means the same place whatever the origin is (#185): LV95 metres and the
/// altitude, in doubles. Anything that keeps a position across frames should keep one of these
/// rather than a world-space <c>Vector3</c>, which changes meaning at every origin shift.
/// Converted with <see cref="WorldOrigin.ToWorld(GlobalPos)"/> and <see cref="WorldOrigin.ToGlobal"/>.
/// </summary>
public readonly record struct GlobalPos(double E, double N, double Alt)
{
    public double HorizontalDistanceTo(GlobalPos other)
    {
        double de = E - other.E, dn = N - other.N;
        return System.Math.Sqrt(de * de + dn * dn);
    }

    public override string ToString() => $"LV95 {E:F2}/{N:F2} alt {Alt:F2}";
}
