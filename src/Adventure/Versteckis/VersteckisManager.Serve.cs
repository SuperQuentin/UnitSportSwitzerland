using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// The server's half of every act (#1268, plan "Network"): the RPCs a client sends and the <c>Serve*</c> paths a bot's
/// brain calls directly, each checked against the server's own positions; the 4 Hz checks on every hider's published
/// body (speed under the walk cap, inside the circle, on the ground, not inside a building); the seeker held at the
/// church door in Hide. A catch is judged only on these server positions, never on where a client drew a prop.
/// </summary>
public partial class VersteckisManager
{
    /// <summary>Requests a second a peer may make; more are dropped.</summary>
    private const int AsksPerSecond = 4;

    private bool Allow(Game g, long peer)
    {
        var run = g.RunOf(peer);
        double now = RealClock.Now;
        if (now - run.LastAsk >= 1) { run.LastAsk = now; run.Asks = 0; }
        return ++run.Asks <= AsksPerSecond;
    }

    // ---- client -> server ----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDisguise(int kind) => Reply(ServeDisguise(Multiplayer.GetRemoteSenderId(), kind));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTag(double e, double n, float alt) => Reply(ServeTag(Multiplayer.GetRemoteSenderId(), e, n, alt));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSniff() => Reply(ServeSniff(Multiplayer.GetRemoteSenderId()));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDecoy() => Reply(ServeDecoy(Multiplayer.GetRemoteSenderId()));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSprint() => Reply(ServeSprint(Multiplayer.GetRemoteSenderId()));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestMoo() => Reply(ServeMoo(Multiplayer.GetRemoteSenderId()));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestObjective(int site, int step) => Reply(ServeObjective(Multiplayer.GetRemoteSenderId(), site, step));

    /// <summary>A refusal goes back to the asking player as a private line (never to a bot).</summary>
    private void Reply(string? why)
    {
        long peer = Multiplayer.GetRemoteSenderId();
        if (why != null && why.Length > 0 && Connected(peer)) _chat?.Tell(peer, why, ChatKind.Error);
    }

    /// <summary>The game a peer hides or seeks in, with its entrant, or a refusal.</summary>
    private (Game? G, VsEntrant? E, string? Why) Act(long peer, VsRole role)
    {
        if (GameOf(peer) is not { } g || g.S.Find(peer) is not { } e) return (null, null, "You are in no game of Versteckis.");
        if (!Allow(g, peer)) return (null, null, "");
        if (g.S.Phase is not (VsPhase.Hide or VsPhase.Seek)) return (null, null, "Not now.");
        if (e.Role != role) return (null, null, role == VsRole.Seeker ? "Only the seeker tags." : "Only a hider does that.");
        if (role == VsRole.Hider && e.Caught) return (null, null, "You were caught: watch the others.");
        return (g, e, null);
    }

    // ---- the hiders ------------------------------------------------------------------------------

    /// <summary>
    /// A hider becomes <paramref name="kind"/>: free in Hide, once in 20 s in Seek with a poof the seeker hears. The kind
    /// must be one the area's hide plan holds (no cow without a herd).
    /// </summary>
    public string? ServeDisguise(long peer, int kind)
    {
        var (g, e, why) = Act(peer, VsRole.Hider);
        if (why != null) return why;
        var s = g!.S;
        if (!VsProps.Valid(kind) || !s.Kinds.Contains((VsProp)kind)) return "Nothing like that in this village.";
        if (e!.Prop == (VsProp)kind) return null;
        double now = Now;
        if (s.Phase == VsPhase.Seek)
        {
            if (now < e.ChangeReadyAt) return $"Changing again in {e.ChangeReadyAt - now:F0} s.";
            e.ChangeReadyAt = now + VsRules.ChangeEvery;
            Sound(g, peer, VsVoice.Poof);
        }
        e.Prop = (VsProp)kind;
        GD.Print($"[vs] {e.Name} disguised as {VsProps.A(e.Prop)}");
        Push();
        return null;
    }

