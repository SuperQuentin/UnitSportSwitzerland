using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The Adventure rewards (#1227, <c>docs/plans/adventure-rewards.md</c>), at <c>World/Adventure/Rewards</c> on the
/// server and every client (RPCs route by node path, as <c>World/Loot</c>). The server checks every claim against
/// its own placement of the tile (<see cref="RewardMap"/>: a forged id cannot exist) and the body's server
/// position, rolls the chests, keeps the seals and guides in memory, times the trials and keeps their board, and
/// judges photos; offline the client plays the server's part, peer 1, key <c>local</c>. Everything is per player:
/// nothing about anyone's chests goes to another peer, and a chest is never used up for the world. Off in a
/// Battle Royale round (<see cref="AdventureService.Active"/>). Docs: <c>docs/notes/adventure/rewards.md</c>.
/// </summary>
public partial class RewardService : Node
{
    public const string NodeName = "Rewards";
    private const double TickEvery = 0.25;

    public static RewardService? Instance { get; private set; }

    private AdventureService _adventure = null!;
    private bool _server, _networked;
    private bool Authority => _adventure.Authority;

    public RewardMap Map { get; private set; } = null!;

    /// <summary>Server: a body other than <c>except</c>'s, or a dropped item, within <c>radius</c> of (E, N) (a pressure plate's weight).</summary>
    public Func<double, double, float, long, bool>? Occupied { get; set; }
    /// <summary>Server: the peers in the same party as a peer, itself included (the seals' co-op rule); null when none.</summary>
    public Func<long, IReadOnlyList<long>?>? PartyOf { get; set; }

    // ---- the authority's state --------------------------------------------------------------------

    private readonly SealBook _seals = new();
    private readonly GuideBook _guides = new();
    private readonly TrialRuns _runs = new();
    private TrialBoard _board = new();
    private readonly Dictionary<long, (long Second, int Count)> _rate = new();
    private readonly Dictionary<string, (string TrialId, double Until)> _ghostDue = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (string TrialId, byte[][] Parts)> _uploads = new();
    private readonly List<long> _tickPeers = new();
    private double _nextTick, _nextPrune;

    /// <summary>Authority: chests opened, edelweiss picked and claims refused since start (checks).</summary>
    public int OpenedCount { get; private set; }
    public int Refusals { get; private set; }

    // ---- the owner's side -------------------------------------------------------------------------

    /// <summary>Client: a chest opened for us: its id (the edition's for a Luxurious one), what it gave, its tier, the francs, a note ("already yours").</summary>
    public event Action<string, List<LootLine>, ChestTier, int, string>? ChestOpened;
    /// <summary>Client: an edelweiss picked or a cairn stone added (its id).</summary>
    public event Action<string>? Picked;
    /// <summary>Client: a seal's state for us changed (chest id, mask; <see cref="Seals.DoneBit"/> when complete).</summary>
    public event Action<string, int>? SealChanged;
    /// <summary>Client: our guide moved on (or arrived).</summary>
    public event Action<GuideMove>? GuideMoved;
    /// <summary>Client: a claim was refused: the id and why.</summary>
    public event Action<string, string>? Refused;
    /// <summary>Client: an offering was made: the shrine, the steps, the total offered.</summary>
    public event Action<string, int, int>? Offered;
    /// <summary>Client: a photo was judged: the spot, whether it counted, why not.</summary>
    public event Action<string, bool, string>? PhotoJudged;
    /// <summary>Client: a trial started (its gates and par), a gate counted, it ended (seconds, medal, a personal best, its rank; medal -1 = out).</summary>
    public event Action<ActiveTrial>? TrialStarted;
    public event Action<string, int>? TrialGate;
    public event Action<string, double, int, bool, int>? TrialEnded;
    /// <summary>Client: a trial's board arrived.</summary>
    public event Action<string>? BoardChanged;
    /// <summary>Client: a ghost arrived for a trial.</summary>
    public event Action<string, List<GhostTrack.Sample>>? GhostReceived;

    /// <summary>Client: the seal states the server told us (chest id -> mask).</summary>
    public IReadOnlyDictionary<string, int> SealMasks => _masks;
    private readonly Dictionary<string, int> _masks = new(StringComparer.Ordinal);
    /// <summary>Client: the guide we follow, if any.</summary>
    public GuideMove? Guide { get; private set; }
    /// <summary>Client: the trial we run, if any.</summary>
    public ActiveTrial? Trial { get; private set; }
    /// <summary>Client: the boards we were sent (trial id -> rows of name, seconds).</summary>
    public IReadOnlyDictionary<string, List<(string Name, double Seconds)>> Boards => _boards;
    private readonly Dictionary<string, List<(string, double)>> _boards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[][]> _ghostParts = new(StringComparer.Ordinal);

    /// <summary>The last refusal heard (checks).</summary>
    public string LastRefusal { get; private set; } = "";

    public static RewardService Create(AdventureService adventure, bool server, bool networked, Func<IChunkSource?> source, (double E, double N) start)
    {
        var s = new RewardService { Name = NodeName, _adventure = adventure, _server = server, _networked = networked };
        s.Map = new RewardMap(source, () => PoiCatalogueHost.Current, server ? 64 : 0, start);
        adventure.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        PoiCatalogueHost.Changed -= Map.Clear;
    }

    public override void _Ready()
    {
        // the places' chests stand at the catalogue's POIs: a new catalogue (the server's arriving) places again
        PoiCatalogueHost.Changed += Map.Clear;
        if (!Authority) return;
        _adventure.Provides("rewards");
        _board = LoadBoard();
        GD.Print($"[rewards] {(_server ? "server" : "offline")}: placement v{RewardPlacer.Version}{(Map.Enabled ? "" : ", none on this course")}");
    }

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;

    private bool Connected(long peer) => _networked && peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    private static double Now => Net.ClockSync.ServerNow;

    // ---- authority: shared checks -----------------------------------------------------------------

    /// <summary>The peer may claim now: adventuring, known, under the rate limit (4 claims a second).</summary>
    private PlayerProgress? Claimant(long peer, string id)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p)
        {
            Refuse(peer, id, "No adventuring now.");
            return null;
        }
        long second = (long)Math.Floor(Now);
        var (s, n) = _rate.GetValueOrDefault(peer);
        n = s == second ? n + 1 : 1;
        _rate[peer] = (second, n);
        if (n > RewardRules.ClaimsPerSecond)
        {
            Refuse(peer, id, "Too many claims at once.");
            return null;
        }
        return p;
    }

    private bool Near(long peer, double e, double n, float alt, float reach, out string why)
    {
        why = "";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) { why = "No body."; return false; }
        double de = at.E - e, dn = at.N - n, d = Math.Sqrt(de * de + dn * dn);
        if (d > reach) { why = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Too far ({d:F0} m)."); return false; }
        if (Math.Abs(at.Alt - alt) > RewardRules.ClaimSlackUpM) { why = "Not there."; return false; }
        return true;
    }

    private string? Refuse(long peer, string id, string why)
    {
        Refusals++;
        GD.Print($"[rewards] peer {peer}: {id}: {why}");
        if (Connected(peer)) RpcId(peer, MethodName.TellRefused, id, why);
        else if (!_networked) TellRefused(id, why);
        return why;
    }

    /// <summary>Rolls a chest of a tier for a player and grants it (a chest, an offering, a cairn set, a trial, a photo): returns the lines and the note.</summary>
    private (List<LootLine> Lines, int Francs, string Note) RollAndGrant(long peer, PlayerProgress p, ChestTier tier, string seedId, string why)
    {
        var chests = p.Get(ChestsProgress.Section);
        int pity = chests.PityOf(tier);
        var roll = ChestLoot.Roll(tier, AdventureHash.Rng(AdventureHash.Of(seedId, p.Key, chests.Count + chests.Opened.Count)), pity);
        var (lines, note) = ChestLoot.Settle(roll, chests.Owned);
        chests.Pity[(int)tier] = ChestLoot.PityAfter(tier, pity, roll.Cosmetic);
        p.Touch(ChestsProgress.Section);
        _adventure.Grant(peer, new Cash(roll.Francs), why);
        var gadgets = p.Get(GadgetsProgress.Section);
        bool gadget = false;
        foreach (var l in lines)
        {
            _adventure.Grant(peer, new Item(l.Item, l.Count), why);
            if (Array.IndexOf(ChestLoot.Gadgets, l.Item) < 0) continue;
            gadgets.Had[l.Item.ToString()] = gadgets.Had.GetValueOrDefault(l.Item.ToString()) + l.Count;
            gadget = true;
        }
        if (gadget) p.Touch(GadgetsProgress.Section);
        return (lines, roll.Francs, note);
    }

    private void SendOpened(long peer, string id, List<LootLine> lines, ChestTier tier, int francs, string note)
    {
        int[] items = lines.Select(l => (int)l.Item).ToArray(), counts = lines.Select(l => l.Count).ToArray();
        if (Connected(peer)) RpcId(peer, MethodName.TellOpened, id, items, counts, (byte)tier, francs, note);
        else if (!_networked) TellOpened(id, items, counts, (byte)tier, francs, note);
    }

    // ---- authority: chests ------------------------------------------------------------------------

    /// <summary>
    /// A peer opens a chest (its client's request, a bot's call): adventuring, the id is a chest of the server's own
    /// placement, the body within <see cref="RewardRules.ClaimReachM"/>, not opened by this player (a Luxurious one: in
    /// this year's edition), its seal complete. Then the roll is the player's own, granted, and the chest is gone for
    /// it alone. Null when opened, else why not.
    /// </summary>
    public async Task<string?> ServeOpen(long peer, string id)
    {
        if (Claimant(peer, id) is not { } p) return "inactive";
        if (await Map.FindAsync(id) is not { Kind: SpotKind.Chest } chest) return Refuse(peer, id, "No such chest.");
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p) return "gone";
        string openId = RewardRules.OpenId(chest, RewardRules.Edition(DateTime.UtcNow));
        var chests = p.Get(ChestsProgress.Section);
        if (chests.Opened.Contains(openId)) return Refuse(peer, openId, "You opened it already.");
        if (!Near(peer, chest.E, chest.N, chest.Alt, RewardRules.ClaimReachM, out string why)) return Refuse(peer, openId, why);
        if (chest.Seal != SealKind.None && !_seals.IsDone(p.Key, chest.Id))
            return Refuse(peer, openId, $"Sealed: {Seals.Hint(chest.Seal)}");
        chests.Opened.Add(openId);
        chests.ByTier[(int)chest.Tier]++;
        chests.ByCanton[chest.Canton] = chests.ByCanton.GetValueOrDefault(chest.Canton) + 1;
        chests.PlacementVersion = RewardPlacer.Version;
        p.Touch(ChestsProgress.Section);
        _seals.Spend(p.Key, chest.Id);
        var (lines, francs, note) = RollAndGrant(peer, p, chest.Tier, openId, "chest:" + openId);
        OpenedCount++;
        GD.Print($"[rewards] {Short(p.Key)} opened {openId} ({chest.Tier}): CHF {francs}, {string.Join(", ", lines.Select(l => $"{l.Count} {l.Item}"))}");
        _adventure.Events.Raise(new ChestOpened(peer, p.Key, openId, (int)chest.Tier, chest.PoiId));
        SendOpened(peer, openId, lines, chest.Tier, francs, note);
        return null;
    }

    // ---- authority: edelweiss and cairns ------------------------------------------------------------

    /// <summary>
    /// A peer picks an edelweiss or adds a stone to a cairn (the gather hold, a bot's call): a spot of the server's own
    /// placement, the body within reach, not taken by this player. An edelweiss is held for an offering; the twentieth
    /// cairn of a canton gives a Precious roll. Null when taken, else why not.
    /// </summary>
    public async Task<string?> ServePick(long peer, string id)
    {
        if (Claimant(peer, id) is not { } p) return "inactive";
        if (await Map.FindAsync(id) is not { Kind: SpotKind.Edelweiss or SpotKind.Cairn } spot) return Refuse(peer, id, "Nothing to pick here.");
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p) return "gone";
        var collect = p.Get(CollectProgress.Section);
        if (collect.Taken.Contains(spot.Id)) return Refuse(peer, id, spot.Kind == SpotKind.Cairn ? "Your stone is on it already." : "You picked it already.");
        if (!Near(peer, spot.E, spot.N, spot.Alt, RewardRules.ClaimReachM, out string why)) return Refuse(peer, id, why);
        collect.Taken.Add(spot.Id);
        bool cantonDone = false;
        if (spot.Kind == SpotKind.Edelweiss)
        {
            collect.Held++;
            collect.Picked++;
        }
        else
        {
            int n = collect.Cairns[spot.Canton] = collect.Cairns.GetValueOrDefault(spot.Canton) + 1;
            cantonDone = n == Cairns.PerCanton;
        }
        p.Touch(CollectProgress.Section);
        GD.Print($"[rewards] {Short(p.Key)} {(spot.Kind == SpotKind.Cairn ? "added a stone to" : "picked")} {spot.Id} ({collect.Held} held)");
        _adventure.Events.Raise(new CollectibleTaken(peer, p.Key, spot.Id, spot.Kind == SpotKind.Cairn ? "cairn" : "edelweiss", spot.Canton));
        if (Connected(peer)) RpcId(peer, MethodName.TellPicked, spot.Id);
        else if (!_networked) TellPicked(spot.Id);
        if (cantonDone)
        {
            var (lines, francs, note) = RollAndGrant(peer, p, ChestTier.Precious, "cairns:" + spot.Canton, "cairns:" + spot.Canton);
            SendOpened(peer, "cairns:" + spot.Canton, lines, ChestTier.Precious, francs, note);
        }
        return null;
    }

    /// <summary>
    /// A peer offers its edelweiss at a summit cross or a chapel (E there, a bot's call): every whole five held move to
    /// offered; each raises stamina through the stamina plan's <c>"stamina"</c> reward (ten steps at most), every
    /// fifth step also gives a Precious roll. Null when offered, else why not.
    /// </summary>
    public string? ServeOffer(long peer, string shrineId)
    {
        if (Claimant(peer, shrineId) is not { } p) return "inactive";
        if (PoiCatalogueHost.Current.Resolve(shrineId) is not { } shrine || !Offering.IsShrine(shrine)) return Refuse(peer, shrineId, "No shrine here.");
        if (!Near(peer, shrine.E, shrine.N, shrine.Elevation, OfferReachM, out string why)) return Refuse(peer, shrineId, why);
        var collect = p.Get(CollectProgress.Section);
        var steps = Offering.Offer(collect);
        if (steps.Count == 0) return Refuse(peer, shrineId, $"Five edelweiss make an offering (you hold {collect.Held}).");
        p.Touch(CollectProgress.Section);
        foreach (var (step, stamina, precious) in steps)
        {
            if (stamina) _adventure.Grant(peer, new Unlock("stamina", "+1"), "offering:" + shrine.Id);
            if (!precious) continue;
            string seed = $"offering:{step}";
            var (lines, francs, note) = RollAndGrant(peer, p, ChestTier.Precious, seed, "offering:" + shrine.Id);
            SendOpened(peer, seed, lines, ChestTier.Precious, francs, note);
        }
        GD.Print($"[rewards] {Short(p.Key)} offered {steps.Count * Offering.PerStep} edelweiss at {shrine.Id} ({collect.Offered} in all)");
        _adventure.Events.Raise(new EdelweissOffered(peer, p.Key, shrine.Id, collect.Offered));
        if (Connected(peer)) RpcId(peer, MethodName.TellOffered, shrine.Id, steps.Count, collect.Offered);
        else if (!_networked) TellOffered(shrine.Id, steps.Count, collect.Offered);
        return null;
    }

    /// <summary>How near a shrine's published position the body must be to offer (the client asks within 3 m).</summary>
    public const float OfferReachM = 10;

    // ---- authority: seals ------------------------------------------------------------------------------

    /// <summary>
    /// A seal step (E at a wood stack or a cowbell, a ring passed, standing on a plate; a bot's call): the server checks
    /// where the body is for that step, then the seal book its order and window. The step counts for every party member
    /// within 60 m. Null when it counted, else why not.
    /// </summary>
    public async Task<string?> ServeSealStep(long peer, string chestId, int step)
    {
        if (Claimant(peer, chestId) is not { } p) return "inactive";
        if (await Map.FindAsync(chestId) is not { Kind: SpotKind.Chest, Seal: not SealKind.None } chest) return Refuse(peer, chestId, "No seal here.");
        var layout = await Map.LayoutAsync(chest);
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p || layout.Kind == SealKind.None) return "gone";
        if (layout.Kind == SealKind.Guide) return Refuse(peer, chestId, "Follow the guide.");
        if (step < 0 || step >= layout.Props.Length) return Refuse(peer, chestId, "No such step.");
        var prop = layout.Props[step];
        float reach = layout.Kind switch
        {
            SealKind.Rings => prop.Radius + 6,
            SealKind.Plates => prop.Radius + 1.5f,
            SealKind.Bells => 3.5f,
            _ => RewardRules.PropReachM,
        };
        if (!Near(peer, prop.E, prop.N, layout.Kind == SealKind.Rings ? prop.Alt - 1.8f : prop.Alt, reach, out string why)) return Refuse(peer, chestId, why);
        if (layout.Kind == SealKind.Plates)
        {
            var other = layout.Props[1 - step];
            if (Occupied?.Invoke(other.E, other.N, other.Radius + 0.6f, peer) != true) return Refuse(peer, chestId, "The other plate is not held down.");
            step = 0;
        }
        var result = StepForParty(peer, p, chest, layout, step);
        return result is SealStep.Ignored ? "ignored" : null;
    }

    private SealStep StepForParty(long peer, PlayerProgress p, RewardSpot chest, SealLayout layout, int step)
    {
        double now = Now;
        var at = _adventure.PositionOf?.Invoke(peer);
        // the co-op rule: the party's members within 60 m of where it was done
        var members = new List<(string, double, double)>();
        var peerOf = new Dictionary<string, long>(StringComparer.Ordinal) { [p.Key] = peer };
        if (PartyOf?.Invoke(peer) is { } party && at is { } here)
            foreach (long m in party)
                if (m != peer && _adventure.Active(m) && _adventure.Progress(m) is { } mp && _adventure.PositionOf?.Invoke(m) is { } mpos)
                {
                    members.Add((mp.Key, mpos.E, mpos.N));
                    peerOf[mp.Key] = m;
                }
        var keys = Seals.Coop(p.Key, members, at?.E ?? chest.E, at?.N ?? chest.N);
        var mine = SealStep.Ignored;
        foreach (string key in keys)
        {
            var r = _seals.Step(key, chest.Id, layout, step, now);
            if (key == p.Key) mine = r;
            if (r is SealStep.Ignored or SealStep.AlreadyDone) continue;
            long who = peerOf[key];
            GD.Print($"[rewards] {Short(key)}: {chest.Id} {layout.Kind} step {step}: {r}");
            SendSeal(who, chest.Id, _seals.MaskOf(key, chest.Id));
            if (r == SealStep.Reset && who == peer)
                Refuse(peer, chest.Id, layout.Kind == SealKind.Bells ? "Wrong bell: listen to the tune again." : "Too slow: start again.");
        }
        return mine;
    }

    private void SendSeal(long peer, string chestId, int mask)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellSeal, chestId, mask);
        else if (!_networked) TellSeal(chestId, mask);
    }

    /// <summary>
    /// A peer wakes a guide at its burrow or perch (E there, a bot's call): it sets off for the chest, server-led
    /// (<see cref="GuideBook"/>); each leg reached goes to the follower as <c>GuideLeg</c>. Null when woken, else why not.
    /// </summary>
    public async Task<string?> ServeGuide(long peer, string chestId)
    {
        if (Claimant(peer, chestId) is not { } p) return "inactive";
        if (await Map.FindAsync(chestId) is not { Kind: SpotKind.Chest, Seal: SealKind.Guide } chest) return Refuse(peer, chestId, "No guide here.");
        var layout = await Map.LayoutAsync(chest);
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p || layout.Props.Length < 2) return "gone";
        if (_seals.IsDone(p.Key, chest.Id)) return Refuse(peer, chestId, "The guide has shown you already.");
        var burrow = layout.Props[0];
        if (!Near(peer, burrow.E, burrow.N, burrow.Alt, RewardRules.PropReachM, out string why)) return Refuse(peer, chestId, why);
        var move = _guides.Wake(p.Key, chest.Id, layout, Now);
        GD.Print($"[rewards] {Short(p.Key)} woke the {layout.Guide} of {chest.Id}");
        SendGuide(peer, move);
        return null;
    }

    private void SendGuide(long peer, GuideMove m)
    {
        if (Connected(peer)) RpcId(peer, MethodName.GuideLeg, m.ChestId, m.Leg, m.Since, m.Done);
        else if (!_networked) GuideLeg(m.ChestId, m.Leg, m.Since, m.Done);
    }

    // ---- authority: photos and fauna -------------------------------------------------------------------

    /// <summary>
    /// A photo reported at a spot (the camera's shot, a bot never): the server judges it from the body's own position
    /// (<see cref="PhotoJudge"/>, the line of sight over the coarse terrain). The first time it counts: the
    /// <c>photos</c> section, <see cref="PhotoTaken"/>, a Fine roll.
    /// </summary>
    public async Task<string?> ServePhoto(long peer, string spotId, float yawDeg, float pitchDeg, float fovDeg)
    {
        if (Claimant(peer, spotId) is not { } p) return "inactive";
        if (PhotoSpots.Resolve(spotId, PoiCatalogueHost.Current) is not { } spot) return Refuse(peer, spotId, PhotoJudge.Why(PhotoVerdict.Unknown));
        if (p.Get(PhotosProgress.Section).Taken.Contains(spot.Id)) return "taken";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Refuse(peer, spotId, "No body.");
        if (!float.IsFinite(yawDeg) || !float.IsFinite(pitchDeg) || fovDeg is < 1 or > 120) return Refuse(peer, spotId, "No such photo.");
        var ground = await Map.SightGround(at.E, at.N, spot.TargetE, spot.TargetN);
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p) return "gone";
        var verdict = PhotoJudge.Judge(spot, at.E, at.N, (float)at.Alt + 1.6f, yawDeg, pitchDeg, fovDeg, ground);
        if (verdict != PhotoVerdict.Taken)
        {
            GD.Print($"[rewards] peer {peer}: photo of {spot.Id}: {verdict}");
            if (Connected(peer)) RpcId(peer, MethodName.TellPhoto, spot.Id, false, PhotoJudge.Why(verdict));
            else if (!_networked) TellPhoto(spot.Id, false, PhotoJudge.Why(verdict));
            return PhotoJudge.Why(verdict);
        }
        p.Get(PhotosProgress.Section).Taken.Add(spot.Id);
        p.Touch(PhotosProgress.Section);
        GD.Print($"[rewards] {Short(p.Key)} took the photo {spot.Id}");
        _adventure.Events.Raise(new PhotoTaken(peer, p.Key, spot.Id, spot.PoiId));
        if (Connected(peer)) RpcId(peer, MethodName.TellPhoto, spot.Id, true, "");
        else if (!_networked) TellPhoto(spot.Id, true, "");
        var (lines, francs, note) = RollAndGrant(peer, p, ChestTier.Fine, "photo:" + spot.Id, "photo:" + spot.Id);
        SendOpened(peer, "photo:" + spot.Id, lines, ChestTier.Fine, francs, note);
        return null;
    }

    /// <summary>An animal photographed (the fauna page): trusted from the client, as it gives nothing but the list; known names only.</summary>
    public void ServeFauna(long peer, string name)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p || !Fauna.Known(name)) return;
        var c = p.Get(CollectProgress.Section);
        c.Fauna[name] = c.Fauna.GetValueOrDefault(name) + 1;
        p.Touch(CollectProgress.Section);
    }

    // ---- authority: trials ---------------------------------------------------------------------------

    /// <summary>A trial's id is one of the catalogue's (<see cref="Trials.Resolve"/>), so a forged one cannot start.</summary>
    public async Task<string?> ServeTrialStart(long peer, string trialId)
    {
        if (Claimant(peer, trialId) is not { } p) return "inactive";
        if (Trials.Resolve(trialId, PoiCatalogueHost.Current) is not { } def) return Refuse(peer, trialId, "No such trial.");
        if (!Near(peer, def.StartE, def.StartN, def.StartAlt, RewardRules.PropReachM + 2, out string why)) return Refuse(peer, trialId, why);
        var ground = await Map.SightGround(def.StartE, def.StartN, def.ToE, def.ToN);
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p) return "gone";
        float Fallback(double e, double n) => def.StartAlt + (float)((def.ToAlt - def.StartAlt) * Along(def, e, n));
        var gates = Trials.Course(def, (e, n) => ground(e, n) ?? Fallback(e, n));
        double now = Now;
        _runs.Start(p.Key, def, gates, now);
        var par = Trials.Par(def, gates);
        GD.Print($"[rewards] {Short(p.Key)} starts {def.Id}: {gates.Length} gates, gold {par.Gold:F0} s");
        var flat = new double[gates.Length * 4];
        for (int i = 0; i < gates.Length; i++) (flat[i * 4], flat[i * 4 + 1], flat[i * 4 + 2], flat[i * 4 + 3]) = (gates[i].E, gates[i].N, gates[i].Alt, gates[i].Radius);
        if (Connected(peer)) RpcId(peer, MethodName.TellTrialStarted, def.Id, flat, now, par.Gold, par.Silver, par.Bronze);
        else if (!_networked) TellTrialStarted(def.Id, flat, now, par.Gold, par.Silver, par.Bronze);
        SendBoard(peer, def.Id);
        return null;
    }

    private static double Along(TrialDef d, double e, double n)
    {
        double vx = d.ToE - d.StartE, vy = d.ToN - d.StartN, len = vx * vx + vy * vy;
        return len < 1 ? 0 : Math.Clamp(((e - d.StartE) * vx + (n - d.StartN) * vy) / len, 0, 1);
    }

    /// <summary>A gate passed: the body at it (its radius plus the lag of a fast body), in order, at a possible pace.</summary>
    public string? ServeTrialGate(long peer, string trialId, int gate)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return "inactive";
        if (!_runs.Running(p.Key, out var def, out var gates, out _) || def!.Id != trialId) return "not running";
        if (gate < 0 || gate >= gates!.Length) return "no such gate";
        var g = gates[gate];
        if (!Near(peer, g.E, g.N, g.Alt, g.Radius + (def.Kind == TrialKind.Glide ? 40 : 20), out string why)) return Refuse(peer, trialId, why);
        var result = _runs.Gate(p.Key, trialId, gate, Now, out double seconds);
        switch (result)
        {
            case TrialGateResult.Next:
                if (Connected(peer)) RpcId(peer, MethodName.TellTrialGate, trialId, gate);
                else if (!_networked) TellTrialGate(trialId, gate);
                return null;
            case TrialGateResult.TooFast:
                GD.Print($"[rewards] {Short(p.Key)} is out of {trialId}: too fast between gates");
                EndTrial(peer, trialId, 0, -1, false, -1);
                return "too fast";
            case TrialGateResult.Finished:
                Finish(peer, p, def, gates, seconds);
                return null;
        }
        return "ignored";
    }

    private void Finish(long peer, PlayerProgress p, TrialDef def, TrialGate[] gates, double seconds)
    {
        var par = Trials.Par(def, gates);
        int medal = Trials.Medal(seconds, par);
        var trials = p.Get(TrialsProgress.Section);
        bool first = !trials.Best.TryGetValue(def.Id, out var best);
        best ??= new TrialBest { Seconds = double.MaxValue };
        bool pb = seconds < best.Seconds, firstGold = medal == 3 && best.Medal < 3;
        if (pb) best.Seconds = Math.Round(seconds, 2);
        best.Medal = Math.Max(best.Medal, medal);
        best.Runs++;
        trials.Best[def.Id] = best;
        p.Touch(TrialsProgress.Section);
        string name = p.LastName.Length > 0 ? p.LastName : "Player";
        int rank = _board.Submit(def.Id, p.Key, name, Math.Round(seconds, 2), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (rank >= 0) SaveBoard();
        if (pb) _ghostDue[p.Key] = (def.Id, Now + 120);
        GD.Print(FormattableString.Invariant($"[rewards] {Short(p.Key)} finished {def.Id} in {seconds:F2} s: {Trials.MedalName(medal)}{(pb ? ", a personal best" : "")}{(rank >= 0 ? $", #{rank + 1} on the board" : "")}"));
        _adventure.Events.Raise(new TrialFinished(peer, p.Key, def.Id, seconds, medal));
        EndTrial(peer, def.Id, seconds, medal, pb, rank);
        if (first) GrantRoll(peer, p, ChestTier.Fine, "trial:" + def.Id);
        if (firstGold) GrantRoll(peer, p, ChestTier.Precious, "trialgold:" + def.Id);
        // everyone sees the board as it is now, the finisher too
        if (rank >= 0) foreach (long other in _adventure.Peers) SendBoard(other, def.Id);
        else SendBoard(peer, def.Id);
    }

    private void GrantRoll(long peer, PlayerProgress p, ChestTier tier, string why)
    {
        var (lines, francs, note) = RollAndGrant(peer, p, tier, why, why);
        SendOpened(peer, why, lines, tier, francs, note);
    }

    private void EndTrial(long peer, string trialId, double seconds, int medal, bool pb, int rank)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellTrialEnded, trialId, seconds, medal, pb, rank);
        else if (!_networked) TellTrialEnded(trialId, seconds, medal, pb, rank);
    }

    /// <summary>Leaves the trial one runs (<c>/trial leave</c>, the quick menu).</summary>
    public void ServeTrialLeave(long peer)
    {
        if (_adventure.Progress(peer) is { } p && _runs.Leave(p.Key)) EndTrial(peer, "", 0, -1, false, -1);
    }

    /// <summary><c>/trial leave</c>: leaves the trial one runs. The chat's sender is the peer (offline, the local player).</summary>
    public string Command(long sender, string rest)
    {
        long peer = _networked ? sender : AdventureService.LocalPeer;
        if (rest.Trim() != "leave") return "Usage: /trial leave";
        if (_adventure.Progress(peer) is not { } p || !_runs.Running(p.Key, out _, out _, out _)) return "You are not running a trial.";
        ServeTrialLeave(peer);
        return "You left the trial.";
    }

    private void SendBoard(long peer, string trialId)
    {
        var rows = _board.Of(trialId);
        string[] names = rows.Select(r => r.Name).ToArray();
        double[] secs = rows.Select(r => r.Seconds).ToArray();
        if (Connected(peer)) RpcId(peer, MethodName.TellBoard, trialId, names, secs);
        else if (!_networked) TellBoard(trialId, names, secs);
    }

    /// <summary>The board of a trial, as the server keeps it (checks, the admin API).</summary>
    public IReadOnlyList<TrialEntry> BoardOf(string trialId) => _board.Of(trialId);

    private string BoardPath => AdventureService.ScratchDir is { } dir ? System.IO.Path.Combine(dir, "trials.json")
        : ProjectSettings.GlobalizePath(WorldChoice.SavePath("user://adventure/trials.json"));

    private string GhostDir => AdventureService.ScratchDir is { } dir ? System.IO.Path.Combine(dir, "ghosts")
        : ProjectSettings.GlobalizePath(WorldChoice.SavePath("user://adventure/ghosts"));

    private TrialBoard LoadBoard()
    {
        try
        {
            if (!File.Exists(BoardPath)) return new TrialBoard();
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<TrialEntry>>>(File.ReadAllText(BoardPath), AdventureJson.Options);
            return TrialBoard.FromData(data);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[rewards] {BoardPath}: {e.Message}");
            return new TrialBoard();
        }
    }

    private void SaveBoard() => JsonStore.SaveAsync(BoardPath, _board.ToData(), AdventureJson.Options, e => GD.PushWarning($"[rewards] saving the trial board: {e.Message}"));

    private static string GhostName(string trialId, string key) => AdventureBook.FileName($"{trialId}_{key}").Replace(".json", ".bin");

    /// <summary>A ghost upload part (after a personal best): assembled, checked, kept while its player is in the board's top 3.</summary>
    private void ServeGhostPart(long peer, string trialId, int part, int parts, byte[] chunk)
    {
        if (_adventure.Progress(peer) is not { } p || !_ghostDue.TryGetValue(p.Key, out var due) || due.TrialId != trialId || Now > due.Until) return;
        if (parts is < 1 or > 16 || part < 0 || part >= parts || chunk.Length > 8192) return;
        if (!_uploads.TryGetValue(peer, out var up) || up.TrialId != trialId || up.Parts.Length != parts) _uploads[peer] = up = (trialId, new byte[parts][]);
        up.Parts[part] = chunk;
        if (up.Parts.Any(x => x == null)) return;
        _uploads.Remove(peer);
        _ghostDue.Remove(p.Key);
        byte[] all = up.Parts.SelectMany(x => x).ToArray();
        if (GhostTrack.Decode(all) == null) { GD.Print($"[rewards] peer {peer}: a ghost for {trialId} that is none"); return; }
        var top = _board.Of(trialId).Take(3).Select(r => r.Key).ToList();
        if (!top.Contains(p.Key)) return;
        try
        {
            Directory.CreateDirectory(GhostDir);
            File.WriteAllBytes(System.IO.Path.Combine(GhostDir, GhostName(trialId, p.Key)), all);
            // only the top 3 keep a ghost
            foreach (var f in Directory.GetFiles(GhostDir, AdventureBook.FileName(trialId + "_").Replace(".json", "") + "*.bin"))
                if (!top.Any(k => f.EndsWith(GhostName(trialId, k), StringComparison.Ordinal))) File.Delete(f);
            GD.Print($"[rewards] {Short(p.Key)}'s ghost of {trialId} kept ({all.Length} B)");
        }
        catch (Exception e) { GD.PushWarning($"[rewards] ghost {trialId}: {e.Message}"); }
    }

    /// <summary>A ghost to race: one's own best if it is kept, else the leader's.</summary>
    private void ServeGhost(long peer, string trialId)
    {
        if (_adventure.Progress(peer) is not { } p) return;
        var rows = _board.Of(trialId);
        foreach (var key in new[] { p.Key }.Concat(rows.Take(1).Select(r => r.Key)))
        {
            string path = System.IO.Path.Combine(GhostDir, GhostName(trialId, key));
            if (!File.Exists(path)) continue;
            byte[] all = File.ReadAllBytes(path);
            const int Chunk = 8000;
            int parts = (all.Length + Chunk - 1) / Chunk;
            for (int i = 0; i < parts; i++)
            {
                byte[] c = all.Skip(i * Chunk).Take(Chunk).ToArray();
                if (Connected(peer)) RpcId(peer, MethodName.TellGhost, trialId, i, parts, c);
                else if (!_networked) TellGhost(trialId, i, parts, c);
            }
            return;
        }
    }

    // ---- authority: the tick (guides) ------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!Authority) return;
        double now = Now;
        if (now < _nextTick) return;
        _nextTick = now + TickEvery;
        _tickPeers.Clear();
        foreach (long peer in _adventure.Peers) _tickPeers.Add(peer);
        foreach (long peer in _tickPeers)
        {
            if (_adventure.Progress(peer) is not { } p || !_guides.Following(p.Key, out _, out _, out _)) continue;
            if (_adventure.PositionOf?.Invoke(peer) is not { } at || _guides.Follow(p.Key, at.E, at.N, now) is not { } move) continue;
            SendGuide(peer, move);
            GuideStep(peer, p, move);
        }
        if (now >= _nextPrune)
        {
            _nextPrune = now + 30;
            _seals.Prune(now);
            _guides.Prune(now);
        }
    }

    /// <summary>A guide's leg reached is a seal step, the last one unseals (the chest's tile placed again if the cache dropped it).</summary>
    private async void GuideStep(long peer, PlayerProgress p, GuideMove move)
    {
        if (await Map.FindAsync(move.ChestId) is not { } chest) return;
        var layout = await Map.LayoutAsync(chest);
        if (!IsInstanceValid(this) || _adventure.Progress(peer) != p) return;
        int leg = move.Done ? layout.Steps : move.Leg - 1;
        if (leg > 0) StepForParty(peer, p, chest, layout, leg);
    }

    /// <summary>Server: a peer left: its seals, guide, trial and uploads go (its sections were saved by the adventure service).</summary>
    public void PeerLeft(long peer)
    {
        _rate.Remove(peer);
        _uploads.Remove(peer);
        if (_adventure.KeyOf(peer) is not { } key) return;
        _seals.Forget(key);
        _guides.Forget(key);
        _runs.Leave(key);
    }

    // ---- the owner's requests ------------------------------------------------------------------------

    private static async void Log(Task<string?> t)
    {
        // the refusals were said where they happened; only the quiet outcomes are left
        if (await t is { } why && why is not ("inactive" or "gone" or "ignored" or "taken")) GD.Print($"[rewards] {why}");
    }

    public void AskOpen(string id)
    {
        if (Authority) Log(ServeOpen(AdventureService.LocalPeer, id));
        else RpcId(1, MethodName.RequestOpen, id);
    }

    public void AskPick(string id)
    {
        if (Authority) Log(ServePick(AdventureService.LocalPeer, id));
        else RpcId(1, MethodName.RequestPick, id);
    }

    public void AskSealStep(string chestId, int step)
    {
        if (Authority) Log(ServeSealStep(AdventureService.LocalPeer, chestId, step));
        else RpcId(1, MethodName.RequestSealStep, chestId, step);
    }

    public void AskOffer(string shrineId)
    {
        if (Authority) ServeOffer(AdventureService.LocalPeer, shrineId);
        else RpcId(1, MethodName.RequestOffer, shrineId);
    }

    public void AskGuide(string chestId)
    {
        if (Authority) Log(ServeGuide(AdventureService.LocalPeer, chestId));
        else RpcId(1, MethodName.RequestGuide, chestId);
    }

    public void ReportPhoto(string spotId, float yawDeg, float pitchDeg, float fovDeg)
    {
        if (Authority) Log(ServePhoto(AdventureService.LocalPeer, spotId, yawDeg, pitchDeg, fovDeg));
        else RpcId(1, MethodName.RequestPhoto, spotId, yawDeg, pitchDeg, fovDeg);
    }

    public void ReportFauna(string name)
    {
        if (Authority) ServeFauna(AdventureService.LocalPeer, name);
        else RpcId(1, MethodName.RequestFauna, name);
    }

    public void AskTrial(string trialId)
    {
        if (Authority) Log(ServeTrialStart(AdventureService.LocalPeer, trialId));
        else RpcId(1, MethodName.RequestTrial, trialId);
    }

    public void ReportGate(string trialId, int gate)
    {
        if (Authority) ServeTrialGate(AdventureService.LocalPeer, trialId, gate);
        else RpcId(1, MethodName.RequestTrialGate, trialId, gate);
    }

    public void AskLeaveTrial()
    {
        if (Authority) ServeTrialLeave(AdventureService.LocalPeer);
        else RpcId(1, MethodName.RequestTrialLeave);
    }

    public void AskBoard(string trialId)
    {
        if (Authority) SendBoard(AdventureService.LocalPeer, trialId);
        else RpcId(1, MethodName.RequestBoard, trialId);
    }

    public void AskGhost(string trialId)
    {
        if (Authority) ServeGhost(AdventureService.LocalPeer, trialId);
        else RpcId(1, MethodName.RequestGhost, trialId);
    }

    /// <summary>Uploads a personal best's ghost in parts (the server asked for none: it takes one only after it timed a best).</summary>
    public void UploadGhost(string trialId, byte[] bytes)
    {
        const int Chunk = 8000;
        int parts = Math.Max(1, (bytes.Length + Chunk - 1) / Chunk);
        for (int i = 0; i < parts; i++)
        {
            byte[] c = bytes.Skip(i * Chunk).Take(Chunk).ToArray();
            if (Authority) ServeGhostPart(AdventureService.LocalPeer, trialId, i, parts, c);
            else RpcId(1, MethodName.RequestGhostPart, trialId, i, parts, c);
        }
    }

    // ---- RPCs: client -> server ----------------------------------------------------------------------

    private long Sender => Multiplayer.GetRemoteSenderId();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestOpen(string id) { if (_server) Log(ServeOpen(Sender, id)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPick(string id) { if (_server) Log(ServePick(Sender, id)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSealStep(string chestId, int step) { if (_server) Log(ServeSealStep(Sender, chestId, step)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestOffer(string shrineId) { if (_server) ServeOffer(Sender, shrineId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestGuide(string chestId) { if (_server) Log(ServeGuide(Sender, chestId)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPhoto(string spotId, float yaw, float pitch, float fov) { if (_server) Log(ServePhoto(Sender, spotId, yaw, pitch, fov)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestFauna(string name) { if (_server) ServeFauna(Sender, name); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTrial(string trialId) { if (_server) Log(ServeTrialStart(Sender, trialId)); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTrialGate(string trialId, int gate) { if (_server) ServeTrialGate(Sender, trialId, gate); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTrialLeave() { if (_server) ServeTrialLeave(Sender); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBoard(string trialId) { if (_server && _adventure.Progress(Sender) != null) SendBoard(Sender, trialId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestGhost(string trialId) { if (_server) ServeGhost(Sender, trialId); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestGhostPart(string trialId, int part, int parts, byte[] chunk) { if (_server) ServeGhostPart(Sender, trialId, part, parts, chunk); }

    // ---- RPCs: server -> the asking peer only ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellOpened(string id, int[] items, int[] counts, byte tier, int francs, string note)
    {
        var lines = new List<LootLine>(items.Length);
        for (int i = 0; i < items.Length && i < counts.Length; i++) lines.Add(new LootLine((ItemId)items[i], counts[i]));
        GD.Print($"[rewards] opened {id}: CHF {francs}, {string.Join(", ", lines.Select(l => $"{l.Count} {l.Item}"))}{(note.Length > 0 ? $" ({note})" : "")}");
        ChestOpened?.Invoke(id, lines, (ChestTier)tier, francs, note);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellPicked(string id) => Picked?.Invoke(id);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellSeal(string chestId, int mask)
    {
        _masks[chestId] = mask;
        SealChanged?.Invoke(chestId, mask);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GuideLeg(string chestId, int leg, double since, bool done)
    {
        var m = new GuideMove(chestId, leg, since, done);
        Guide = done ? null : m;
        GuideMoved?.Invoke(m);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellRefused(string id, string why)
    {
        LastRefusal = why;
        Refused?.Invoke(id, why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellOffered(string shrineId, int steps, int offered) => Offered?.Invoke(shrineId, steps, offered);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellPhoto(string spotId, bool taken, string why) => PhotoJudged?.Invoke(spotId, taken, why);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellTrialStarted(string trialId, double[] gates, double since, double gold, double silver, double bronze)
    {
        var list = new TrialGate[gates.Length / 4];
        for (int i = 0; i < list.Length; i++) list[i] = new TrialGate(gates[i * 4], gates[i * 4 + 1], (float)gates[i * 4 + 2], (float)gates[i * 4 + 3]);
        Trial = new ActiveTrial(trialId, list, since, new TrialPar(gold, silver, bronze)) { Next = 0 };
        TrialStarted?.Invoke(Trial);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellTrialGate(string trialId, int gate)
    {
        if (Trial is { } t && t.Id == trialId) t.Next = Math.Max(t.Next, gate + 1);
        TrialGate?.Invoke(trialId, gate);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellTrialEnded(string trialId, double seconds, int medal, bool pb, int rank)
    {
        string id = trialId.Length > 0 ? trialId : Trial?.Id ?? "";
        Trial = null;
        TrialEnded?.Invoke(id, seconds, medal, pb, rank);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellBoard(string trialId, string[] names, double[] seconds)
    {
        var rows = new List<(string, double)>();
        for (int i = 0; i < names.Length && i < seconds.Length; i++) rows.Add((names[i], seconds[i]));
        _boards[trialId] = rows;
        BoardChanged?.Invoke(trialId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellGhost(string trialId, int part, int parts, byte[] chunk)
    {
        if (parts is < 1 or > 16 || part < 0 || part >= parts) return;
        if (!_ghostParts.TryGetValue(trialId, out var got) || got.Length != parts) _ghostParts[trialId] = got = new byte[parts][];
        got[part] = chunk;
        if (got.Any(x => x == null)) return;
        _ghostParts.Remove(trialId);
        if (GhostTrack.Decode(got.SelectMany(x => x).ToArray()) is { } samples) GhostReceived?.Invoke(trialId, samples);
    }
}

/// <summary>A trial this client runs: its gates, the server time it started, its par, and the next gate.</summary>
public sealed record ActiveTrial(string Id, TrialGate[] Gates, double Since, TrialPar Par)
{
    public int Next { get; set; }
}

/// <summary>The animals the fauna page lists (#1227, rewards plan 7): what can be photographed today, and the ones that wait for a wildlife system.</summary>
public static class Fauna
{
    public const string Cow = "Cow", Marmot = "Marmot";
    /// <summary>Shown greyed, "not yet in Switzerland's game".</summary>
    public static readonly string[] NotYet = { "Ibex", "Chamois" };

    public static bool Known(string name) => name is Cow or Marmot || Birds.BirdCatalog.ByName(name) != null;
}
