using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Net.Regions;

/// <summary>
/// The dedicated server's regions (#1456, G2a of #1443), at <c>World/Regions</c>: one local frame and
/// physics space per cluster of bodies (<see cref="RegionPlanner"/>), so a bot 300 km from the server's
/// origin walks at the same float precision as one beside it, and with the global tiling no flat frame
/// has to serve players 500 km apart.
/// <list type="bullet">
/// <item>The main region is the server's world as before: every body near the start stays in it, so
/// play there runs exactly as it did.</item>
/// <item>The main <see cref="ChunkManager"/> keeps loading the tiles round every body, wherever its
/// region: a body in another region leaves a plain anchor (a shadow, no collision) in the main frame.
/// The data the server serves and reads (heights, the steamer, the airport stands, the dormant fleets)
/// comes from there as before; a region only adds the physics.</item>
/// <item>A handover moves a body to another region: a bot only once the target has built collision
/// under it (a ghost anchor asks for it first), a player's proxy at once.</item>
/// </list>
/// Bodies never measure each other across regions in floats: across regions, the server measures in
/// <see cref="GlobalPos"/>.
/// </summary>
public partial class ServerRegions : Node
{
    public const string NodeName = "Regions";
    /// <summary>How often the clusters are decided again.</summary>
    public const double DecideEverySeconds = 0.5;
    /// <summary>A bot waits at most this for collision under it in the region it is handed to.</summary>
    public const double HandoverTimeoutSeconds = 15;
    /// <summary>An empty region is freed after this (a body passing back and forth keeps its terrain).</summary>
    public const double EmptyGraceSeconds = 10;

    public static ServerRegions? Instance { get; private set; }
    /// <summary><c>--noregions</c>: every body in the main region, as before #1456 (a check's "before").</summary>
    public static bool Off => CmdArgs.Has("--noregions");

    /// <summary>
    /// Whether new regions are ENU frames at their cluster's Earth mean (#1513, G2 C4): when the main world is an
    /// anchor frame (<c>--frame earth</c>). Otherwise each is the flat LV95 frame on its whole km, as before, and a
    /// handover is a plain translation. Not over the flat tiles: the flat <see cref="ChunkManager"/> answers heights as
    /// LV95 altitudes in world Y, which an ENU frame's Y is not (a body there stands beside its ground).
    /// </summary>
    public bool Enu => Main.IsEnu;

    private RegionPlanner.Anchoring Anchoring => Enu ? RegionPlanner.Anchoring.Earth : RegionPlanner.Anchoring.Lv95Km;

    /// <summary>
    /// Makes a region's terrain in a frame, and the ground it answers: a flat <see cref="ChunkManager"/> on the server's
    /// own settings and tile cache, or an <see cref="Terrain.Earth.EarthWorld"/> on the leaves under <c>--frame earth</c>
    /// (#1513), the only ground an ENU region can stand on.
    /// </summary>
    public Func<WorldOrigin, (Node Terrain, IWorldGround Ground)>? MakeGround { get; set; }
    /// <summary>Whatever else a region holds beside its terrain (its trunks), or null.</summary>
    public Func<Node, WorldOrigin, Node?>? MakeExtras { get; set; }

    public ServerRegion Main { get; }
    public IEnumerable<ServerRegion> All => _regions.Values;

    private readonly Node3D _players;
    private readonly Dictionary<int, ServerRegion> _regions = new();
    private readonly Dictionary<FootPlayer, Handover> _pending = new();
    private readonly Dictionary<FootPlayer, Node3D> _shadows = new();
    private readonly Dictionary<int, double> _emptySince = new();
    private readonly List<RegionPlanner.Body> _bodies = new();
    private readonly List<RegionPlanner.Region> _known = new();
    private readonly List<FootPlayer> _scratch = new();
    /// <summary>The bodies above the Space handover (hysteresis: in above 60 km, out below 50), kept out of the planner (#1480).</summary>
    private readonly HashSet<FootPlayer> _inSpace = new();
    private readonly RegionMirrors _mirrors = new();
    private readonly List<GlobalPos> _botsAt = new();
    private List<FootPlayer> _decided = new();
    private int _nextId = 1;
    private double _clock, _decideAt;

    private sealed class Handover(ServerRegion target, Node3D? ghost, double since)
    {
        public ServerRegion Target = target;
        public Node3D? Ghost = ghost;
        public double Since = since;
    }

