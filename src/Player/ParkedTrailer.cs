using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A trailer standing in the world on its own (#70), as a <c>Vehicles.VehicleBody</c> sees it: its
/// first section is the body's node, the rest (a drawbar trailer's body behind its dolly) stand at
/// the angle they were left at. Nobody rides it — <see cref="RideKind.Trailer"/> is never mounted —
/// a truck backs its hitch under the pivot and couples.
/// </summary>
public sealed class ParkedTrailer : Rideable
{
    public TrailerSpec Spec { get; }
    public int Code { get; }
    /// <summary>Its own joints (a drawbar trailer's dolly-to-body), rad.</summary>
    public Vector3 Angles { get; }

    public ParkedTrailer(int code, Vector3 angles)
    {
        Spec = TrailerCatalog.For(code) ?? TrailerCatalog.All[0];
        Code = TrailerCatalog.For(code) != null ? TrailerCatalog.Clean(code) : TrailerCatalog.Code(0, 0f);
        Angles = angles;
    }

    public override RideKind Kind => RideKind.Trailer;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override float MaxHealth => 400f;

    private float Load => TrailerCatalog.Load(Code);

    public override (Vector3 Centre, Vector3 Size) ParkedBox =>
        Measured(("trailer", TrailerCatalog.Index(Code), 0), _ => HeavyRig.CreateTrailer(Spec, 0, 1f));

    public override Node3D BuildVisual(int riderIndex)
    {
        var root = HeavyRig.CreateTrailer(Spec, 0, Load);
        for (int k = 1; k < Spec.Sections.Length; k++)
        {
            var rig = HeavyRig.CreateTrailer(Spec, k, Load);
            rig.Name = $"Section{k}";
            rig.Transform = NodeLocal(k);
            root.AddChild(rig);
        }
        return root;
    }

    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        for (int k = 1; k < Spec.Sections.Length; k++)
        {
            int section = k;
            var (centre, size) = Measured(("trailer", TrailerCatalog.Index(Code), section), _ => HeavyRig.CreateTrailer(Spec, section, 1f));
            yield return (NodeLocal(section), centre, size);
        }
    }

    /// <summary>Section <paramref name="k"/> in the first section's node space, from the angles.</summary>
    public Transform3D NodeLocal(int k)
    {
        var bodies = Spec.Sections.Select(s => new HeavyTrain.Body(s, 0f)).ToArray();
        var p = Vector2.Zero;
        float psi = 0f;
        for (int i = 1; i <= k && i < bodies.Length; i++)
        {
            var joint = p + new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * bodies[i - 1].HitchZ;
            psi += Angles[i - 1];
            p = joint - new Vector2(Mathf.Cos(psi), Mathf.Sin(psi)) * bodies[i].PivotZ;
        }
        return new Transform3D(new Basis(Vector3.Up, psi), new Vector3(-p.Y, 0f, -p.X));
    }

    /// <summary>Where it hangs on a truck (kingpin, drawbar eye), in its node space, and how high.</summary>
    public Vector3 PivotNode
    {
        get
        {
            var b = new HeavyTrain.Body(Spec.Sections[0], 0f);
            return new Vector3(0f, 0f, -b.PivotZ);
        }
    }

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion) { }
}
