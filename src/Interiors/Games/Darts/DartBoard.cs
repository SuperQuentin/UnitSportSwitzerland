using Godot;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// A steel-tip dartboard (#1324), pure: where a point on its face scores. Millimetres from the bull's centre, x to the
/// right as the thrower sees it, y up. Twenty segments clockwise from 20 at the top, the treble ring 99-107 mm, the
/// double 162-170 mm, the outer bull 15.9 mm, the bull 6.35 mm (the WDF's sizes); the board 451 mm across.
/// </summary>
public static class DartBoard
{
    public static readonly int[] Order = { 20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5 };
    public const float Bull = 6.35f, OuterBull = 15.9f, TrebleIn = 99f, TrebleOut = 107f, DoubleIn = 162f, DoubleOut = 170f, Radius = 225.5f;
    /// <summary>The throw line's distance from the board's face [m], and the bull's height over the floor [m].</summary>
    public const float Oche = 2.37f, BullHeight = 1.73f;

    /// <summary>What a dart scores: its value, its multiplier (0 a miss, 1-3; the bulls are 25 x1 and 25 x2), its name.</summary>
    public readonly record struct Hit(int Value, int Multiplier)
    {
        public int Score => Value * Multiplier;
        public bool Double => Multiplier == 2;
        public string Name => Multiplier == 0 ? "miss" : Value == 25 ? Multiplier == 2 ? "bull" : "25"
            : Multiplier switch { 3 => $"T{Value}", 2 => $"D{Value}", _ => $"{Value}" };
    }

    public static Hit Score(Vector2 mm)
    {
        float r = mm.Length();
        if (r <= Bull) return new Hit(25, 2);
        if (r <= OuterBull) return new Hit(25, 1);
        if (r > DoubleOut) return new Hit(0, 0);
        int value = Segment(mm);
        int mult = r >= DoubleIn ? 2 : r >= TrebleIn && r <= TrebleOut ? 3 : 1;
        return new Hit(value, mult);
    }

    /// <summary>The segment's number at a point (its angle from the top, clockwise).</summary>
    public static int Segment(Vector2 mm)
    {
        float deg = Mathf.RadToDeg(Mathf.Atan2(mm.X, mm.Y));
        int i = (int)Mathf.Floor(((deg + 9 + 360) % 360) / 18f) % 20;
        return Order[i];
    }

    /// <summary>The middle of a bed: segment <paramref name="value"/> at multiplier <paramref name="mult"/> (25 the bull).</summary>
    public static Vector2 Aim(int value, int mult)
    {
        if (value == 25) return Vector2.Zero;
        int i = Array.IndexOf(Order, value);
        float a = Mathf.DegToRad(i * 18f);
        float r = mult switch { 3 => (TrebleIn + TrebleOut) / 2, 2 => (DoubleIn + DoubleOut) / 2, _ => (OuterBull + TrebleIn) / 2 + 10 };
        return new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r;
    }
}
