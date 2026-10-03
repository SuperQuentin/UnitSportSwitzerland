using Godot;

namespace UnitSport.Audio;

/// <summary>
/// A sound source on a piece of house furniture (#433): an instrument (many notes at once, an
/// <see cref="AudioStreamPolyphonic"/>) or a running tap (one loop). Heard through
/// <see cref="Hearing"/> like a radio, so a piano in the next room is muffled and silent in the
/// street. Its <c>Position</c> is the interior-local spot and must be set before <c>AddChild</c>
/// (Hearing takes it in _Ready, docs/notes/audio/hearing.md).
/// </summary>
public partial class PropSpeaker : AudioStreamPlayer3D
{
    private readonly Hearing _hearing = new(9000f);
    private AudioStreamPlaybackPolyphonic? _poly;

    /// <summary>The level before what lies between, dB.</summary>
    public float BaseDb { get; set; }

    /// <summary>A loop to play (the tap), or null for an instrument's notes.</summary>
    public AudioStream? Loop { get; init; }

    public override void _Ready()
    {
        SfxBus.Ensure();
        Bus = SfxBus.Name;
        UnitSize = Loop != null ? 1.2f : 4f;
        MaxDistance = Loop != null ? 14f : 40f;
        AttenuationFilterDb = -12f;
        Stream = Loop ?? new AudioStreamPolyphonic { Polyphony = 24 };
        _hearing.Attach(this);
        VolumeDb = BaseDb;
        Play();
        if (Loop == null) _poly = GetStreamPlayback() as AudioStreamPlaybackPolyphonic;
    }

    public override void _Process(double delta)
    {
        _hearing.Step(this, (float)delta);
        VolumeDb = BaseDb + _hearing.Db;
    }

    /// <summary>Strikes one note: <paramref name="velocity"/> 0..1 sets its level.</summary>
    public void Strike(AudioStream note, float velocity)
    {
        if (_poly == null)
        {
            if (!Playing) Play();
            _poly = GetStreamPlayback() as AudioStreamPlaybackPolyphonic;
            if (_poly == null) return;
        }
        _poly.PlayStream(note, 0f, Mathf.LinearToDb(Mathf.Clamp(velocity, 0.05f, 1f)), 1f);
    }
}
