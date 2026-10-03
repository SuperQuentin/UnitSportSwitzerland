using System.Globalization;

namespace UnitSport.World;

/// <summary>
/// <c>/seastate &lt;0..1|calm|chop|storm|gamey&gt;</c> and <c>--sea-state</c> (#299): pure text in, a sea
/// state out. The chat applies it (offline here, online on the server, which sends it round);
/// <see cref="WaterField.SetSeaState"/> turns it into wave amplitudes.
/// </summary>
public static class SeaStateCommand
{
    public const float Calm = 0f, Chop = 0.35f, Storm = 0.7f, Gamey = 1f;

    /// <summary>The named states, mildest first.</summary>
    public static readonly (string Name, float Value)[] Named =
        { ("calm", Calm), ("chop", Chop), ("storm", Storm), ("gamey", Gamey) };

    public const string Usage = "/seastate [0..1 | calm | chop | storm | gamey]";

    /// <summary>A name or a number 0..1 (invariant culture, a comma taken as the decimal point).</summary>
    public static bool TryParse(string text, out float value, out string error)
    {
        value = 0f;
        error = string.Empty;
        string t = text.Trim().ToLowerInvariant();
        foreach (var (name, v) in Named)
            if (t == name) { value = v; return true; }
        if (float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            && float.IsFinite(f) && f >= 0f && f <= 1f)
        {
            value = f;
            return true;
        }
        error = $"'{text.Trim()}' is not a sea state. Usage: {Usage}";
        return false;
    }

    /// <summary>
    /// <c>--sea-state &lt;0..1|name&gt;</c> on a command line (server, or an offline client for
    /// screenshots); null when absent or unreadable (<paramref name="error"/> says why).
    /// </summary>
    public static float? FromArgs(string[] args, out string error)
    {
        error = string.Empty;
        int i = Array.IndexOf(args, "--sea-state");
        if (i < 0) return null;
        if (i + 1 >= args.Length) { error = "--sea-state needs a value: 0..1, calm, chop, storm or gamey"; return null; }
        return TryParse(args[i + 1], out float v, out error) ? v : null;
    }

    /// <summary>"chop (0.35)", or "0.50 (between chop and storm)" for a value between names.</summary>
    public static string Describe(float value)
    {
        foreach (var (name, v) in Named)
            if (Math.Abs(value - v) < 0.005f) return $"{name} ({v.ToString("0.##", CultureInfo.InvariantCulture)})";
        string number = value.ToString("0.00", CultureInfo.InvariantCulture);
        for (int i = 1; i < Named.Length; i++)
            if (value < Named[i].Value) return $"{number} (between {Named[i - 1].Name} and {Named[i].Name})";
        return number;
    }
}
