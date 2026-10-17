using System;

namespace UnitSport.Bots;

// Plain C#, no Godot: linked into the unit tests (BrTacticsTests).

/// <summary>What a Battle Royale bot chooses to do next (#837): one goal at a time, re-chosen every thought.</summary>
public enum BrGoal { Roam, Loot, Fight, Finish, Heal, Vest, Zone, Investigate }

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
    float LootGreed);

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

        // loot: a gun first, then rounds, then whatever greed says
        if (s.CrateDistance <= CrateDraw)
        {
            if (!s.HasGun) Offer(BrGoal.Loot, 62f);
            else if (s.Rounds < 20) Offer(BrGoal.Loot, 45f);
            else Offer(BrGoal.Loot, 15f + 25f * s.LootGreed);
        }

        // a shot heard: the bold go and look
        if (s.HasGun && s.HeardDistance <= EarDraw) Offer(BrGoal.Investigate, 30f + 25f * s.Boldness);
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
