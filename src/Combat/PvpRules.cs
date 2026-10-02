using Godot;
using UnitSport.Core;

namespace UnitSport.Combat;

/// <summary>
/// Server: whether foot weapons may hurt players (#178). Off by default, so free roam stays
/// peaceful; <c>/pvp on</c> (admin), <c>--pvp</c> on the server's command line or a Battle
/// Royale match turns it on. Hits are relayed by
/// <c>Items.ItemEvents</c>, which asks here first.
/// </summary>
public static class PvpRules
{
    public static bool Enabled { get; set; } = CmdArgs.Has("--pvp");

    /// <summary>
    /// A game mode's say over one shooter/victim pair, asked before <see cref="Enabled"/>: true or
    /// false decides, null leaves it to <see cref="Enabled"/>. A Battle Royale match sets it.
    /// </summary>
    public static Func<long, long, bool?>? Override { get; set; }

    /// <summary>Whether <paramref name="shooter"/>'s weapons may hurt <paramref name="victim"/> now.</summary>
    public static bool Allows(long shooter, long victim) => Override?.Invoke(shooter, victim) ?? Enabled;

    /// <summary>Server: a hit was passed on to its victim (shooter peer, victim peer, damage). For match statistics.</summary>
    public static event Action<long, long, float>? HitRelayed;

    internal static void RaiseHit(long shooter, long victim, float damage) => HitRelayed?.Invoke(shooter, victim, damage);
}
