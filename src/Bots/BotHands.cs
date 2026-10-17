using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Bots;

/// <summary>
/// A bot's hands (#837): what a player's <c>ItemController</c> does with a held weapon, on the server
/// for a body it runs. The same rules as a player's: the weapon's rate of fire (<see cref="WeaponDef.Interval"/>),
/// one round from the pack per shot, the spread and its bloom, the pellets traced by
/// <see cref="PlayerHits"/>, the hit put through the server's own checks (<see cref="ItemEvents.ServerHit"/>),
/// the shot heard and seen by every peer that sees the bot. The arm pose follows from the replicated
/// <c>HeldItemId</c> and <c>ItemAction</c>, as a player's does. Aim error is the brain's business:
/// the hands fire where they are told.
/// </summary>
public static class BotHands
{
    /// <summary>The weapon a bot would fire now: the held one if it can, else the best gun with rounds, else a blade.</summary>
    public static (int Slot, WeaponDef Weapon)? Pick(Bot bot)
    {
        var inv = bot.Inventory;
        (int, WeaponDef)? blade = null, gun = null;
        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            if (inv[i].IsEmpty || Weapons.Get(inv[i].Id) is not { } w) continue;
            if (w.Melee) blade ??= (i, w);
            else if (w.Ammo == ItemId.None || inv.CountPlain(w.Ammo) > 0)
            {
                if (i == inv.Selected) return (i, w);
                if (gun is not { } g || w.Damage * w.Pellets / w.Interval > g.Item2.Damage * g.Item2.Pellets / g.Item2.Interval) gun = (i, w);
            }
        }
        return gun ?? blade;
    }

    /// <summary>
    /// Fires (or stabs) at <paramref name="target"/>, a world point, when the weapon is ready. False when
    /// it is not (cycling, no weapon, no rounds) or the target is out of a blade's reach.
    /// </summary>
    public static bool Fire(Bot bot, Vector3 target)
    {
        if (ItemEventsServer is not { } events || Pick(bot) is not var (slot, weapon)) return false;
        var body = bot.Body;
        if (body.Eliminated || body.KnockedOut || body.Down != 0) return false;
        double now = GameClock.Now;
        if (now < bot.NextShotAt) return false;
        bot.Inventory.Select(slot);
        body.HeldItemId = (int)weapon.Id;
        body.ItemAction = 1;   // aiming: the arms come up on every peer

        var eye = body.EyePosition;
        var aim = (target - eye).Normalized();
        if (!aim.IsFinite() || aim.LengthSquared() < 0.5f) return false;
        PlayerHits.HitSink sink = (victim, at, dir, hit) => events.ServerHit(bot.Id, at, dir, hit);
        if (weapon.Melee)
        {
            if (eye.DistanceTo(target) > weapon.Range + 0.5f) return false;
            bot.NextShotAt = now + weapon.Interval;
            PlayerHits.Stab(body, eye, aim, weapon, sink);
            return true;
        }
        if (weapon.Ammo != ItemId.None && bot.Inventory.TakePlain(weapon.Ammo, 1) < 1) return false;
        bot.NextShotAt = now + weapon.Interval;
        // the spread's bloom, as ItemController's: each shot heats the barrel, idle time cools it
        bot.Heat = WeaponDef.Cool(bot.Heat, (float)(now - bot.HeatAt));
        float spread = weapon.SpreadAt(bot.Heat);
        bot.Heat = weapon.Heat(bot.Heat);
        bot.HeatAt = now;
        events.ServerSend(bot.Id, ItemEventKind.Shot, ItemEvents.MuzzleOf(body, aim), aim, ((int)weapon.Id).ToString());
        body.BodyJolt();
        PlayerHits.Shoot(body, eye, aim, weapon, spread, sink);
        return true;
    }

    /// <summary>Gives a bot a weapon and rounds for it (<c>/bot arm</c>, a mode's kit).</summary>
    public static void Arm(Bot bot, ItemId weapon, int rounds)
    {
        if (Weapons.Get(weapon) is not { } w) return;
        int slot = Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(i => bot.Inventory[i].IsEmpty, 1);
        bot.Inventory.Put(slot, new ItemStack(weapon, 1));
        if (w.Ammo != ItemId.None && rounds > 0) bot.Inventory.Add(w.Ammo, rounds);
        bot.Inventory.Select(slot);
        bot.Body.HeldItemId = (int)weapon;
    }

    private static ItemEvents? ItemEventsServer => BotDirector.Instance?.GetParent()?.GetNodeOrNull<ItemEvents>(ItemEvents.NodeName);
}
