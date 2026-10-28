using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Alcohol;

/// <summary>One breath reading as a screen shows it (#1271): who blew, who was measured, the level, the mouth-alcohol seconds left, which device.</summary>
public readonly record struct BreathResult(long Tester, long Subject, double Level, int WaitSeconds, BreathKind Kind)
{
    public bool Waiting => WaitSeconds > 0;
}

/// <summary>Which device a reading comes from: the electronic one on yourself, the single-use tube, the electronic one on a friend.</summary>
public enum BreathKind : byte { Electronic = 0, Tube = 1, Friend = 2 }

/// <summary>
/// Alcohol (#1271, <c>docs/plans/adventure-alcohol.md</c>, <c>docs/notes/adventure/alcohol.md</c>), at <c>World/Alcohol</c> on the
/// server and every client (RPCs route by node path), made when <see cref="Systems.Adventure"/> is on. Offline the client plays
/// the server's part, as the Adventure layer does.
///
/// <para>
/// <b>The server keeps the drink log</b> (the <c>alcohol</c> progress section, <see cref="AlcoholProgress"/>) and every reading is
/// its number: a client reports a drink (<c>ReportDrank</c>), asks for its own reading or offers a friend a test, and the server
/// computes the level from the log (<see cref="Bac"/>) and tells the screens (<c>TellResult</c>). A client that does not report
/// a drink only spares itself the effects. The section reaches its owner through the progress sync, so the owner computes its own
/// level (for the effects) from the same log, on the same real clock (<see cref="AlcoholClock"/>), at most four times a second:
/// no <c>Level</c> message is needed.
/// </para>
/// <para>
/// <b>The effects</b> (<see cref="AlcoholRules.Effects"/>) act on the local body through one <see cref="AlcoholBody"/> set on
/// <see cref="FootPlayer.Drunk"/>: lag and noise on the vehicle input, lag and drift on the walk wish, the camera sway, stumbles,
/// no free climbing, passing out. Nobody else's level is sent anywhere; the others see only the stumble and the lying body.
/// </para>
/// </summary>
public partial class AlcoholService : Node
{
    public const string NodeName = "Alcohol";
    /// <summary>The invite kind of a breath test offered to a friend.</summary>
    public const string InviteId = "breath";
    /// <summary>A test is offered within this distance, m.</summary>
    public const float OfferReach = 4.5f;

    public static AlcoholService? Instance { get; private set; }

    /// <summary>Checks: forces the mode on this copy (null: the server's or the setting).</summary>
    public static AlcoholMode? ModeOverride { get; set; }

    private bool _server, _networked;
    private AdventureService _adventure = null!;
    private AlcoholMode _serverMode = AlcoholMode.Real;
    private readonly Random _rng = new();
    private readonly Func<float> _random;

    // ---- hooks, set by the world that makes it ---------------------------------------------------------

    /// <summary>A peer's body: on the server its proxy, on a client the remote copy (or the own body).</summary>
    public Func<long, FootPlayer?>? BodyOf { get; set; }
    /// <summary>Client: the local player's body (null in a vehicle's seat view or the free camera).</summary>
    public Func<FootPlayer?>? Me { get; set; }
    /// <summary>A peer's display name.</summary>
    public Func<long, string>? NameOf { get; set; }
    /// <summary>Client: shows a line to the local player.</summary>
    public Action<string>? Toast { get; set; }

    public AlcoholService() => _random = () => (float)_rng.NextDouble();

