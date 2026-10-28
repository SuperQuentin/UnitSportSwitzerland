using Godot;

namespace UnitSport.Interiors.Pinball;

// The server's side of pinball (#1325): the Serve* actions (a player's RPC or a bot's call), the rules'
// mirror fed by the player up's frames, the turn handed from client to client, the state sent to the
// peers inside a venue.
public partial class PinballTables
{
    /// <summary>How near a machine's front a body must be to start or join it [m].</summary>
    private const float Reach = 1.4f;
    private readonly Random _seeds = new();
    private double _housekeeping;

    // ---- the actions ---------------------------------------------------------------------------

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> presses Start at machine <paramref name="index"/> of venue
    /// <paramref name="key"/>. An idle machine (or a finished game) starts a game with them up; a game in its
    /// first ball takes them as one more player (up to four). A bot calls this with its own id.
    /// </summary>
    public bool ServeStart(long peer, string key, int index)
    {
        if (Plan(peer, key) is not { } l || index < 0 || index >= l.Furniture.Count || l.Furniture[index].Type != FurnitureType.Pinball) return false;
        if (!Near(peer, l, index)) return Refuse(peer, "Step up to the machine.");
        var m = MachineOf(key, index, TableOf(l.Furniture[index]));
        if (m.Rules is { State: PinballRules.Phase.Playing } r)
        {
            if (m.Players.Contains(peer) || m.Joining.Contains(peer)) return false;
            if (m.Players.Count + m.Joining.Count >= PinballRules.MaxPlayers || r.Players.Exists(p => p.Ball > 1))
                return Refuse(peer, "This game is under way: wait for the next one.");
            m.Joining.Enqueue(peer);
            // the player up's client presses Start for them, so the add rides the game's own log
            Send(m.Runner, MethodName.Join, key, index);
            GD.Print($"[pinball] peer {peer} joins the game at {key} #{index}");
            return true;
        }
        m.Seed = _seeds.Next(1, int.MaxValue);
        m.Rules = new PinballRules(PinballLayouts.Get(m.Table), m.Seed);
        m.Players.Clear();
        m.Players.Add(peer);
        m.Joining.Clear();
        m.Log.Clear();
        m.Runner = peer;
        m.LastFrame = Clock;
        GD.Print($"[pinball] peer {peer} starts a game of {PinballLayouts.Get(m.Table).Title} at {key} #{index}");
        Send(peer, MethodName.Run, key, index, m.Table, m.Seed, Array.Empty<byte>());
        Changed(key, index, m);
        return true;
    }

    /// <summary>
    /// Server: a frame from the player up's client: the game's events since the last one (applied to the
    /// rules' mirror and the log), the balls' and flippers' poses (for the watchers), its game time.
    /// </summary>
    public bool ServeFrame(long peer, string key, int index, double time, byte[] events, byte[] poses)
    {
        if (!Machines.TryGetValue((key, index), out var m) || m.Runner != peer || m.Rules is not { } rules) return false;
        m.LastFrame = Clock;
        var list = PinballGame.UnpackEvents(events);
        foreach (var e in list)
        {
            // Start pressed during a game: the player who asked to join comes in
            if (e.Kind == PinballSolver.EventKind.Switch && e.Id == PinballGame.StartPress && rules.State == PinballRules.Phase.Playing
                && m.Joining.Count > 0)
                m.Players.Add(m.Joining.Dequeue());
            PinballGame.Mirror(rules, new[] { e }, e.Time);
        }
        rules.Advance(time);
        m.Log.AddRange(list);
        foreach (long other in PeersInside(key))
            if (other != peer) RpcId(other, MethodName.Frame, key, index, poses, events);
        if (!Online && peer != MyId) Frame(key, index, poses, events);
        AfterRules(key, index, m);
        return true;
    }

