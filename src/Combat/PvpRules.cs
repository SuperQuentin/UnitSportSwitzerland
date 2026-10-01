using Godot;

namespace UnitSport.Combat;

/// <summary>
/// Server: whether foot weapons may hurt players (#178). Off by default, so free roam stays
/// peaceful; <c>/pvp on</c> (admin), <c>--pvp</c> on the server's command line or a Battle
/// Royale match turns it on. Hits are relayed by
/// <c>Items.ItemEvents</c>, which asks here first.
/// </summary>
public static class PvpRules
{
    public static bool Enabled { get; set; } = OS.GetCmdlineUserArgs().Contains("--pvp");

    /// <summary>Server: a hit was passed on to its victim (shooter peer, victim peer, damage). For match statistics.</summary>
    public static event Action<long, long, float>? HitRelayed;

    internal static void RaiseHit(long shooter, long victim, float damage) => HitRelayed?.Invoke(shooter, victim, damage);
}
