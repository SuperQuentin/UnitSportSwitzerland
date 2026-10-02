using Godot;
using UnitSport.Player;

namespace UnitSport.Audio;

/// <summary>
/// The footsteps of somebody else (#375): another player or a pedestrian walking past used to be
/// silent. Client only, from the copy's own motion (the synchronised position), at the gait's
/// cadence and on the ground under them, from their feet. Nothing goes over the network.
/// Only within <see cref="Reach"/> of the ears: a step 40 m off is below everything else anyway.
/// </summary>
public partial class BodySteps : Node
{
    private const float Reach = 35f;

    private readonly FootPlayer _body;
    private AudioStreamPlayer3D _voice = null!;
    private readonly Random _rng = new();
    private Vector3 _last;
    private float _speed, _accum = 0.6f;
    private bool _have;

    public BodySteps(FootPlayer body) => _body = body;

    public BodySteps() : this(null!) { }

    public override void _Ready()
    {
        Name = "Steps";
        _voice = new AudioStreamPlayer3D
        {
            Name = "Voice", Bus = SfxBus.Name, UnitSize = 2.5f, MaxDistance = Reach + 5f,
            AttenuationFilterCutoffHz = 9000f, MaxPolyphony = 2,
        };
        AddChild(_voice);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (dt <= 0 || _body == null || !IsInstanceValid(_body)) return;
        var at = _body.GlobalPosition;
        if (!_have) { _last = at; _have = true; return; }
        var move = at - _last;
        _last = at;
        // an origin shift or a teleport is not a stride
        if (move.LengthSquared() > 25f) return;
        float horizontal = new Vector2(move.X, move.Z).Length() / dt;
        _speed = Mathf.Lerp(_speed, horizontal, 1f - Mathf.Exp(-dt / 0.15f));

        bool walking = _body.Ride == RideKind.OnFoot && _body.RidingWith == 0 && !_body.Ragdolled
                       && !_body.IsSwimming && Mathf.Abs(move.Y / dt) < 2.5f && _speed > 0.5f;
        if (!walking || Ears.Of(this) is not { } ear || ear.DistanceSquaredTo(at) > Reach * Reach)
        {
            _accum = 0.6f;
            return;
        }
        _accum += Avatar.HumanMeshBuilder.Cadence(_speed) * dt;
        if (_accum < 1f) return;
        _accum -= 1f;
        float run = Mathf.Clamp(_speed / _body.RunSpeed, 0f, 1f);
        var surface = _body.Terrain is { } chunks ? Surfaces.At(chunks, at, _body.Indoors, _body) : Surface.Grass;
        var (stream, pitch, db) = Surfaces.Steps(surface).Pick(_rng);
        _voice.GlobalPosition = at + Vector3.Up * 0.05f;
        _voice.Stream = stream;
        _voice.PitchScale = pitch * (0.92f + 0.16f * (float)_rng.NextDouble());
        _voice.VolumeDb = db + Mathf.LinearToDb(0.3f + 0.4f * run);
        _voice.Play();
    }
}
