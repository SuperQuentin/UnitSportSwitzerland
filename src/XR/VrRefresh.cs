namespace UnitSport.XR;

/// <summary>Choosing the headset's refresh rate among those the runtime offers (#1036). No Godot: tier 0 tests it.</summary>
public static class VrRefresh
{
    /// <summary>The offered rate nearest <paramref name="want"/> at or under it, else the lowest offered; null if none is.</summary>
    public static float? Pick(IEnumerable<float> offered, float want)
    {
        float? under = null, lowest = null;
        foreach (float hz in offered)
        {
            if (hz <= 0f) continue;
            if (hz <= want + 0.5f && (under == null || hz > under)) under = hz;
            if (lowest == null || hz < lowest) lowest = hz;
        }
        return under ?? lowest;
    }
}