    /// <summary>A decoy: a still copy of the hider's prop at its feet for 60 s, one a round.</summary>
    public string? ServeDecoy(long peer)
    {
        var (g, e, why) = Act(peer, VsRole.Hider);
        if (why != null) return why;
        if (e!.DecoyUsed) return "One decoy a round.";
        if (PositionOf(peer) is not { } at || BodyOf(peer) is not { } body) return "Nowhere to put it.";
        e.DecoyUsed = true;
        var d = new VsDecoy { Id = g!.NextDecoy++, Owner = peer, Prop = e.Prop, E = at.E, N = at.N, Alt = (float)at.Alt, Yaw = body.NetYaw, Until = Now + VsRules.DecoySeconds };
        g.S.Decoys.Add(d);
        GD.Print(FormattableString.Invariant($"[vs] {e.Name} left a decoy ({VsProps.A(d.Prop)}) at {d.E:F1}/{d.N:F1}"));
        Push();
        return null;
    }

    /// <summary>A disguised sprint: 2 s at 6.5 m/s, thumps the seeker hears within 25 m; one charge, back in 30 s.</summary>
    public string? ServeSprint(long peer)
    {
        var (g, e, why) = Act(peer, VsRole.Hider);
        if (why != null) return why;
        double now = Now;
        if (now < e!.SprintReadyAt) return null;   // the client tried early: its own burst is refused by the walk check
        e.SprintReadyAt = now + VsRules.SprintRecharge;
        g!.RunOf(peer).SprintUntil = now + VsRules.SprintSeconds + 0.5;
        Sound(g, peer, VsVoice.Thump);
        Push();
        return null;
    }

    /// <summary>The prop's voice where the hider is, heard within 60 m: +1 point, 20 s apart, 5 a round, never in the last 20 s.</summary>
    public string? ServeMoo(long peer)
    {
        var (g, e, why) = Act(peer, VsRole.Hider);
        if (why != null) return why;
        var s = g!.S;
        double now = Now;
        if (s.Phase != VsPhase.Seek) return "Wait for the seeker: a Moo counts in Seek.";
        if (now < e!.MooReadyAt) return $"Again in {e.MooReadyAt - now:F0} s.";
        e.MooReadyAt = now + VsRules.MooCooldown;
        Sound(g, peer, VsProps.Of(e.Prop).Voice);
        if (VsRules.MooScores(e.Moos, s.PhaseEnds - now))
        {
            e.Moos++;
            e.MooTotal++;
            e.Score++;
            e.RoundScore++;
        }
        Push();
        return null;
    }

    // ---- the seeker ------------------------------------------------------------------------------

    /// <summary>
    /// A tag at an LV95 point: within reach of the seeker's server position, the seeker inside the circle; a hider whose
    /// footprint (its prop, its <c>NetYaw</c>, its server position) lies within 0.3 m of the point is caught. A decoy or
    /// nothing is a miss: 4 s before the next tag.
    /// </summary>
    public string? ServeTag(long peer, double e, double n, float alt)
    {
        var (g, seeker, why) = Act(peer, VsRole.Seeker);
        if (why != null) return why;
        var s = g!.S;
        if (s.Phase != VsPhase.Seek) return "Count to the end first.";
        var run = g.RunOf(peer);
        double now = Now;
        if (PositionOf(peer) is not { } me) return null;
        // a tag out of reach is refused whatever the cooldown (a forged one, or a client that drew a prop elsewhere)
        if (!VsRules.InReach(me.E, me.N, e, n))
        {
            GD.Print(FormattableString.Invariant($"[vs] {seeker!.Name}'s tag refused: {Math.Sqrt((e - me.E) * (e - me.E) + (n - me.N) * (n - me.N)):F1} m away"));
            return "Too far to tag.";
        }
        if (now < run.TagReadyAt) return null;
        if (!VsRules.Inside(s.E, s.N, s.Radius, me.E, me.N)) return "Outside the line you cannot tag.";
        // a hider's body first (nearest the point), then the decoys
        VsEntrant? hit = null;
        double best = double.MaxValue;
        foreach (var h in s.Entrants)
        {
            if (!h.Hiding || PositionOf(h.Peer) is not { } at || BodyOf(h.Peer) is not { } body) continue;
            var prop = h.Disguised ? h.Prop : VsProp.MilkCan;   // undisguised (on an objective): a body's own footprint
            if (!VsRules.TagHits(prop, at.E, at.N, body.NetYaw, e, n)) continue;
            double d = (at.E - e) * (at.E - e) + (at.N - n) * (at.N - n);
            if (d < best) { best = d; hit = h; }
        }
        if (hit != null)
        {
            run.TagReadyAt = now + VsRules.TagCooldown;
            Catch(g, hit, peer);
            return null;
        }
        run.TagReadyAt = now + VsRules.MissCooldown;
        foreach (var d in s.Decoys)
            if (VsRules.TagHits(d.Prop, d.E, d.N, d.Yaw, e, n))
            {
                s.Decoys.Remove(d);
                SoundAt(g, d.E, d.N, d.Owner, VsVoice.Poof);
                GD.Print($"[vs] {seeker!.Name} tagged {Who(s, d.Owner)}'s decoy");
                Push();
                break;
            }
        if (Connected(peer)) RpcId(peer, MethodName.Missed);
        GD.Print(FormattableString.Invariant($"[vs] {seeker!.Name} missed at {e:F1}/{n:F1}"));
        return null;
    }

