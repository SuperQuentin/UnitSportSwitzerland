using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// Spectators (#914): every player within <see cref="WatchRadius"/> of a running battle, not in it,
/// gets its state twice a second and on each change (a cut graded, a verdict, a laugh): names, aura,
/// the turn, the combo being performed and its grades, the last verdict. <c>AuraWatchHud</c> draws it
/// with a hint to cheer (the cheers count for the crowd bonus). Leaving the radius, or the battle
/// ending, sends <c>Unwatch</c>. Bots never get it (a negative id is "everyone but" to Godot).
/// </summary>
public partial class AuraManager
{
    public const float WatchRadius = 35f;
    private const double WatchPeriod = 0.5, WatchMinGap = 0.1;

    private readonly List<long> _watchNow = new();
    private readonly List<long> _watchGone = new();

    private void StepWatch(Match m, double now)
    {
        if (now < m.WatchAt + WatchMinGap || !m.WatchDirty && now < m.WatchAt + WatchPeriod) return;
        m.WatchAt = now;
        m.WatchDirty = false;
        var r = m.Rules;
        _watchNow.Clear();
        foreach (int peer in Multiplayer.GetPeers())
        {
            if (peer == r.A || peer == r.B || peer <= 0) continue;
            if (Body(peer) is not { } body || body.Global.HorizontalDistanceTo(m.Centre) > WatchRadius) continue;
            _watchNow.Add(peer);
        }
        _watchGone.Clear();
        foreach (long w in m.Watchers)
            if (!_watchNow.Contains(w)) _watchGone.Add(w);
        foreach (long w in _watchGone)
        {
            m.Watchers.Remove(w);
            if (Connected(w)) RpcId(w, MethodName.Unwatch, m.Id);
        }
        if (_watchNow.Count == 0) return;

        var moves = new byte[r.Plan.Length];
        var grades = new byte[r.Grades.Length];
        for (int i = 0; i < moves.Length; i++) moves[i] = (byte)r.Plan[i];
        for (int i = 0; i < grades.Length; i++) grades[i] = (byte)r.Grades[i];
        var last = r.LastTurn;
        foreach (long w in _watchNow)
        {
            m.Watchers.Add(w);
            RpcId(w, MethodName.Watch, m.Id, Who(r.A), Who(r.B), r.A, r.B, r.TotalA, r.TotalB, r.Turn, r.Performer, (int)r.Phase,
                r.PhaseEnds, r.Beat, r.T0, moves, grades, r.LaughsA, r.LaughsB, r.Winner, m.Music,
                last?.Performer ?? 0, last?.Score.Total ?? 0, m.Centre.E, m.Centre.N);
        }
    }

    private void WatchEnd(Match m)
    {
        foreach (long w in m.Watchers)
            if (Connected(w)) RpcId(w, MethodName.Unwatch, m.Id);
        m.Watchers.Clear();
    }

    // ---- client ----------------------------------------------------------------------------

    /// <summary>A battle the local player watches from nearby, as the server last said.</summary>
    public sealed class WatchView
    {
        public int Id;
        public string NameA = "", NameB = "", Music = "";
        public long A, B, Performer, Winner, LastPerformer;
        public int TotalA, TotalB, Turn, LaughsA, LaughsB, LastTotal;
        public AuraPhase Phase;
        public double PhaseEnds, Beat = AuraRules.BeatSeconds, T0;
        public AuraMove[] Plan = Array.Empty<AuraMove>();
        public AuraGrade[] Grades = Array.Empty<AuraGrade>();
        public GlobalPos Centre;
        /// <summary>Local game time of the last update: dropped when the server falls silent.</summary>
        public double At;
        public string NameOf(long peer) => peer == A ? NameA : NameB;
        public int Round => Turn / 2 + 1;
    }

    /// <summary>The battle being watched, or null.</summary>
    public WatchView? Watched => _watched is { } w && GameClock.Now - w.At < 3.0 ? w : null;
    private WatchView? _watched;

    /// <summary>The beat the local player sees battles at: its own, the watched one's, or the fixed 120.</summary>
    public double BeatHere => Current?.Beat ?? Watched?.Beat ?? AuraRules.BeatSeconds;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Watch(int id, string nameA, string nameB, long a, long b, int totalA, int totalB, int turn, long performer, int phase,
        double phaseEnds, double beat, double t0, byte[] moves, byte[] grades, int laughsA, int laughsB, long winner, string music,
        long lastPerformer, int lastTotal, double e, double n)
    {
        if (_watched == null || _watched.Id != id)
        {
            _watched = new WatchView { Id = id };
            GD.Print($"[aura] watching #{id}: {nameA} vs {nameB}");
        }
        var w = _watched;
        w.NameA = nameA; w.NameB = nameB; w.A = a; w.B = b; w.TotalA = totalA; w.TotalB = totalB; w.Turn = turn;
        w.Performer = performer; w.Phase = (AuraPhase)phase; w.PhaseEnds = phaseEnds; w.Beat = beat; w.T0 = t0;
        if (w.Plan.Length != moves.Length) w.Plan = new AuraMove[moves.Length];
        for (int i = 0; i < moves.Length; i++) w.Plan[i] = (AuraMove)moves[i];
        if (w.Grades.Length != grades.Length) w.Grades = new AuraGrade[grades.Length];
        for (int i = 0; i < grades.Length; i++) w.Grades[i] = (AuraGrade)grades[i];
        w.LaughsA = laughsA; w.LaughsB = laughsB; w.Winner = winner; w.Music = music;
        w.LastPerformer = lastPerformer; w.LastTotal = lastTotal; w.Centre = new GlobalPos(e, n, 0);
        w.At = GameClock.Now;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Unwatch(int id)
    {
        if (_watched?.Id != id) return;
        _watched = null;
        GD.Print($"[aura] stopped watching #{id}");
    }
}