    /// <summary>Server: <paramref name="peer"/> steps away from machine <paramref name="index"/>; up, their ball ends (skipped).</summary>
    public bool ServeLeave(long peer, string key, int index)
    {
        if (!Machines.TryGetValue((key, index), out var m)) return false;
        if (m.Runner == peer) SkipRunner(key, index, m, "left the machine");
        return true;
    }

    // ---- turns -----------------------------------------------------------------------------------

    /// <summary>After the rules moved: a game over frees the machine; a turn passed goes to the next player's client.</summary>
    private void AfterRules(string key, int index, Machine m)
    {
        var rules = m.Rules!;
        if (rules.State != PinballRules.Phase.Playing)
        {
            if (m.Runner != 0) GD.Print($"[pinball] {key} #{index}: game over, {string.Join(", ", rules.Players.Select(p => p.Score))}");
            m.Runner = 0;
            m.Joining.Clear();
            Changed(key, index, m);
            return;
        }
        if (rules.Up < m.Players.Count && m.Players[rules.Up] != m.Runner) HandOver(key, index, m);
        else Changed(key, index, m);
    }

    /// <summary>The turn passed: the next player still here runs the table from the log; one gone is skipped.</summary>
    private void HandOver(string key, int index, Machine m)
    {
        var rules = m.Rules!;
        double at = m.Log.Count > 0 ? m.Log[^1].Time : 0;
        int step = m.Log.Count > 0 ? m.Log[^1].Step : 0;
        for (int guard = 0; guard < 8 && rules.State == PinballRules.Phase.Playing; guard++)
        {
            long next = m.Players[rules.Up];
            if (Present(next, key))
            {
                m.Runner = next;
                m.LastFrame = Clock;
                GD.Print($"[pinball] {key} #{index}: player {rules.Up + 1} (peer {next}) is up");
                Send(next, MethodName.Run, key, index, m.Table, m.Seed, PinballGame.PackEvents(m.Log));
                Changed(key, index, m);
                return;
            }
            var skip = new PinballSolver.Event(++step, PinballSolver.EventKind.Switch, PinballGame.SkipMark);
            PinballGame.Mirror(rules, new[] { skip }, at);
            m.Log.Add(skip);
        }
        AfterRules(key, index, m);
    }

    /// <summary>The player up is gone or stepped away: their ball (and turns) end, the next one is up.</summary>
    private void SkipRunner(string key, int index, Machine m, string why)
    {
        if (m.Rules is not { State: PinballRules.Phase.Playing } rules) { m.Runner = 0; Changed(key, index, m); return; }
        GD.Print($"[pinball] {key} #{index}: peer {m.Runner} {why}: skipped");
        long was = m.Runner;
        int step = m.Log.Count > 0 ? m.Log[^1].Step + 1 : 1;
        var skip = new PinballSolver.Event(step, PinballSolver.EventKind.Switch, PinballGame.SkipMark);
        PinballGame.Mirror(rules, new[] { skip }, skip.Time);
        m.Log.Add(skip);
        m.Runner = 0;
        if (was != 0) Send(was, MethodName.Stop, key, index);
        AfterRules(key, index, m);
    }

    private bool Present(long peer, string key) =>
        !Online ? peer == MyId : peer == MyId || Multiplayer.GetPeers().Contains((int)peer) && InteriorManager.Instance?.SpaceOf(peer) == key;

    // ---- housekeeping: a player up gone silent or out of the venue -------------------------------

    private void ServerProcess(double delta)
    {
        if (Online && !Multiplayer.IsServer()) return;
        _housekeeping -= delta;
        if (_housekeeping > 0) return;
        _housekeeping = 0.5;
        double now = Clock;
        List<(string, int, Machine, string)>? gone = null;
        foreach (var ((key, index), m) in Machines)
        {
            if (m.Runner == 0 || m.Rules is not { State: PinballRules.Phase.Playing }) continue;
            if (!Present(m.Runner, key)) (gone ??= new()).Add((key, index, m, "left the venue"));
            else if (Online && m.Runner != MyId && now - m.LastFrame > RunnerTimeout) (gone ??= new()).Add((key, index, m, "went silent"));
        }
        if (gone != null)
            foreach (var (key, index, m, why) in gone) SkipRunner(key, index, m, why);
    }

