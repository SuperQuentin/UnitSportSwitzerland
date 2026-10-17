using System;
using System.Linq;
using Godot;
using UnitSport.Items;

namespace UnitSport.Bots;

/// <summary>Where a bot is walking and how it is getting on (<see cref="BotSkills.MoveTo"/>).</summary>
public sealed class BotNav
{
    public double CheckAt;
    public int Stuck;
    public bool JumpHeld;
    /// <summary>The goal of the walk under way, and the nearest it has come to it.</summary>
    public Vector3 Target;
    public float Best;
    /// <summary>A wall follow (<see cref="BotSkills.MoveTo"/>): on, which side the wall is (+1 left), the heading, the distance where it began.</summary>
    public bool Following;
    public int Side = 1;
    public Vector3 FollowDir;
    public float HitDist;
    /// <summary>A way round chosen by a scan, followed until <see cref="SteerUntil"/>.</summary>
    public Vector3 Steer;
    public double SteerUntil;
    /// <summary>The longest clear way the last scan found, m (for <c>--botlog</c>: near 0 = boxed in).</summary>
    public float LastOpen = -1;
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
    /// Walks towards <paramref name="target"/> (world). Straight there while the way is open; a low wall
    /// is jumped (the body mantles a ledge it jumps at). A wall it cannot jump starts a wall follow: it
    /// keeps the wall on one side (the more open side first) until the straight way is open again and it
    /// is nearer the goal than where it met the wall, the "bug" way round any obstacle. Picking the most
    /// open way afresh each time shuttled between a wall and the way back (seen: 15 m to and fro for a
    /// minute while the zone came). Headway is judged towards the goal over 4 s, so shuttling counts as
    /// stuck: the follow switches sides, and a bot still stuck takes any open way out for a few seconds.
    /// True once within <paramref name="arrive"/> m (flat); the wish is then zero.
    /// </summary>
    public static bool MoveTo(Bot bot, Vector3 target, bool run, float arrive, PhysicsDirectSpaceState3D space, double now)
    {
        var body = bot.Body;
        var nav = bot.Nav;
        var pos = body.GlobalPosition;
        var to = new Vector3(target.X - pos.X, 0, target.Z - pos.Z);
        float dist = to.Length();
        bot.Intent.Jump = false;
        // a new goal (a street route's next waypoint is one too): a fresh walk; its stuck count stays, so a
        // bot that keeps failing waypoint after waypoint still ends up taking a way out. Judged against the
        // old goal, a waypoint 5 m on read as no headway and sent it off on an escape: a zig-zag.
        if (nav.Target.DistanceSquaredTo(target) > 1f)
        {
            nav.Target = target;
            nav.Following = false;
            nav.Best = dist;
            nav.CheckAt = now + CheckEvery;
        }
        if (dist <= arrive)
        {
            bot.Intent.Wish = Vector3.Zero;
            nav.Following = false;
            nav.Best = dist;
            nav.CheckAt = now + CheckEvery;
            return true;
        }
        var dir = to / dist;

        // headway towards the goal, every few seconds: none is being stuck (shuttling included)
        if (now >= nav.CheckAt)
        {
            if (nav.Best - dist < Progress)
            {
                nav.Stuck++;
                // boxed in under a roof: only the late collision put it there (a building built round it after
                // it landed); out to the nearest open ground, as the void rescue puts a body back on the ground
                if (nav.Stuck >= 4 && nav.LastOpen >= 0f && nav.LastOpen < 3f && Roofed(space, body, pos) && OpenGroundNear(space, body, pos) is { } outside)
                {
                    GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" boxed in under a roof at {body.Global}: out to {outside}");
                    body.DebugLaunch(outside, Vector3.Zero);
                    nav.Stuck = 0;
                    nav.LastOpen = -1;
                }
                else if (nav.Following && nav.Stuck % 2 == 0)
                {
                    nav.Side = -nav.Side;   // the wall the other way round
                    nav.FollowDir = -nav.FollowDir;
                }
                else if (nav.Stuck >= 3)
                {
                    // still nowhere: any open way out of here for a while, then try again
                    nav.Steer = MostOpen(space, body, pos, dir, goalWeight: 0f);
                    nav.LastOpen = LastScanOpen;
                    nav.SteerUntil = now + Detour + Math.Min(nav.Stuck, 3);
                    nav.Following = false;
                }
            }
            else nav.Stuck = 0;
            nav.Best = Math.Min(nav.Best, dist);
            nav.CheckAt = now + CheckEvery * 2;
        }

