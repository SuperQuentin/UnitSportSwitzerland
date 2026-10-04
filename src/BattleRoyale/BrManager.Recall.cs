using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Respawn tickets (#480, docs/notes/br/polish.md): in a squad match before zone <see cref="RecallBefore"/>,
/// a player who goes out leaves a dogtag in their death box. A team-mate who takes it to one of the
/// Postauto stops (<see cref="BrState.RecallPoints"/>) and uses it there brings them back: dropped by
/// wingsuit over the stop with a knife and two bandages. Once per player and match.
/// </summary>
public partial class BrManager
{
    /// <summary>Recalls close when this zone phase starts.</summary>
    public const int RecallBefore = 4;
    /// <summary>How near a stop the tag must be used, m (the server allows a little more).</summary>
    public const float RecallReach = 6f;
    /// <summary>Postauto stops per match.</summary>
    public const int RecallStops = 4;

    // ---- the stops ------------------------------------------------------------------------------

    /// <summary>The stops as zone points (metres east/north of the region centre) and altitudes.</summary>
    public IEnumerable<(Vector2 At, float Alt)> Stops()
    {
        var p = _state.RecallPoints;
        if (p == null) yield break;
        for (int i = 0; i + 2 < p.Length; i += 3) yield return (new Vector2(p[i], p[i + 1]), p[i + 2]);
    }

    /// <summary>
    /// Server: <see cref="RecallStops"/> road points spread over the first circle, the first drawn from
    /// the seed, each next the farthest from those taken. Pure. Null when the roads are too few.
    /// </summary>
    public static float[]? PickStops(IReadOnlyList<BrLoot.RoadPoint> roads, BrArea area, Vector2 centre, float radius, int seed)
    {
        var inside = roads.Select(r => (At: new Vector2((float)(r.E - area.E), (float)(r.N - area.N)), r.Alt))
            .Where(r => r.At.DistanceTo(centre) < radius * 0.9f).ToList();
        if (inside.Count < RecallStops) return null;
        var taken = new List<(Vector2 At, float Alt)> { inside[new Random(seed ^ 0x2545f491).Next(inside.Count)] };
        while (taken.Count < RecallStops)
            taken.Add(inside.MaxBy(c => taken.Min(t => t.At.DistanceSquaredTo(c.At))));
        return taken.SelectMany(t => new[] { t.At.X, t.At.Y, t.Alt }).ToArray();
    }

    /// <summary>Whether recalls are still open now (before zone <see cref="RecallBefore"/>, in a squad match).</summary>
    private bool RecallOpen => _state.Phase == BrPhase.Playing && _state.TeamSize > 1 && _state.RecallPoints != null
                               && _state.Zone().At(ClockSync.ServerNow - _state.Started).Phase < RecallBefore;

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: what a fallen squad player leaves for their team, while recalls are open.</summary>
    private bool LeavesTag(BrEntrant e) => e.Team != 0 && !e.Recalled && RecallOpen
                                           && _state.Entrants.Any(m => m.Team == e.Team && m.Alive && m != e);

    /// <summary>
    /// Server: a standing squad player used a dogtag at a stop. The team-mate who went out last (and
    /// was never recalled) is back in: alive, unplaced, dropped over the stop by their own client.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRecall()
    {
        if (!_server || !RecallOpen || Origin == null) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (_state.Find(peer) is not { Alive: true, Downed: false, Team: not 0 } r
            || _players?.GetNodeOrNull<Node3D>(peer.ToString()) is not { } body) return;
        var (be, bn) = Origin.ToLv95(body.GlobalPosition);
        var here = new Vector2((float)(be - _state.AreaE), (float)(bn - _state.AreaN));
        var stop = Stops().OrderBy(s => s.At.DistanceTo(here)).FirstOrDefault();
        if (stop.At.DistanceTo(here) > RecallReach + 4f) return;
        var t = _state.Entrants.Where(m => m.Team == r.Team && !m.Alive && !m.Recalled && Multiplayer.GetPeers().Contains((int)m.Peer))
            .OrderByDescending(m => m.OutAt).FirstOrDefault();
        if (t == null) return;
        t.Alive = true;
        t.Recalled = true;
        t.Place = 0;
        t.Downed = false;
        GD.Print($"[br] recalled: {t.Name}, by {r.Name}");
        RpcId(t.Peer, MethodName.Recalled, _state.AreaE + stop.At.X, _state.AreaN + stop.At.Y, (double)stop.Alt);
        Rpc(MethodName.RecallNews, t.Peer, peer);
        Broadcast($"{r.Name} recalled {t.Name} at a Postauto stop!");
        Push();
    }

    // ---- client ---------------------------------------------------------------------------------

    /// <summary>The dogtag used (<see cref="ItemUse.Recall"/>): at a stop, ask the server; else say where to go.</summary>
    public string? TryRecall()
    {
        if (!InMatch || !MeAlive || LocalPlayer() is not { Downed: false } me) return "A dogtag is for a Battle Royale team-mate.";
        if (!RecallOpen) return $"Too late: recalls close at zone {RecallBefore}.";
        var here = ZonePoint(me.GlobalPosition);
        if (!Stops().Any(s => s.At.DistanceTo(here) <= RecallReach)) return "Take the tag to a Postauto stop (yellow on the map).";
        RpcId(1, MethodName.RequestRecall);
        return null;
    }

    /// <summary>The nearest stop to this player, for the compass while a tag is carried.</summary>
    public (Vector2 At, float Alt)? NearestStop(Vector2 from) =>
        Stops().Select(s => ((Vector2 At, float Alt)?)s).MinBy(s => s!.Value.At.DistanceTo(from));

    /// <summary>Whether this player carries a dogtag (the HUD's hint, the compass's stop).</summary>
    public bool CarryingTag => Inventory()?.Contains(ItemId.Dogtag) == true;

    /// <summary>This client is back in (a team-mate recalled it): over the stop, by wingsuit, a knife and two bandages.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Recalled(double e, double n, double alt)
    {
        if (_server || LocalPlayer() is not { } me || Origin == null) return;
        StopSpectating();
        _watching = 0;
        var at = Origin.ToWorld(e, n, alt + 180);
        me.Respawn(at);
        me.Leap(at, -me.Camera.GlobalTransform.Basis.Z with { Y = 0 } * 20f, RideKind.Wingsuit);
        if (Inventory() is { } inv)
        {
            inv.Add(ItemId.Knife, 1);
            inv.Add(ItemId.Bandage, 2);
        }
        me.Announce("RECALLED — BACK IN", true);
        GD.Print("[br] recalled: back in");
    }

    /// <summary>Everyone: the feed; the recaller's tag is spent.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RecallNews(long recalled, long by)
    {
        if (_server) return;
        string t = _state.Find(recalled)?.Name ?? "?", b = _state.Find(by)?.Name ?? "?";
        _feed.Add(new FeedLine($"{b} recalled {t}", recalled == Me || by == Me, Time.GetTicksMsec() / 1000.0));
        if (_feed.Count > 8) _feed.RemoveAt(0);
        if (by == Me && Inventory() is { } inv)
            for (int i = 0; i < Items.Inventory.Size; i++)
                if (inv[i].Id == ItemId.Dogtag) { inv.TakeOne(i); break; }
    }

    // ---- the signs ------------------------------------------------------------------------------

    private readonly List<Node3D> _stopSigns = new();
    private float[]? _signsFor;

    /// <summary>Client: a yellow Postauto sign at each stop of this match, gone with it.</summary>
    private void StopSigns()
    {
        var points = InMatch && _state.Running ? _state.RecallPoints : null;
        if (points == _signsFor) return;
        foreach (var s in _stopSigns) s.QueueFree();
        _stopSigns.Clear();
        _signsFor = points;
        if (points == null || Origin == null) return;
        foreach (var (at, alt) in Stops())
        {
            var sign = BuildSign();
            GetParent().AddChild(sign);
            sign.GlobalPosition = Origin.ToWorld(_state.AreaE + at.X, _state.AreaN + at.Y, alt);
            _stopSigns.Add(sign);
        }
    }

    private static Node3D BuildSign()
    {
        var root = new Node3D { Name = "PostautoStop" };
        static MeshInstance3D Box(Vector3 size, Vector3 at, Color c) => new()
        {
            Mesh = new BoxMesh { Size = size },
            Position = at,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        root.AddChild(Box(new Vector3(0.08f, 2.6f, 0.08f), new Vector3(0, 1.3f, 0), new Color(0.35f, 0.35f, 0.38f)));
        root.AddChild(Box(new Vector3(0.9f, 0.6f, 0.06f), new Vector3(0, 2.4f, 0), new Color(1f, 0.8f, 0.0f)));          // Postauto yellow
        root.AddChild(Box(new Vector3(0.5f, 0.18f, 0.07f), new Vector3(0, 2.42f, 0), new Color(0.1f, 0.1f, 0.1f)));       // the horn stripe
        root.AddChild(new OmniLight3D { Position = new Vector3(0, 2.9f, 0), LightColor = new Color(1f, 0.85f, 0.3f), LightEnergy = 1.5f, OmniRange = 8f });
        return root;
    }
}