    private void Catch(Game g, VsEntrant hider, long by)
    {
        var s = g.S;
        hider.Caught = true;
        hider.CaughtAt = Now;
        hider.Holding = -1;
        DropCarry(g, hider.Peer);
        if (s.Find(by) is { } seeker)
        {
            seeker.Score += VsRules.CatchPoints;
            seeker.RoundScore += VsRules.CatchPoints;
            seeker.Catches++;
        }
        foreach (var e in s.Entrants)
            if (!e.Bot && !e.Left && Connected(e.Peer)) RpcId(e.Peer, MethodName.Caught, hider.Peer, by);
        BotCaught(g, hider.Peer, by);
        GD.Print($"[vs] {Who(s, by)} caught {hider.Name} ({VsProps.A(hider.Prop)}), {s.HidersLeft} left");
        Push();
    }

    /// <summary>
    /// A sniff: for 3 s each hider within 30 m gives off a puff, up to 4 m off where it is; sent to the seeker only
    /// (never to a bot's id: a bot's brain is handed them directly).
    /// </summary>
    public string? ServeSniff(long peer)
    {
        var (g, seeker, why) = Act(peer, VsRole.Seeker);
        if (why != null) return why;
        var s = g!.S;
        if (s.Phase != VsPhase.Seek) return "Count to the end first.";
        if (seeker!.Sniffs <= 0) return "No sniff left: one comes back a minute.";
        if (PositionOf(peer) is not { } me) return null;
        seeker.Sniffs--;
        if (seeker.SniffBackAt <= 0) seeker.SniffBackAt = Now + VsRules.SniffRecharge;
        var es = new List<double>();
        var ns = new List<double>();
        foreach (var h in s.Entrants)
        {
            if (!h.Hiding || PositionOf(h.Peer) is not { } at) continue;
            if ((at.E - me.E) * (at.E - me.E) + (at.N - me.N) * (at.N - me.N) > VsRules.SniffRadius * VsRules.SniffRadius) continue;
            double a = _rng.NextDouble() * Math.Tau, r = _rng.NextDouble() * VsRules.SniffOffset;
            es.Add(at.E + Math.Cos(a) * r);
            ns.Add(at.N + Math.Sin(a) * r);
        }
        if (seeker.Bot) BotSniffed(peer, es, ns);
        else if (Connected(peer)) RpcId(peer, MethodName.Sniffed, es.ToArray(), ns.ToArray());
        GD.Print($"[vs] {seeker.Name} sniffed: {es.Count} puffs");
        Push();
        return null;
    }

    // ---- objectives (plan 4.3) -------------------------------------------------------------------

