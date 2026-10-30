using Godot;
using UnitSport.Adventure;
using UnitSport.Occasions.Festivals.Fahnen;

namespace UnitSport.Occasions.Festivals;

// Fahnenschwingen on the festival service (#1269 part 5, protocol 68): the server plans a routine from the slot a request falls in
// (so players who ask together swing together), the client plays it and sends what it did (stamps and gesture sectors), the server
// grades it with the same pure rules, settles the whole slot a moment after it ended (the in-step bonus needs everyone's grades) and pays
// through Award. Bots take the same two server methods; they make up the field and earn nothing.
public partial class FestivalService
{
    private sealed record FahneRun(long Peer, string Festival, string Instance, long Slot, double Start, FahnenRoutine Routine);

    private sealed record FahnePending(FahneRun Run, FahnenOutcome Outcome, double SettleAt);

    private readonly Dictionary<long, FahneRun> _fahneRuns = new();
    private readonly List<FahnePending> _fahnePending = new();
    private readonly List<(FahneRun Run, double Skill)> _fahneBots = new();
    private double _nextFahneTick;

    /// <summary>Seconds after a routine's end that a slot is settled: every client has sent what it did by then.</summary>
    public const double FahneSettleSeconds = 2.5;

    /// <summary>A submission may be this late (s) after the routine's end, or this far ahead of the clock.</summary>
    private const double FahneGrace = 6, FahneAhead = 0.6;

    /// <summary>Client: a routine was planned for this player (its start on the server clock, its seed).</summary>
    public event Action<double, int>? FahneStarted;

    /// <summary>Client: a routine was settled: score, Batzen granted, whether it was in step with another, figures landed Good or better, figures, dropped.</summary>
    public event Action<int, int, bool, int, int, bool>? FahneScored;

    /// <summary>The Älplerfest's flag ground, if it is on, the game is open and the festival is held in this region.</summary>
    public (RunningFestival Run, double E, double N)? FahneGroundNow()
    {
        if (RunningOf(FestivalCatalog.AelplerfestId) is not { Site: { } site } run || FestivalCatalog.Get(run.Id) is not { } def
            || !def.IsOpen(FestivalCatalog.FahneGame, run.Day)) return null;
        var (e, n) = FestivalCatalog.FahneGround(site);
        return (run, e, n);
    }

