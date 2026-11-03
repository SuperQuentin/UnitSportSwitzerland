using Godot;
using UnitSport.Avatar;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>
/// A <see cref="Craft"/> in the world (#882): #861's saucer rig (<see cref="UfoRig"/>) or the
/// mothership (<see cref="MothershipRig"/>), put at its spot, tilted for a crash, smoking, its
/// switches followed over the shot's time. Never flown and never saved: the director frees it with
/// the shot. Its beam is a real one (<see cref="BeamApex"/>, added to <see cref="TractorBeams"/> by
/// the director), and its ramp is solid while an actor walks down it.
/// </summary>
public partial class CraftProp : Node3D
{
    public readonly Craft Spec;
    private UfoRig? _saucer;
    private MothershipRig? _mothership;
    private float _legs = -1f, _ramp = -1f;
    private UfoSwitches _now;
    private float _height;

    public CraftProp(Craft spec)
    {
        Spec = spec;
        Name = spec.Mothership ? "Mothership" : "Saucer";
    }

    public CraftProp() : this(new Craft(default)) { }

    public override void _Ready()
    {
        if (Spec.Mothership) AddChild(_mothership = MothershipRig.Create());
        else AddChild(_saucer = UfoRig.Create(null));
        if (Spec.Smoke)
        {
            // the wreck's column of dark smoke (the Battle Royale's crashed plane), already risen
            var smoke = BattleRoyale.BrCrates.Smoke(new Color(0.22f, 0.22f, 0.24f, 0.62f));
            if (smoke is CpuParticles3D p) p.Preprocess = 8f;
            smoke.Position = new Vector3(0.6f, UfoMeshBuilder.DomeBase - 0.6f, 0.4f);
            AddChild(smoke);
        }
    }

    /// <summary>
    /// Stands it at <paramref name="ground"/> (the surface under its spot, world space) facing its
    /// heading: on its legs, or <see cref="Spot.H"/> above, dug in by <see cref="Craft.Sink"/>, tilted.
    /// </summary>
    public void Put(Vector3 at, float groundY)
    {
        _height = Spec.At.Agl ? Spec.At.H : Spec.At.H - groundY;
        var yaw = new Basis(Vector3.Up, -Mathf.DegToRad(Spec.Heading));
        // the front (−Z) dips for a positive nose, the right side goes down for a positive bank
        var tilt = new Basis(Vector3.Right, -Mathf.DegToRad(Spec.Nose)) * new Basis(Vector3.Back, -Mathf.DegToRad(Spec.Bank));
        GlobalTransform = new Transform3D(yaw * tilt, at with { Y = (Spec.At.Agl ? groundY + Spec.At.H : Spec.At.H) - Spec.Sink });
    }

    /// <summary>One frame at <paramref name="t"/> s of the shot (the actors' clock): the switches, the legs and the ramp easing.</summary>
    public void Show(double t, float dt)
    {
        _now = Spec.Switches?.Invoke(t) ?? new UfoSwitches(Ramp: false);
        if (_mothership != null)
        {
            _mothership.Beam = _now.Beam;
            return;
        }
        if (_saucer == null) return;
        bool legsDown = _now.Legs ?? _height < 7f;
        bool rampOpen = _now.Ramp ?? false;
        // the first frame as it is; then the legs and ramp move as the craft's own do (about a second)
        _legs = _legs < 0f ? (legsDown ? 1f : 0f) : Mathf.MoveToward(_legs, legsDown ? 1f : 0f, dt * 1.1f);
        _ramp = _ramp < 0f ? (rampOpen ? 1f : 0f) : Mathf.MoveToward(_ramp, rampOpen ? 1f : 0f, dt * 0.9f);
        bool onGround = _height < 0.5f;
        float hum = Spec.Smoke ? 0f : onGround ? 0.08f : 0.45f;
        _saucer.Show(new UfoRig.Look(hum, _legs, _ramp, _now.Beam, _now.Lights, onGround), dt);
    }

    /// <summary>The hatch while its beam is on (world space), for <see cref="TractorBeams.Scripted"/>; null otherwise. A mothership's reaches no ground.</summary>
    public Vector3? BeamApex => _saucer != null && _now.Beam ? GlobalTransform * UfoMeshBuilder.Hatch : null;

    /// <summary>The ramp is open (or opening past half).</summary>
    public bool RampDown => _ramp > 0.9f;

    /// <summary>The saucer's own frame, for its ramp (<see cref="SaucerRamp"/>).</summary>
    public Transform3D Frame => GlobalTransform;
}

/// <summary>
/// The open ramp of a saucer in its own frame (rig space: −Z its front): where it hinges at the hatch,
/// which way it runs down, and a solid board to walk down it (#882). The ramp the rig draws has no
/// collision; a walker put at its top needs one for the shot.
/// </summary>
public static class SaucerRamp
{
    /// <summary>The hinge under the hatch.</summary>
    public static Vector3 Hinge => AircraftMeshBuilder.Flip(new Vector3(0, UfoMeshBuilder.RampHingeY, UfoMeshBuilder.RampHingeZ));

    /// <summary>Down the ramp (unit, rig space): forward and down at its open angle.</summary>
    public static Vector3 Down => new(0, -Mathf.Sin(UfoMeshBuilder.RampOpenAngle), -Mathf.Cos(UfoMeshBuilder.RampOpenAngle));

    /// <summary>Feet <paramref name="along"/> m down the ramp from its hinge, just over its top face.</summary>
    public static Vector3 At(float along) => Hinge + Down * along + Vector3.Up * 0.05f;

    /// <summary>A solid board where the open ramp lies, in rig space, its top face the ramp's.</summary>
    public static StaticBody3D Board()
    {
        float length = UfoMeshBuilder.RampLength + 0.4f;
        var body = new StaticBody3D { Name = "RampBoard" };
        var basis = new Basis(Vector3.Right, -UfoMeshBuilder.RampOpenAngle);
        var centre = Hinge + Down * (length * 0.5f) - basis.Y * 0.05f;
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(UfoMeshBuilder.RampWidth, 0.1f, length) },
            Transform = new Transform3D(basis, centre),
        });
        return body;
    }
}
