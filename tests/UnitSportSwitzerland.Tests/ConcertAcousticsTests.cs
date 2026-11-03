using Godot;
using UnitSport.Concert;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A stadium's sound, worked out (src/Concert/ConcertAcoustics.cs, linked in, #984).</summary>
public class ConcertAcousticsTests
{
    // an end stage at the north end of an 80 x 120 m seating front, its hang 12 m up facing south (+z)
    private static readonly PaSource Hang = new(new Vector3(-15f, 12f, -55f), new Vector3(0f, 0f, 1f));
    private static readonly Vector2 Front = new(40f, 60f), Outer = new(70f, 90f);

    [Fact]
    public void The_sound_is_late_by_its_distance_at_the_speed_of_sound()
    {
        var near = ConcertAcoustics.Hear(Hang, new Vector3(-15f, 12f, -21.7f), Outer);
        Assert.Equal(33.3f / 343f, near.Delay, 3);
        var far = ConcertAcoustics.Hear(Hang, new Vector3(0f, 15f, 75f), Outer);
        Assert.InRange(far.Delay, 0.37f, 0.40f);   // the far stand, ~131 m: the band 0.38 s after the lights
    }

    [Fact]
    public void A_line_array_loses_3_dB_a_doubling_close_and_6_far()
    {
        Assert.Equal(0f, ConcertAcoustics.ThrowDb(40f), 3);
        Assert.Equal(-3f, ConcertAcoustics.ThrowDb(80f), 3);
        Assert.Equal(-9f, ConcertAcoustics.ThrowDb(160f), 3);
        Assert.Equal(4f, ConcertAcoustics.ThrowDb(2f), 3);      // capped under the hang
        // Godot's own point-source falloff, which the speaker corrects to it
        Assert.Equal(-6.02f, ConcertAcoustics.PointDb(80f, 40f), 2);
        Assert.Equal(3f, ConcertAcoustics.PointDb(5f, 40f), 3);
    }

    [Fact]
    public void The_air_takes_the_highs_with_distance()
    {
        Assert.Equal(20000f, ConcertAcoustics.AirCutoffHz(5f));
        Assert.InRange(ConcertAcoustics.AirCutoffHz(50f), 7500f, 9000f);
        Assert.InRange(ConcertAcoustics.AirCutoffHz(150f), 4000f, 5000f);
        Assert.True(ConcertAcoustics.AirCutoffHz(300f) < ConcertAcoustics.AirCutoffHz(150f));
        Assert.Equal(1500f, ConcertAcoustics.AirCutoffHz(5000f));
    }

    [Fact]
    public void Behind_a_hang_is_quiet_and_dull_in_front_it_is_full()
    {
        var front = ConcertAcoustics.Directivity(Vector3.Back, new Vector3(0.3f, 0f, 1f));
        Assert.Equal(0f, front.Db);
        Assert.Equal(20000f, front.CutoffHz);
        var behind = ConcertAcoustics.Directivity(Vector3.Back, Vector3.Forward);
        Assert.Equal(-12f, behind.Db, 3);
        Assert.Equal(500f, behind.CutoffHz, 0);
        var side = ConcertAcoustics.Directivity(Vector3.Back, Vector3.Right);
        Assert.InRange(side.Db, -6f, -3f);
        // no facing (a round's average): all round
        Assert.Equal(0f, ConcertAcoustics.Directivity(Vector3.Zero, Vector3.Forward).Db);
    }

    [Fact]
    public void A_delay_tower_adds_level_and_highs_where_it_covers_and_never_a_second_arrival()
    {
        var withTower = Hang with { Tower = new Vector3(-15f, 9f, -5f), TowerFacing = Vector3.Back };
        var ear = new Vector3(-15f, 1.7f, 30f);
        var bare = ConcertAcoustics.Hear(Hang, ear, Outer);
        var helped = ConcertAcoustics.Hear(withTower, ear, Outer);
        Assert.Equal(bare.Delay, helped.Delay);              // timed to the mains
        Assert.True(helped.Db > bare.Db + 1f);
        Assert.True(helped.CutoffHz > bare.CutoffHz);
        // behind the tower, little changes
        var before = new Vector3(-15f, 1.7f, -30f);
        Assert.InRange(ConcertAcoustics.Hear(withTower, before, Outer).Db - ConcertAcoustics.Hear(Hang, before, Outer).Db, 0f, 1.5f);
    }

    [Fact]
    public void Outside_the_bowl_it_is_muffled_unless_a_wall_already_did_it()
    {
        var outside = new Vector3(0f, 1.7f, Outer.Y + 30f);
        var heard = ConcertAcoustics.Hear(Hang, outside, Outer);
        Assert.Equal(1f, heard.Outside);
        Assert.True(heard.CutoffHz <= ConcertAcoustics.OutsideCutoff + 1f);
        var walled = ConcertAcoustics.Hear(Hang, outside, Outer, blocked: 1f);
        Assert.Equal(0f, walled.Outside);
        Assert.True(walled.Db > heard.Db + 8f);
        // an open air has no bowl
        Assert.Equal(0f, ConcertAcoustics.Hear(Hang, outside, Vector2.Zero).Outside);
    }