    /// <summary>Whether the body of <paramref name="peer"/> stands on the flag ground.</summary>
    public bool OnFahneGround(long peer) =>
        FahneGroundNow() is { } g && _adventure.PositionOf?.Invoke(peer) is { } at
        && Math.Sqrt((at.E - g.E) * (at.E - g.E) + (at.N - g.N) * (at.N - g.N)) <= FestivalCatalog.FahneGroundRadius;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> asks to swing the flag. The routine starts at the next slot boundary
    /// at least <see cref="FahnenRules.LeadSeconds"/> away. Returns null when it was planned, else why not.
    /// </summary>
    public string? ServeFahneStart(long peer)
    {
        if (!Authority) return "Not here.";
        if (FahneGroundNow() is not { } g) return Tell(peer, "The flag swingers are not out yet.");
        if (!OnFahneGround(peer)) return Tell(peer, "Go to the flag ground to swing.");
        if (!_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        double now = Net.ClockSync.ServerNow;
        if (_fahneRuns.TryGetValue(peer, out var old) && now < old.Start + FahnenRules.Seconds(old.Routine.TotalBeats) + FahneGrace)
            return Tell(peer, "You are swinging already.");

        long slot = FahnenRules.SlotOf(now);
        var run = new FahneRun(peer, g.Run.Id, g.Run.Instance, slot, FahnenRules.SlotStart(slot), FahnenRules.Plan(FahnenRules.SeedOf(g.Run.Instance, slot)));
        _fahneRuns[peer] = run;
        if (Connected(peer)) RpcId(peer, MethodName.TellFahneStart, run.Start, run.Routine.Seed);
        else if (!_networked && peer > 0) TellFahneStart(run.Start, run.Routine.Seed);
        return null;
    }

    /// <summary>
    /// Server (and a bot's way in): what <paramref name="peer"/> did in its routine. Read back with <see cref="FahnenRules.FromWire"/>,
    /// refused when it cannot be a client's (stamps ahead of the clock, a routine that was never planned or is long over, a body that
    /// left the ground), graded and queued for the slot's settlement. Returns null when it counted, else why not.
    /// </summary>
    public string? ServeFahneRoutine(long peer, int[] stamps, byte[] sectors)
    {
        if (!Authority) return "Not here.";
        if (!_fahneRuns.Remove(peer, out var run)) return "No routine to hand in.";
        double now = Net.ClockSync.ServerNow, length = FahnenRules.Seconds(run.Routine.TotalBeats);
        if (now > run.Start + length + FahneGrace) return Tell(peer, "Too late: that routine is over.");
        if (FahnenRules.FromWire(stamps, sectors) is not { } inputs) return Tell(peer, "That routine makes no sense.");
        if (FahnenRules.LastStamp(inputs) / 1000.0 > now - run.Start + FahneAhead) return Tell(peer, "That routine is from the future.");
        if (!OnFahneGround(peer)) return Tell(peer, "You left the flag ground.");

        var outcome = FahnenRules.Grade(run.Routine, inputs);
        double settle = Math.Max(now, run.Start + length) + FahneSettleSeconds;
        _fahnePending.Add(new FahnePending(run, outcome, settle));
        return null;
    }

    /// <summary>A bot (or a check) swings the flag with this skill (0..1): it asks, and hands in its routine when it would have finished.</summary>
    public string? ServeFahneBot(long peer, double skill)
    {
        if (ServeFahneStart(peer) is { } why) return why;
        _fahneBots.Add((_fahneRuns[peer], skill));
        return null;
    }

    private void FahneTick()
    {
        if (!Authority || _fahneBots.Count + _fahnePending.Count == 0) return;
        double now = Net.ClockSync.ServerNow;
        for (int i = _fahneBots.Count - 1; i >= 0; i--)
        {
            var (run, skill) = _fahneBots[i];
            if (now < run.Start + FahnenRules.Seconds(run.Routine.TotalBeats) + 0.1) continue;
            _fahneBots.RemoveAt(i);
            var (stamps, path) = FahnenRules.ToWire(FahnenRules.BotRoutine(run.Routine, skill, (int)(run.Slot ^ run.Peer)));
            if (_fahneRuns.TryGetValue(run.Peer, out var current) && current == run) ServeFahneRoutine(run.Peer, stamps, path);
        }
        // settle every slot that is due, whole: the in-step bonus compares the players of one slot
        foreach (long slot in _fahnePending.Where(p => p.SettleAt <= now).Select(p => p.Run.Slot).Distinct().ToList())
        {
            var group = _fahnePending.Where(p => p.Run.Slot == slot).ToList();
            _fahnePending.RemoveAll(p => p.Run.Slot == slot);
            var synced = FahnenRules.SyncedOf(group.Select(p => p.Outcome.Grades).ToList());
            for (int i = 0; i < group.Count; i++) SettleFahne(group[i], synced[i]);
        }
    }

    private void SettleFahne(FahnePending p, bool synced)
    {
        int score = FahnenRules.WithSync(p.Outcome.Score, synced);
        int batzen = FahnenRules.BatzenFor(score);
        long peer = p.Run.Peer;
        int granted = batzen > 0 || score > 0
            ? Award(peer, p.Run.Festival, batzen, $"swung the flag, {score} points{(synced ? " in step" : "")}", FestivalCatalog.FahneGame, score)
            : 0;
        LastFahne = (peer, score, granted, synced);
        int figures = p.Outcome.Grades.Length;
        if (Connected(peer)) RpcId(peer, MethodName.TellFahneResult, score, granted, synced, p.Outcome.Hits, figures, p.Outcome.Dropped);
        else if (!_networked && peer > 0) TellFahneResult(score, granted, synced, p.Outcome.Hits, figures, p.Outcome.Dropped);
    }

    /// <summary>Authority: the last routine settled (peer, score, Batzen granted, in step), for the checks.</summary>
    public (long Peer, int Score, int Batzen, bool Synced) LastFahne { get; private set; }

    public override void _PhysicsProcess(double delta)
    {
        if (Authority) DesalpeTick(delta);
        if (!Authority || Net.ClockSync.ServerNow < _nextFahneTick) return;
        _nextFahneTick = Net.ClockSync.ServerNow + 0.25;
        FahneTick();
    }

    // ---- the owner's requests ---------------------------------------------------------------------

    /// <summary>Client: ask to swing the flag. Offline the local player is the authority.</summary>
    public void AskFahneStart()
    {
        if (Authority) ServeFahneStart(AdventureService.LocalPeer);
        else RpcId(1, MethodName.RequestFahneStart);
    }

    /// <summary>Client: hand in the routine just played.</summary>
    public void AskFahneRoutine(int[] stamps, byte[] sectors)
    {
        if (Authority) ServeFahneRoutine(AdventureService.LocalPeer, stamps, sectors);
        else RpcId(1, MethodName.RequestFahneRoutine, stamps, sectors);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestFahneStart()
    {
        if (_server) ServeFahneStart(Multiplayer.GetRemoteSenderId());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestFahneRoutine(int[] stamps, byte[] sectors)
    {
        if (_server) ServeFahneRoutine(Multiplayer.GetRemoteSenderId(), stamps, sectors);
    }

    // ---- server -> owner --------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellFahneStart(double start, int seed) => FahneStarted?.Invoke(start, seed);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellFahneResult(int score, int batzen, bool synced, int hits, int figures, bool dropped) =>
        FahneScored?.Invoke(score, batzen, synced, hits, figures, dropped);
}