    /// <summary>
    /// An objective's step: 0 starts a hold at the site (the bell, the fire) or takes the can at its farm door; 1 finishes
    /// the hold (its time on the game clock) or sets the can on the bench; 2 lets go. The body must be within 2.5 m; the
    /// disguise drops while held and comes back 6 s after.
    /// </summary>
    public string? ServeObjective(long peer, int site, int step)
    {
        var (g, e, why) = Act(peer, VsRole.Hider);
        if (why != null) return why;
        var s = g!.S;
        if (s.Phase != VsPhase.Seek) return "The village sleeps until the seeker comes: objectives are for Seek.";
        if ((uint)site >= s.Objectives.Count) return "No such thing here.";
        var o = s.Objectives[site];
        if (o.Done) return "Done already this round.";
        if (PositionOf(peer) is not { } at) return null;
        var run = g.RunOf(peer);
        double now = Now;
        bool can = o.Site == VsSite.Can;
        double siteE = can && step == 0 ? o.FromE : o.E, siteN = can && step == 0 ? o.FromN : o.N;
        bool near = (at.E - siteE) * (at.E - siteE) + (at.N - siteN) * (at.N - siteN) <= (VsRules.SiteReach + 0.5) * (VsRules.SiteReach + 0.5);
        switch (step)
        {
            case 0:
                if (!near) return "Closer.";
                if (can && o.Carrier != 0 && o.Carrier != peer) return "Someone carries it.";
                run.HoldFrom = now;
                run.HoldSite = site;
                run.CarryMetres = 0;
                e!.Holding = site;
                e.DisguiseBackAt = 0;
                if (can) o.Carrier = peer;
                GD.Print($"[vs] {e.Name} {(can ? "took the milk can" : $"holds the {o.Site.ToString().ToLowerInvariant()}")}");
                Push();
                return null;
            case 1:
                if (run.HoldSite != site || run.HoldFrom < 0) return "Start it first.";
                if (!near) return can ? "Set it on the bench." : "You let go.";
                if (!can && now - run.HoldFrom < VsRules.HoldSeconds(o.Site) - 0.25) return "Hold on a little longer.";
                if (can && run.CarryMetres > VsRules.CanCarryMax * VsRules.SpeedSlack) return "That was no straight walk.";
                Complete(g, e!, o);
                LetGo(g, e!, run);
                return null;
            default:
                if (run.HoldSite == site) LetGo(g, e!, run);
                return null;
        }
    }

    private void LetGo(Game g, VsEntrant e, Run run)
    {
        if (run.HoldSite >= 0 && run.HoldSite < g.S.Objectives.Count && g.S.Objectives[run.HoldSite].Carrier == e.Peer)
            g.S.Objectives[run.HoldSite].Carrier = 0;
        run.HoldFrom = -1;
        run.HoldSite = -1;
        e.Holding = -1;
        e.DisguiseBackAt = Now + VsRules.DisguiseBack;
        Push();
    }

    private void DropCarry(Game g, long peer)
    {
        foreach (var o in g.S.Objectives) if (o.Carrier == peer) o.Carrier = 0;
        if (g.Runs.TryGetValue(peer, out var run)) { run.HoldFrom = -1; run.HoldSite = -1; }
    }

    /// <summary>An objective done: its points to the doer and every hider still hiding, the whole area hears it; all three wake the village.</summary>
    private void Complete(Game g, VsEntrant doer, VsObjective o)
    {
        var s = g.S;
        o.Done = true;
        o.By = doer.Peer;
        o.Carrier = 0;
        var (mine, each) = VsRules.ObjectivePoints(o.Site);
        doer.Score += mine;
        doer.RoundScore += mine;
        foreach (var h in s.Entrants)
            if (h != doer && h.Hiding) { h.Score += each; h.RoundScore += each; }
        switch (o.Site)
        {
            case VsSite.Bell: doer.Bells++; break;
            case VsSite.Fire: doer.Fires++; break;
            default: doer.Cans++; break;
        }
        if (o.Site == VsSite.Bell) SoundAt(g, o.E, o.N, doer.Peer, VsVoice.Bell, everyone: true);
        if (!s.Awake && s.Objectives.All(x => x.Done))
        {
            s.Awake = true;
            foreach (var h in s.Entrants)
                if (h.Hiding) { h.Score += VsRules.AwakeBonus; h.RoundScore += VsRules.AwakeBonus; }
            Tell(s, "The village is awake!");
        }
        Tell(s, o.Site switch
        {
            VsSite.Bell => $"{doer.Name} rang the church bell!",
            VsSite.Fire => $"{doer.Name} lit the Höhenfeuer!",
            _ => $"{doer.Name} delivered the milk can!",
        });
        BotHeard(g, o.E, o.N, VsVoice.Bell);
        GD.Print($"[vs] {doer.Name} completed the {o.Site.ToString().ToLowerInvariant()} (+{mine}, +{each} each)");
        Push();
    }

    // ---- sounds ---------------------------------------------------------------------------------

    /// <summary>A sound at a hider's server position, to the peers that can hear it (within 80 m; the bell to everyone in the game).</summary>
    private void Sound(Game g, long peer, VsVoice voice)
    {
        if (PositionOf(peer) is { } at) SoundAt(g, at.E, at.N, peer, voice);
    }

