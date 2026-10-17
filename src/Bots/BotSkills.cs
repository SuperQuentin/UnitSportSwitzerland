using System;
using System.Linq;
using Godot;
using UnitSport.Items;

namespace UnitSport.Bots;

/// <summary>Where a bot is walking and how it is getting on (<see cref="BotSkills.MoveTo"/>).</summary>
public sealed class BotNav
{
    public Vector3 From;
    public double CheckAt;
    public double DetourUntil;
    public int DetourSide = 1;
    public int Stuck;
    public bool JumpHeld;
}

/// <summary>
/// What a bot's brain chains (#837), shared by every mode: walking somewhere, picking a crate over,
/// deciding what is worth carrying. Each writes the bot's <see cref="Bot.Intent"/> or its pack through
/// the server paths a player's requests use, never the world directly.
/// </summary>
public static class BotSkills
{
    /// <summary>A ray this long, at knee and head height, tells a wall from a step.</summary>
    private const float Probe = 2.2f, Knee = 0.45f, Head = 1.7f;
    /// <summary>Making less than this in <see cref="CheckEvery"/> while walking is being stuck.</summary>
    private const float Progress = 0.8f;
    private const double CheckEvery = 2.0, Detour = 2.5;

    /// <summary>
    /// Walks towards <paramref name="target"/> (world): straight there, round what stands in the way
    /// (a probe left and right of a blocked line), over what is low (a jump: the body mantles a ledge it
    /// jumps at), and off at an angle for a while when it makes no headway, alternating sides. True once
    /// within <paramref name="arrive"/> m (flat); the wish is then zero.
    /// </summary>
    public static bool MoveTo(Bot bot, Vector3 target, bool run, float arrive, PhysicsDirectSpaceState3D space, double now)
    {
        var body = bot.Body;
        var nav = bot.Nav;
        var pos = body.GlobalPosition;
        var to = new Vector3(target.X - pos.X, 0, target.Z - pos.Z);
        float dist = to.Length();
        bot.Intent.Jump = false;
        if (dist <= arrive)
        {
            bot.Intent.Wish = Vector3.Zero;
            nav.CheckAt = now + CheckEvery;
            nav.From = pos;
            return true;
        }
        var dir = to / dist;

        // no headway for a while: off at an angle, the other side next time
        if (now >= nav.CheckAt)
        {
            if (nav.CheckAt > 0 && new Vector3(pos.X - nav.From.X, 0, pos.Z - nav.From.Z).Length() < Progress)
            {
                nav.Stuck++;
                nav.DetourSide = -nav.DetourSide;
                nav.DetourUntil = now + Detour;
            }
            else nav.Stuck = 0;
            nav.From = pos;
            nav.CheckAt = now + CheckEvery;
        }
        if (now < nav.DetourUntil) dir = dir.Rotated(Vector3.Up, nav.DetourSide * Mathf.DegToRad(80f));

        // a wall ahead at the knee: round it if a side is clear, over it if it is low
        if (Blocked(space, body, pos, dir, Knee))
        {
            bool low = !Blocked(space, body, pos, dir, Head);
            var left = dir.Rotated(Vector3.Up, Mathf.DegToRad(45f));
            var right = dir.Rotated(Vector3.Up, Mathf.DegToRad(-45f));
            if (low && body.IsOnFloor() && !nav.JumpHeld) bot.Intent.Jump = true;   // a jump at a ledge is a mantle
            else if (!Blocked(space, body, pos, left, Knee)) dir = left;
            else if (!Blocked(space, body, pos, right, Knee)) dir = right;
        }
        nav.JumpHeld = bot.Intent.Jump;   // a press, then a release: the body jumps on the edge
        bot.Intent.Wish = dir;
        bot.Intent.Run = run;
        return false;
    }

    private static bool Blocked(PhysicsDirectSpaceState3D space, Player.FootPlayer body, Vector3 pos, Vector3 dir, float height)
    {
        var from = pos + Vector3.Up * height;
        var hit = BotRays.Cast(space, from, from + dir * Probe, body.GetRid());
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is Player.FootPlayer) return false;
        // a slope it can walk is not a wall
        return hit["normal"].AsVector3().Y < 0.6f;
    }

    /// <summary>
    /// Whether a bot wants this stack (#837): a gun it does not have or better than its best, rounds for
    /// a gun it carries (or any rounds while unarmed), meds, a vest, a blade when it has none. The rest
    /// stays for the players.
    /// </summary>
    public static bool Wants(Bot bot, ItemStack stack)
    {
        var inv = bot.Inventory;
        if (Weapons.Get(stack.Id) is { } w)
        {
            if (w.Melee) return !Enumerable.Range(0, Inventory.Size).Any(i => Weapons.Get(inv[i].Id) is { Melee: true });
            return !inv.Contains(stack.Id) && Enumerable.Range(0, Inventory.HotbarSize).Any(i => inv[i].IsEmpty || Weapons.Get(inv[i].Id) is { Melee: false } g && g.Damage * g.Pellets / g.Interval < w.Damage * w.Pellets / w.Interval);
        }
        if (stack.Id is ItemId.Bandage or ItemId.FirstAidKit) return inv.CountPlain(stack.Id) < 6;
        if (stack.Id == ItemId.ArmorVest) return !inv.Contains(ItemId.ArmorVest) && bot.Body.Armor < 1f;
        // rounds: for a gun it carries, or any while it has none
        bool armed = Enumerable.Range(0, Inventory.Size).Any(i => Weapons.Get(inv[i].Id) is { Melee: false });
        return Enumerable.Range(0, Inventory.Size).Any(i => Weapons.Get(inv[i].Id) is { Melee: false } g && g.Ammo == stack.Id)
            || !armed && Weapons.All.Any(g => g.Ammo == stack.Id);
    }

    /// <summary>
    /// Takes one wanted stack out of a crate it stands at, through <c>BrCrates.TryTake</c> (a player's
    /// checks). A gun goes on the hotbar. True when it took something; false when nothing there is
    /// wanted any more (the crate is done for this bot).
    /// </summary>
    public static bool TakeOne(Bot bot, BattleRoyale.BrCrates crates, BattleRoyale.Crate crate)
    {
        var stacks = crate.Stacks();
        for (int i = 0; i < stacks.Count; i++)
        {
            var s = stacks[i];
            if (!Wants(bot, s) || bot.Inventory.Room(s.Id) < s.Count) continue;
            if (crates.TryTake(bot.Id, crate.Id, i, s.Id, s.Count) != BattleRoyale.BrCrates.TakeResult.Taken) return false;
            if (Weapons.Get(s.Id) is { } && Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(h => bot.Inventory[h].IsEmpty, -1) is var free and >= 0)
                bot.Inventory.Put(free, s);
            else bot.Inventory.Add(s);
            GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" took {s.Count} {s.Id} from {crate.Label}");
            return true;
        }
        return false;
    }
}
