using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A trailer standing in the world on its own (#70), as a <c>Vehicles.VehicleBody</c> sees it: its
/// first section is the body's node, the rest (a drawbar trailer's body behind its dolly) stand at
/// the angle they were left at. Nobody rides it — <see cref="RideKind.Trailer"/> is never mounted —
/// a truck backs its hitch under the pivot and couples.
///
/// <para>
/// It collides as its parts, not as one box round everything: a semi's body from its floor up
/// and its running gear, with nothing under the nose, so a tractor's fifth wheel slides in under
/// it; a dolly only round its axle and turntable, so a truck backs up to the drawbar's eye; a boat
/// trailer from its winch post back, its A-frame free, so a tow ball reaches the coupler (#463).
/// </para>
/// </summary>
public sealed class ParkedTrailer : Rideable
{
    public TrailerSpec Spec { get; }
    public int Code { get; }
    /// <summary>Its own joints (a drawbar trailer's dolly-to-body), rad.</summary>
    public Vector3 Angles { get; }

    private readonly HeavyTrain.Body[] _bodies;
    /// <summary>Its sections as bodies, loaded: what <see cref="HeavyGround"/> stands on the ground.</summary>
    public IReadOnlyList<HeavyTrain.Body> Bodies => _bodies;

    public ParkedTrailer(int code, Vector3 angles)
    {
        Spec = TrailerCatalog.For(code) ?? TrailerCatalog.All[0];
        Code = TrailerCatalog.For(code) != null ? TrailerCatalog.Clean(code) : TrailerCatalog.Code(0, 0f);
        Angles = angles;
        // loaded, as the rigs are: the node's origin is the loaded centre of mass
        _bodies = Spec.Sections.Select(s => new HeavyTrain.Body(s, s.PayloadMax * Load)).ToArray();
    }

    public override RideKind Kind => RideKind.Trailer;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override float MaxHealth => 400f;

    /// <summary>A boat trailer's cradle (#463): its boat, parked on it, rides it.</summary>
    public override VehicleDeck[] Decks => Truck.TrailerDecks(Spec, Code);

    private float Load => TrailerCatalog.Load(Code);

    /// <summary>A semi's floor, m: the underside of its nose, over a fifth wheel.</summary>
    private float Floor => Spec.Body == TrailerBody.Tanker ? 1.1f : 1.2f;

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var s = Spec.Sections[0];
            float cg = _bodies[0].CgAt;
            if (s.Pivot == Coupling.Drawbar)
            {
                // the dolly round its axle and turntable, not its drawbar
                float axle = s.Axles[0].At;
                return Box(cg, axle - 0.8f, axle + 0.8f, 0f, s.HitchHeight + 0.05f, s.Width);
            }
            // a mounted implement (#494) on its stands, its headstock free for the linkage to reach
            if (Spec.Mounted) return Box(cg, 0.35f, s.Length, 0.1f, s.Height, s.Width);
            if (Spec.Boat != 0)
            {
                // the frame and its bunks, down to the tyres; not the A-frame ahead of the winch post. The
                // boat on it is a boat of its own, with its own hull
                return Box(cg, Spec.BowAt, s.Length, 0f, Spec.BoatKeel + 0.1f, s.Width);
            }
            return Box(cg, 0f, s.Length, Floor - 0.1f, s.Height, s.Width);
        }
    }

    /// <summary>A box between two stations (metres behind the front) and two heights, in the node's space.</summary>
    private static (Vector3 Centre, Vector3 Size) Box(float cg, float fromAt, float toAt, float y0, float y1, float width) =>
        (new Vector3(0f, (y0 + y1) * 0.5f, -(cg - (fromAt + toAt) * 0.5f)), new Vector3(width, y1 - y0, toAt - fromAt));

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default)
    {
        var root = HeavyRig.CreateTrailer(Spec, 0, Load);
        // a dropped implement stands on the ground (#494): its lift is the tractor's, and it has none
        root.Lowered = Spec.Mounted;
        for (int k = 1; k < Spec.Sections.Length; k++)
        {
            var rig = HeavyRig.CreateTrailer(Spec, k, Load);
            rig.Name = $"Section{k}";
            rig.Transform = NodeLocal(k);
            root.AddChild(rig);
        }
        return root;
    }

    /// <summary>A semi's running gear is its first extra box: a box apart from its body's (<see cref="ExtraBoxes"/>).</summary>
    public bool HasGearBox => Spec.Sections[0].Pivot is not (Coupling.Drawbar or Coupling.Ball or Coupling.ThreePoint);

    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        var s0 = Spec.Sections[0];
        if (HasGearBox)
        {
            // a semi's running gear under its body, from ahead of the first axle to the back
            float first = s0.Axles.Min(a => a.At);
            var (c, size) = Box(_bodies[0].CgAt, first - 0.8f, s0.Length, 0f, Floor - 0.1f, s0.Width);   // down to the tyres: it stands on them
            yield return (Transform3D.Identity, c, size);
        }
        for (int k = 1; k < Spec.Sections.Length; k++)
        {
            int section = k;
            var (centre, size) = Measured(("trailer", TrailerCatalog.Index(Code), section, Mathf.RoundToInt(Load * 10f)),
                _ => HeavyRig.CreateTrailer(Spec, section, Load));
            yield return (NodeLocal(section), centre, size);
        }
    }

    /// <summary>Section <paramref name="k"/> in the first section's node space, from the angles.</summary>
    public Transform3D NodeLocal(int k)
    {
        var p = Vector2.Zero;
        float psi = 0f;
        for (int i = 1; i <= k && i < _bodies.Length; i++)
        {
            var joint = p + new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * _bodies[i - 1].HitchZ;
            psi += Angles[i - 1];
            p = joint - new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * _bodies[i].PivotZ;
        }
        return new Transform3D(new Basis(Vector3.Up, psi), new Vector3(-p.Y, 0f, -p.X));
    }

    /// <summary>Where it hangs on a truck (kingpin, drawbar eye), in its node space, on the ground.</summary>
    public Vector3 PivotNode => new(0f, 0f, -_bodies[0].PivotZ);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion) { }
}