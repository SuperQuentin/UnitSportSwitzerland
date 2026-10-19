using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Flying the saucer (#861, docs/notes/player/ufo.md): the switches pressed (ramp / legs, beam,
/// lamps) become edges of the <see cref="FlightInput"/> intent, so a script or a bot flies it
/// through <see cref="FlyControls"/> the same way; the free height under the legs; getting out at
/// the foot of the ramp. The flight is <see cref="Ufo"/>, the beam <see cref="TractorBeams"/>.
/// </summary>
public partial class FootPlayer
{
    private const int UfoPressHatch = 1, UfoPressBeam = 2, UfoPressLights = 4;
    /// <summary>Switches pressed since the last flight step (bits), consumed by it.</summary>
    private int _ufoPress;

    /// <summary>G / X, H / D-pad ←, L / D-pad → in the saucer's seat: remembered for the next step.</summary>
    private bool HandleUfoInput(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return false;
        if (e.IsActionPressed(PlayerInput.CarDoor)) _ufoPress |= UfoPressHatch;
        else if (e.IsActionPressed(PlayerInput.TractorBeam)) _ufoPress |= UfoPressBeam;
        else if (e.IsActionPressed(PlayerInput.LightsToggle)) _ufoPress |= UfoPressLights;
        else return false;
        return true;
    }

    /// <summary>Whether <paramref name="bit"/> was pressed since the last step; clears it.</summary>
    private bool TakeUfoPress(int bit)
    {
        bool pressed = (_ufoPress & bit) != 0;
        _ufoPress &= ~bit;
        return pressed;
    }

    /// <summary>Before the saucer's step: the free height under its legs, by a ray (terrain, roofs, the flat test world).</summary>
    private void UfoStep(Ufo ufo)
    {
        // static ground only (terrain, roofs, the test world's floor): what the beam holds, a player or
        // a car under it, is nothing to land on
        float ground = TractorBeams.GroundUnder(this, GlobalPosition + Vector3.Up * 0.2f, float.NaN, GetRid());
        ufo.Below = float.IsNaN(ground) ? 999f : GlobalPosition.Y - ground;
        // no terrain to measure from (the flat test world): the HUD's height is the ray's
        if (Clearance >= 999f) Clearance = ufo.Below;
    }

    /// <summary>Feet at <paramref name="feet"/> (put on the terrain when under it) if a person stands there clear; null if not.</summary>
    private Vector3? ClearStandAt(Vector3 feet)
    {
        if (!Indoors && Terrain != null && Terrain.TryGetHeight(feet, out float g) && Mathf.Abs(g - feet.Y) < 1.5f)
            feet = feet with { Y = g };
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _standProbe,
            Transform = new Transform3D(Basis.Identity, feet + Vector3.Up * (StandHeight * 0.5f + 0.1f)),
            CollisionMask = CollisionMask,
            Exclude = SelfExclude,
        };
        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0 ? feet + Vector3.Up * 0.1f : null;
    }

    // ---- in someone else's beam, on foot ------------------------------------------------------

    private enum BeamHold : byte { None, Lifted, Lowering }
    private BeamHold _beamHold;
    private FootPlayer? _beamPilot;
    private float _beamTime;

    /// <summary>Held in a UFO's tractor beam, or being let down from one (probes and the HUD read it).</summary>
    public bool InBeam => _beamHold != BeamHold.None;

    /// <summary>
    /// On foot, on this body's own peer (a client's player, a server's bot): someone standing still
    /// in a beam is lifted toward its hatch and held under it; walking frees them (#861). Let go (they
    /// walked, or the beam went off) they are let down gently, steering with the walk. Applied here,
    /// as a blast's push is, from the beams every peer reads (<see cref="TractorBeams.Active"/>).
    /// </summary>
    private bool BeamPhysics(float dt, bool onFloor)
    {
        if (_beamHold == BeamHold.None && TractorBeams.Active(GetTree()).Count == 0) return false;
        // where the walk wants to go, world space: the keys (camera-relative) or a bot's intent
        Vector3 walk;
        if (!Npc)
        {
            var stick = PlayerInput.Move;
            walk = new Basis(Vector3.Up, _viewYaw) * new Vector3(stick.X, 0, stick.Y);
        }
        else walk = WalkControls?.Invoke().Item1 ?? Vector3.Zero;
        bool walking = walk.LengthSquared() > 0.09f;

        var chest = GlobalPosition + Vector3.Up * 0.9f;
        var beam = TractorBeams.At(GetTree(), chest, this);
        if (beam is { } b && !walking && (_beamHold == BeamHold.Lifted || onFloor && MathX.FlatLength(Velocity) < 0.6f))
        {
            if (_beamHold != BeamHold.Lifted) GD.Print($"[beam] {Name} lifted by {b.Pilot.Name}");
            _beamHold = BeamHold.Lifted;
            HoldOff(b.Pilot);
            _beamTime += dt;
            Velocity = TractorBeamMath.Pull(b.Apex, chest, b.Velocity, _beamTime);
            MoveAndSlide();
            return true;
        }
        if (_beamHold == BeamHold.None) return false;
        if (onFloor)
        {
            GD.Print($"[beam] {Name} back on the ground");
            _beamHold = BeamHold.None;
            HoldOff(null);
            return false;
        }
        // let go in the air: down at the beam's gentle rate, the walk steering a little
        _beamHold = BeamHold.Lowering;
        Velocity = TractorBeamMath.Lower + MathX.Flat(walk).LimitLength(1f) * 2.5f;
        MoveAndSlide();
        return true;
    }

    /// <summary>No collision with the saucer lifting this body (its capsule reaches below the belly), or with none.</summary>
    private void HoldOff(FootPlayer? pilot)
    {
        if (_beamPilot == pilot) return;
        if (_beamPilot != null && IsInstanceValid(_beamPilot)) RemoveCollisionExceptionWith(_beamPilot);
        _beamPilot = pilot;
        if (pilot != null) AddCollisionExceptionWith(pilot);
    }

    /// <summary>The saucer this player flies, or null.</summary>
    public Ufo? Saucer => _ride as Ufo;
}
