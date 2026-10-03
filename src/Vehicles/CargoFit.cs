namespace UnitSport.Vehicles;

/// <summary>
/// What fits in a hold and when a vehicle is in one (#418), as plain numbers: no Godot, so the rules
/// are unit tested. Sizes are (across, height, length), points are in the hold's own frame with the
/// hold's middle at the origin and its floor at <c>-height/2</c>.
/// </summary>
public static class CargoFit
{
    /// <summary>Room left round a vehicle in a hold, m: across, overhead and fore and aft.</summary>
    public const float Clearance = 0.1f;

    /// <summary>
    /// A vehicle whose hull is <paramref name="w"/> × <paramref name="h"/> × <paramref name="l"/> fits a
    /// hold of <paramref name="bayW"/> × <paramref name="bayH"/> × <paramref name="bayL"/>, driven in
    /// nose or tail first (never across: it went in through the door at one end).
    /// </summary>
    public static bool Fits(float w, float h, float l, float bayW, float bayH, float bayL) =>
        w + Clearance <= bayW && h + Clearance <= bayH && l + Clearance <= bayL;

    /// <summary>
    /// A vehicle standing at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) (its
    /// ground point, the hold's frame) is in the hold: over its floor (the middle of the vehicle within
    /// the floor's plan, grown by <paramref name="grow"/>), its wheels at most 0.5 m under the floor
    /// (on a ramp's top) and below the roof.
    /// </summary>
    public static bool Inside(float x, float y, float z, float bayW, float bayH, float bayL, float grow)
    {
        float floor = -bayH * 0.5f;
        return System.MathF.Abs(x) <= bayW * 0.5f + grow && System.MathF.Abs(z) <= bayL * 0.5f + grow
            && y >= floor - 0.5f - grow && y <= bayH * 0.5f;
    }
}
