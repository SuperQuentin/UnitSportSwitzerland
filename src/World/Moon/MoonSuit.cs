using UnitSport.Player;

namespace UnitSport.World.Moon;

/// <summary>
/// The suit on the Moon (#1941, docs/plans/moon-surface.md section 6): worn automatically while the anchor is on the
/// Moon; its helmet on outside a colony's air and off inside (<see cref="ColonyAirlocks.Pressurised"/>, read at the
/// head). No oxygen timer, as the user decided. Read every 0.25 s by <c>OccasionHats</c>, which sets the replicated
/// outfit and headwear: other players see it, and a bot's body gets the same from its own outfit bits.
/// </summary>
public static class MoonSuit
{
    /// <summary>The suit a walker wears now, or null off the Moon.</summary>
    public static (bool Helmet, bool Suit)? Wanted(FootPlayer p)
    {
        if (MoonSurface.Active is not { OnMoon: true }) return null;
        // in the pressurised rover's cabin (#1943) the helmet is off too
        if (p.Ride == MoonRovers.Cabin.Kind) return (false, true);
        var head = p.GlobalPosition + Godot.Vector3.Up * 1.6f;
        return (!ColonyAirlocks.Pressurised(head), true);
    }
}
