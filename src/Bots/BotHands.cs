using System;
using System.Linq;
using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

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
        // a sword in the pack and the target at its reach (#1371): the swing is the hands' answer, a gun's range is not
        if (HasSword(bot) && Pick(bot) is null && bot.Body.GlobalPosition.DistanceTo(target) <= SwordReach + 0.9f) return Swing(bot, target);
        if (ItemEventsServer is not { } events || Pick(bot) is not var (slot, weapon)) return false;
        var body = bot.Body;
        // on foot only, as a player's items (ItemController.UsablePlayer): nobody shoots from the wheel
        if (body.Eliminated || body.KnockedOut || body.Down != 0 || BotDriving.Mounted(bot)) return false;
        double now = BotDirector.Clock;
        if (now < bot.NextShotAt) return false;
        Cancel(bot);   // a shot ends any bandaging
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

    /// <summary>
    /// Starts dressing a wound or putting on a vest, as a player's <c>ItemController</c> does: the item
    /// on the hotbar, the use takes its time (<see cref="ItemController.MedicalSeconds"/>; a vest 0.5 s),
    /// the arms show it (<c>ItemAction</c> 2), and it applies at the end (<see cref="Tick"/>) unless the
    /// bot fires meanwhile, which cancels it and keeps the item. False when there is nothing to use or
    /// no need (full health, full armour, down).
    /// </summary>
    public static bool Use(Bot bot, ItemId item)
    {
        var body = bot.Body;
        if (bot.UseUntil > 0 || body.Eliminated || body.KnockedOut || body.Down != 0 || BotDriving.Mounted(bot)) return false;
        int slot = Enumerable.Range(0, Inventory.Size).FirstOrDefault(i => bot.Inventory[i].Id == item && !bot.Inventory[i].IsEmpty, -1);
        if (slot < 0 || ItemDefs.Get(item) is not { } def) return false;
        bool vest = def.Use == ItemUse.Armor;
        if (vest ? body.Armor >= FootPlayer.MaxArmor - 0.01f : body.Health >= FootPlayer.MaxHealth - 0.01f) return false;
        // on the hotbar first, as a player must (a pack slot cannot be held)
        if (!Inventory.IsHotbar(slot))
        {
            int free = Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(i => bot.Inventory[i].IsEmpty, Inventory.HotbarSize - 1);
            bot.Inventory.SwapWithHotbar(slot, free);
            slot = free;
        }
        bot.Inventory.Select(slot);
        body.HeldItemId = (int)item;
        body.ItemAction = 2;
        bot.UseItem = item;
        bot.UseUntil = BotDirector.Clock + (vest ? 0.5 : Math.Max(0.6, ItemController.MedicalSeconds(item)));
        return true;
    }

    // ---- the sword (#1371) -------------------------------------------------------------------------------------

    /// <summary>Whether the bot carries a sword on its hotbar.</summary>
    public static bool HasSword(Bot bot) => SwordSlot(bot) >= 0;

    private static int SwordSlot(Bot bot)
    {
        for (int i = 0; i < Inventory.HotbarSize; i++)
            if (bot.Inventory[i].Id == ItemId.Sword && !bot.Inventory[i].IsEmpty) return i;
        return -1;
    }

    /// <summary>The longest a swing reaches from the body's centre, for a brain's choice of distance.</summary>
    public static float SwordReach => SwordRules.Def(SwordMove.Slash1).Reach;

    /// <summary>
    /// Swings the sword at <paramref name="target"/> (a world point): the hands press the attack for a moment, and the machine
    /// (the same as a player's) chains the combo while it is asked every tick. False when there is no sword, the body is not
    /// up, or the target is out of reach (the brain closes in first). The moves and hits go through <c>SwordService</c>'s
    /// server path (<see cref="SwordService.ServerAct"/>, <see cref="SwordService.ServerHit"/>): the same guard as a player's.
    /// </summary>
    public static bool Swing(Bot bot, Vector3 target)
    {
        int slot = SwordSlot(bot);
        var body = bot.Body;
        if (slot < 0 || body.Eliminated || body.KnockedOut || body.Down != 0 || BotDriving.Mounted(bot)) return false;
        if (body.GlobalPosition.DistanceTo(target) > SwordReach + 0.9f) return false;
        Cancel(bot);
        bot.Inventory.Select(slot);
        body.HeldItemId = (int)ItemId.Sword;
        body.ItemAction = 1;
        bot.SwordWantUntil = BotDirector.Clock + 0.12;
        bot.SwordTarget = target;
        return true;
    }

    /// <summary>Dodges away from <paramref name="from"/> (a world point): a dash with the i-frames, for the stamina, through the server's guard.</summary>
    public static bool Dodge(Bot bot, Vector3 from)
    {
        if (!HasSword(bot) || bot.Body.Eliminated || bot.Body.Down != 0) return false;
        bot.DodgeWantUntil = BotDirector.Clock + 0.12;
        bot.DodgeAway = from;
        return true;
    }

    /// <summary>The director, every frame: the sword's machine stepped with what the hands were asked, its moves and blows sent through the service.</summary>
    private static void SwordTick(Bot bot, double now)
    {
        var m = bot.Sword;
        var body = bot.Body;
        int slot = SwordSlot(bot);
        if (slot < 0 || !bot.Alive || body.Down != 0)
        {
            if (m.Busy || m.Charging) m.Reset();
            return;
        }
        bool want = now < bot.SwordWantUntil, wantDodge = now < bot.DodgeWantUntil;
        if (!want && !wantDodge && !m.Busy && now - m.EndedAt > 1.0 && m.Stamina.Points >= SwordStamina.Max - 0.01f) return;   // nothing to do, nothing to refill
        var dir = bot.SwordTarget - body.GlobalPosition;
        float yaw = Mathf.Atan2(-dir.X, -dir.Z);
        // a pulse, never a hold: the press is the edge, and the next tick lets go
        bot.SwordPulse = want && !bot.SwordPulse;
        var input = new SwordInput { Attack = bot.SwordPulse, Tap = true, Dodge = wantDodge && !bot.SwordPulse, Grounded = true };
        m.Step(now, input);
        var svc = ServerSwords;
        if (m.Began)
        {
            if (m.Move == SwordMove.Dodge)
            {
                var away = (body.GlobalPosition - bot.DodgeAway) with { Y = 0 };
                body.SwordDash((away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back) * 13f);
                svc?.ServerAct(bot.Id, SwordMove.Dodge, body.Rotation.Y);
            }
            else
            {
                body.LookYaw = yaw;
                body.SquareToView = true;
                bot.SwordTraced = false;
                svc?.ServerAct(bot.Id, m.Move, yaw, SwordPoses.Code(0, (int)m.Move + bot.SwordSwings++));
            }
        }
        if (svc != null && SwordRules.IsBlow(m.Move))
        {
            // the blade goes live: reach and cone against what the server holds; once more late in the window
            double into = m.Into(now);
            var d = SwordRules.Def(m.Move);
            if (m.ActiveBegan) { bot.SwordTraced = false; svc.ServerTrace(bot.Id, m.Move, yaw); }
            else if (m.Live(now) && !bot.SwordTraced && into >= d.Startup + d.Active * 0.6)
            {
                bot.SwordTraced = true;
                svc.ServerTrace(bot.Id, m.Move, yaw);
            }
        }
    }

    private static SwordService? ServerSwords => BotDirector.Instance?.Swords;

    /// <summary>The director, every frame: a use that has run its time applies (the item is spent then, not before).</summary>
    public static void Tick(Bot bot, double now)
    {
        SwordTick(bot, now);
        if (bot.UseUntil <= 0 || now < bot.UseUntil) return;
        var item = bot.UseItem;
        bot.UseUntil = 0;
        bot.Body.ItemAction = 0;
        int slot = Enumerable.Range(0, Inventory.Size).FirstOrDefault(i => bot.Inventory[i].Id == item && !bot.Inventory[i].IsEmpty, -1);
        if (slot < 0 || ItemDefs.Get(item) is not { } def) return;
        bool done = def.Use == ItemUse.Armor ? bot.Body.AddArmor(FootPlayer.MaxArmor) : bot.Body.Heal(def.Heal);
        if (done) bot.Inventory.TakeOne(slot);
    }

    /// <summary>Firing or switching cancels a use under way; the item is kept.</summary>
    public static void Cancel(Bot bot)
    {
        if (bot.UseUntil <= 0) return;
        bot.UseUntil = 0;
        bot.Body.ItemAction = 0;
    }

    /// <summary>Gives a bot a weapon and rounds for it (<c>/bot arm</c>, a mode's kit).</summary>
    public static void Arm(Bot bot, ItemId weapon, int rounds)
    {
        if (weapon == ItemId.Sword)
        {
            int free = Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(i => bot.Inventory[i].IsEmpty, 1);
            bot.Inventory.Put(free, new ItemStack(weapon, 1));
            bot.Inventory.Select(free);
            bot.Body.HeldItemId = (int)weapon;
            return;
        }
        if (Weapons.Get(weapon) is not { } w) return;
        int slot = Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(i => bot.Inventory[i].IsEmpty, 1);
        bot.Inventory.Put(slot, new ItemStack(weapon, 1));
        if (w.Ammo != ItemId.None && rounds > 0) bot.Inventory.Add(w.Ammo, rounds);
        bot.Inventory.Select(slot);
        bot.Body.HeldItemId = (int)weapon;
    }

    private static ItemEvents? ItemEventsServer => BotDirector.Instance?.GetParent()?.GetNodeOrNull<ItemEvents>(ItemEvents.NodeName);
}