    // ---- where people stand --------------------------------------------------------------------

    /// <summary>The venue's plan if <paramref name="peer"/> is inside it (offline: the one player is), else null.</summary>
    private InteriorLayout? Plan(long peer, string key)
    {
        if (InteriorManager.Instance is not { } im || im.PlanOf(key) is not { } l) return null;
        if (Online && im.SpaceOf(peer) != key) { GD.Print($"[pinball] peer {peer} is not in {key}: refused"); return null; }
        return l;
    }

    private bool Near(long peer, InteriorLayout l, int index)
    {
        if (InteriorManager.Instance?.BodyLocal(peer, l) is not { } at) return !Online;
        // interiors stand under the world: offline a body outside the building is no body to judge by
        if (!Online && Mathf.Abs(at.Y) > 50f) return true;
        var local = PieceFrame(l, l.Furniture[index]).AffineInverse() * at;
        return Mathf.Abs(local.X) < 0.6f && local.Z > 0 && local.Z < 0.7f + Reach && Mathf.Abs(local.Y) < 2.5f;
    }

    private bool Refuse(long peer, string why)
    {
        GD.Print($"[pinball] peer {peer}: {why}");
        if (peer == MyId) Told?.Invoke(why);
        else if (Online && peer > 0) RpcId(peer, MethodName.Tell, why);
        return false;
    }

    // ---- sending -------------------------------------------------------------------------------

    /// <summary>An RPC to one peer, or straight in when that peer is this one (offline, a listen server).</summary>
    private void Send(long peer, StringName method, params Variant[] args)
    {
        if (peer == MyId || !Online) Call(method, args);
        else if (peer > 0) RpcId(peer, method, args);
    }

    /// <summary>A machine changed: its players, who is up, the backbox and lamps, to everyone inside.</summary>
    private void Changed(string key, int index, Machine m)
    {
        var screen = m.Rules != null && (m.Rules.State != PinballRules.Phase.Attract)
            ? PinballScreen.Of(m.Rules).Pack() : PinballScreen.Idle(PinballLayouts.Get(m.Table)).Pack();
        var players = m.Players.ToArray();
        if (Online)
            foreach (long peer in PeersInside(key)) RpcId(peer, MethodName.State, key, index, m.Table, players, m.Runner, screen);
        if (!Online || !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase)) State(key, index, m.Table, players, m.Runner, screen);
    }

    private IEnumerable<long> PeersInside(string key)
    {
        if (InteriorManager.Instance is not { } im || !Online) yield break;
        foreach (int peer in Multiplayer.GetPeers())
            if (im.SpaceOf(peer) == key) yield return peer;
    }

    // ---- client RPC entry points -------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStart(string key, int index)
    {
        if (Multiplayer.IsServer()) ServeStart(Multiplayer.GetRemoteSenderId(), key, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestFrame(string key, int index, double time, byte[] events, byte[] poses)
    {
        if (Multiplayer.IsServer()) ServeFrame(Multiplayer.GetRemoteSenderId(), key, index, time, events, poses);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave(string key, int index)
    {
        if (Multiplayer.IsServer()) ServeLeave(Multiplayer.GetRemoteSenderId(), key, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestState(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        foreach (var ((k, index), m) in Machines)
        {
            if (k != key) continue;
            var screen = m.Rules != null ? PinballScreen.Of(m.Rules).Pack() : PinballScreen.Idle(PinballLayouts.Get(m.Table)).Pack();
            RpcId(peer, MethodName.State, key, index, m.Table, m.Players.ToArray(), m.Runner, screen);
        }
    }
}
