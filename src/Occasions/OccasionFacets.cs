namespace UnitSport.Occasions;

/// <summary>
/// The separable parts of an occasion. Each can be switched off per occasion in
/// <c>occasions.json</c>, and the player's opt-out removes exactly the <see cref="Cosmetic"/> ones.
/// </summary>
[Flags]
public enum OccasionFacets
{
    None = 0,
    /// <summary>Props in the world: jack-o'-lanterns, lights, town trees, pumpkin patches.</summary>
    Decorations = 1 << 0,
    /// <summary>Sun, sky grade, mist, snow, falling snow, creatures.</summary>
    Atmosphere = 1 << 1,
    /// <summary>Seasonal ambience, bell tunes, the menu jingle.</summary>
    Audio = 1 << 2,
    /// <summary>Seasonal items in loot tables and gathering.</summary>
    Loot = 1 << 3,
    /// <summary>The treat / gift hunt.</summary>
    Hunt = 1 << 4,
    /// <summary>The hat everyone wears while the occasion runs.</summary>
    Hats = 1 << 5,
    /// <summary>
    /// A festival's games and score (#1269): gameplay, so a player's "Off" hides the bunting and never the Batzen or the party
    /// score, and the server decides whether they run.
    /// </summary>
    Games = 1 << 6,

    /// <summary>
    /// What a player may turn off for themselves. Loot and the hunt are gameplay, and whether
    /// they run is the server's call, not the viewer's.
    /// </summary>
    Cosmetic = Decorations | Atmosphere | Audio | Hats,
    Gameplay = Loot | Hunt | Games,
    All = Cosmetic | Gameplay,
}
