using System;

namespace UnitSport.Bots;

// Plain C#, no Godot: linked into the unit tests (BrTacticsTests).

/// <summary>What a Battle Royale bot chooses to do next (#837): one goal at a time, re-chosen every thought.</summary>
public enum BrGoal { Roam, Loot, Fight, Finish, Heal, Vest, Zone, Investigate, Regroup, Revive, Recall, Ping, Assist }

/// <summary>
/// What a bot knows when it chooses (#837), filled by <c>BrBrain</c> from its senses, its pack and the
/// zone, never from the world directly. Distances in metres; <c>float.PositiveInfinity</c> for none.
/// </summary>
public readonly record struct BrSituation(
    float Health,
    float Armor,
    bool HasGun,
    int Rounds,
    float GunRange,
    int Meds,
    bool HasVest,
    float EnemyDistance,
    float DownedEnemyDistance,
    float HeardDistance,
    bool HurtRecently,
    float SecondsSinceEnemy,
    float OutsideBy,
    float ZoneRunNeeded,
    float CrateDistance,
    float Boldness,
    float LootGreed,
    // squads (#837 phase 4); the defaults are solo
    float LeaderDistance = float.PositiveInfinity,
    float MateDownedDistance = float.PositiveInfinity,
    float MateFightDistance = float.PositiveInfinity,
    bool CarriesTag = false,
    float StopDistance = float.PositiveInfinity,
    float PingDistance = float.PositiveInfinity,
    bool CrateHasTag = false,
    float EnemyNearMate = float.PositiveInfinity);

/// <summary>
/// The Battle Royale bot's choices as scores (#837): the highest goal wins. Pure and unit-tested
/// (<c>BrTacticsTests</c>), so a change of mind is a change here with a test, not a branch buried in
/// the brain. The scores read as priorities:
/// <list type="bullet">
/// <item>the zone first once it hurts or is about to cut the bot off (100, 85);</item>
/// <item>a visible enemy in reach (70 to 90, bolder higher); a downed one is finished (78);</item>
/// <item>healing when hurt and no enemy for a few seconds (65 up), a vest when it has none on (60);</item>
/// <item>loot when unarmed (62) or short of rounds (45), else as greedy as it is;</item>
/// <item>a shot heard draws the bold (30 to 55); else roam towards the zone (10).</item>
/// <item>squads: a downed mate within 120 m revived when no enemy is within 40 m of either (75), a mate's fight joined (60 up), a human's
/// ping followed (55), a dogtag taken to a stop (90: before loot and an early run for the circle, offered only
/// while the zone on the way costs less than the health it can spare), the leader rejoined when far (50 to 58).</item>
/// </list>
/// </summary>
public static class BrTactics
{
    /// <summary>Seconds with no enemy in sight before a bot feels safe to bandage.</summary>
    public const float SafeAfter = 4f;
    /// <summary>A knife's fight: an unarmed bot rushes only this close.</summary>
    public const float KnifeRush = 18f;
    /// <summary>How far a crate draws a bot.</summary>
    public const float CrateDraw = 120f;
    /// <summary>A shot this far is worth a look.</summary>
    public const float EarDraw = 300f;
    /// <summary>
    /// What the goal it is already pursuing is worth on top: a bot sticks to what it is doing unless
    /// something clearly better comes up (seen without it: loot and roam swapped every thought as a crate
    /// came in and out of sight, zone and loot as a margin flickered, and the bot ran nowhere).
    /// </summary>
    public const float Stickiness = 12f;
    /// <summary>Further than this from its team's leader, a squad bot goes back.</summary>
    public const float RegroupFrom = 60f;
    /// <summary>
    /// A downed mate this close is worth the walk: a downed player bleeds out in 30 s, which is about 20 s of
    /// running (120 m) and the 5 s hold.
    /// </summary>
    public const float ReviveDraw = 120f;
    /// <summary>
    /// No enemy in sight this close to the bot or to the downed mate before it goes to revive. Only close ones:
    /// waiting until no enemy at all had been seen for a few seconds (one 140 m off kept it waiting) let every
    /// downed mate of a match bleed out (18 downs, no revive).
    /// </summary>
    public const float ReviveClear = 40f;
    /// <summary>A fallen mate's box with their dogtag draws from this far (the team knows where they fell).</summary>
    public const float TagDraw = 400f;
    /// <summary>A mate's fight this close is joined.</summary>
    public const float AssistDraw = 200f;

