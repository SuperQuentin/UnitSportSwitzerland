using System.Globalization;

namespace UnitSport.Adventure.Alcohol;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/alcohol.md).

/// <summary><c>--alcohol off|mild|real</c> (#1271): off, drinks carry no alcohol; mild, half the effects and no passing out.</summary>
public enum AlcoholMode { Off = 0, Mild = 1, Real = 2 }

/// <summary>
/// What a level does to the player (<see cref="AlcoholRules.Effects"/>); the thresholds are the Swiss road limits.
/// <c>SwayDegrees</c>: camera sway at 0.2 Hz, never in VR. <c>Lag</c>: vehicle and walk input lag, s. <c>SteerNoise</c>: steering noise on
/// a vehicle, 0..1 of the lock. <c>DriftDegrees</c>: the walk direction's slow drift, at most. <c>Blur</c>: edge blur on a flat screen, a
/// vignette in VR, 0..1.
/// </summary>
public readonly record struct DrunkEffects(
    double Level, float SwayDegrees, float Lag, float SteerNoise, float DriftDegrees, float Blur,
    bool Stumbles, bool NoClimb, bool PassesOut)
{
    public bool Any => SwayDegrees > 0 || Lag > 0;
}

/// <summary>An electronic breathalyzer's screen, or why it has none yet.</summary>
public readonly record struct BreathReading(bool Waiting, int WaitMinutes, string Main, string Limit, double Permille);

/// <summary>A single-use test's tube: clear before it is used, green under 0.5, amber over.</summary>
public enum TubeColour { Clear, Green, Amber }

/// <summary>The rules of the alcohol plan that need no Godot: thresholds, effects, readings, the drinks the game adds.</summary>
public static class AlcoholRules
{
    /// <summary>The general Swiss road limit, per mille (0.1 for new and professional drivers, 0.8 a qualified offence).</summary>
    public const double Limit = 0.5, NewDriverLimit = 0.1, QualifiedLimit = 0.8;
    /// <summary>Drunk above this: the "you have been drinking" icon.</summary>
    public const double Tipsy = 0.3;
    /// <summary>Passing out from here, waking below <see cref="WakeAt"/> after at least <see cref="OutSeconds"/>.</summary>
    public const double PassOutAt = 2.5, WakeAt = 2.4;
    public const float OutSeconds = 60f;
    /// <summary>A stumble lasts this long, s, and comes every <see cref="StumbleMin"/> to <see cref="StumbleMax"/> s from 1.5 per mille.</summary>
    public const float StumbleSeconds = 1.1f, StumbleMin = 20f, StumbleMax = 40f;
    /// <summary>A breath test blows for this long, s.</summary>
    public const float BlowSeconds = 5f;

    /// <summary>The fondue's alcohol per portion: a wine pour, and the kirsch dash on top, grams.</summary>
    public const double FondueWineGrams = 4, FondueKirschGrams = 3;
    /// <summary>Gluehwein (festivals plan, its item comes later): 2 dl at 10 %.</summary>
    public const double GluehweinMl = 200, GluehweinAbv = 0.10;

    public static double GluehweinGrams => Bac.Grams(GluehweinMl, GluehweinAbv);

