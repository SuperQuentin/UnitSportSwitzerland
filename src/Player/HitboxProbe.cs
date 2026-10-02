using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --hitboxcheck [--at E,N]</c>
///
/// <para>
/// Measures every collision and hit box against the mesh it stands for, instead of trusting the
/// numbers typed beside it:
/// </para>
/// <list type="bullet">
/// <item>each mount: drawn bounds, the capsule it moves with, its parked box and its hurtbox — the
/// parked box must lie inside what is drawn and the hurtbox must be what is drawn;</item>
/// <item>live rays through a real physics space: a round through a plane's wingtip and a
/// helicopter's rotor disc (both well outside the capsule) must hit the hurtbox and resolve to the
/// body, and a ray that does not ask for areas must not see it;</item>
/// <item>buildings, where the tiles around <c>--at</c> have any: a ray from outside at every wall
/// and roof of the real <c>.bldg</c> data, through the same shape <see cref="ChunkNode"/> builds.
/// The render culls back faces, so a wall seen from outside is wound to face out; the collision
/// uses the same raw winding and is one-sided, so this is the question of whether it stops you.</item>
/// </list>
/// Non-zero exit on any failure.
/// </summary>
public partial class HitboxProbe : Node3D
{
    public static bool Requested() => CmdArgs.Has("--hitboxcheck");

    private readonly ChunkManager? _chunks;   // null on --world flat: no buildings to test
    private readonly WorldOrigin _origin;
    private int _frame;
    private int _failures;
    private readonly List<(StaticBody3D Body, Rideable Ride)> _live = new();
    private readonly List<(StaticBody3D Body, BuildingTile Tile)> _buildings = new();

    public HitboxProbe(ChunkManager? chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        GD.Print("[hitbox] mount        drawn (w x h x l)        capsule (r, h)   parked box            hurtbox");
        var mounts = new Rideable[] { new Bicycle(), new Skis(), new Canopy(true), new Canopy(false), new Wingsuit(), new Helicopter(), new Plane() };
        float x = 0;
        foreach (var ride in mounts)
        {
            // a body far above anything, with the drawn visual and its hurtbox, as a player would carry it
            var body = new StaticBody3D { Name = ride.Kind.ToString(), Position = new Vector3(x += 40f, 5000f, 0) };
            AddChild(body);
            var visual = ride.BuildVisual(1);
            body.AddChild(visual);
            var hurt = Hurtbox.Fit(visual);
            var drawn = Avatar.MeshBounds.Of(visual);
            _live.Add((body, ride));

            string parked = "-";
            if (ride.IsVehicle)
            {
                var (c, s) = ride.ParkedBox;
                var box = new Aabb(c - s * 0.5f, s);
                parked = $"{s.X:F2}x{s.Y:F2}x{s.Z:F2}";
                if (!Inside(box, drawn)) Fail($"{ride.Kind}: parked box {box} reaches outside the drawn {drawn}");
            }
            // the hurtbox is two boxes (body and cabin): together they must span what is drawn
            Aabb? union = null;
            if (hurt != null)
                foreach (var child in hurt.GetChildren())
                    if (child is CollisionShape3D { Shape: BoxShape3D hb } cs)
                    {
                        var part = new Aabb(cs.Position - hb.Size / 2, hb.Size);
                        union = union is { } u ? u.Merge(part) : part;
                    }
            var hs = union?.Size ?? Vector3.Zero;
            if (hurt == null || (hs - drawn.Size).Length() > 0.01f) Fail($"{ride.Kind}: hurtbox {hs} is not the drawn {drawn.Size}");
            GD.Print($"[hitbox] {ride.Kind,-11} {drawn.Size.X,5:F2} x {drawn.Size.Y,5:F2} x {drawn.Size.Z,5:F2}   "
                + $"{ride.BodyRadius:F2}, {ride.BodyHeight:F2}      {parked,-20}  {hs.X:F2}x{hs.Y:F2}x{hs.Z:F2}");
        }
        LoadBuildings();
    }

