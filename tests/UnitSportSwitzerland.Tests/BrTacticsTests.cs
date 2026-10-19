using UnitSport.Bots;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A Battle Royale bot's choices (#837, src/Bots/BrTactics.cs, linked in).</summary>
public class BrTacticsTests
{
    private const float None = float.PositiveInfinity;

    private static BrSituation Calm(bool gun = true) => new(
        Health: 100, Armor: 0, HasGun: gun, Rounds: gun ? 30 : 0, GunRange: 80, Meds: 2, HasVest: false,
        EnemyDistance: None, DownedEnemyDistance: None, HeardDistance: None, HurtRecently: false,
        SecondsSinceEnemy: 60, OutsideBy: 0, ZoneRunNeeded: 0, CrateDistance: None, Boldness: 0.5f, LootGreed: 0.5f);

    [Fact]
    public void With_nothing_to_do_it_roams() => Assert.Equal(BrGoal.Roam, BrTactics.Choose(Calm()));

    [Fact]
    public void Unarmed_it_goes_for_the_crate() =>
        Assert.Equal(BrGoal.Loot, BrTactics.Choose(Calm(gun: false) with { CrateDistance = 60 }));

    [Fact]
    public void An_enemy_in_reach_beats_loot_and_healing() =>
        Assert.Equal(BrGoal.Fight, BrTactics.Choose(Calm() with { EnemyDistance = 40, CrateDistance = 10, Health = 50 }));

    [Fact]
    public void Out_of_its_guns_reach_and_unhurt_it_does_not_charge() =>
        Assert.NotEqual(BrGoal.Fight, BrTactics.Choose(Calm() with { EnemyDistance = 300 }));

    [Fact]
    public void Unarmed_it_rushes_only_close() =>
        Assert.Equal(BrGoal.Fight, BrTactics.Choose(Calm(gun: false) with { EnemyDistance = 10 }));

    [Fact]
    public void The_zone_hurting_beats_everything()
    {
        var s = Calm() with { OutsideBy = 50, EnemyDistance = 20, DownedEnemyDistance = 5 };
        Assert.Equal(BrGoal.Zone, BrTactics.Choose(s));
    }

    [Fact]
    public void A_downed_enemy_is_finished() =>
        Assert.Equal(BrGoal.Finish, BrTactics.Choose(Calm() with { DownedEnemyDistance = 15 }));

    [Fact]
    public void It_bandages_only_once_it_feels_safe()
    {
        Assert.Equal(BrGoal.Heal, BrTactics.Choose(Calm() with { Health = 30 }));
        Assert.NotEqual(BrGoal.Heal, BrTactics.Choose(Calm() with { Health = 30, SecondsSinceEnemy = 1 }));
        Assert.NotEqual(BrGoal.Heal, BrTactics.Choose(Calm() with { Health = 30, Meds = 0 }));
    }

    [Fact]
    public void A_vest_goes_on_when_safe() =>
        Assert.Equal(BrGoal.Vest, BrTactics.Choose(Calm() with { HasVest = true }));

    [Fact]
    public void A_shot_heard_draws_it_with_a_gun_only()
    {
        Assert.Equal(BrGoal.Investigate, BrTactics.Choose(Calm() with { HeardDistance = 150 }));
        Assert.Equal(BrGoal.Roam, BrTactics.Choose(Calm(gun: false) with { HeardDistance = 150 }));
    }

    [Fact]
    public void It_sticks_to_what_it_is_doing_unless_something_clearly_better_comes_up()
    {
        // a crate a little further: roaming does not drop it for a near-equal score, looting keeps it
        var s = Calm() with { CrateDistance = 100, LootGreed = 0f };   // loot 15 vs roam 10
        Assert.Equal(BrGoal.Loot, BrTactics.Choose(s));
        Assert.Equal(BrGoal.Loot, BrTactics.Choose(s, BrGoal.Loot));                   // once looting, it keeps looting
        Assert.Equal(BrGoal.Roam, BrTactics.Choose(s, BrGoal.Roam));                   // 15 does not beat 10 + 12
        // the zone, about to cut it off, beats looting even with looting's stickiness
        Assert.Equal(BrGoal.Zone, BrTactics.Choose(s with { ZoneRunNeeded = 300, HasGun = false }, BrGoal.Loot));
        // an enemy in reach beats anything sticky but the zone hurting
        Assert.Equal(BrGoal.Fight, BrTactics.Choose(s with { EnemyDistance = 30 }, BrGoal.Heal));
    }

