namespace UnitSport.Items;

/// <summary>
/// How a weapon item behaves, beside its <see cref="ItemDef"/>: what it spends, how hard and how
/// far it hits players (<see cref="PlayerHits"/>). Damage is per pellet; a pellet past
/// <see cref="FalloffFrom"/> loses strength down to <see cref="FarFactor"/> at <see cref="Range"/>.
/// </summary>
/// <param name="Ammo">The item a shot spends; <see cref="ItemId.None"/> for a blade.</param>
/// <param name="Interval">Seconds between two shots (the shotgun's is its pump cycle).</param>
/// <param name="SpreadDeg">Half-angle of the pellet cone, degrees.</param>
/// <param name="AimFov">Field of view while aiming (the hunting rifle's scope zooms).</param>
/// <param name="Pitch">The shot sound's pitch: a pistol cracks, a hunting rifle booms.</param>
public sealed record WeaponDef(
    ItemId Id,
    ItemId Ammo,
    float Damage,
    int Pellets,
    float SpreadDeg,
    float Range,
    float FalloffFrom,
    float Interval,
    float AimFov,
    float Pitch,
    float HeadMultiplier = 2f,
    float FarFactor = 0.4f)
{
    public bool Melee => Ammo == ItemId.None;

    /// <summary>The most one shot can do to one player: every pellet in the head. The server refuses more.</summary>
    public float MaxHit => Damage * Pellets * HeadMultiplier;

    /// <summary>One pellet's damage at <paramref name="distance"/> metres.</summary>
    public float DamageAt(float distance)
    {
        if (distance <= FalloffFrom || Range <= FalloffFrom) return Damage;
        float t = Math.Clamp((distance - FalloffFrom) / (Range - FalloffFrom), 0f, 1f);
        return Damage * (1f - t * (1f - FarFactor));
    }
}

public static class Weapons
{
    public const float MeleeReach = 2.2f;

    public static readonly WeaponDef[] All =
    {
        // nine pellets of 9 is a one-shot at arm's length, a scratch at 40 m
        new(ItemId.Shotgun, ItemId.Shells, Damage: 9f, Pellets: 9, SpreadDeg: 3.5f, Range: 45f, FalloffFrom: 10f,
            Interval: HeldItemVisual.PumpDelay + HeldItemVisual.PumpTime + 0.1f, AimFov: 50f, Pitch: 1f, HeadMultiplier: 1.5f, FarFactor: 0.2f),
        new(ItemId.Pistol, ItemId.Ammo9mm, Damage: 20f, Pellets: 1, SpreadDeg: 0.7f, Range: 70f, FalloffFrom: 25f,
            Interval: 0.28f, AimFov: 55f, Pitch: 1.65f),
        new(ItemId.Rifle, ItemId.Ammo75, Damage: 26f, Pellets: 1, SpreadDeg: 0.3f, Range: 300f, FalloffFrom: 120f,
            Interval: 0.16f, AimFov: 40f, Pitch: 1.3f),
        new(ItemId.HuntingRifle, ItemId.Ammo75, Damage: 70f, Pellets: 1, SpreadDeg: 0.03f, Range: 700f, FalloffFrom: 400f,
            Interval: 1.4f, AimFov: 9f, Pitch: 0.82f, FarFactor: 0.7f),
        new(ItemId.Knife, ItemId.None, Damage: 34f, Pellets: 1, SpreadDeg: 0f, Range: MeleeReach, FalloffFrom: MeleeReach,
            Interval: 0.55f, AimFov: 70f, Pitch: 1f, HeadMultiplier: 1.5f),
    };

    private static readonly Dictionary<ItemId, WeaponDef> ById = All.ToDictionary(w => w.Id);

    public static WeaponDef? Get(ItemId id) => ById.GetValueOrDefault(id);
}