    /// <param name="current">The goal it is pursuing now (it keeps it unless another beats it by <see cref="Stickiness"/>).</param>
    public static BrGoal Choose(in BrSituation s, BrGoal? current = null)
    {
        var best = BrGoal.Roam;
        float score = current == BrGoal.Roam ? 10f + Stickiness : 10f;
        void Offer(BrGoal goal, float value)
        {
            if (goal == current) value += Stickiness;
            if (value > score) { best = goal; score = value; }
        }

        // the zone: hurting now, or about to cut the bot off
        if (s.OutsideBy > 0f) Offer(BrGoal.Zone, 100f);
        else if (s.ZoneRunNeeded > 0f) Offer(BrGoal.Zone, 85f);

        // a fight it can take: in its gun's reach, or close enough to rush with a blade
        if (s.EnemyDistance < float.PositiveInfinity)
        {
            if (s.HasGun && s.Rounds > 0 && s.EnemyDistance <= s.GunRange * 1.2f) Offer(BrGoal.Fight, 70f + 20f * s.Boldness);
            else if (s.EnemyDistance <= KnifeRush) Offer(BrGoal.Fight, 60f + 25f * s.Boldness);
            else if (s.HurtRecently) Offer(BrGoal.Fight, 55f);   // shot at from afar: face it, close in or take cover
        }
        if (s.DownedEnemyDistance <= 40f) Offer(BrGoal.Finish, 78f);

        // patching up, once nobody has been in sight for a few seconds
        bool safe = s.EnemyDistance == float.PositiveInfinity && s.SecondsSinceEnemy >= SafeAfter && !s.HurtRecently;
        if (safe && s.Meds > 0 && s.Health < 60f) Offer(BrGoal.Heal, 65f + (60f - s.Health) * 0.3f);
        if (safe && s.HasVest && s.Armor < 1f) Offer(BrGoal.Vest, 60f);

        // loot: a fallen mate's dogtag first (squads), a gun, then rounds, then whatever greed says
        if (s.CrateHasTag && s.CrateDistance <= TagDraw) Offer(BrGoal.Loot, 80f);
        if (s.CrateDistance <= CrateDraw)
        {
            if (!s.HasGun) Offer(BrGoal.Loot, 62f);
            else if (s.Rounds < 20) Offer(BrGoal.Loot, 45f);
            else Offer(BrGoal.Loot, 15f + 25f * s.LootGreed);
        }

        // a shot heard: the bold go and look
        if (s.HasGun && s.HeardDistance <= EarDraw) Offer(BrGoal.Investigate, 30f + 25f * s.Boldness);

        // squads: a downed mate first (once nobody is close), a mate's fight, a human's ping, a dogtag to a stop,
        // and back to the leader when far
        if (s.MateDownedDistance <= ReviveDraw && s.EnemyDistance > ReviveClear && s.EnemyNearMate > ReviveClear && !s.HurtRecently)
            Offer(BrGoal.Revive, 75f);
        if (s.MateFightDistance <= AssistDraw && (s.HasGun || s.MateFightDistance <= KnifeRush * 2f))
            Offer(BrGoal.Assist, 60f + 10f * s.Boldness);
        if (s.PingDistance < float.PositiveInfinity) Offer(BrGoal.Ping, 55f);
        // a mate's life before loot and before running early for the circle: a stop is only offered when there is
        // time to get there and back inside (BrManager.NearestStop); only a zone that hurts comes first
        // (90, and 102 with stickiness: on through a zone that hurts, until the stop costs too much health)
        if (s.CarriesTag && s.StopDistance < float.PositiveInfinity) Offer(BrGoal.Recall, 90f);
        if (s.LeaderDistance > RegroupFrom && s.LeaderDistance < float.PositiveInfinity)
            Offer(BrGoal.Regroup, s.LeaderDistance > RegroupFrom * 2.5f ? 58f : 50f);
        return best;
    }

    /// <summary>
    /// How far off a bot aims, degrees (#837): its profile's error at 50 m, wider with distance (the
    /// square root: far shots are harder, not hopeless), wider again while the target moves fast, and
    /// tighter as it keeps the target in sight (settles over two seconds).
    /// </summary>
    public static float AimError(float errorAt50, float distance, float targetSpeed, float secondsOnTarget)
    {
        float range = MathF.Sqrt(MathF.Max(distance, 5f) / 50f);
        float moving = 1f + Math.Clamp(targetSpeed / 6f, 0f, 1f);
        float settle = 1.6f - 0.6f * Math.Clamp(secondsOnTarget / 2f, 0f, 1f);
        return errorAt50 * range * moving * settle;
    }

    /// <summary>
    /// Whether the zone is about to cut a bot off (#837): the seconds it would take to walk into the next
    /// circle against the time before that circle closes, with a margin. 0 when there is time, else the
    /// distance still to cover.
    /// </summary>
    public static float ZoneRunNeeded(float distanceToNextEdge, float secondsLeft, float speed = 4.5f)
    {
        if (distanceToNextEdge <= 0f) return 0f;
        // slower than its run (5.8 m/s) and a long margin: walls, detours and a fight on the way
        return distanceToNextEdge / speed * 1.5f + 30f >= secondsLeft ? distanceToNextEdge : 0f;
    }
}
