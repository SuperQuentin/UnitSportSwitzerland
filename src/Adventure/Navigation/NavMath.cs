using System;
using System.Globalization;
using Godot;

namespace UnitSport.Adventure.Navigation;

// Pure maths and text (Godot's structs only, no engine): linked into the unit tests (NavMathTests, NavFormatTests).

/// <summary>Distances and climbs as the HUD writes them (#1226, navigation plan 3): invariant culture, whatever the locale.</summary>
public static class NavFormat
{
    /// <summary>The rounding step a label changes on: 10 m under 1 km, 100 m above (1 km above 20 km).</summary>
    public static int Step(double m) => m < 1000 ? (int)Math.Round(m / 10) : m < 20000 ? 100_000 + (int)Math.Round(m / 100) : 1_000_000 + (int)Math.Round(m / 1000);

    /// <summary><c>850 m</c>, <c>3.4 km</c>, <c>27 km</c>.</summary>
    public static string Distance(double m)
    {
        if (m < 1000) return ((int)(Math.Round(m / 10) * 10)).ToString(CultureInfo.InvariantCulture) + " m";
        if (m < 20000) return (Math.Round(m / 100) / 10).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        return Math.Round(m / 1000).ToString("0", CultureInfo.InvariantCulture) + " km";
    }

    /// <summary><c>▲1,210 m</c> / <c>▼80 m</c>; empty within ±30 m (not worth saying).</summary>
    public static string Climb(double dz)
    {
        if (Math.Abs(dz) <= 30) return "";
        string n = (Math.Round(Math.Abs(dz) / 10) * 10).ToString("#,0", CultureInfo.InvariantCulture);
        return (dz > 0 ? "▲" : "▼") + n + " m";
    }

    /// <summary><c>Piz Palü · 3.4 km ▲1,210 m</c>, or <c>in the area</c> inside a search radius.</summary>
    public static string Label(string name, double m, double dz, float radiusM)
    {
        if (radiusM > 0 && m <= radiusM) return name.Length > 0 ? name + " · in the area" : "in the area";
        string climb = Climb(dz);
        string d = climb.Length > 0 ? Distance(m) + " " + climb : Distance(m);
        return name.Length > 0 ? name + " · " + d : d;
    }
}

/// <summary>The target marker's screen maths (#1226, navigation plan 3).</summary>
public static class NavMath
{
    /// <summary>
    /// Where an off-screen marker sits: on an ellipse inset by <paramref name="inset"/> px from the screen's edge,
    /// toward <paramref name="dir"/> (screen space, y down) from the centre.
    /// </summary>
    public static Vector2 EdgePoint(Vector2 screen, Vector2 dir, float inset = 48f)
    {
        var c = screen * 0.5f;
        float a = Mathf.Max(c.X - inset, 1), b = Mathf.Max(c.Y - inset, 1);
        if (dir.LengthSquared() < 1e-8f) dir = Vector2.Up;
        dir = dir.Normalized();
        // the ray from the centre meets the ellipse x²/a² + y²/b² = 1 at t
        float t = 1f / Mathf.Sqrt(dir.X * dir.X / (a * a) + dir.Y * dir.Y / (b * b));
        return c + dir * t;
    }

    /// <summary>
    /// The screen direction toward a point the camera may have behind it: the projected offset from the centre,
    /// flipped when behind (a projection through the camera's plane mirrors it).
    /// </summary>
    public static Vector2 OffscreenDir(Vector2 projected, Vector2 screen, bool behind)
    {
        var d = projected - screen * 0.5f;
        if (behind) d = -d;
        // straight behind projects onto the centre: point down, "turn round"
        return d.LengthSquared() < 1f ? Vector2.Down : d;
    }

    /// <summary>The marker's size: 28 px far (2 km and more) to 18 px near (50 m and less).</summary>
    public static float Size(double m) => Mathf.Lerp(18f, 28f, Mathf.Clamp((float)((m - 50) / 1950), 0f, 1f));
}
