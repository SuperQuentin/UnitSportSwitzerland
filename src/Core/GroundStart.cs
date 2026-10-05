using Godot;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Explore started from the menus begins on foot, on open ground (#517): not in the fly camera
/// high above the spawn, and not inside a house the spawn point happens to fall in. Run behind
/// the loading screen once the body is down: it waits for the ground and the buildings' collision
/// around the body, then moves it to the nearest dry spot with open sky over it and room to
/// stand. Command-line runs keep the fly-camera start their tools expect.
/// </summary>
public sealed class GroundStart
{
    /// <summary>How far out the search goes, metres, in rings this far apart.</summary>
    private const float Reach = 150, Ring = 3;
    /// <summary>Waiting longer than this for building collision, the search runs with what there is.</summary>
    private const double MaxWait = 10;

    private readonly ChunkManager _chunks;
    private readonly FootPlayer _player;
    private double _waited;

    public bool Done { get; private set; }

    public GroundStart(ChunkManager chunks, FootPlayer player)
    {
        _chunks = chunks;
        _player = player;
    }

    /// <summary>One loading frame: true once the body stands somewhere clear (or the search gave up).</summary>
    public bool Step(double delta)
    {
        if (Done) return true;
        _waited += delta;
        var at = _player.GlobalPosition;
        bool ready = _chunks.HasCollisionAt(at) && _chunks.BuildingCollisionDoneAt(at);
        if (!ready && _waited < MaxWait) return false;

        Done = true;
        if (FindClear(at) is { } spot)
        {
            if (MathX.FlatDistance(spot, at) > 0.5f) _player.PlaceAt(spot, _player.Rotation.Y);
            GD.Print($"[spawn] on foot at {spot.Round()}, {MathX.FlatDistance(spot, at):F0} m from the spawn point");
        }
        else GD.PushWarning($"[spawn] no open ground within {Reach} m of {at.Round()}; left where it fell");
        return true;
    }

    /// <summary>The nearest standing spot to <paramref name="center"/> in widening rings, or null.</summary>
    private Vector3? FindClear(Vector3 center)
    {
        var space = _player.GetWorld3D().DirectSpaceState;
        var self = new Godot.Collections.Array<Rid> { _player.GetRid() };
        var body = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = 0.45f, Height = 1.8f },
            Exclude = self,
        };

        for (float r = 0; r <= Reach; r += Ring)
        {
            int count = r == 0 ? 1 : (int)(Mathf.Tau * r / Ring);
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.Tau * i / count;
                var p = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                if (Clear(space, self, body, p) is { } spot) return spot;
            }
        }
        return null;
    }

    private Vector3? Clear(PhysicsDirectSpaceState3D space, Godot.Collections.Array<Rid> self,
        PhysicsShapeQueryParameters3D body, Vector3 p)
    {
        if (!_chunks.HasCollisionAt(p) || !_chunks.TryGetHeight(p, out float ground)) return null;
        // not in a lake or a river
        if (_chunks.TryGetSurface(p, out float surface) && surface > ground + 0.2f) return null;

        // open sky: a ray from high above meets the ground first, not a roof, a bridge or a tree
        var ray = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, ground + 80, p.Z), new Vector3(p.X, ground - 4, p.Z));
        ray.Exclude = self;
        var hit = space.IntersectRay(ray);
        if (hit.Count == 0) return null;
        float floor = hit["position"].AsVector3().Y;
        if (floor > ground + 1.5f || hit["collider"].As<Node>()?.Name.ToString() == "BuildingBody") return null;

        // room to stand: a body's capsule there touches nothing (walls, trunks, fences)
        body.Transform = new Transform3D(Basis.Identity, new Vector3(p.X, floor + 1.05f, p.Z));
        if (space.IntersectShape(body, 1).Count > 0) return null;
        return new Vector3(p.X, floor + 1f, p.Z);
    }
}