    public static AlcoholMode ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => AlcoholMode.Off,
        "mild" => AlcoholMode.Mild,
        _ => AlcoholMode.Real,
    };

    /// <summary>What <paramref name="level"/> per mille does under <paramref name="mode"/>; mild feels the half of it and never passes out.</summary>
    public static DrunkEffects Effects(double level, AlcoholMode mode)
    {
        if (mode == AlcoholMode.Off) return default;
        double l = mode == AlcoholMode.Mild ? level * 0.5 : level;
        float sway = l < 0.3 ? 0f : l < 0.5 ? 0.3f : l < 1.5 ? 0.6f : 0.9f;
        float lag = l < 0.5 ? 0f : l < 0.8 ? 0.1f : l < 1.5 ? 0.2f : 0.3f;
        float noise = l < 0.8 ? 0f : l < 1.5 ? 0.05f : 0.1f;
        float drift = l < 0.8 ? 0f : l < 1.5 ? 8f : 15f;
        float blur = l < 0.8 ? 0f : l < 1.5 ? 0.4f : 0.7f;
        return new DrunkEffects(level, sway, lag, noise, drift, blur, Stumbles: l >= 1.5, NoClimb: l >= 1.5, PassesOut: mode == AlcoholMode.Real && level >= PassOutAt);
    }

    /// <summary>The electronic device's screen: <c>0.62 ‰ · 0.31 mg/L</c> (breath mg/L = per mille / 2) and the limit line, or <c>WAIT 12 min</c>.</summary>
    public static BreathReading Electronic(double level, double mouthLeft)
    {
        if (mouthLeft > 0)
        {
            int minutes = (int)Math.Ceiling(mouthLeft / 60.0);
            return new BreathReading(true, minutes, string.Create(CultureInfo.InvariantCulture, $"WAIT {minutes} min"), "mouth alcohol", 0);
        }
        double v = Math.Round(level, 2);
        string main = string.Create(CultureInfo.InvariantCulture, $"{v:0.00} ‰ · {v / 2:0.00} mg/L");
        string limit = v > Limit ? "> 0.5 ‰: do not drive" : "under the 0.5 ‰ limit";
        return new BreathReading(false, 0, main, limit, v);
    }

    /// <summary>The single-use tube: green under 0.5 per mille, amber from there.</summary>
    public static TubeColour Tube(double level) => level < Limit ? TubeColour.Green : TubeColour.Amber;

    /// <summary>The tube's words; an early reading (mouth alcohol) says it is unreliable.</summary>
    public static string TubeText(TubeColour c, bool unreliable)
    {
        string words = c == TubeColour.Green ? "Green: under 0.5 ‰" : "Amber: over 0.5 ‰";
        return unreliable ? words + " (too soon after a drink: unreliable)" : words;
    }
}

/// <summary>
/// A short memory of inputs for lag (#1271): <see cref="Push"/> the input of this step, <see cref="At"/> the one from
/// <c>lag</c> seconds ago. A fixed ring, so no allocation after the first call.
/// </summary>
public sealed class DelayLine<T>
{
    private readonly double[] _t;
    private readonly T[] _v;
    private int _head, _count;

    public DelayLine(int capacity = 96) { _t = new double[capacity]; _v = new T[capacity]; }

    public void Clear() { _head = _count = 0; }

    public void Push(double time, T value)
    {
        _head = (_head + 1) % _t.Length;
        _t[_head] = time;
        _v[_head] = value;
        if (_count < _t.Length) _count++;
    }

    /// <summary>The newest value pushed at or before <c>now - lag</c>; the oldest kept when none is that old; <paramref name="fallback"/> when empty.</summary>
    public T At(double now, double lag, T fallback)
    {
        if (_count == 0) return fallback;
        double want = now - lag;
        int i = _head;
        for (int n = 0; n < _count; n++)
        {
            if (_t[i] <= want || n == _count - 1) return _v[i];
            i = (i - 1 + _t.Length) % _t.Length;
        }
        return fallback;
    }
}

/// <summary>What <see cref="DrunkState.Step"/> says happened.</summary>
public enum DrunkEvent { None, Stumble, PassOut, WakeUp }

/// <summary>
/// The stumble and passing-out clocks of a drunk body (#1271): from 1.5 per mille a stumble every 20 to 40 s; from 2.5
/// passing out for at least 60 s, waking once the level is under 2.4. Game time, stepped by the body.
/// </summary>
public sealed class DrunkState
{
    private float _nextStumble = -1;
    private bool _out;

    public float OutLeft { get; private set; }
    public bool Out => _out;

    public DrunkEvent Step(in DrunkEffects fx, float dt, Func<float> random01)
    {
        if (_out)
        {
            OutLeft = Math.Max(0, OutLeft - dt);
            // stays out while the minute runs, and while the level is still at or over the waking level
            // (Stumbles: the mode still has effects at this level; off wakes at once)
            if (OutLeft > 0 || fx.Stumbles && fx.Level >= AlcoholRules.WakeAt) return DrunkEvent.None;
            _out = false;
            _nextStumble = -1;
            return DrunkEvent.WakeUp;
        }
        if (fx.PassesOut)
        {
            _out = true;
            OutLeft = AlcoholRules.OutSeconds;
            return DrunkEvent.PassOut;
        }
        if (!fx.Stumbles) { _nextStumble = -1; return DrunkEvent.None; }
        if (_nextStumble < 0) _nextStumble = AlcoholRules.StumbleMin + random01() * (AlcoholRules.StumbleMax - AlcoholRules.StumbleMin);
        _nextStumble -= dt;
        if (_nextStumble > 0) return DrunkEvent.None;
        _nextStumble = -1;
        return DrunkEvent.Stumble;
    }
}
