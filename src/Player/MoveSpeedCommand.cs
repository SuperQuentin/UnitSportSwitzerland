using System.Globalization;

namespace UnitSport.Player;

/// <summary>
/// <c>/movespeed [&lt;factor&gt; | normal] [player]</c> (#1485): pure text in, a movement speed factor out.
/// Any positive finite number, no upper cap (an admin's tool); <c>1</c> or <c>normal</c> is normal
/// speed. The chat applies it (offline here, online on the server, which sends it to the body's
/// owner); <c>FootPlayer.SpeedFactor</c> scales the body's walking, swimming, climbing and
/// creative flight by it.
/// </summary>
public static class MoveSpeedCommand
{
    public const string Usage = "/movespeed [<factor> | normal] [player]  (any positive factor, 1 = normal)";

    /// <summary>
    /// A factor: <c>5</c>, <c>0.5</c>, <c>x20</c> or <c>20x</c> (invariant culture, a comma taken as the
    /// decimal point), or <c>normal</c> / <c>reset</c> / <c>off</c> for 1. Zero, negative and non-finite are refused.
    /// </summary>
    public static bool TryParse(string text, out float factor, out string error)
    {
        factor = 1f;
        error = string.Empty;
        string t = text.Trim().ToLowerInvariant();
        if (t is "normal" or "reset" or "off") return true;
        if (t.StartsWith('x')) t = t[1..];
        else if (t.EndsWith('x')) t = t[..^1];
        if (float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            && float.IsFinite(f) && f > 0f)
        {
            factor = f;
            return true;
        }
        error = $"'{text.Trim()}' is not a speed factor. Usage: {Usage}";
        return false;
    }

    /// <summary>"x5", "x0.5", "normal (x1)".</summary>
    public static string Describe(float factor) =>
        factor == 1f ? "normal (x1)" : "x" + factor.ToString("0.###", CultureInfo.InvariantCulture);
}