    public ServerRegions(ServerRegion main, Node3D players)
    {
        Name = NodeName;
        Main = main;
        _players = players;
        _regions[main.Id] = main;
        // before the bodies' own physics: a handover happens between steps, never within one
        ProcessPriority = int.MinValue;
        ProcessPhysicsPriority = int.MinValue;
    }

    public override void _EnterTree()
    {
        Instance = this;
        GetTree().NodeAdded += OnNodeAdded;
    }

    public override void _ExitTree()
    {
        GetTree().NodeAdded -= OnNodeAdded;
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// The region a new body at <paramref name="at"/> starts in: the main one near home, else the region
    /// of a body within <see cref="RegionPlanner.LinkM"/>, else a new one there. The next decision may
    /// still move it; a bot spawned far away starts in its own frame and never loses precision.
    /// </summary>
    public ServerRegion For(GlobalPos at)
    {
        if (Off || at.HorizontalDistanceTo(Main.Anchor) <= RegionPlanner.HomeM) return Main;
        foreach (var body in Bodies())
            if (body.Region is { } r && !_inSpace.Contains(body) && Global(body).HorizontalDistanceTo(at) <= RegionPlanner.LinkM) return r;
        return Create(RegionPlanner.Centre(new[] { new RegionPlanner.Body(0, at, RegionPlanner.None) }, new() { 0 }, Anchoring));
    }

    /// <summary>
    /// Puts <paramref name="body"/> at <paramref name="at"/> at once, in the region that place belongs to:
    /// a bot called to a human's side from another region changes frame now, not after the next decision.
    /// On a client (no regions) it is a plain move in the body's frame.
    /// </summary>
    public static void Put(FootPlayer body, GlobalPos at)
    {
        if (Instance is not { } regions || body.Region == null)
        {
            if (body.Origin is { } frame) body.GlobalPosition = frame.ToWorld(at);
            return;
        }
        var target = regions.For(at);
        if (regions._pending.Remove(body, out var h)) regions.DropGhost(h);
        if (target == body.Region) body.GlobalPosition = target.Origin.ToWorld(at);
        else regions.Finish(body, target, at);
    }

    /// <summary>Where a body is, exactly: what a proxy's owner published, or the frame's reading of a body the server runs.</summary>
    private static GlobalPos Global(FootPlayer body) => body.IsMultiplayerAuthority() ? body.Global : body.NetGlobal;

    private IEnumerable<FootPlayer> Bodies()
    {
        _scratch.Clear();
        for (int i = 0; i < _players.GetChildCount(); i++)
            if (_players.GetChild(i) is FootPlayer { Puppet: false } p && p.IsInsideTree()) _scratch.Add(p);
        return _scratch;
    }

    /// <summary>
    /// A body's physics follows its region when Godot puts it (or anything under it with collision, a
    /// truck's trailer sections) into a world: entering the tree resets a body to the viewport's space.
    /// </summary>
    private void OnNodeAdded(Node node)
    {
        if (node is FootPlayer body && body.GetParent() == _players)
        {
            body.Region ??= Main;
            body.ApplyRegionSpace();
            // spawned straight into its own region (a bot far away, For)
            if (!body.Region.IsMain) CallDeferred(MethodName.Shadow, body);
            return;
        }
        if (node is not CollisionObject3D co) return;
        for (var up = node.GetParent(); up != null; up = up.GetParent())
        {
            if (up is FootPlayer owner)
            {
                if (owner.Region is { IsMain: false } r) FootPlayer.SetSpace(node, r.Space);
                return;
            }
            // the regions' own, the terrains and trunks (every region has its own), other worlds
            if (up == this || up == _players || up is ChunkManager or Terrain.Earth.EarthWorld or World.TreeColliders or Viewport) return;
            if (up == GetParent())
            {
                // the server's own world: solid things the far regions copy round their bots
                if (co is not Area3D) _mirrors.Track(co);
                return;
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (!Off && _clock >= _decideAt)
        {
            _decideAt = _clock + DecideEverySeconds;
            Decide();
        }
        Advance();
        FollowShadows();
        _mirrors.Follow(Main);
    }

    /// <summary>Clusters the bodies again and starts the handovers and the regions that takes.</summary>
    private void Decide()
    {
        _bodies.Clear();
        var bodies = new List<FootPlayer>(Bodies());
        // above the Space handover (#1480): out of the planner, parked in the main region. An orbit moves
        // 7.7 km a second: in a region of its own it re-anchored every second, and linked by its LV95 place
        // to bodies on the ground under it, it would drag their region round the Earth. Nothing up there
        // to bump into on the server; what it publishes is its truth.
        var planned = new List<FootPlayer>(bodies.Count);
        foreach (var body in bodies)
        {
            bool space = Core.Curvature.SpaceRegime(_inSpace.Contains(body), Global(body).Alt);
            if (space) _inSpace.Add(body); else _inSpace.Remove(body);
            if (!space) { planned.Add(body); continue; }
            var at = _pending.TryGetValue(body, out var ph) ? ph.Target : body.Region ?? Main;
            if (at != Main) Start(body, Main);
        }
        _inSpace.RemoveWhere(b => !GodotObject.IsInstanceValid(b) || !b.IsInsideTree());
        foreach (var body in planned)
        {
            int region = _pending.TryGetValue(body, out var h) ? h.Target.Id : (body.Region ?? Main).Id;
            _bodies.Add(new RegionPlanner.Body((long)body.GetInstanceId(), Global(body), region));
        }
        _known.Clear();
        foreach (var r in _regions.Values) _known.Add(new RegionPlanner.Region(r.Id, r.Anchor));
        var plan = RegionPlanner.Decide(_bodies, _known, Main.Anchor, ref _nextId, Anchoring);
        foreach (var made in plan.Create) Create(made.Anchor, made.Id);

        int i = 0;
        foreach (var body in planned)
        {
            var want = _regions[plan.RegionOf[_bodies[i++].Id]];
            var now = _pending.TryGetValue(body, out var h) ? h.Target : body.Region ?? Main;
            if (want != now) Start(body, want);
        }
        FreeEmpty(bodies);
        _decided = bodies;
        _mirrors.Update(Main, _regions.Values, BotsIn);
    }

    /// <summary>Where the bots the server runs in <paramref name="region"/> are (players' proxies bump into nothing here).</summary>
    private IEnumerable<GlobalPos> BotsIn(ServerRegion region)
    {
        _botsAt.Clear();
        foreach (var body in _decided)
            if (body.Region == region && body.IsMultiplayerAuthority() && GodotObject.IsInstanceValid(body)) _botsAt.Add(Global(body));
        return _botsAt;
    }

    /// <summary>For checks: how many main-frame objects are copied into far regions now.</summary>
    public int Mirrors => _mirrors.Count;

    private ServerRegion Create(GlobalPos anchor, int id = -1)
    {
        if (id < 0) id = _nextId++;
        // an ENU frame at the anchor (#1513): true north, true metres, its gravity down its own up (a space's default
        // gravity is -Y, and Y is the anchor's up); or the flat LV95 frame on the anchor's whole km
        var origin = Enu ? new WorldOrigin(FrameAxes.Enu(anchor.Ecef)) : new WorldOrigin(anchor.Lv95.E, anchor.Lv95.N);
        // a world of its own, so a physics space of its own (OwnWorld3D would keep it out of reach: World3D null)
        var world = new World3D();
        var root = new SubViewport
        {
            Name = $"Region{id}",
            World3D = world,
            Disable3D = false,
            Size = new Vector2I(2, 2),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(root);
        var (terrain, ground) = MakeGround!(origin);
        root.AddChild(terrain);
        if (MakeExtras?.Invoke(terrain, origin) is { } extras) root.AddChild(extras);
        var region = new ServerRegion(id, origin, terrain, ground, world, root);
        _regions[id] = region;
        GD.Print($"[regions] made {region}");
        return region;
    }

    /// <summary>Starts moving <paramref name="body"/> to <paramref name="target"/>: at once for a proxy, after its ground for a bot.</summary>
    private void Start(FootPlayer body, ServerRegion target)
    {
        if (_pending.Remove(body, out var old)) DropGhost(old);
        var from = body.Region ?? Main;
        if (target == from) return;
        if (!body.IsMultiplayerAuthority())
        {
            Finish(body, target, Global(body));
            return;
        }
        // a bot: the target builds collision round a ghost where the bot is before the bot goes there
        var ghost = new Node3D { Name = $"Ghost_{body.Name}", Position = target.Origin.ToWorld(Global(body)) };
        GhostParent(target).AddChild(ghost);
        target.Ground.AddAnchor(ghost, collision: true);
        _pending[body] = new Handover(target, ghost, _clock);
    }

    private Node GhostParent(ServerRegion region) => region.Root ?? (Node)this;

    private void DropGhost(Handover h)
    {
        if (h.Ghost == null || !GodotObject.IsInstanceValid(h.Ghost)) return;
        h.Target.Ground.RemoveAnchor(h.Ghost);
        h.Ghost.QueueFree();
        h.Ghost = null;
    }

    /// <summary>The bots whose ground is ready in their next region go there.</summary>
    private void Advance()
    {
        if (_pending.Count == 0) return;
        List<FootPlayer>? done = null;
        foreach (var (body, h) in _pending)
        {
            if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree()) { (done ??= new()).Add(body); continue; }
            var at = h.Target.Origin.ToWorld(Global(body));
            if (h.Ghost != null) h.Ghost.Position = at;
            // ready when the target has collision there, when the body has none where it is either (in the
            // air, not placed yet), or after the timeout (a body never waits for good)
            bool ready = h.Target.Ground.HasCollisionAt(at)
                || body.Terrain?.HasCollisionAt(body.GlobalPosition) != true
                || _clock - h.Since > HandoverTimeoutSeconds;
            if (!ready) continue;
            (done ??= new()).Add(body);
            Finish(body, h.Target, Global(body));
        }
        if (done == null) return;
        foreach (var body in done)
            if (_pending.Remove(body, out var h)) DropGhost(h);
    }

    private void Finish(FootPlayer body, ServerRegion target, GlobalPos at)
    {
        var from = body.Region ?? Main;
        body.EnterRegion(target, at);
        GD.Print($"[regions] {body.Name} from {from.Id} to {target}");
        if (target.IsMain && _shadows.Remove(body, out var shadow))
        {
            Main.Ground.RemoveAnchor(shadow);
            shadow.QueueFree();
        }
        else if (!target.IsMain) EnsureShadow(body);
    }

    /// <summary>The main terrain keeps the tiles round a body in another region loaded, as for every body before regions.</summary>
    private void EnsureShadow(FootPlayer body)
    {
        if (_shadows.ContainsKey(body)) return;
        var shadow = new Node3D { Name = $"Shadow_{body.Name}", Position = Main.Origin.ToWorld(Global(body)) };
        AddChild(shadow);
        _shadows[body] = shadow;
        Main.Ground.AddAnchor(shadow, collision: false);
    }

    private void Shadow(FootPlayer body)
    {
        if (GodotObject.IsInstanceValid(body) && body.IsInsideTree() && body.Region is { IsMain: false }) EnsureShadow(body);
    }

    /// <summary>Each shadow where its body is, in the main frame (float precision is no matter for which tiles load).</summary>
    private void FollowShadows()
    {
        if (_shadows.Count == 0) return;
        List<FootPlayer>? gone = null;
        foreach (var (body, shadow) in _shadows)
        {
            if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree())
            {
                (gone ??= new()).Add(body);
                continue;
            }
            shadow.Position = Main.Origin.ToWorld(Global(body));
        }
        if (gone == null) return;
        foreach (var body in gone)
            if (_shadows.Remove(body, out var shadow))
            {
                Main.Ground.RemoveAnchor(shadow);
                shadow.QueueFree();
            }
    }

    /// <summary>A region nobody is in nor heading to, for <see cref="EmptyGraceSeconds"/>, is freed with its terrain.</summary>
    private void FreeEmpty(List<FootPlayer> bodies)
    {
        if (_regions.Count == 1) return;
        List<int>? free = null;
        foreach (var r in _regions.Values)
        {
            if (r.IsMain) continue;
            bool used = false;
            foreach (var body in bodies)
                if (body.Region == r) { used = true; break; }
            foreach (var h in _pending.Values)
                if (h.Target == r) { used = true; break; }
            if (used) { _emptySince.Remove(r.Id); continue; }
            double since = _emptySince.TryGetValue(r.Id, out var s) ? s : _emptySince[r.Id] = _clock;
            if (_clock - since > EmptyGraceSeconds) (free ??= new()).Add(r.Id);
        }
        if (free == null) return;
        foreach (int id in free)
        {
            var r = _regions[id];
            _regions.Remove(id);
            _emptySince.Remove(id);
            r.Root!.QueueFree();
            GD.Print($"[regions] freed {r}");
        }
    }
}
