using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Net.Regions;

/// <summary>
/// The server's main-frame collision, copied into the far regions round their bots (#1456): parked
/// vehicles, the Versteckis decor, the Battle Royale's structures and crates, anything solid the server
/// holds in its own world. Those stay where the network and the services find them (the main frame);
/// each region gets a static copy of the ones within <see cref="ReachM"/> of a bot in it, in its frame
/// and its space, following the original as it moves. A bot there bumps into a parked car as it would
/// at home, and its rays see it.
/// </summary>
internal sealed class RegionMirrors
{
    /// <summary>A copy is made for what stands within this of a bot (its collision reach).</summary>
    public const double ReachM = Terrain.CollisionReach.RadiusM;
    /// <summary>And dropped past this: no make-and-free on the edge.</summary>
    public const double ReleaseM = Terrain.CollisionReach.ReleaseM;
    /// <summary><c>--nomirrors</c>: no copies (a check's "before": a far bot walks through a parked car).</summary>
    public static bool Off => CmdArgs.Has("--nomirrors");

    private sealed class Mirror(ServerRegion region, StaticBody3D body)
    {
        public readonly ServerRegion Region = region;
        public readonly StaticBody3D Body = body;
        public Transform3D Last;
    }

    private readonly HashSet<CollisionObject3D> _sources = new();
    private readonly Dictionary<(int Region, CollisionObject3D Source), Mirror> _mirrors = new();
    private readonly List<(int, CollisionObject3D)> _drop = new();
    private readonly List<Vector3> _bots = new();

    /// <summary>A main-frame object with collision: copied into the regions near it from now on.</summary>
    public void Track(CollisionObject3D source)
    {
        if (!_sources.Add(source)) return;
        source.TreeExiting += () => Forget(source);
    }

    public int Count => _mirrors.Count;

    private void Forget(CollisionObject3D source)
    {
        _sources.Remove(source);
        _drop.Clear();
        foreach (var key in _mirrors.Keys) if (key.Source == source) _drop.Add(key);
        foreach (var key in _drop) Free(key);
    }

    private void Free((int, CollisionObject3D) key)
    {
        if (_mirrors.Remove(key, out var m) && GodotObject.IsInstanceValid(m.Body)) m.Body.QueueFree();
    }

    /// <summary>
    /// Makes and drops the copies: for each far region, what stands within <see cref="ReachM"/> of one of
    /// its bots (<paramref name="botsOf"/>: their Earth positions).
    /// </summary>
    public void Update(ServerRegion main, IEnumerable<ServerRegion> regions, System.Func<ServerRegion, IEnumerable<GlobalPos>> botsOf)
    {
        if (Off) return;
        var live = new HashSet<int>();
        foreach (var region in regions)
        {
            if (region.IsMain) continue;
            live.Add(region.Id);
            _bots.Clear();
            foreach (var g in botsOf(region)) _bots.Add(main.Origin.ToWorld(g));
            foreach (var source in _sources)
            {
                if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree()) continue;
                var at = source.GlobalPosition;
                double nearest = double.MaxValue;
                foreach (var b in _bots) nearest = System.Math.Min(nearest, new Vector2(at.X - b.X, at.Z - b.Z).Length());
                var key = (region.Id, source);
                bool has = _mirrors.ContainsKey(key);
                if (!has && nearest <= ReachM) Make(main, region, source);
                else if (has && nearest > ReleaseM) Free(key);
            }
        }
        // the copies of a region that was freed went with its viewport
        _drop.Clear();
        foreach (var key in _mirrors.Keys) if (!live.Contains(key.Region)) _drop.Add(key);
        foreach (var key in _drop) _mirrors.Remove(key);
    }

    /// <summary>Each copy where its original now is (a parked car someone drives off, a crate that lands).</summary>
    public void Follow(ServerRegion main)
    {
        foreach (var ((_, source), m) in _mirrors)
        {
            if (!GodotObject.IsInstanceValid(source) || !GodotObject.IsInstanceValid(m.Body)) continue;
            var t = source.GlobalTransform;
            if (t == m.Last) continue;
            m.Last = t;
            m.Body.GlobalTransform = Place(main, m.Region, source, t);
        }
    }

    /// <summary>
    /// The original's placement in the region's frame: its Earth position when it keeps one exactly (a vehicle's), its
    /// turn against the local east-up-south kept (a few µs, only when the original moved).
    /// </summary>
    private static Transform3D Place(ServerRegion main, ServerRegion region, CollisionObject3D source, Transform3D t)
    {
        var global = source is VehicleBody v ? v.Global : main.Origin.ToGlobal(t.Origin);
        // turned as the region's frame turns against the main one there (an ENU region, #1513); none between flat frames
        var shift = region.Origin.Frame.Carry(main.Origin.Frame, t.Origin, global);
        return new Transform3D(shift.Transform.Basis * t.Basis, region.Origin.ToWorld(global));
    }

    private void Make(ServerRegion main, ServerRegion region, CollisionObject3D source)
    {
        var body = new StaticBody3D { Name = $"Mirror_{source.Name}", CollisionLayer = source.CollisionLayer, CollisionMask = 0 };
        var inverse = source.GlobalTransform.AffineInverse();
        int shapes = 0;
        AddShapes(source, source, inverse, body, ref shapes);
        if (shapes == 0)
        {
            body.Free();
            return;
        }
        region.Root!.AddChild(body);
        var m = new Mirror(region, body) { Last = source.GlobalTransform };
        body.GlobalTransform = Place(main, region, source, m.Last);
        _mirrors[(region.Id, source)] = m;
    }

    /// <summary>The shapes under <paramref name="node"/>, placed as on the original; a nested collision object is copied on its own.</summary>
    private static void AddShapes(Node node, CollisionObject3D source, Transform3D inverse, StaticBody3D body, ref int shapes)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            if (child is CollisionObject3D) continue;
            if (child is CollisionShape3D { Disabled: false, Shape: { } shape } cs)
            {
                body.AddChild(new CollisionShape3D { Shape = shape, Transform = inverse * cs.GlobalTransform });
                shapes++;
            }
            AddShapes(child, source, inverse, body, ref shapes);
        }
    }
}