    [Fact]
    public void The_stand_behind_slaps_back_late()
    {
        var echoes = new Echo[4];
        var ear = new Vector3(0f, 1.7f, 10f);
        int n = ConcertAcoustics.Echoes(new Vector3(0f, 12f, -55f), Vector3.Back, ear, Front, ConcertAcoustics.Stadium.ReflectDb, Vector3.Right, echoes);
        Assert.Equal(4, n);
        Assert.True(echoes[0].Db >= echoes[1].Db && echoes[1].Db >= echoes[2].Db);
        // the two side stands, twins at ~110 ms, make one tap from the middle; the far stand's front
        // 50 m behind (about 2 x 50 m more path, ~290 ms) the other
        Assert.Equal(2, ConcertAcoustics.Taps(echoes, out var first, out var second));
        Assert.InRange(first.DelayMs, 100f, 120f);
        Assert.InRange(first.Pan, -0.05f, 0.05f);
        Assert.InRange(second.DelayMs, 270f, 300f);
        Assert.InRange(second.Db, -30f, -9f);
        // the stand behind the stage, behind the hang: weakest
        Assert.True(echoes[3].Db < second.Db);
        // from outside the bowl: none; an open air: none
        Assert.Equal(0, ConcertAcoustics.Echoes(Vector3.Zero, Vector3.Back, new Vector3(0f, 0f, 100f), Front, -9f, Vector3.Right, echoes));
        Assert.Equal(0, ConcertAcoustics.Echoes(Vector3.Zero, Vector3.Back, ear, Vector2.Zero, -9f, Vector3.Right, echoes));
    }

    [Fact]
    public void Far_from_the_PA_there_is_more_room_and_an_arena_has_more_than_a_stadium()
    {
        Assert.True(ConcertAcoustics.Wet(ConcertAcoustics.Stadium, -12f) > ConcertAcoustics.Wet(ConcertAcoustics.Stadium, 0f));
        Assert.True(ConcertAcoustics.Wet(ConcertAcoustics.Stadium, -200f) <= 0.45f);
        Assert.True(ConcertAcoustics.Arena.Wet > ConcertAcoustics.Stadium.Wet && ConcertAcoustics.Stadium.Wet > ConcertAcoustics.OpenAir.Wet);
        Assert.True(ConcertAcoustics.Arena.Size > ConcertAcoustics.Stadium.Size && ConcertAcoustics.Stadium.Size > ConcertAcoustics.OpenAir.Size);
    }

    private static CrowdCue Song(float t, int section = 1, float build = 0f, float sinceDrop = float.PositiveInfinity) =>
        new(true, t, -1f, 0f, false, section, 0.7f, build, sinceDrop);

    [Fact]
    public void The_crowd_follows_the_show()
    {
        float bed = ConcertAcoustics.Crowd(Song(60f)).RoarDb;
        Assert.InRange(bed, -24f, -15f);
        Assert.True(ConcertAcoustics.Crowd(Song(60f, section: 3)).RoarDb > bed + 5f);           // singing along
        Assert.True(ConcertAcoustics.Crowd(Song(60f, build: 1f)).RoarDb > bed + 6f);            // a build lifts them
        Assert.True(ConcertAcoustics.Crowd(Song(60f, sinceDrop: 0.2f)).RoarDb > -5f);           // the drop
        Assert.Equal(-80f, ConcertAcoustics.Crowd(Song(60f)).ApplauseDb);                       // no clapping over a song
        Assert.True(ConcertAcoustics.Crowd(Song(0.5f)).RoarDb > bed);                           // they know this one
        var gap = new CrowdCue(true, -1f, 0.5f, 5.5f, false, 1, 0f, 0f, float.PositiveInfinity);
        var mix = ConcertAcoustics.Crowd(gap);
        Assert.True(mix.ApplauseDb > -6f && mix.RoarDb > -8f);                                   // the song ends: cheers
        Assert.True(ConcertAcoustics.Crowd(gap with { GapTime = 4f, GapLeft = 2f }).ApplauseDb < mix.ApplauseDb);
        Assert.True(ConcertAcoustics.Crowd(gap with { GapTime = 5.5f, GapLeft = 0.5f }).RoarDb > ConcertAcoustics.Crowd(gap with { GapTime = 4f, GapLeft = 2f }).RoarDb);
        Assert.True(ConcertAcoustics.Crowd(gap with { GapTime = 4f, Finale = true }).ApplauseDb > ConcertAcoustics.Crowd(gap with { GapTime = 4f }).ApplauseDb);
        Assert.Equal(new CrowdMix(-80f, -80f), ConcertAcoustics.Crowd(default));
    }

    [Fact]
    public void The_crowd_is_all_round_in_the_bowl_and_dull_outside()
    {
        Assert.Equal((0f, 20000f), ConcertAcoustics.CrowdAt(new Vector3(0f, 2f, 10f), Front, Outer));
        var (db, cut) = ConcertAcoustics.CrowdAt(new Vector3(0f, 2f, Outer.Y + 40f), Front, Outer);
        Assert.True(db < -15f && cut < 600f);
        Assert.Equal(-80f, ConcertAcoustics.CrowdAt(new Vector3(0f, 2f, 1000f), Front, Outer).Db);
        // an open air: by the distance from its field
        Assert.True(ConcertAcoustics.CrowdAt(new Vector3(0f, 2f, Front.Y + 60f), Front, Vector2.Zero).Db < -6f);
    }
}
