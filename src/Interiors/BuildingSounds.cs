using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors;

/// <summary>
/// What buildings sound like with people in them. Client only, driven by <see cref="InteriorManager"/>.
///
/// <list type="bullet">
/// <item>Doors latch, creak and shut where they are, on both sides of the doorway.</item>
/// <item>A building with players inside, whose door is shut, gives them away: footsteps crossing
/// a room, furniture knocked, an inner door, all low-passed by the walls and placed on the wall
/// nearest the listener. It is decided on this client from the server's table of who is where,
/// so it costs no traffic: which room they are in is not known, and not needed.</item>
/// <item>Inside, near a way out, the street comes through the front door: muffled and
/// fading as it swings shut and silent once it is, clear while it stands open.</item>
/// </list>
/// </summary>
public partial class BuildingSounds : Node
{
    /// <summary>A building with players inside: its plan box in world space, and how many.</summary>
    public readonly record struct Occupied(Vector3 Center, Vector2 Axis, float HalfWidth, float HalfDepth, int Count);

    /// <summary>Occupants are heard within this distance of the building's walls.</summary>
    private const float HearRange = 30f;

    private readonly AudioStreamPlayer3D[] _pool = new AudioStreamPlayer3D[8];
    private int _next;
    private AudioStreamPlayer3D _street = null!;
    private readonly Random _rng = new();
    private readonly List<(double At, Vector3 Where, SfxBank Bank, float Pitch, float Db)> _queue = new();
    private double _clock;
    private float _streetGain;

    public override void _Ready()
    {
        for (int i = 0; i < _pool.Length; i++)
        {
            _pool[i] = new AudioStreamPlayer3D { Bus = SfxBus.Name, TopLevel = true, UnitSize = 6f, MaxDistance = 60f };
            AddChild(_pool[i]);
        }
        _street = new AudioStreamPlayer3D
        {
            Name = "Street", Bus = SfxBus.Name, TopLevel = true, Stream = SfxSynth.Street,
            UnitSize = 4f, MaxDistance = 14f, AttenuationFilterDb = -18f,
        };
        AddChild(_street);
    }

    /// <summary>A door opening or shutting, heard at a doorway.</summary>
    public void Door(Vector3 at, bool open)
    {
        var bank = open ? SfxSynth.DoorOpenBank : SfxSynth.DoorCloseBank;
        Play(at, bank, 1f, open ? -4f : -2f, 20500f);
    }

    /// <summary>
    /// Once a tick: the occupied buildings near the listener (shut ones only; an open door is
    /// heard and seen for real), and where the street comes in from with how open that way is.
    /// </summary>
    public void Tick(double dt, Vector3 listener, IEnumerable<Occupied> occupied, (Vector3 At, float Open)? street)
    {
        _clock += dt;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            var q = _queue[i];
            if (q.At > _clock) continue;
            _queue.RemoveAt(i);
            // through a wall: the highs gone, and quiet
            Play(q.Where, q.Bank, q.Pitch, q.Db, 650f);
        }

        foreach (var o in occupied)
        {
            var wall = NearestOnWalls(o, listener);
            if (wall.DistanceTo(listener) > HearRange) continue;
            // a few things a second in a busy house, one now and then in a quiet one
            float rate = 0.35f * Math.Min(o.Count, 3);
            if (_rng.NextDouble() > rate * dt) continue;
            Occupant(o, wall);
        }

        // a shut door keeps the street out: it fades with the door's swing and stops once latched;
        // eased, so walking in or out through the door fades it rather than clicking it on or off
        float want = street is { Open: > 0.02f } s ? s.Open : 0f;
        _streetGain += (want - _streetGain) * (1f - Mathf.Exp(-(float)dt / 0.12f));
        if (street is { } at) _street.GlobalPosition = at.At;
        if (_streetGain > 0.01f)
        {
            if (!_street.Playing) _street.Play();
            _street.VolumeDb = -6f + Mathf.LinearToDb(_streetGain);
            _street.AttenuationFilterCutoffHz = Mathf.Lerp(450f, 8000f, _streetGain);
        }
        else if (_street.Playing && want == 0f) _street.Stop();
    }

    /// <summary>One thing heard from inside: a walk across a room, a knock, an inner door.</summary>
    private void Occupant(Occupied o, Vector3 wall)
    {
        double r = _rng.NextDouble();
        if (r < 0.6)
        {
            // a few steps, moving along the wall
            var along = new Vector3(o.Axis.X, 0, o.Axis.Y) * (_rng.NextDouble() < 0.5 ? 1 : -1);
            int steps = 4 + _rng.Next(4);
            for (int i = 0; i < steps; i++)
                _queue.Add((_clock + i * (0.45 + _rng.NextDouble() * 0.1), wall + along * (i * 0.6f),
                    SfxSynth.StepsBank, 0.8f, -6f));
        }
        else if (r < 0.85)
            _queue.Add((_clock, wall, SfxSynth.ImpactBank, 0.55f + (float)_rng.NextDouble() * 0.2f, -8f));
        else
            _queue.Add((_clock, wall, SfxSynth.DoorCloseBank, 0.9f, -8f));
    }

    /// <summary>The point of a building's plan box nearest the listener, at head height.</summary>
    private static Vector3 NearestOnWalls(Occupied o, Vector3 listener)
    {
        var d = new Vector2(listener.X - o.Center.X, listener.Z - o.Center.Z);
        var ax = o.Axis;
        var across = new Vector2(-ax.Y, ax.X);
        float u = Mathf.Clamp(d.Dot(ax), -o.HalfWidth, o.HalfWidth);
        float v = Mathf.Clamp(d.Dot(across), -o.HalfDepth, o.HalfDepth);
        var p = ax * u + across * v;
        return new Vector3(o.Center.X + p.X, o.Center.Y, o.Center.Z + p.Y);
    }

    private void Play(Vector3 at, SfxBank bank, float pitch, float db, float cutoff)
    {
        var p = _pool[_next];
        _next = (_next + 1) % _pool.Length;
        var (stream, jitter, volume) = bank.Pick(_rng);
        p.Stream = stream;
        p.PitchScale = pitch * jitter;
        p.VolumeDb = db + volume;
        p.AttenuationFilterCutoffHz = cutoff;
        p.GlobalPosition = at;
        p.Play();
    }
}
