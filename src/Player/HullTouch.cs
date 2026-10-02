using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// For checks (#378): a swimmer strokes into the side of a parked boat on the swell and must meet
/// its hull where the hull is drawn. Every physics frame it compares the hull's collision shape with
/// the drawn frame (<see cref="VehicleBody.Visual"/>, an empty posed frame on a headless peer), and
/// every contact the swimmer's slide reports with that shape as drawn; it also watches that the hull
/// does not push the swimmer under. Used by <c>--boatcheck</c>, <c>--steamercheck</c> and
/// <c>--boatnet B</c>, on the authority's boat and on a copy.
/// </summary>
public static class HullTouch
{
    /// <summary>
    /// What a swim into a hull measured. <see cref="WorstPose"/>: the collision shape's bounds' farthest
    /// corner from the drawn one, m. <see cref="MaxTilt"/>: its largest tilt off level, degrees.
    /// <see cref="Contacts"/>: frames the swimmer touched the boat; <see cref="WorstOff"/>: the farthest
    /// contact point from the hull's surface as drawn, m; <see cref="LevelOff"/>: the same points' mean
    /// distance from where a level hull at the body would have been, m. <see cref="Deepest"/>: the
    /// swimmer's feet under the surface at worst while touching, m; <see cref="UnderFor"/>: the longest
    /// the head stayed under while touching, s.
    /// </summary>
    public readonly record struct Result(int Frames, float WorstPose, float MaxTilt, int Contacts, float WorstOff, float LevelOff,
        float Deepest, float UnderFor)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Frames} frames, collision off the drawn hull {WorstPose * 100f:F1} cm at worst, tilted up to {MaxTilt:F1}°; " +
            $"{Contacts} frames touching, contacts {WorstOff * 100f:F1} cm off the drawn hull at worst (a level hull: {LevelOff * 100f:F0} cm on average); " +
            $"feet {Deepest:F2} m under at worst touching, head under {UnderFor:F1} s at most");
    }

    /// <summary>
    /// The hull's collision shape, where it is now and where the hull is drawn (world); false without
    /// one or with no drawn frame. A box sits at <see cref="Rideable.ParkedBox"/>'s centre, a shaped
    /// hull (<see cref="Rideable.BuildParkedHull"/>) at the frame itself.
    /// </summary>
    public static bool Hull(VehicleBody boat, out Shape3D shape, out Transform3D collision, out Transform3D drawn)
    {
        shape = null!;
        collision = drawn = Transform3D.Identity;
        if (boat.GetNodeOrNull<CollisionShape3D>("Hull") is not { Shape: { } s } hull || boat.Visual is not { } frame) return false;
        shape = s;
        collision = hull.GlobalTransform;
        drawn = frame.GlobalTransform * Offset(boat, s);
        return true;
    }

    private static Transform3D Offset(VehicleBody boat, Shape3D shape) =>
        shape is BoxShape3D ? new Transform3D(Basis.Identity, boat.Ride.ParkedBox.Centre) : Transform3D.Identity;

    /// <summary>The shape's bounds in its own frame.</summary>
    public static Aabb Bounds(Shape3D shape) => shape switch
    {
        BoxShape3D box => new Aabb(-box.Size * 0.5f, box.Size),
        ConvexPolygonShape3D convex => PointBounds(convex.Points),
        _ => shape.GetDebugMesh().GetAabb(),
    };

    private static Aabb PointBounds(Vector3[] points)
    {
        var bounds = new Aabb(points[0], Vector3.Zero);
        foreach (var p in points) bounds = bounds.Expand(p);
        return bounds;
    }

    /// <summary>
    /// The signed distance of a world point from the hull's shape placed at <paramref name="at"/>
    /// (negative inside): exact for a box; the steamer's shaped hull across its section there.
    /// </summary>
    public static float Distance(Rideable ride, Shape3D shape, Transform3D at, Vector3 world)
    {
        var p = at.AffineInverse() * world;
        if (shape is ConvexPolygonShape3D && ride is Steamer) return Steamer.ParkedHullDistance(p);
        var half = Bounds(shape).Size * 0.5f;
        var q = new Vector3(Mathf.Abs(p.X) - half.X, Mathf.Abs(p.Y) - half.Y, Mathf.Abs(p.Z) - half.Z);
        var outside = new Vector3(Mathf.Max(q.X, 0f), Mathf.Max(q.Y, 0f), Mathf.Max(q.Z, 0f)).Length();
        return outside + Mathf.Min(Mathf.Max(q.X, Mathf.Max(q.Y, q.Z)), 0f);
    }

    /// <summary>
    /// Puts <paramref name="me"/> in the water <paramref name="off"/> m off the boat's side as drawn (the
    /// side it is on) (at <paramref name="along"/> m from its middle, + ahead), then strokes into the side for
    /// <paramref name="seconds"/>, measuring. <paramref name="place"/> false: from where the swimmer is.
    /// </summary>
    public static async Task<Result> Swim(Node host, FootPlayer me, VehicleBody boat, double seconds, float off = 1.2f, float along = 0f, bool place = true)
    {
        if (place && Hull(boat, out var hullShape, out _, out var at))
        {
            // on the side the swimmer is on: online its copy elsewhere slides there from where it was,
            // and through the boat it shoved the boat aside
            var side = (at.Basis.X with { Y = 0 }).Normalized();
            if ((me.GlobalPosition - at.Origin).Dot(side) < 0f) side = -side;
            var ahead = (-at.Basis.Z with { Y = 0 }).Normalized();
            me.StartSwimmingAtSurface(at.Origin + side * (Bounds(hullShape).End.X + off) + ahead * along);
            await host.ToSignal(host.GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        }
        int frames = 0, contacts = 0, levelN = 0;
        // the physics step met the shape as it was posed the frame before (posed in _Process)
        Transform3D? before = null;
        float worstPose = 0f, tilt = 0f, worstOff = 0f, levelSum = 0f, deepest = 0f, under = 0f, underMax = 0f;
        // into the side, square to it as drawn: the hull's long side, not its middle (76 m of steamer)
        me.WalkControls = () =>
        {
            if (!GodotObject.IsInstanceValid(boat) || !Hull(boat, out _, out _, out var d)) return (Vector3.Zero, false);
            var across = (d.Basis.X with { Y = 0 }).Normalized();
            float s = Mathf.Sign((me.GlobalPosition - d.Origin).Dot(across));
            return (-across * (s == 0f ? 1f : s), false);
        };
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        float dt = 1f / Engine.PhysicsTicksPerSecond;
        while (Time.GetTicksMsec() / 1000.0 < end && GodotObject.IsInstanceValid(boat) && me.IsSwimming)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (!Hull(boat, out var shape, out var collision, out var drawn)) continue;
            frames++;
            // the collision shape against the drawn hull: every corner of its bounds
            var bounds = Bounds(shape);
            for (int c = 0; c < 8; c++)
            {
                var corner = bounds.GetEndpoint(c);
                worstPose = Mathf.Max(worstPose, (collision * corner).DistanceTo(drawn * corner));
            }
            tilt = Mathf.Max(tilt, Mathf.RadToDeg(collision.Basis.Y.Normalized().AngleTo(Vector3.Up)));
            // where the swimmer's slide met the boat, against the hull as drawn and as a level one would be
            var level = boat.GlobalTransform * Offset(boat, shape);
            bool touching = false;
            for (int i = 0; i < me.GetSlideCollisionCount(); i++)
            {
                var hit = me.GetSlideCollision(i);
                for (int j = 0; j < hit.GetCollisionCount(); j++)
                {
                    if (hit.GetCollider(j) != boat) continue;
                    touching = true;
                    var p = hit.GetPosition(j);
                    float d = Mathf.Abs(Distance(boat.Ride, shape, drawn, p));
                    if (before is { } last) d = Mathf.Min(d, Mathf.Abs(Distance(boat.Ride, shape, last, p)));
                    worstOff = Mathf.Max(worstOff, d);
                    levelSum += Mathf.Abs(Distance(boat.Ride, shape, level, p));
                    levelN++;
                }
            }
            before = drawn;
            if (!touching) { under = 0f; continue; }
            contacts++;
            deepest = Mathf.Max(deepest, me.SwimDepth);
            under = me.HeadUnderwater ? under + dt : 0f;
            underMax = Mathf.Max(underMax, under);
        }
        me.WalkControls = null;
        return new Result(frames, worstPose, tilt, contacts, worstOff, levelN > 0 ? levelSum / levelN : 0f, deepest, underMax);
    }

    /// <summary>
    /// For pictures: the hull's collision shape drawn as wire edges on it (magenta, seen through the
    /// water and the hull), moving with the shape. Freed with the boat.
    /// </summary>
    public static void Overlay(VehicleBody boat)
    {
        if (boat.GetNodeOrNull<CollisionShape3D>("Hull") is not { Shape: { } shape } hull || hull.HasNode("HullOverlay")) return;
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        if (shape is ConvexPolygonShape3D && boat.Ride is Steamer)
        {
            // the steamer's sections, and the lines along the hull between them
            var zs = SteamerMeshBuilder.HullStations;
            Vector2[]? last = null;
            for (int k = 0; k < zs.Length; k++)
            {
                var ring = Steamer.ParkedSection(zs[k]);
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    mesh.SurfaceAddVertex(BoatMeshBuilder.Flip(new Vector3(ring[j].X, ring[j].Y, zs[k])));
                    mesh.SurfaceAddVertex(BoatMeshBuilder.Flip(new Vector3(ring[i].X, ring[i].Y, zs[k])));
                }
                if (last != null && last.Length == ring.Length)
                    for (int i = 0; i < ring.Length; i++)
                    {
                        mesh.SurfaceAddVertex(BoatMeshBuilder.Flip(new Vector3(last[i].X, last[i].Y, zs[k - 1])));
                        mesh.SurfaceAddVertex(BoatMeshBuilder.Flip(new Vector3(ring[i].X, ring[i].Y, zs[k])));
                    }
                last = ring;
            }
        }
        else
        {
            var h = Bounds(shape).Size * 0.5f;
            for (int a = 0; a < 3; a++)
                for (int u = -1; u <= 1; u += 2)
                    for (int v = -1; v <= 1; v += 2)
                    {
                        var from = new float[3];
                        var to = new float[3];
                        from[a] = -1; to[a] = 1;
                        from[(a + 1) % 3] = to[(a + 1) % 3] = u;
                        from[(a + 2) % 3] = to[(a + 2) % 3] = v;
                        mesh.SurfaceAddVertex(new Vector3(from[0], from[1], from[2]) * h);
                        mesh.SurfaceAddVertex(new Vector3(to[0], to[1], to[2]) * h);
                    }
        }
        mesh.SurfaceEnd();
        hull.AddChild(new MeshInstance3D
        {
            Name = "HullOverlay",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.1f, 0.9f),
                NoDepthTest = true,
                RenderPriority = 10,
            },
        });
    }
}