    /// <summary>The real .bldg tiles around --at, each in the same shape ChunkNode gives the world.</summary>
    private void LoadBuildings()
    {
        if (_chunks?.Source is not { } source) return;
        var (e, n) = SpawnPoint.ParseTarget();
        var centre = TileId.FromLv95(e, n);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var tile = source.LoadBuildingsAsync(new TileId(centre.E + dx, centre.N + dy)).Result;
                if (tile == null || tile.Buildings.Count == 0) continue;
                var body = new StaticBody3D { Position = new Vector3(0, -20000f - _buildings.Count * 3000f, 0) };
                AddChild(body);
                body.AddChild(new CollisionShape3D { Shape = ChunkNode.BuildingShape(BuildingMeshBuilder.BuildCollisionFaces(tile)) });
                _buildings.Add((body, tile));
            }
    }

    public override void _PhysicsProcess(double delta)
    {
        // the space has to have stepped once with the new shapes in it
        if (++_frame < 3) return;
        SetPhysicsProcess(false);

        var space = GetWorld3D().DirectSpaceState;
        foreach (var (body, ride) in _live)
        {
            if (ride is not (Plane or Helicopter)) continue;
            var drawn = Avatar.MeshBounds.Of(body.GetChild<Node3D>(0));
            // down through a point near the tip of the widest extent: a wingtip, the rotor's rim
            var tip = body.GlobalPosition + new Vector3(drawn.Position.X + 0.3f, 0, drawn.GetCenter().Z);
            var from = tip + Vector3.Up * 20f;
            var to = tip + Vector3.Down * 20f;
            float outside = Mathf.Abs(tip.X - body.GlobalPosition.X) - ride.BodyRadius;

            var q = PhysicsRayQueryParameters3D.Create(from, to, Hurtbox.Layer);
            q.CollideWithAreas = true;
            var hit = space.IntersectRay(q);
            var owner = hit.Count > 0 ? Hurtbox.BodyOf(hit["collider"].AsGodotObject()) : null;
            GD.Print($"[hitbox] ray through the {ride.Kind} {outside:F1} m outside its capsule: "
                + (owner == body ? "hit, resolved to the body" : hit.Count > 0 ? $"hit {hit["collider"]}, resolved to {owner}" : "MISSED"));
            if (owner != body) Fail($"{ride.Kind}: a round through the edge of what is drawn did not hit it");

            var plain = PhysicsRayQueryParameters3D.Create(from, to);   // default mask, no areas
            if (space.IntersectRay(plain).Count > 0) Fail($"{ride.Kind}: an ordinary ray (movement, cameras) sees the hurtbox");
        }

        CheckBuildings(space);

        GD.Print(_failures == 0 ? "[hitbox] RESULT: ok" : $"[hitbox] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void CheckBuildings(PhysicsDirectSpaceState3D space)
    {
        int tested = 0, stopped = 0;
        foreach (var (body, tile) in _buildings)
            foreach (var b in tile.Buildings)
            {
                // outside is away from the building's own centre
                var c = Vector3.Zero;
                for (int i = 0; i < b.Triangles.Length; i += 3) c += new Vector3(b.Triangles[i], b.Triangles[i + 1], b.Triangles[i + 2]);
                c /= b.Triangles.Length / 3;
                for (int t = 0; t < b.TriangleCount && tested < 5000; t++)
                {
                    var (a, p1, p2) = b.Tri(t);
                    var nrm = (p1 - a).Cross(p2 - a);
                    if (nrm.Length() < 0.5f) continue;   // slivers are no wall to walk into
                    var mid = (a + p1 + p2) / 3f;
                    nrm = nrm.Normalized();
                    var outward = nrm.Dot(mid - c) >= 0 ? nrm : -nrm;
                    tested++;
                    var ray = PhysicsRayQueryParameters3D.Create(body.GlobalPosition + mid + outward * 1.5f,
                        body.GlobalPosition + mid - outward * 0.3f);
                    if (space.IntersectRay(ray).Count > 0) stopped++;
                }
            }
        if (tested == 0) { GD.Print("[hitbox] buildings: none in the 3x3 tiles around --at, NOT TESTED (run it with --at in a town)"); return; }
        float share = stopped / (float)tested;
        GD.Print($"[hitbox] buildings: {stopped}/{tested} faces stop a ray from outside ({share:P1}) over {_buildings.Count} tiles");
        if (share < 0.99f) Fail("building faces let a ray from outside through");
    }

    private static bool Inside(Aabb inner, Aabb outer) =>
        inner.Position.X >= outer.Position.X - 0.02f && inner.Position.Y >= outer.Position.Y - 0.02f
        && inner.Position.Z >= outer.Position.Z - 0.02f && inner.End.X <= outer.End.X + 0.02f
        && inner.End.Y <= outer.End.Y + 0.02f && inner.End.Z <= outer.End.Z + 0.02f;

    private void Fail(string what)
    {
        _failures++;
        GD.Print($"[hitbox] FAIL {what}");
    }
}
