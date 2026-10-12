using Godot;

namespace UnitSport.Player;

/// <summary>
/// How a train stands on the ground (#70): each section pitched between its axles, or between the
/// pin the section ahead carries it on and its own axles, at the height of the ground under them.
/// The driven train does this itself every step (<c>FootPlayer.Heavy</c>); a parked truck, bus or
/// lone trailer (<c>Vehicles.VehicleBody</c>) uses this, so getting out does not drop it flat.
/// </summary>
public static class HeavyGround
{
    /// <summary>
    /// World transforms of each section. <paramref name="origin"/> is the first section's yaw at its
    /// centre of mass (its height is ignored); <paramref name="nodeLocal"/> places section k in the
    /// first section's node space from the articulation; <paramref name="ground"/> is the ground's
    /// height under a point.
    /// </summary>
    public static Transform3D[] Stand(Transform3D origin, IReadOnlyList<HeavyTrain.Body> bodies,
        System.Func<int, Transform3D> nodeLocal, System.Func<Vector3, float> ground)
    {
        var poses = new Transform3D[bodies.Count];
        if (bodies.Count == 0) return poses;
        var yaw = new Transform3D(new Basis(Vector3.Up, origin.Basis.GetEuler().Y), origin.Origin);

        // the first section: on its two axle groups, or on its one axle (a dolly) level
        var b0 = bodies[0];
        var fwd = -yaw.Basis.Z;
        float front = HeavyTrain.FrontAxleAt(b0.Spec), rear = HeavyTrain.RearGroupAt(b0.Spec);
        float yR = ground(yaw.Origin + fwd * (b0.CgAt - rear));
        float pitch0 = 0f, y0 = yR;
        if (rear - front > 0.5f)
        {
            float yF = ground(yaw.Origin + fwd * (b0.CgAt - front));
            pitch0 = Mathf.Clamp(Mathf.Atan2(yF - yR, rear - front), -0.35f, 0.35f);
            y0 = yR + (yF - yR) * (rear - b0.CgAt) / (rear - front);
        }
        poses[0] = new Transform3D(yaw.Basis * new Basis(Vector3.Right, pitch0), yaw.Origin with { Y = y0 });

        for (int k = 1; k < bodies.Count; k++)
        {
            var flat = yaw * nodeLocal(k);
            var b = bodies[k];
            var parent = bodies[k - 1];
            float zRear = b.CgAt - HeavyTrain.RearGroupAt(b.Spec);   // + forward of the CG
            float g = ground(flat.Origin + (-flat.Basis.Z) * zRear);
            float pitch = 0f;
            if (HeavyTrain.Carries(b.Spec.Pivot))
            {
                // the pin, where the section ahead carries it
                var pin = poses[k - 1] * new Vector3(0, parent.Spec.HitchHeight, -parent.HitchZ);
                float d = Mathf.Max(b.PivotZ - zRear, 0.5f);
                pitch = Mathf.Clamp(Mathf.Atan2(pin.Y - g, d) - Mathf.Atan2(LevelPivot(b.Spec, parent.Spec), d), -0.4f, 0.4f);
            }
            poses[k] = new Transform3D(flat.Basis * new Basis(Vector3.Right, pitch), flat.Origin with { Y = g - zRear * Mathf.Sin(pitch) });
        }
        return poses;
    }

    /// <summary>
    /// How high a carried section's pivot is when it stands level: its own coupler's height (a ball
    /// trailer's), else the hitch it hangs on (a kingpin is built for its fifth wheel).
    /// </summary>
    public static float LevelPivot(SectionSpec section, SectionSpec parent) =>
        float.IsNaN(section.PivotHeight) ? parent.HitchHeight : section.PivotHeight;
}
