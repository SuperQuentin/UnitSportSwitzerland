using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Set-up telescopes (#1440, <c>items/telescope</c>): who is looking through which, one player at a
/// time, at <c>World/Telescopes</c> on the server and every client (RPCs route by node path). E at a
/// telescope within <see cref="Reach"/> asks the server (<see cref="ServeLook"/>, which a bot could
/// call as well); granted, the view opens (<see cref="TelescopeView"/>); leaving frees it, and so do
/// a disconnect and the telescope being packed up. Offline this client plays the server.
/// The telescope's place and its packing up are <see cref="PlacedObjects"/>' (<c>PlacedKind.Telescope</c>).
/// </summary>
public partial class Telescopes : Node
{
    public const string NodeName = "Telescopes";

    /// <summary>How close to the tripod E looks through it, m; the server allows a little more.</summary>
    public const float Reach = 2.2f;
    private const float ServerSlack = 2f;

    /// <summary>The client's telescopes (the server's one serves <see cref="RemoveProblem"/> and the bots).</summary>
    public static Telescopes? Instance { get; private set; }

    private static Telescopes? _serving;

    private bool _server;
    private PlacedObjects? _placed;

    /// <summary>Server: the peer looking through each telescope.</summary>
    private readonly Dictionary<long, long> _users = new();

    /// <summary>Client: the telescopes someone else is looking through, as the server says.</summary>
    private readonly HashSet<long> _busy = new();

    /// <summary>The local player who asked last: the view stands them behind the eyepiece.</summary>
    private FootPlayer? _asker;

    /// <summary>The last refusal, for the checks.</summary>
    public string LastRefused { get; private set; } = "";

    public static Telescopes Create(Node world, bool server)
    {
        var t = new Telescopes { Name = NodeName, _server = server };
        world.AddChild(t);
        if (!server) Instance = t;
        if (server || _serving == null) _serving = t;
        return t;
    }

    private bool Online => NetLink.Online(this);
    private bool Serving => _server || !Online;

    public override void _Ready()
    {
        _placed = GetParent().GetNodeOrNull<PlacedObjects>(PlacedObjects.NodeName);
        if (_placed != null) _placed.Removed += OnRemoved;
        if (_server) Multiplayer.PeerDisconnected += OnPeerGone;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_serving == this) _serving = null;
        if (_placed != null) _placed.Removed -= OnRemoved;
        if (_server) Multiplayer.PeerDisconnected -= OnPeerGone;
    }

    /// <summary>The set-up telescope node (its <see cref="TelescopeVisual.TubeName"/> child turns).</summary>
    public Node3D? NodeOf(long id) => _placed?.GetNodeOrNull<Node3D>($"P{id}");

    /// <summary>The telescope within reach of the player on foot, outdoors, or null.</summary>
    public PlacedObject? TelescopeAt(FootPlayer p)
    {
        if (_placed == null || p.Indoors || p.Ride != RideKind.OnFoot) return null;
        PlacedObject? best = null;
        float bestD = Reach;
        foreach (var o in _placed.All.Values)
        {
            if (o.Kind != PlacedKind.Telescope) continue;
            float d = o.WorldTransform(_placed.Origin).Origin.DistanceTo(p.GlobalPosition);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    // ---- the prompt and E -------------------------------------------------------------------------

    private (long Id, bool Busy, InputDevice Device) _promptKey;
    private string? _promptText;

    /// <summary>The prompt at a telescope in reach and the telescope to outline, or null.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } t || TelescopeView.Current != null || t.TelescopeAt(p) is not { } o) return null;
        return new(t.PromptText(o), Outline.Of(t.NodeOf(o.Id)));
    }

    private string PromptText(PlacedObject o)
    {
        var key = (o.Id, _busy.Contains(o.Id), PlayerInput.HintDevice);
        if (_promptText != null && key == _promptKey) return _promptText;
        _promptKey = key;
        return _promptText = key.Item2
            ? "Someone is looking through the telescope"
            : $"{InputHints.Tag(PlayerInput.InteractMount)} Look through the telescope";
    }

    /// <summary>E at a telescope: ask to look through it.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } t || TelescopeView.Current != null || t.TelescopeAt(p) is not { } o) return false;
        if (t._busy.Contains(o.Id))
        {
            ItemController.Instance?.Toast("Someone is looking through it.");
            return true;
        }
        t._asker = p;
        if (t.Online) t.RpcId(1, MethodName.AskLook, o.Id);
        else t.Answer(t.ServeLook(1, o.Id), o.Id);
        return true;
    }

    /// <summary>The view closed: the telescope is free again.</summary>
    public void Leave(long id)
    {
        if (Online) RpcId(1, MethodName.AskLeave, id);
        else ServeLeave(1, id);
    }

    // ---- the server ---------------------------------------------------------------------------------

    /// <summary>
    /// Server: <paramref name="peer"/> looks through telescope <paramref name="id"/>. Null when granted,
    /// else why not. The seam a bot would take (it has no reason to: nothing changes in the game).
    /// </summary>
    public string? ServeLook(long peer, long id)
    {
        string? refused = _placed == null || !_placed.All.TryGetValue(id, out var o) || o.Kind != PlacedKind.Telescope ? "It is not there any more."
            : _users.TryGetValue(id, out long user) && user != peer ? "Someone is looking through it."
            : !InReach(peer, o) ? "Too far away."
            : null;
        if (refused != null) return refused;
        // one telescope per player
        foreach (var (other, by) in _users.ToList())
            if (by == peer && other != id) Free(other);
        _users[id] = peer;
        if (Online) Rpc(MethodName.Busy, id, peer);
        GD.Print($"[telescope] #{id} looked through by {peer}");
        return null;
    }

    /// <summary>Server: <paramref name="peer"/> stopped looking through <paramref name="id"/>.</summary>
    public void ServeLeave(long peer, long id)
    {
        if (_users.TryGetValue(id, out long user) && user == peer) Free(id);
    }

    /// <summary>Server: why telescope <paramref name="o"/> cannot be packed up now, or null.</summary>
    public static string? RemoveProblem(PlacedObject o) =>
        o.Kind == PlacedKind.Telescope && _serving is { } s && s._users.ContainsKey(o.Id) ? "Someone is looking through it." : null;

    private bool InReach(long peer, PlacedObject o)
    {
        if (!Online) return true;
        if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body) return false;
        return body.Global.DistanceTo(GlobalPos.FromLv95(o.E, o.N, o.Altitude)) <= Reach + ServerSlack;
    }

    private void Free(long id)
    {
        if (!_users.Remove(id)) return;
        if (Online) Rpc(MethodName.Busy, id, 0L);
        GD.Print($"[telescope] #{id} free");
    }

    private void OnPeerGone(long peer)
    {
        foreach (var (id, by) in _users.ToList())
            if (by == peer) Free(id);
    }

    private void OnRemoved(long id)
    {
        if (Serving) Free(id);
        _busy.Remove(id);
        // the telescope this client looks through went: the view closes
        if (TelescopeView.Current is { } view && view.Id == id) view.Close();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskLook(long id)
    {
        long peer = Multiplayer.GetRemoteSenderId();
        string? refused = ServeLook(peer, id);
        RpcId(peer, MethodName.Answered, id, refused ?? "");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskLeave(long id) => ServeLeave(Multiplayer.GetRemoteSenderId(), id);

    // ---- the client ---------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Answered(long id, string refused) => Answer(refused.Length > 0 ? refused : null, id);

    /// <summary>Who looks through telescope <paramref name="id"/> now (0: nobody).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Busy(long id, long peer)
    {
        if (peer != 0 && peer != Multiplayer.GetUniqueId()) _busy.Add(id);
        else _busy.Remove(id);
    }

    private void Answer(string? refused, long id)
    {
        if (refused != null)
        {
            LastRefused = refused;
            ItemController.Instance?.Toast(refused);
            return;
        }
        if (NodeOf(id) is { } node) TelescopeView.Begin(this, id, node, _asker);
        else Leave(id);
    }
}