    [Fact]
    public void Aim_widens_with_distance_and_speed_and_settles_with_time()
    {
        float near = BrTactics.AimError(2f, 25, 0, 2), far = BrTactics.AimError(2f, 200, 0, 2);
        Assert.True(far > near);
        Assert.True(BrTactics.AimError(2f, 50, 6, 2) > BrTactics.AimError(2f, 50, 0, 2));
        Assert.True(BrTactics.AimError(2f, 50, 0, 0) > BrTactics.AimError(2f, 50, 0, 2));
        Assert.Equal(2f, BrTactics.AimError(2f, 50, 0, 2), 3);
    }

    [Fact]
    public void The_zone_calls_when_the_walk_would_not_make_it()
    {
        Assert.Equal(0f, BrTactics.ZoneRunNeeded(0, 10));
        Assert.Equal(0f, BrTactics.ZoneRunNeeded(100, 200));
        Assert.Equal(400f, BrTactics.ZoneRunNeeded(400, 100));
    }

    // ---- squads (#837 phase 4) ----

    [Fact]
    public void A_downed_mate_is_revived_once_no_enemy_is_close() =>
        Assert.Equal(BrGoal.Revive, BrTactics.Choose(Calm() with { MateDownedDistance = 80, CrateDistance = 20, LeaderDistance = 100 }));

    [Fact]
    public void An_enemy_close_comes_before_a_revive()
    {
        Assert.Equal(BrGoal.Fight, BrTactics.Choose(Calm() with { MateDownedDistance = 30, EnemyDistance = 25, SecondsSinceEnemy = 0 }));
        Assert.NotEqual(BrGoal.Revive, BrTactics.Choose(Calm() with { MateDownedDistance = 30, SecondsSinceEnemy = 1 }));
    }

    [Fact]
    public void A_mates_fight_is_joined_over_loot() =>
        Assert.Equal(BrGoal.Assist, BrTactics.Choose(Calm() with { MateFightDistance = 120, CrateDistance = 30 }));

    [Fact]
    public void The_zone_comes_before_the_team() =>
        Assert.Equal(BrGoal.Zone, BrTactics.Choose(Calm() with { ZoneRunNeeded = 300, MateDownedDistance = 50, PingDistance = 200 }));

    [Fact]
    public void A_humans_ping_and_a_dogtag_beat_roaming()
    {
        Assert.Equal(BrGoal.Ping, BrTactics.Choose(Calm() with { PingDistance = 150 }));
        Assert.Equal(BrGoal.Recall, BrTactics.Choose(Calm() with { CarriesTag = true, StopDistance = 400 }));
        Assert.Equal(BrGoal.Roam, BrTactics.Choose(Calm() with { CarriesTag = true }));   // no stop known
    }

    [Fact]
    public void A_dogtag_goes_to_the_stop_before_more_loot() =>
        Assert.Equal(BrGoal.Recall, BrTactics.Choose(Calm(gun: false) with { CarriesTag = true, StopDistance = 600, CrateDistance = 30 }));

    [Fact]
    public void Far_from_its_leader_it_goes_back_but_a_greedy_crate_close_by_still_wins()
    {
        Assert.Equal(BrGoal.Regroup, BrTactics.Choose(Calm() with { LeaderDistance = 90 }));
        Assert.Equal(BrGoal.Roam, BrTactics.Choose(Calm() with { LeaderDistance = 30 }));
        Assert.Equal(BrGoal.Loot, BrTactics.Choose(Calm(gun: false) with { LeaderDistance = 90, CrateDistance = 40 }));
    }
}
