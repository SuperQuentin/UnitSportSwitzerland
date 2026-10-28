using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors.Pinball;

// A client's side of pinball (#1325): asking, being handed the table to run, and showing the machines of the
// venue it stands in that others play: the poses interpolated a little behind, the backbox and lamps, the sounds.
public partial class PinballTables
{
    /// <summary>A refusal for this player ("wait for the next game"), for the HUD.</summary>
    public event Action<string>? Told;

    /// <summary>This peer is up at a machine: run its table (a new game, or one taken over from its log).</summary>
    public event Action<string, int, PinballGame>? RunHere;

    /// <summary>Press Start for a player joining this peer's game.</summary>
    public event Action<string, int>? JoinHere;

    /// <summary>This peer is no longer up at a machine.</summary>
    public event Action<string, int>? StopHere;

    /// <summary>How far behind the stream the watchers' balls are shown [s]: a frame and a bit.</summary>
    private const double Delay = 0.09;
    private string _asked = "";
    private readonly Dictionary<(string, int), (byte[] Bytes, PinballScreen Screen)> _screens = new();
    private readonly Vector3[] _balls = new Vector3[PinballCabinet.MaxBalls];
    private readonly float[] _flips = new float[4];

    // ---- asking --------------------------------------------------------------------------------

    public void AskStart(string key, int index)
    {
        if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestStart, key, index);
        else ServeStart(MyId, key, index);
    }

    public void AskLeave(string key, int index)
    {
        if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestLeave, key, index);
        else ServeLeave(MyId, key, index);
    }

    /// <summary>The player up's frame: the game's new events and the poses, to the server.</summary>
    public void SendFrame(string key, int index, PinballGame g)
    {
        var events = PinballGame.PackEvents(g.TakeOutbox());
        var poses = PinballGame.PackPoses(g.Solver);
        if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestFrame, key, index, g.Time, events, poses);
        else ServeFrame(MyId, key, index, g.Time, events, poses);
    }

    // ---- what the server sends ---------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(string key, int index, int table, long[] players, long runner, byte[] screen)
    {
        var m = MachineOf(key, index, table);
        if (Online && !Multiplayer.IsServer())
        {
            m.Players.Clear();
            m.Players.AddRange(players);
            m.Runner = runner;
        }
        m.Screen = screen;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Run(string key, int index, int table, int seed, byte[] log)
    {
        var m = MachineOf(key, index, table);
        m.Seed = seed;
        if (Online && !Multiplayer.IsServer()) m.Runner = MyId;
        var game = new PinballGame(PinballLayouts.Get(table), seed);
        var events = PinballGame.UnpackEvents(log);
        if (events.Count == 0) game.Start();
        else game.Resume(events);
        GD.Print($"[pinball] up at {key} #{index}: {PinballLayouts.Get(table).Title}, {(events.Count == 0 ? "a new game" : $"player {game.Rules.Up + 1}, ball {game.Rules.Current?.Ball}")}");
        if (RunHere != null) RunHere(key, index, game);
        else
        {
            // nobody here to play it (the player walked off): hand the turn on
            AskLeave(key, index);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Join(string key, int index) => JoinHere?.Invoke(key, index);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Stop(string key, int index)
    {
        if (Machines.TryGetValue((key, index), out var m) && Online && !Multiplayer.IsServer() && m.Runner == MyId) m.Runner = 0;
        StopHere?.Invoke(key, index);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Frame(string key, int index, byte[] poses, byte[] events)
    {
        if (!Machines.TryGetValue((key, index), out var m)) return;
        m.Before = m.Now;
        m.BeforeAt = m.NowAt;
        m.Now = PinballGame.UnpackPoses(poses);
        m.NowAt = Time.GetTicksMsec() / 1000.0;
        if (InteriorManager.Instance?.BuiltNode(key)?.PinballOf(index) is { } cabinet)
            foreach (var e in PinballGame.UnpackEvents(events)) cabinet.Hear(e);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tell(string why) => Told?.Invoke(why);

    // ---- showing the machines others play ------------------------------------------------------

    public override void _Process(double delta)
    {
        ServerProcess(delta);
        ClientProcess();
    }

    private void ClientProcess()
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase) && Online && Multiplayer.IsServer()) return;
        var im = InteriorManager.Instance;
        if (im?.Current is not { } here)
        {
            if (_asked.Length > 0 && Online && !Multiplayer.IsServer()) Machines.Clear();
            _asked = "";
            return;
        }
        if (_asked != here.Key)
        {
            _asked = here.Key;
            if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestState, here.Key);
        }
        if (im.BuiltNode(here.Key) is not { } node) return;
        double now = Time.GetTicksMsec() / 1000.0;
        var mine = Running();
        foreach (var ((key, index), m) in Machines)
        {
            if (key != here.Key || mine is { } r && r.Key == key && r.Index == index) continue;
            if (node.PinballOf(index) is not { } cabinet) continue;
            if (m.Screen.Length > 0)
            {
                // unpacked only when it changed
                if (!_screens.TryGetValue((key, index), out var shown) || !ReferenceEquals(shown.Bytes, m.Screen))
                    _screens[(key, index)] = shown = (m.Screen, PinballScreen.Unpack(m.Screen));
                cabinet.ShowScreen(shown.Screen, now);
            }
            if (m.Runner == 0 || m.Now is not { } poses)
            {
                cabinet.ShowPoses(ReadOnlySpan<Vector3>.Empty, cabinet.RestAngles);
                continue;
            }
            // between the last two frames, a little behind the latest
            float w = 1f;
            if (m.Before is { } before && m.NowAt > m.BeforeAt)
                w = Mathf.Clamp((float)((now - Delay - m.BeforeAt) / (m.NowAt - m.BeforeAt)), 0, 1);
            int n = Math.Min(poses.Balls.Length, _balls.Length);
            for (int i = 0; i < n; i++)
                _balls[i] = m.Before is { } b && i < b.Balls.Length ? b.Balls[i].Lerp(poses.Balls[i], w) : poses.Balls[i];
            int f = Math.Min(poses.Flippers.Length, _flips.Length);
            for (int i = 0; i < f; i++)
                _flips[i] = m.Before is { } b2 && i < b2.Flippers.Length ? Mathf.Lerp(b2.Flippers[i], poses.Flippers[i], w) : poses.Flippers[i];
            cabinet.ShowPoses(_balls.AsSpan(0, n), _flips.AsSpan(0, f));
            int down = poses.Down;
            cabinet.ShowDrops(wall => wall < 31 && (down >> wall & 1) != 0);
        }
    }

    private string NameOf(long peer) => GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName)?.NameOfPeer(peer) ?? $"Player {peer}";
}
