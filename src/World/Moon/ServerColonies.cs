using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The Moon colonies on a dedicated server (#2000, after C3b #1852): a child of a Moon region's ground
/// (<see cref="ServerBodyGround"/>), which builds each colony within <see cref="ReachM"/> of one of the region's bodies (its
/// bots and its players) as a client's <see cref="MoonSurface"/> builds it round the camera: the same
/// <see cref="ColonyBuilder.Build"/>, so its modules' interiors (#2005: the interiors system's faces), its tubes, stairs, pads
/// and airlocks are solid for the bots the server runs there, its airlocks the ones <see cref="ColonyDoors"/> serves, and
/// its crew bots of the server's own, watched by the region's players (<see cref="ColonyColonists.Eyes"/>) instead of a
/// camera. Placed once in the region's frame, which never moves; dropped past <see cref="DropM"/> from every body.
/// </summary>
public partial class ServerColonies : Node
{
    /// <summary>A colony is built when a body of the region comes this near its centre, metres.</summary>
    public const double ReachM = 700;

    /// <summary>And dropped when every body is this far, metres.</summary>
    public const double DropM = 1100;

    private readonly ServerBodyGround _ground;
    private readonly BodyHeights _heights;
    private readonly WorldOrigin _origin;
    private readonly Dictionary<string, Node3D> _built = new();
    // the colonies whose rovers are parked (once each build, when the ground's collision is under their slots)
    private readonly HashSet<string> _parked = new();
    private readonly List<Vector3> _eyes = new();
    private double _scan;

    public ServerColonies(ServerBodyGround ground, BodyHeights heights, WorldOrigin origin)
    {
        _ground = ground;
        _heights = heights;
        _origin = origin;
        Name = "Colonies";
    }

    /// <summary>The colonies this region has built, by id (for the checks).</summary>
    public IReadOnlyDictionary<string, Node3D> Built => _built;

    /// <summary>The region's players' places (world, its frame), refreshed each scan: the crew's watchers.</summary>
    private IReadOnlyList<Vector3> Eyes() => _eyes;

    public override void _PhysicsProcess(double delta)
    {
        _scan -= delta;
        if (_scan > 0 || _origin.Frame.Axes is not { } axes) return;
        _scan = 0.5;
        _eyes.Clear();
        foreach (var a in _ground.Anchors)
            if (IsInstanceValid(a) && a.IsInsideTree() && a is Player.FootPlayer { Npc: false } p) _eyes.Add(p.GlobalPosition);
        foreach (var c in MoonColonies.All)
        {
            double near = double.MaxValue;
            foreach (var a in _ground.Anchors)
            {
                if (!IsInstanceValid(a) || !a.IsInsideTree()) continue;
                var g = a.GlobalPosition;
                var p = axes.ToEcef(g.X, g.Y, g.Z);
                var n = _heights.NormalUnder(p);
                near = Math.Min(near, Math.Acos(Math.Clamp(Dot(n, c.Normal), -1, 1)) * Ellipsoid.Moon.A);
            }
            bool have = _built.ContainsKey(c.Id);
            if (have && !_parked.Contains(c.Id)) Park(c);
            if (!have && near < ReachM) Build(c, axes);
            else if (have && near > DropM)
            {
                _built.Remove(c.Id, out var node);
                _parked.Remove(c.Id);
                node!.QueueFree();
                GD.Print($"[colonies] the server's {c.Name}: dropped ({near:F0} m from every body)");
            }
        }
    }

    /// <summary>
    /// The colony's depot rovers (<see cref="MoonRoverDepot"/>) put down by the server once the region's ground is solid
    /// under every slot (its squares follow the bodies), so its colonists drive them on their EVAs and players take them.
    /// </summary>
    private void Park(MoonColony c)
    {
        var root = _built[c.Id];
        var plan = ColonyPlan.For(c);
        var slots = MoonRoverDepot.Slots(c, plan);
        if (slots.Count == 0)
        {
            _parked.Add(c.Id);
            foreach (var crew in ColonyColonists.In(root)) crew.CrewReady = true;
            return;
        }
        foreach (var s in slots)
            if (!_ground.HasCollisionAt(root.GlobalTransform * s.At)) return;
        int n = MoonRoverDepot.Park(c, plan, root.GlobalTransform, _origin);
        _parked.Add(c.Id);
        foreach (var crew in ColonyColonists.In(root)) crew.CrewReady = true;
        GD.Print($"[colonies] the server's {c.Name}: {n} rover(s) parked");
    }

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private void Build(MoonColony c, FrameAxes axes)
    {
        var g0 = Ellipsoid.Moon.ToGeodetic(_heights.Ground(c.Normal));
        var frame = new EnuFrame(g0, Ellipsoid.Moon);
        float GroundY(double e, double n)
        {
            var (_, y, _) = frame.ToLocal(_heights.Ground(MoonSites.Offset(new MoonSite(c.Id, c.Name, MoonSiteKind.Colony, c.Lat, c.Lon, []), e, n)));
            return (float)y;
        }
        var root = ColonyBuilder.Build(c, ColonyPlan.For(c), GroundY, (_origin, Eyes));
        // its crew wait for its rovers (Park), so the first EVA finds one
        foreach (var crew in ColonyColonists.In(root)) crew.CrewReady = false;
        AddChild(root);
        // from the colony's frame into the region's, in doubles (as the ground's squares: the region's frame never moves)
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(frame.Origin);
        var south = new EcefPos(-frame.North.X, -frame.North.Y, -frame.North.Z);
        root.GlobalTransform = new Transform3D(new Basis(Turn(frame.East), Turn(frame.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
        _built[c.Id] = root;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[colonies] the server's {c.Name}: built ({root.GetChildCount()} nodes, plateau {g0.H:F1} m)"));
    }
}
