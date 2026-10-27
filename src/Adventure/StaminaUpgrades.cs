namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (StaminaTests). The service's side is StaminaUpgrades.Service.cs.

/// <summary>The <c>"stamina"</c> progress section (#1225): how many max-stamina steps the player earned, 0..10.</summary>
public sealed class StaminaProgress
{
    public int Steps { get; set; }
}

/// <summary>A player's max stamina went up a step (or several): <paramref name="Steps"/> is the new count.</summary>
public sealed record StaminaRaised(long Peer, string Key, int Steps) : AdventureEvent(Peer, Key);

/// <summary>
/// Max stamina upgrades (#1225, <c>docs/plans/adventure-stamina.md</c> 4): the section, and the
/// <c>"stamina"</c> reward kind the rewards plan grants (one step per 5 edelweiss offered) and quests may
/// grant: <c>"+1"</c> adds that many steps, clamped to <see cref="MaxSteps"/>.
/// </summary>
public static partial class StaminaUpgrades
{
    public const string Kind = "stamina";
    /// <summary>The cap, the same as <c>Player.Stamina.MaxSteps</c> (+10 max a step, 200 at most).</summary>
    public const int MaxSteps = 10;

    public static readonly SectionKey<StaminaProgress> Section = new(Kind, 1);

    /// <summary>
    /// The steps a reward argument adds: <c>"+N"</c> with N from 1 to <see cref="MaxSteps"/>. Null for
    /// <c>"+0"</c>, a negative or garbage (refused and logged by the caller).
    /// </summary>
    public static int? ParseSteps(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        var s = arg.Trim();
        if (s[0] != '+') return null;
        if (!int.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) return null;
        return n is >= 1 and <= MaxSteps ? n : null;
    }

    /// <summary>
    /// The reward handler (<c>AdventureService.RegisterReward("stamina", ...)</c>): checks the peer
    /// adventures, writes the section, touches it, then raises <see cref="StaminaRaised"/>, in that order.
    /// Null when granted, else why not.
    /// </summary>
    public static string? Grant(long peer, PlayerProgress progress, string arg, Func<long, bool> active, AdventureEvents events)
    {
        if (!active(peer)) return "not adventuring now";
        if (ParseSteps(arg) is not int add) return $"'{arg}' is not a number of steps (+1..+{MaxSteps})";
        var s = progress.Get(Section);
        int was = s.Steps;
        s.Steps = Math.Clamp(was + add, 0, MaxSteps);
        if (s.Steps == was) return $"already at the cap ({MaxSteps} steps)";
        progress.Touch(Section);
        events.Raise(new StaminaRaised(peer, progress.Key, s.Steps));
        return null;
    }
}