        if (now < nav.SteerUntil) dir = nav.Steer;
        else if (nav.Following)
        {
            // off the wall once the straight way is open and it is nearer than where it met the wall
            if (!Blocked(space, body, pos, dir, Knee) && dist < nav.HitDist - 1f) nav.Following = false;
            else dir = nav.FollowDir = Hug(space, body, pos, nav.FollowDir, nav.Side);
        }
        else if (Blocked(space, body, pos, dir, Knee))
        {
            if (!Blocked(space, body, pos, dir, Head) && body.IsOnFloor() && !nav.JumpHeld) bot.Intent.Jump = true;   // a jump at a ledge is a mantle
            else
            {
                // the wall on the side that is more open the other way: follow it from here
                var open = MostOpen(space, body, pos, dir, goalWeight: 1f);
                nav.LastOpen = LastScanOpen;
                nav.Side = open.Cross(dir).Y > 0f ? -1 : 1;
                nav.Following = true;
                nav.HitDist = dist;
                dir = nav.FollowDir = open;
            }
        }
        nav.JumpHeld = bot.Intent.Jump;   // a press, then a release: the body jumps on the edge
        bot.Intent.Wish = dir;
        bot.Intent.Run = run;
        return false;
    }

    /// <summary>
    /// The next heading along a wall kept on <paramref name="side"/> (+1 = on its left): from turned 75°
    /// towards the wall, swept away from it, the first open one. Turning towards the wall first keeps
    /// contact round outside corners; sweeping away gets round inside ones.
    /// </summary>
    private static Vector3 Hug(PhysicsDirectSpaceState3D space, Player.FootPlayer body, Vector3 pos, Vector3 heading, int side)
    {
        for (int k = 0; k <= 10; k++)
        {
            var cand = heading.Rotated(Vector3.Up, Mathf.DegToRad(side * (75f - k * 20f)));
            if (!Blocked(space, body, pos, cand, Knee)) return cand;
        }
        return -heading;
    }

    [ThreadStatic] private static float LastScanOpen;

    /// <summary>Angles a scan tries, degrees off the wanted way.</summary>
    private static readonly float[] ScanAngles = { 30, -30, 60, -60, 90, -90, 120, -120, 150, -150, 180 };
    /// <summary>How far a scan looks down each way.</summary>
    private const float ScanReach = 6f;

    /// <summary>
    /// The most open way round from here: each scanned way scored by how far it is clear (up to
    /// <see cref="ScanReach"/>), times how much it still heads towards <paramref name="dir"/>
    /// (<paramref name="goalWeight"/> 0 = any way out).
    /// </summary>
    private static Vector3 MostOpen(PhysicsDirectSpaceState3D space, Player.FootPlayer body, Vector3 pos, Vector3 dir, float goalWeight)
    {
        var best = -dir;
        float bestScore = float.MinValue, open = 0f;
        var from = pos + Vector3.Up * Knee;
        foreach (float deg in ScanAngles)
        {
            var way = dir.Rotated(Vector3.Up, Mathf.DegToRad(deg));
            var hit = BotRays.Cast(space, from, from + way * ScanReach, body.GetRid());
            float clear = hit.Count == 0 || hit["collider"].AsGodotObject() is Player.FootPlayer ? ScanReach
                : from.DistanceTo(hit["position"].AsVector3());
            open = Mathf.Max(open, clear);
            float score = clear * (1f + goalWeight * (way.Dot(dir) + 1f));
            if (score > bestScore) { bestScore = score; best = way; }
        }
        LastScanOpen = open;
        return best;
    }

    /// <summary>Something solid over the head (a roof, a ceiling) within 30 m.</summary>
    private static bool Roofed(PhysicsDirectSpaceState3D space, Player.FootPlayer body, Vector3 pos)
    {
        var hit = BotRays.Cast(space, pos + Vector3.Up * 2.2f, pos + Vector3.Up * 30f, body.GetRid());
        return hit.Count > 0 && hit["collider"].AsGodotObject() is not Player.FootPlayer;
    }

    /// <summary>
    /// The nearest spot with open sky and ground under it, on rings from 6 m to 40 m round <paramref name="pos"/>:
    /// a ray down from 40 m up hits the ground there first, not a roof. Null when there is none.
    /// </summary>
    private static Vector3? OpenGroundNear(PhysicsDirectSpaceState3D space, Player.FootPlayer body, Vector3 pos)
    {
        for (float r = 6f; r <= 40f; r += 4f)
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.Tau / 12f;
                var at = pos + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                var hit = BotRays.Cast(space, at + Vector3.Up * 40f, at + Vector3.Down * 20f, body.GetRid());
                if (hit.Count == 0 || hit["collider"].AsGodotObject() is Player.FootPlayer) continue;
                var ground = hit["position"].AsVector3();
                // the ground, not a roof: within a few metres of where the bot stands, and level enough
                if (Mathf.Abs(ground.Y - pos.Y) < 4f && hit["normal"].AsVector3().Y > 0.7f) return ground + Vector3.Up * 0.2f;
            }
        return null;
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