    private void SoundAt(Game g, double e, double n, long from, VsVoice voice, bool everyone = false)
    {
        const double reach = 80;
        foreach (long p in Multiplayer.GetPeers())
        {
            bool inGame = g.S.Find(p) is { Left: false };
            bool near = PositionOf(p) is { } at && (at.E - e) * (at.E - e) + (at.N - n) * (at.N - n) <= reach * reach;
            if (near || everyone && inGame) RpcId(p, MethodName.Sounded, from, (int)voice, e, n);
        }
        BotHeard(g, e, n, voice);
    }

    // ---- the 4 Hz checks ------------------------------------------------------------------------

    /// <summary>
    /// Four times a second in Hide and Seek: the seeker held at the door in Hide; every hider's published body under its
    /// walk cap (a sprint's while one runs), inside the circle, on the ground, not inside a building. A failed check counts
    /// as out of bounds: revealed after 3 s, caught after 10 s. A carried can's path is measured here too.
    /// </summary>
    private void Check(Game g, double now)
    {
        if (now < g.NextCheck) return;
        double dt = now - (g.NextCheck - VsRules.CheckEvery);
        g.NextCheck = now + VsRules.CheckEvery;
        var s = g.S;
        bool changed = false;
        long timer = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var e in s.Entrants)
        {
            if (e.Left || PositionOf(e.Peer) is not { } at) continue;
            var run = g.RunOf(e.Peer);
            if (e.Role == VsRole.Seeker)
            {
                // blind at the door: beyond 3 m the server puts it back
                if (s.Phase == VsPhase.Hide && (at.E - s.DoorE) * (at.E - s.DoorE) + (at.N - s.DoorN) * (at.N - s.DoorN) > VsRules.DoorHold * VsRules.DoorHold * 4
                    && now - s.PhaseStarted > 1.5 && now - run.LastAt > 0.75)
                {
                    GD.Print(FormattableString.Invariant($"[vs] {e.Name} left the church door in Hide: put back"));
                    PutAtDoor(g, e.Peer);
                    run.LastAt = now;
                }
                continue;
            }
            if (!e.Hiding) continue;
            bool ok = true;
            string? fault = null;
            // the speed over a second's window: positions arrive in bursts (the owner's sync, /speed), a step is no speed
            if (run.Last is not { } last || now - run.LastAt > 3) { run.Last = at; run.LastAt = now; }
            else if (now - run.LastAt >= 1.0)
            {
                float cap = e.Disguised ? VsProps.Of(e.Prop).WalkCap : 6.5f;
                bool sprinting = now < run.SprintUntil + 1.0;
                if (!VsRules.SpeedOk(last.E, last.N, at.E, at.N, now - run.LastAt, cap, sprinting)) run.SpeedBadUntil = now + 1.0;
                if (run.HoldSite >= 0 && s.Objectives[run.HoldSite].Carrier == e.Peer)
                    run.CarryMetres += Math.Sqrt((at.E - last.E) * (at.E - last.E) + (at.N - last.N) * (at.N - last.N));
                run.Last = at;
                run.LastAt = now;
            }
            if (now < run.SpeedBadUntil) { ok = false; fault = "speed"; }
            if (!VsRules.Inside(s.E, s.N, s.Radius, at.E, at.N)) { ok = false; fault ??= "bounds"; }
            // inside a building: through a door (an interior is far under the world) or through a wall
            bool indoors = Interiors.InteriorManager.SurfacePoint(at) != at;
            if (indoors || g.Site?.InBuilding(at.E, at.N) == true) { ok = false; fault ??= "building"; }
            else if (!VsRules.GroundOk((float)at.Alt, g.Site?.GroundAt(at.E, at.N))) { ok = false; fault ??= "ground"; }
            if (ok)
            {
                run.OutSince = -1;
                if (e.Revealed) { e.Revealed = false; changed = true; }
                continue;
            }
            if (run.OutSince < 0)
            {
                run.OutSince = now;
                GD.Print($"[vs] {e.Name} fails the {fault} check");
            }
            if (!e.Revealed && now - run.OutSince >= VsRules.OutRevealSeconds) { e.Revealed = true; changed = true; }
            if (s.Phase == VsPhase.Seek && now - run.OutSince >= VsRules.OutCaughtSeconds) Catch(g, e, s.Seeker);
        }
        ServerStats.Ran("versteckis", timer);
        if (changed) Push();
    }
}
