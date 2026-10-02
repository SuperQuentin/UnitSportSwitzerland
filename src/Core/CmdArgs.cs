using System;
using System.Globalization;

namespace UnitSport.Core;

/// <summary>
/// The user command line (the words after <c>--</c>), read once and parsed one way (#221). Every
/// overload taking a <c>string[]</c> is pure, so tier 0 tests it (tests/UnitSportSwitzerland.Tests/CmdArgsTests.cs);
/// the others read <see cref="All"/>, the engine's args cached at first use: they never change while
/// the game runs. Numbers are always InvariantCulture (docs/notes/general/invariant-culture-floats.md).
/// Rule note: docs/notes/core/cmd-args.md.
/// </summary>
public static class CmdArgs
{
#if GODOT
    /// <summary>The user args, cached. Shared: never modify it (copy it first).</summary>
    public static readonly string[] All = Godot.OS.GetCmdlineUserArgs();

    /// <inheritdoc cref="Has(string[], string)"/>
    public static bool Has(string flag) => Has(All, flag);

    /// <inheritdoc cref="Value(string[], string, int, bool)"/>
    public static string? Value(string flag, int at = 1, bool notFlag = false) => Value(All, flag, at, notFlag);

    /// <inheritdoc cref="Double(string[], string, int)"/>
    public static double? Double(string flag, int at = 1) => Double(All, flag, at);

    /// <inheritdoc cref="Float(string[], string, int)"/>
    public static float? Float(string flag, int at = 1) => Float(All, flag, at);

    /// <inheritdoc cref="Int(string[], string, int)"/>
    public static int? Int(string flag, int at = 1) => Int(All, flag, at);

    /// <inheritdoc cref="FlagWithShot(string[], string)"/>
    public static (bool Requested, string? Shot) FlagWithShot(string prefix) => FlagWithShot(All, prefix);
#endif

    /// <summary>Whether <paramref name="flag"/> is one of the args (exact match).</summary>
    public static bool Has(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

    /// <summary>
    /// The word <paramref name="at"/> places after the first <paramref name="flag"/> (1: the next one),
    /// or null when the flag is absent or the line ends first. A word starting with <c>--</c> counts as
    /// a value unless <paramref name="notFlag"/> is set.
    /// </summary>
    public static string? Value(string[] args, string flag, int at = 1, bool notFlag = false)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0 || i + at >= args.Length) return null;
        string v = args[i + at];
        return notFlag && v.StartsWith("--", StringComparison.Ordinal) ? null : v;
    }

    /// <summary><see cref="Value(string[], string, int, bool)"/> as a double, null when absent or not a number.</summary>
    public static double? Double(string[] args, string flag, int at = 1)
        => double.TryParse(Value(args, flag, at), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary><see cref="Value(string[], string, int, bool)"/> as a float, null when absent or not a number.</summary>
    public static float? Float(string[] args, string flag, int at = 1)
        => float.TryParse(Value(args, flag, at), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;

    /// <summary><see cref="Value(string[], string, int, bool)"/> as an int, null when absent or not an integer.</summary>
    public static int? Int(string[] args, string flag, int at = 1)
        => int.TryParse(Value(args, flag, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>
    /// The probe switch <c>--xcheck[,shot]</c>: the first arg starting with <paramref name="prefix"/>,
    /// and the word after its first comma (a screenshot path, a prefix…), null when there is none.
    /// </summary>
    public static (bool Requested, string? Shot) FlagWithShot(string[] args, string prefix)
    {
        foreach (var a in args)
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }
}
