using UnitSport.Avatar.Face;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A dancing face following the music (src/Avatar/Face/DanceFace.cs, linked in, #728).</summary>
public class DanceFaceTests
{
    private static FaceState At(int style, int section, float level = 0.8f, float kick = 0f, float bar = 0f, float burst = 0f,
        int beat = 1, int floor = 0, uint seed = 0u) =>
        DanceFace.Target(new DanceHearing(style, section, level, kick, bar, burst, beat, 2, floor), seed);

    [Fact]
    public void A_calm_part_dreams_and_a_peak_grins_and_opens_on_the_hit()
    {
        var calm = At(0, 0);
        Assert.True(calm.OpenL < 0.6f && calm.Smile > 0f);
        var peakHit = At(0, 2, kick: 1f);
        var peakRest = At(0, 2, kick: 0f);
        Assert.True(peakHit.Jaw > peakRest.Jaw + 0.3f);
        Assert.True(peakRest.Smile > calm.Smile);
    }

    [Fact]
    public void A_cd_with_a_singer_moves_the_lips_on_the_voice_not_the_kick()
    {
        DanceHearing H(float voice, float syllable, float vowel, float kick = 0f, int section = 1) =>
            new(0, section, 0.8f, kick, 0f, 0f, 1, 2, 0, voice, syllable, vowel);
        var singing = DanceFace.Target(H(1f, 1f, 0.8f), 0u);   // seed 0: a singer personality
        var quiet = DanceFace.Target(H(0f, 0f, 0f, kick: 1f), 0u);
        Assert.True(singing.Jaw > quiet.Jaw + 0.4f);          // the voice opens it, a kick without a voice does not
        Assert.True(singing.Wide > 0.3f);                     // "ee"
        Assert.True(DanceFace.Target(H(1f, 1f, -0.8f), 0u).Wide < -0.3f);   // "oo"
        // someone else mumbles a verse and sings the chorus
        Assert.True(DanceFace.Target(H(1f, 1f, 0f), 1u).Jaw < singing.Jaw);
        Assert.True(DanceFace.Target(H(1f, 1f, 0f, section: 3), 1u).Jaw > DanceFace.Target(H(1f, 1f, 0f), 1u).Jaw);
    }

    [Fact]
    public void A_chorus_sings_oh_and_ee_on_alternate_beats()
    {
        Assert.True(At(0, 3, beat: 0).Wide < 0f);
        Assert.True(At(0, 3, beat: 1).Wide > 0f);
        Assert.True(At(0, 3).Jaw > At(0, 1).Jaw);
    }

    [Fact]
    public void A_new_section_is_a_wow_and_the_bar_lifts_the_brows()
    {
        Assert.True(At(0, 2, burst: 1f).Brow > 0.8f);
        Assert.True(At(0, 1, bar: 1f).Brow > At(0, 1).Brow + 0.3f);
    }

    [Fact]
    public void Each_style_has_its_temper()
    {
        Assert.True(At(1, 2).Brow < 0f, "rock snarls in a peak");
        Assert.True(At(3, 1).Squint >= 0.35f, "hip-hop is half-lidded");
        Assert.True(At(4, 1).OpenL <= 0.35f, "chill floats with the eyes closed");
        Assert.Equal(FaceEyes.Hearts, At(6, 1).Special);
    }

    [Fact]
    public void Breaking_concentrates_and_silence_rests()
    {
        var floor = At(3, 2, kick: 1f, burst: 1f, floor: 1);
        Assert.True(floor.Squint >= 0.5f && floor.Jaw < 0.4f);
        Assert.True(At(0, 2, level: 0.01f, kick: 1f).Jaw < At(0, 2, kick: 1f).Jaw);
    }

    [Fact]
    public void A_held_note_stays_open_between_the_syllable_spikes()
    {
        DanceHearing H(float voice, float syllable, float vowel, int section = 1) =>
            new(0, section, 0.8f, 0f, 0f, 0f, 1, 2, 0, voice, syllable, vowel);
        var held = DanceFace.Target(H(1f, 0f, 0f), 0u);      // the syllable has long decayed
        var onset = DanceFace.Target(H(1f, 1f, 0f), 0u);
        Assert.True(held.Jaw > 0.5f, $"a held note leaves the mouth open (jaw {held.Jaw:F2})");
        Assert.True(onset.Jaw > held.Jaw + 0.2f, "a syllable still pops it wider");
    }

    [Fact]
    public void A_quiet_verse_and_a_mumbling_dancer_still_move_the_lips()
    {
        DanceHearing H(float voice, float syllable, float vowel, uint seed) =>
            new(0, 1, 0.8f, 0f, 0f, 0f, 1, 2, 0, voice, syllable, vowel);
        // a third of the track's loud vocal, on a syllable: the level is expanded so it reads
        Assert.True(DanceFace.Target(H(0.35f, 1f, 0f, 1u), 1u).Jaw > 0.25f,
            "a quiet verse syllable opens the mouth of a dancer who only mumbles");
        // and the mumbler is no longer nearly shut at full voice
        Assert.True(DanceFace.Target(H(1f, 1f, 0f, 1u), 1u).Jaw > 0.4f);
    }

    [Fact]
    public void The_aperture_lane_opens_the_jaw_and_a_consonant_closes_it()
    {
        DanceHearing H(float aperture, float consonant, float syllable = 0f) =>
            new(0, 1, 0.8f, 0f, 0f, 0f, 1, 2, 0, 1f, syllable, 0f, aperture, consonant);
        var wide = DanceFace.Target(H(0.9f, 0f), 0u);
        var narrow = DanceFace.Target(H(0.1f, 0f), 0u);
        Assert.True(wide.Jaw > narrow.Jaw + 0.4f, "an open vowel's aperture opens the jaw");
        Assert.True(DanceFace.Target(H(0.9f, 1f), 0u).Jaw < wide.Jaw - 0.2f, "a consonant presses it shut");
        // a held open vowel, on a dancer who only mumbles: the aperture carries it between syllables
        Assert.True(DanceFace.Target(H(0.9f, 0f), 1u).Jaw > 0.35f);
    }

    [Fact]
    public void An_ah_opens_wider_than_an_ee_or_an_oo()
    {
        DanceHearing H(float vowel) => new(0, 3, 0.8f, 0f, 0f, 0f, 1, 2, 0, 1f, 1f, vowel);
        Assert.True(DanceFace.Target(H(0f), 0u).Jaw > DanceFace.Target(H(0.8f), 0u).Jaw + 0.15f);
        Assert.True(DanceFace.Target(H(-0.8f), 0u).Jaw > 0.5f, "an oo is still open, just rounder");
        Assert.True(DanceFace.Target(H(-0.8f), 0u).Wide < -0.3f);
    }

    [Fact]
    public void The_same_music_and_dancer_give_the_same_face()
    {
        Assert.Equal(At(2, 3, kick: 0.7f, seed: 9u), At(2, 3, kick: 0.7f, seed: 9u));
    }
}
