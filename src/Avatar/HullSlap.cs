using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Water slapping a hull (#380), heard on every peer from the hull it draws: the water's height up
/// the forward hull (a point given in rig space) is watched frame to frame on this peer's own
/// waves, and when it climbs the hull fast (the bow coming down onto a wave, a crest running into
/// it) the hull slaps, as loud as it climbed fast. So it grows with speed and with the sea: flat
/// out into a swell it bangs, idling in a swell it laps, in a calm at rest it is all but silent.
/// Measured (speedboat, <c>--boatcheck speedboat,shots,wake</c>): calm idle 0.2 faint laps a second, gamey idle
/// 0.8 (strength 0.03), gamey half ahead (21 km/h) 1.2 at 0.2.
/// </summary>
public sealed class HullSlap
{
    /// <summary>The water climbing the hull slower than this (m/s) is silent.</summary>
    private const float Quiet = 0.1f;
    /// <summary>Climbing this much faster than <see cref="Quiet"/> is a full slap.</summary>
    private const float Full = 1.6f;
    /// <summary>Heard no farther than this (m): beyond, nothing is computed.</summary>
    private const float Range = 120f;

    private readonly Node3D _rig;
    private readonly Vector3 _at;
    private readonly float _pitch, _volumeDb, _unitSize;
    private readonly AudioStreamPlayer3D[] _voices = new AudioStreamPlayer3D[2];
    private int _next;
    private float _last = float.NaN, _wait;
    private static readonly System.Random Rng = new(380);

    /// <param name="rig">The drawn hull.</param>
    /// <param name="at">Where on it the water is watched, rig space: the forward hull at the waterline.</param>
    /// <param name="pitch">1 for a runabout; lower for a bigger hull.</param>
    public HullSlap(Node3D rig, Vector3 at, float pitch, float volumeDb, float unitSize)
    {
        _rig = rig;
        _at = at;
        _pitch = pitch;
        _volumeDb = volumeDb;
        _unitSize = unitSize;
    }

    /// <summary>Slaps so far, and their summed strength (0..1 each): for probes.</summary>
    public int Count { get; private set; }
    public float Sum { get; private set; }

    /// <summary>A frame: true when it slapped (probes).</summary>
    public bool Tick(float dt)
    {
        _wait -= dt;
        // heard from the body's ears, not the camera (#375)
        if (dt <= 0f || !_rig.IsInsideTree() || Audio.Ears.Of(_rig) is not { } ear) return false;
        var at = _rig.GlobalTransform * _at;
        if (ear.DistanceSquaredTo(at) > Range * Range || !World.WaterField.TryLevelAt(at, out float level))
        {
            _last = float.NaN;
            return false;
        }
        float wet = level - at.Y;
        float climb = float.IsNaN(_last) ? 0f : (wet - _last) / dt;
        _last = wet;
        // the water must be at the hull to slap it: not far under a hull in the air, not over its deck
        if (climb <= Quiet || _wait > 0f || wet < -0.25f || wet > 1.5f) return false;
        float strength = Mathf.Clamp((climb - Quiet) / Full, 0f, 1f);
        Count++;
        Sum += strength;
        Play(at, strength);
        // a hard slap rings a while; the lapping of a chop comes quicker
        _wait = 0.12f + 0.2f * strength;
        return true;
    }

    private void Play(Vector3 at, float strength)
    {
        if (DisplayServer.GetName() == "headless") return;
        var voice = _voices[_next];
        if (voice == null)
        {
            voice = _voices[_next] = new AudioStreamPlayer3D
            {
                Name = "HullSlap", UnitSize = _unitSize, MaxDistance = Range, Bus = Audio.SfxBus.Name, TopLevel = true,
            };
            _rig.AddChild(voice);
        }
        _next = (_next + 1) % _voices.Length;
        var (stream, pitch, db) = Audio.SfxSynth.HullSlapBank.Pick(Rng);
        voice.Stream = stream;
        // harder is louder and a little lower (more water, a bigger knock)
        voice.PitchScale = _pitch * pitch * (1.1f - 0.2f * strength);
        voice.VolumeDb = _volumeDb + db + Mathf.LinearToDb(0.12f + 0.88f * strength);
        voice.GlobalPosition = at;
        voice.Play();
    }
}
