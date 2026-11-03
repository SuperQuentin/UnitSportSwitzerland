using System;
using Godot;

namespace UnitSport.Ui;

// Pure maths (Godot's structs only, no engine): linked into the unit tests (CompassMathTests).

/// <summary>
/// The compass strip's maths (#1226, navigation plan 1): bearings, where a marker lands on the strip, which
/// labels a crowded strip drops. Shared by the Battle Royale strip and the free-roam one (<see cref="CompassStrip"/>).
/// </summary>
public static class CompassMath
{
    /// <summary>The bearing of a direction given as (east, north): 0 north, 90 east, clockwise, in [0, 360).</summary>
    public static float Bearing(Vector2 eastNorth) => Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(eastNorth.X, eastNorth.Y)), 360f);

    /// <summary>A bearing relative to the heading, in [-180, 180).</summary>
    public static float Relative(float bearing, float heading) => Mathf.Wrap(bearing - heading, -180f, 180f);

    /// <summary>
    /// Where a marker at <paramref name="rel"/> degrees lands on a strip <paramref name="width"/> px wide showing
    /// ±<paramref name="halfSpan"/>°, and whether it is pinned to an edge (behind the view).
    /// </summary>
    public static (float X, bool Pinned) Place(float rel, float width, float halfSpan)
    {
        bool pinned = Mathf.Abs(rel) > halfSpan;
        float x = width * 0.5f + Mathf.Clamp(rel, -halfSpan, halfSpan) * width / (halfSpan * 2f);
        return (Mathf.Clamp(x, 8f, width - 8f), pinned);
    }

    /// <summary>
    /// Declutter: <paramref name="labelled"/>[i] says whether marker i keeps its label. Markers are in priority
    /// order (most important first); one within <paramref name="minPx"/> of an earlier labelled one loses its
    /// label (its icon stays). At most <paramref name="max"/> are drawn: the rest get <c>drawn = false</c>.
    /// </summary>
    public static void Declutter(ReadOnlySpan<float> xs, Span<bool> drawn, Span<bool> labelled, int max = 8, float minPx = 10f)
    {
        int shown = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            drawn[i] = shown < max;
            if (!drawn[i]) { labelled[i] = false; continue; }
            shown++;
            labelled[i] = true;
            for (int j = 0; j < i; j++)
                if (drawn[j] && labelled[j] && Mathf.Abs(xs[j] - xs[i]) < minPx) { labelled[i] = false; break; }
        }
    }
}
