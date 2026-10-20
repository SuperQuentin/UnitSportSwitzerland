using Godot;
using UnitSport.Concert.Mvr;

namespace UnitSport.Concert;

/// <summary>
/// An imported rig's scene objects collide (#974): one static body beside the stage's, its pieces as
/// <see cref="MvrCollision"/> judges them (boxes, hulls, one trimesh), so a player stands on a deck,
/// walks up its stairs (a hull's ramp) and bumps into a truss tower. Unlike the stage's own body the
/// beams' ray is not told to ignore it: a beam stops on a deck or a screen of the set. Client only, as
/// the stage's collision is (the server builds no concert).
/// </summary>
public partial class ConcertRig
{
    /// <summary>The imported objects' collision, for the checks: boxes, hulls, trimesh triangles.</summary>
    public int ObjectBoxes { get; private set; }
    public int ObjectHulls { get; private set; }
    public int ObjectTriangles { get; private set; }

    /// <summary>The imported objects' body, or null without any.</summary>
    public StaticBody3D? ObjectsBody { get; private set; }

    /// <summary>Called from <c>BuildObjects</c> with the meshes it decoded, so they are not decoded twice.</summary>
    private void BuildObjectCollision(RigData rig, Vector3[][] positions, int[][] indices)
    {
        var plan = MvrCollision.Plan(rig);
        var body = new StaticBody3D { Name = "MvrObjectsBody" };
        foreach (var p in plan)
        {
            if (p.Kind is not (MvrCollision.Kind.Box or MvrCollision.Kind.Hull)) continue;
            uint owner = body.CreateShapeOwner(body);
            if (p.Kind == MvrCollision.Kind.Box)
            {
                body.ShapeOwnerAddShape(owner, new BoxShape3D { Size = p.Size });
                body.ShapeOwnerSetTransform(owner, new Transform3D(p.Axes, p.Centre));
                ObjectBoxes++;
            }
            else
            {
                var o = rig.Objects[p.Object];
                body.ShapeOwnerAddShape(owner, new ConvexPolygonShape3D { Points = MvrCollision.Hull(positions[o.Mesh], RigData.X(o.Xf)) });
                ObjectHulls++;
            }
        }
        var faces = MvrCollision.Faces(rig, plan, positions, indices);
        if (faces.Length > 0)
        {
            uint owner = body.CreateShapeOwner(body);
            body.ShapeOwnerAddShape(owner, new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true });
            ObjectTriangles = faces.Length / 3;
        }
        if (ObjectBoxes + ObjectHulls + ObjectTriangles == 0) { body.Free(); return; }
        AddChild(body);
        ObjectsBody = body;
        GD.Print($"[concert] {rig.Objects.Count} imported pieces collide as {ObjectBoxes} boxes, {ObjectHulls} hulls, {ObjectTriangles} triangles");
    }

    /// <summary>
    /// How far a beam from <paramref name="from"/> goes along <paramref name="dir"/> (rig frame, at most
    /// <paramref name="max"/>) before something stops it: the physics ray (the stage's body excepted, the
    /// imported objects' not), then the venue's own shape.
    /// </summary>
    public float BeamReach(Vector3 from, Vector3 dir, float max)
    {
        float reach = max;
        if (_ray != null)
        {
            _ray.Position = from + dir * 0.4f;
            _ray.TargetPosition = _ray.Basis.Inverse() * (dir * max);
            _ray.ForceRaycastUpdate();
            if (_ray.IsColliding()) reach = _ray.GlobalPosition.DistanceTo(_ray.GetCollisionPoint()) / Scale.X + 0.4f;
        }
        // the venue's own walls and roof, whether or not its collision is built (it is only near a body)
        return Math.Min(reach, VenueReach(from, dir, max));
    }
}
