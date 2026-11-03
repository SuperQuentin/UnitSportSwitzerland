using Godot;
using UnitSport.Audio;
using UnitSport.Items;

namespace UnitSport.Concert;

/// <summary>
/// One PA hang's voice (#984): a <see cref="RadioSpeaker"/> (fetched, on the shared clock, heard through
/// walls by <see cref="Hearing"/>) that sounds like a stadium's line array from where the ears are
/// (<see cref="ConcertAcoustics.Hear"/>): late by its distance at the speed of sound, so the far stand
/// hears the band after the lights and two hangs flam a little off the axis; a line array's throw; the
/// highs lost to the air; dull behind the stage; its delay tower filling in further out; muffled
/// outside the bowl. Only the sound is late: the song's clock (<see cref="SongStart"/>) and the desk
/// keep the music's time. The hangs of one PA never duck each other.
/// </summary>
public partial class ConcertSpeaker : RadioSpeaker
{
    /// <summary>
    /// How hard the playback is pulled onto a moving delay, and how far: walking off changes the delay
    /// a few ms a second, which a seek would click on; a pitch a hair off catches it up instead (it is
    /// the Doppler shift a moving listener hears anyway). Past ~17 m/s the speaker seeks as a radio does.
    /// </summary>
    private const float PullGain = 0.5f, PullMax = 0.03f, PullDeadband = 0.015f;

    public PaSource Source { get; init; }
    /// <summary>The bowl's footprint, half extents in the stadium frame; zero for an open air.</summary>
    public Vector2 OuterHalf { get; init; }

    /// <summary>When the song started on the shared clock; the speaker plays it from then plus its delay.</summary>
    public double SongStart { get; set; }

    /// <summary>The model at the ears this frame. For the probes.</summary>
    public PaHeard Heard { get; private set; }

    public override void _Ready()
    {
        ConcertSound.EnsureBuses();
        base._Ready();
        Bus = ConcertSound.BusName;
    }

    public override void _Process(double delta)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Step(delta);
        ConcertSound.CostTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    private void Step(double delta)
    {
        if (GetParent() is Node3D frame && Ears.Of(this) is { } ear)
            Heard = ConcertAcoustics.Hear(Source, frame.ToLocal(ear), OuterHalf, HeardBlocked);
        StartedAt = SongStart + Heard.Delay;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        base._Process(delta);
        ConcertSound.RadioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        // the PA as one: a sibling hang louder at the ears is no reason to duck
        float duck = MusicFocus.Focus is ConcertSpeaker f && f.GetParent() == GetParent() ? 0f : DuckDb;
        float d = GlobalPosition.DistanceTo(Ears.Of(this) ?? GlobalPosition);
        // Godot already falls off as a point source: the line array's throw is the difference
        VolumeDb = SourceDb + duck + Heard.Db - ConcertAcoustics.PointDb(d, UnitSize);
        // Hearing wrote its cutoff for the walls this frame (only while it plays): the lower one wins
        if (!On || CdId == 0) return;
        AttenuationFilterCutoffHz = MathF.Min(AttenuationFilterCutoffHz, Heard.CutoffHz);

        if (!Playing || double.IsNaN(HeardPosition)) return;
        double err = WantedPosition - HeardPosition;
        float pitch = Math.Abs(err) < PullDeadband ? 1f : 1f + (float)Math.Clamp(err * PullGain, -PullMax, PullMax);
        if (MathF.Abs(pitch - PitchScale) > 0.001f) PitchScale = pitch;
    }
}