    public static AlcoholService Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new AlcoholService { Name = NodeName, _server = server, _networked = networked, _adventure = adventure };
        if (server) s._serverMode = AlcoholRules.ParseMode(CmdArgs.Value("--alcohol"));
        if (CmdArgs.Value("--alcohol-clock") is { } c && double.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
            AlcoholClock.Speed(speed);
        adventure.GetParent().AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_adventure != null && !_server) _adventure.SectionChanged -= OnSection;
    }

    public override void _Ready()
    {
        if (_server)
        {
            InviteService.Server?.Register(new InviteKind
            {
                Id = InviteId, Label = "breath test", Seconds = 30, TellAccepted = false,
                Accept = (peer, invite) => ServeTestOf(invite.From, peer) ?? $"You blew into {Who(invite.From)}'s breathalyzer.",
            });
        }
        else
        {
            _adventure.SectionChanged += OnSection;
            _sfx = new AudioStreamPlayer { Name = "AlcoholSfx", Bus = "Master", VolumeDb = -4f };
            AddChild(_sfx);
        }
        GD.Print($"[alcohol] {(_server ? "server" : _networked ? "client" : "offline")}, mode {Mode.ToString().ToLowerInvariant()}");
    }

    /// <summary>The mode here: a check's, the server's own, the server's as <c>Arrival</c> carried it, or the setting offline.</summary>
    public AlcoholMode Mode => ModeOverride ?? (_server ? _serverMode
        : _networked ? (AlcoholMode)Permissions.ServerAlcohol
        : (AlcoholMode)Math.Clamp(GameSettings.Current.AlcoholMode, 0, 2));

    /// <summary>This copy decides: the server, or an offline client.</summary>
    public bool Authority => _server || !_networked;

    /// <summary>The local player's id (1 offline, its own peer online).</summary>
    public long Self => _networked ? Multiplayer.GetUniqueId() : AdventureService.LocalPeer;

    private ChatManager? Chat => GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private string Who(long peer) => NameOf?.Invoke(peer) ?? $"#{peer}";
    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    // ---- the drink log and the readings, on the authority -----------------------------------------------

    private PlayerProgress? ProgressOf(long peer) => _adventure.Progress(peer) ?? (Authority ? _adventure.SessionFor(peer) : null);

    /// <summary>A body mass from the figure's build, kg (shown nowhere): 60 to 90, 75 when unknown.</summary>
    public double MassOf(long peer) => BodyOf?.Invoke(peer) is { } body ? MassOfBits(body.AppearanceBits) : Bac.DefaultKg;

    public static double MassOfBits(int bits) => Appearance.Unpack(bits) is { } a ? a.Build switch
    {
        BodyBuild.Slim => 62,
        BodyBuild.Curvy => 70,
        BodyBuild.Lean => 72,
        BodyBuild.Broad => 85,
        BodyBuild.Stocky => 90,
        _ => 60,
    } : Bac.DefaultKg;

    /// <summary>
    /// Authority: <paramref name="peer"/> drank <paramref name="ml"/> of a drink of strength <paramref name="abv"/>: its grams go in
    /// the log. The fondue pot and the Gluehwein of the festivals come through here too. False when alcohol is off.
    /// </summary>
    public bool ServeDrink(long peer, double ml, double abv, bool fed, string why)
    {
        if (!Authority || Mode == AlcoholMode.Off || ProgressOf(peer) is not { } p) return false;
        double grams = Bac.Grams(ml, abv);
        if (grams <= 0) return false;
        Add(peer, p, grams, fed, why);
        return true;
    }

    /// <summary>Authority: a fixed amount of ethanol, grams (the fondue's wine and kirsch).</summary>
    public bool ServeGrams(long peer, double grams, bool fed, string why)
    {
        if (!Authority || Mode == AlcoholMode.Off || grams <= 0 || ProgressOf(peer) is not { } p) return false;
        Add(peer, p, grams, fed, why);
        return true;
    }

    private void Add(long peer, PlayerProgress p, double grams, bool fed, string why)
    {
        var a = p.Get(AlcoholProgress.Section);
        Bac.Add(a.Drinks, AlcoholClock.Now, grams, fed);
        p.Touch(AlcoholProgress.Section);
        GD.Print(FormattableString.Invariant($"[alcohol] {Who(peer)} drinks {grams:F1} g ({why}): level {LevelOf(peer):F2}"));
    }

    /// <summary>Authority: the level of <paramref name="peer"/> now (or at <paramref name="at"/>), per mille.</summary>
    public double LevelOf(long peer, double? at = null) =>
        ProgressOf(peer) is { } p ? Bac.Level(p.Get(AlcoholProgress.Section).Drinks, at ?? AlcoholClock.Now, MassOf(peer)) : 0;

    /// <summary>Authority: seconds of mouth alcohol left for <paramref name="peer"/>.</summary>
    public double MouthLeftOf(long peer) => ProgressOf(peer) is { } p ? Bac.MouthLeft(p.Get(AlcoholProgress.Section).Drinks, AlcoholClock.Now) : 0;

    /// <summary>Authority: a server-run bot has no drink log: it reads 0.00 (a bot never drinks).</summary>
    private bool IsBot(long peer) => BodyOf?.Invoke(peer) is { Bot: true };

    /// <summary>Authority: <paramref name="peer"/> blew into its own device: the reading goes to its screen.</summary>
    public void ServeSelfTest(long peer, bool single)
    {
        if (!Authority) return;
        double level = Mode == AlcoholMode.Off ? 0 : LevelOf(peer), mouth = Mode == AlcoholMode.Off ? 0 : MouthLeftOf(peer);
        Deliver(peer, new BreathResult(peer, peer, level, (int)Math.Ceiling(mouth), single ? BreathKind.Tube : BreathKind.Electronic));
    }

    /// <summary>Authority: <paramref name="tester"/> offers <paramref name="to"/> a test (hold E at them with the device): an invite they accept. Null when sent, else why not.</summary>
    public string? ServeOffer(long from, long to)
    {
        if (!Authority) return "Not here.";
        if (from == to) return Refuse(from, "Nobody to test.");
        if (BodyOf?.Invoke(from) is { } a && BodyOf?.Invoke(to) is { } b && a.GlobalPosition.DistanceTo(b.GlobalPosition) > OfferReach)
            return Refuse(from, "Get closer first.");
        // a server-run bot accepts at once and reads 0.00; offline a check's partner does too
        if (IsBot(to) || !_networked) return ServeTestOf(from, to) is { } no ? Refuse(from, no) : null;
        if (InviteService.Server is not { } invites) return Refuse(from, "Invites are not running.");
        string reply = invites.Send(InviteId, from, to, $"{Who(from)} offers you a breath test", "Blow into their breathalyzer: you both see the reading", out var sent, 0, 30);
        if (sent == null) return Refuse(from, reply);
        Chat?.Tell(from, $"[alcohol] {reply}", ChatKind.System);
        return null;
    }

    /// <summary>Authority: the accepted test of <paramref name="subject"/> by <paramref name="tester"/>: the reading goes to both screens. Null when done.</summary>
    public string? ServeTestOf(long tester, long subject)
    {
        if (!Authority) return "Not here.";
        double level = 0, mouth = 0;
        if (!IsBot(subject) && Mode != AlcoholMode.Off)
        {
            level = LevelOf(subject);
            mouth = MouthLeftOf(subject);
        }
        var r = new BreathResult(tester, subject, level, (int)Math.Ceiling(mouth), BreathKind.Friend);
        Deliver(tester, r);
        if (subject != tester) Deliver(subject, r);
        GD.Print(FormattableString.Invariant($"[alcohol] {Who(tester)} tests {Who(subject)}: {level:F2} per mille{(mouth > 0 ? " (mouth alcohol)" : "")}"));
        return null;
    }

    /// <summary>
    /// <c>/alcohol</c>: what the mode is (nothing shows a level: measure it). <c>/alcohol test drink &lt;beers&gt; &lt;minutes ago&gt;</c>, the
    /// checks seam (with <c>--alcohol-test</c> on a server): beers drunk that long ago.
    /// </summary>
    public string Command(long sender, string rest)
    {
        long peer = _networked ? sender : AdventureService.LocalPeer;
        var w = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0)
            return $"[alcohol] Alcohol is {Mode.ToString().ToLowerInvariant()} here. Nothing shows your level: measure it with a breathalyzer.";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (w[0] == "test" && w.Length >= 4 && w[1] == "drink" && (!_server || CmdArgs.Has("--alcohol-test"))
            && double.TryParse(w[2], System.Globalization.NumberStyles.Float, inv, out double beers)
            && double.TryParse(w[3], System.Globalization.NumberStyles.Float, inv, out double minutes))
        {
            if (Mode == AlcoholMode.Off || ProgressOf(peer) is not { } p) return "[alcohol] Alcohol is off.";
            Bac.Add(p.Get(AlcoholProgress.Section).Drinks, AlcoholClock.Now - minutes * 60, beers * Bac.Grams(330, 0.05), fed: false);
            p.Touch(AlcoholProgress.Section);
            GD.Print(FormattableString.Invariant($"[alcohol] test: {Who(peer)} drank {beers} beers {minutes} min ago: level {LevelOf(peer):F2}"));
            return "[alcohol] test: drinks logged.";
        }
        return "[alcohol] Usage: /alcohol";
    }

    private string Refuse(long to, string why)
    {
        if (_networked && Connected(to)) Chat?.Tell(to, $"[alcohol] {why}", ChatKind.System);
        else if (!_networked) Toast?.Invoke(why);
        return why;
    }

    private void Deliver(long to, BreathResult r)
    {
        if (!_networked) { Show(r); return; }
        if (Connected(to)) RpcId(to, MethodName.TellResult, r.Tester, r.Subject, (int)Math.Round(r.Level * 100), r.WaitSeconds, (byte)r.Kind);
    }

    // ---- the owner's side ---------------------------------------------------------------------------------

    /// <summary>The local player drank <paramref name="item"/> (<see cref="ItemController"/>): the server logs it. <paramref name="fed"/>: the stomach is full.</summary>
    public void Drank(ItemId item, bool fed)
    {
        if (ItemDefs.Get(item) is not { Abv: > 0f, DrinkMl: > 0f } def) return;
        if (Authority) ServeDrink(Self, def.DrinkMl, def.Abv, fed, def.Name);
        else RpcId(1, MethodName.ReportDrank, (int)item, fed);
    }

    /// <summary>The local player starts to blow (the device's tone).</summary>
    public void BlowStart() => PlaySfx(AlcoholSounds.Blow);

    /// <summary>The five seconds are over: the beeps, and the server's reading (the single-use tube was taken from the pack by the caller).</summary>
    public void Blown(bool single)
    {
        _sfx?.Stop();
        PlaySfx(AlcoholSounds.Beep);
        if (Authority) ServeSelfTest(Self, single);
        else RpcId(1, MethodName.RequestSelfTest, single);
    }

    /// <summary>The local player offers <paramref name="target"/> a test (E at them with the electronic device).</summary>
    public void AskTest(long target)
    {
        if (Authority) ServeOffer(Self, target);
        else RpcId(1, MethodName.RequestOffer, target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReportDrank(int item, bool fed)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (ItemDefs.Get((ItemId)item) is { Abv: > 0f, DrinkMl: > 0f } def) ServeDrink(peer, def.DrinkMl, def.Abv, fed, def.Name);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSelfTest(bool single) { if (_server) ServeSelfTest(Multiplayer.GetRemoteSenderId(), single); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestOffer(long target) { if (_server) ServeOffer(Multiplayer.GetRemoteSenderId(), target); }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellResult(long tester, long subject, int centi, int waitSeconds, byte kind)
    {
        if (_server) return;
        Show(new BreathResult(tester, subject, centi / 100.0, waitSeconds, (BreathKind)Math.Min(kind, (byte)2)));
    }

    /// <summary>The last reading this player's screen showed (checks).</summary>
    public BreathResult? LastResult { get; private set; }
    /// <summary>A reading arrived for the local player's screen.</summary>
    public event Action<BreathResult>? Result;

    private void Show(BreathResult r)
    {
        LastResult = r;
        if (r.Kind == BreathKind.Tube) PlaySfx(AlcoholSounds.Hiss);
        Result?.Invoke(r);
        Toast?.Invoke(Describe(r));
    }

    /// <summary>The reading as one line (the toast, the log): the screen's own words.</summary>
    public string Describe(BreathResult r)
    {
        string name = r.Kind == BreathKind.Friend && r.Subject != Self ? Who(r.Subject) + ": " : "";
        if (r.Kind == BreathKind.Tube)
            return AlcoholRules.TubeText(AlcoholRules.Tube(r.Level), r.Waiting);
        var e = AlcoholRules.Electronic(r.Level, r.WaitSeconds);
        return name + (e.Waiting ? e.Main : e.Main + " · " + e.Limit);
    }

    // ---- the effects on the local body ------------------------------------------------------------------------

    /// <summary>The local body's filter: its effects follow the level.</summary>
    public AlcoholBody Body { get; } = new();
    /// <summary>The local player's level now, per mille (sampled at 4 Hz). For the effects and the checks; the HUD never shows it.</summary>
    public double Level { get; private set; }
    /// <summary>The local level changed enough to redraw (the tipsy icon, the blur).</summary>
    public event Action? LevelChanged;

    private double _nextSample;
    private AudioStreamPlayer? _sfx;
    private double _refusedAt = -100;

    /// <summary>The VR comfort vignette's strength from the level (read by the XR rig; 0 sober).</summary>
    public static float VrVignette => Instance is { } s && s.Body.Fx.Blur > 0f ? s.Body.Fx.Blur * 0.6f : 0f;

    private void OnSection(string id)
    {
        if (id == AlcoholProgress.Section.Id) _nextSample = 0;
    }

    public override void _Process(double delta)
    {
        if (_server) return;
        double now = GameClock.Now;
        if (now < _nextSample) return;
        _nextSample = now + 0.25;
        Resample();
    }

    /// <summary>Samples the level from the synced log and hands the effects to the body (at most four times a second).</summary>
    public void Resample()
    {
        var me = Me?.Invoke();
        double kg = me != null ? MassOfBits(me.AppearanceBits) : Bac.DefaultKg;
        var log = _adventure.Mine.Get(AlcoholProgress.Section).Drinks;
        double level = LevelOverride ?? (log.Count == 0 ? 0 : Bac.Level(log, AlcoholClock.Now, kg));
        var mode = Mode;
        Body.Fx = AlcoholRules.Effects(level, mode);
        Body.ViewEffects = GameSettings.Current.AlcoholViewEffects;
        Body.Vr = XR.XrSession.Active;
        Body.ClimbRefused ??= OnClimbRefused;
        if (me != null && me.Drunk != Body) me.Drunk = Body;
        if (Math.Abs(level - Level) > 0.005 || level == 0 != (Level == 0))
        {
            Level = level;
            LevelChanged?.Invoke();
        }
    }

    /// <summary>Checks: the level the effects follow, whatever the log says (null: the log's).</summary>
    public static double? LevelOverride { get; set; }

    private void OnClimbRefused()
    {
        double now = GameClock.Now;
        if (now - _refusedAt < 4) return;
        _refusedAt = now;
        Toast?.Invoke("Not in this state.");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_server || Me?.Invoke() is not { } me || me.Drunk != Body) return;
        switch (Body.State.Step(Body.Fx, (float)delta, _random))
        {
            case DrunkEvent.Stumble when me.Ride == RideKind.OnFoot:
                me.Fall(AlcoholRules.StumbleSeconds);
                Toast?.Invoke("You stumble.");
                break;
            case DrunkEvent.PassOut:
                me.PassOut(true);
                Toast?.Invoke("You pass out.");
                break;
            case DrunkEvent.WakeUp:
                me.PassOut(false);
                Toast?.Invoke("You come to.");
                break;
        }
    }

    private void PlaySfx(AudioStream stream)
    {
        if (_sfx == null || NetworkManager.DedicatedServer) return;
        _sfx.Stream = stream;
        _sfx.Play();
    }
}
