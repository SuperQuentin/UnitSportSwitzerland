using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// The Sechseläuten, the player's side (#1358, <c>docs/notes/occasions/festival-sechselaeuten.md</c>): three things on two existing actions, so no new binding
/// exists. At the pyre, while bets are open and this player has not bet, <b>Use</b> (LMB / pad X / the VR trigger through <c>XrPad</c>) steps the minute picker round
/// the twelve buckets and <b>E</b> (pad Y / a VR grip) places the bet; once the head has burst, E grills a Cervelat on the embers. At the Zunft's Weibel E takes the
/// day's purse. A bottom-centre panel says what is at hand, the round's state, the pool and, after the bang, the burn time and the winners. The server decides
/// everything and tells nothing of the burn time before the head bursts. Nothing is built per frame: the lines are cached on the values they show.
/// </summary>
public partial class SechseGame : CanvasLayer
{
    public const string NodeName = "SechseGame";

    public static SechseGame? Instance { get; private set; }

    /// <summary>Whether Use belongs to this screen now (stepping the minute picker): the items let go of it.</summary>
    public static bool TakesUse => Instance is { _betting: true };

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", VolumeDb = -4f };
    private readonly Random _rng = new(1358);
    private FestivalService? _svc;
    private bool _hooked, _betting, _wasUse;
    private int _target = -1, _bucket = SechselaeutenRules.DefaultBucket, _lastMine;
    private double _distance = double.MaxValue;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, int, int, int, int, bool) _promptKey = ((InputDevice)(-1), -1, -1, -1, -1, false);
    private string[] _lines = [], _drawn = [];
    private bool _msgShown, _targetShown;
    private (InputDevice, int, int, int, int, int, int, int, int, double) _linesKey = ((InputDevice)(-1), -1, -1, -1, -1, -1, -1, -1, -1, -1);

    /// <summary>What is at hand: 0 the pyre, 2 the Weibel (<see cref="FestivalService.SechsePurse"/>), or -1.</summary>
    internal int NearTarget => _target;

    /// <summary>The bucket the picker stands at (1 to 12).</summary>
    internal int Bucket => _bucket;

    /// <summary>Use steps the picker now.</summary>
    internal bool Betting => _betting;

    internal string Message => _message;

    /// <summary>The panel's lines as drawn (checks read what a player is shown).</summary>
    internal string[] Lines => _lines;

    /// <summary>The use key, replaced by a check.</summary>
    internal Func<bool> UseHeld = () => PlayerInput.Held(PlayerInput.UseItem);

    public SechseGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        AddChild(_sfx);
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (_svc != null)
        {
            _svc.SechseTold -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.SechseTold += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnTold(BooggView view)
    {
        if (view.Msg.Length > 0 && !view.Msg.StartsWith("The head burst", StringComparison.Ordinal)) Say(view.Msg);   // the panel already shows the result
        if (view.Mine > 0 && _lastMine == 0) Play(SechselaeutenSounds.CoinStake);
        _lastMine = view.Mine;
        _view.QueueRedraw();
    }

    private void OnRefused(string why)
    {
        // only the festival's own refusals reach this screen: the ones said at the pyre or the Weibel
        if (_svc?.SechseNow() == null || _target < 0) return;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
        _view.QueueRedraw();
    }

    private void Play(SfxBank bank)
    {
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = -4f + jitter;
        _sfx.Play();
    }

    // ---- what is near ---------------------------------------------------------------------------------

    private BooggView? ViewNow => _svc?.SechseView is { } v && _svc.SechseNow() is { } m && v.Round > 0 ? v : null;

    public override void _Process(double delta)
    {
        Hook();
        int target = -1;
        _distance = double.MaxValue;
        if (_svc?.SechseNow() is { } m && _local() is { } p)
        {
            var g = p.Global;
            _distance = SechselaeutenRules.Dist(g.E, g.N, SechselaeutenRules.PyreAt(m.Site));
            target = SechselaeutenRules.NearWeibel(m.Site, g.E, g.N) ? FestivalService.SechsePurse : SechselaeutenRules.NearPyre(m.Site, g.E, g.N) ? 0 : -1;
        }
        else _lastMine = 0;
        _target = target;
        var view = ViewNow;
        _betting = target == 0 && view is { Phase: (int)BooggPhase.Betting, Mine: 0 };
        bool use = _betting && UseHeld();
        if (use && !_wasUse)
        {
            _bucket = _bucket % SechselaeutenRules.Buckets + 1;
            _view.QueueRedraw();
        }
        _wasUse = use;
        bool shown = target >= 0 || _distance <= 70 || GameClock.Now - _messageAt < 5;
        if (_view.Visible != shown) _view.Visible = shown;
        bool msg = GameClock.Now - _messageAt < 5;
        var lines = shown ? BuildLines() : _lines;
        if (msg != _msgShown || !ReferenceEquals(lines, _drawn) || (_target >= 0) != _targetShown)
        {
            _msgShown = msg;
            _drawn = lines;
            _targetShown = _target >= 0;
            _view.QueueRedraw();
        }
    }

    // ---- the interaction -------------------------------------------------------------------------------

    private bool PurseTaken()
    {
        if (_svc?.SechseNow() is not { } m) return false;
        return _svc.Mine.Instances.GetValueOrDefault(m.Run.Instance)?.Played.GetValueOrDefault(FestivalRules.PlayKey(SechselaeutenRules.PurseGame, m.Run.Day)) > 0;
    }

    /// <summary>The prompt of what E does here: place the bet, grill a Cervelat, take the purse. Null elsewhere, and while the pyre burns.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.SechseNow() == null || game._target < 0) return null;
        var view = game.ViewNow;
        bool purse = game._target == FestivalService.SechsePurse;
        int phase = view?.Phase ?? -1, mine = view?.Mine ?? 0;
        bool taken = purse && game.PurseTaken();
        string? text = purse ? (taken ? null : $"Take the Zunft's purse ({SechselaeutenRules.PurseBatzen} Batzen)")
            : phase == (int)BooggPhase.Betting && mine == 0 ? $"Bet {SechselaeutenRules.Stake} Batzen on {SechselaeutenRules.Label(game._bucket)}"
            : phase == (int)BooggPhase.Done ? "Grill a Cervelat on the embers"
            : null;
        if (text == null) return null;
        var device = PlayerInput.HintDevice;
        var key = (device, game._target, phase, mine, game._bucket, taken);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, text);
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: places the bet, grills or takes the purse. False when none of them is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.SechseNow() == null || game._target < 0) return false;
        var view = game.ViewNow;
        if (game._target == FestivalService.SechsePurse)
        {
            if (game.PurseTaken()) return false;
            svc.AskSechse(FestivalService.SechsePurse);
            return true;
        }
        if (view is not { } v) return false;
        if (v.Phase == (int)BooggPhase.Betting && v.Mine == 0)
        {
            svc.AskSechse(FestivalService.SechseBet, game._bucket);
            return true;
        }
        if (v.Phase == (int)BooggPhase.Done)
        {
            svc.AskSechse(FestivalService.SechseGrill);
            return true;
        }
        return false;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    /// <summary>The panel's lines, rebuilt only when a value they show changes.</summary>
    private string[] BuildLines()
    {
        var view = ViewNow;
        int minute = (int)(World.WorldClock.CurrentHour * 60);
        int board = view?.Board.Length ?? 0;
        var key = (PlayerInput.HintDevice, _target, view?.Round ?? 0, view?.Phase ?? -1, view?.Mine ?? 0, _bucket, view?.Bets ?? 0, view?.Pool ?? 0, board + (view?.Won ?? 0) * 100, view?.Phase == 0 ? minute : 0);
        if (key == _linesKey) return _lines;
        _linesKey = key;
        if (_target == FestivalService.SechsePurse)
            return _lines = ["The Zunft's Weibel", $"One purse a day, {SechselaeutenRules.PurseBatzen} Batzen, for those who bet on the Böögg.", PurseTaken() ? "You have it for today." : ""];
        if (view is not { } v) return _lines = ["The Böögg", "The guilds are building the pyre."];
        switch ((BooggPhase)v.Phase)
        {
            case BooggPhase.Betting:
                string lit = $"The pyre is lit at {(int)SechselaeutenRules.LightHour}:00 (it is {minute / 60:00}:{minute % 60:00}).";
                return _lines = v.Mine > 0
                    ? ["The Böögg", $"You bet {SechselaeutenRules.Label(v.Mine)}. {v.Bets} bets, pool {v.Pool} Batzen.", lit]
                    : ["The Böögg", $"Bet {v.Stake} Batzen on the minute the head bursts: {SechselaeutenRules.Label(_bucket)}   ({v.Bets} bets, pool {v.Pool})",
                        _target == 0 ? InputHints.Format("{use_item} change the minute   {interact_mount} place the bet") : "", lit];
            case BooggPhase.Burning:
                return _lines = ["The Böögg burns", $"Bets are closed: {v.Bets} bets, pool {v.Pool} Batzen." + (v.Mine > 0 ? $" You bet {SechselaeutenRules.Label(v.Mine)}." : ""), "Who guessed the minute the head will burst?"];
            default:
                var lines = new List<string> { "BANG!", $"The head burst after {FestivalService.Num(v.Minutes)} minutes ({SechselaeutenRules.Label(SechselaeutenRules.BucketOf(v.Minutes))}). Pool {v.Pool} Batzen." };
                if (v.Mine > 0) lines.Add($"You bet {SechselaeutenRules.Label(v.Mine)}: {v.Won} Batzen from the pool.");
                for (int i = 0; i < Math.Min(3, v.Board.Length); i++)
                    lines.Add($"{i + 1}. {v.Board[i].Name}{(v.Board[i].Bot ? " (bot)" : "")}  {SechselaeutenRules.Label(v.Board[i].Bucket)}  {v.Board[i].Won} Batzen");
                if (_target == 0) lines.Add(InputHints.Format("{interact_mount} grill a Cervelat on the embers"));
                return _lines = lines.ToArray();
        }
    }

    private sealed partial class View : Control
    {
        public SechseGame Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.SechseNow() == null) return;
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            bool message = g._message.Length > 0 && GameClock.Now - g._messageAt < 5;
            if (g._target >= 0 || g._distance <= 70)
            {
                var lines = g.BuildLines();
                int filled = 0;
                foreach (string l in lines) if (l.Length > 0) filled++;
                float height = 36 + 22 * Math.Max(0, filled - 1) + (message ? 20 : 0);
                float top = c.Y + 52 - height;   // the panel grows upward from a fixed bottom, clear of the hotbar
                DrawRect(new Rect2(c.X - 400, top, 800, height), new Color(0, 0, 0, 0.45f), true);
                float y = top + 22;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    if (i == 0) Text(lines[i], new Vector2(c.X, y), UiTheme.FontHeading, UiTheme.Text, true);
                    else Text(lines[i], new Vector2(c.X, y), i == lines.Length - 1 && g._target == 0 ? UiTheme.FontSmall : UiTheme.FontBody, i == 1 ? Gold : UiTheme.TextDim);
                    y += i == 0 ? 26 : 22;
                }
                if (message) Text(g._message, new Vector2(c.X, y), UiTheme.FontSmall, Amber);
                return;
            }
            if (message) Text(g._message, c, UiTheme.FontBody, Amber);
        }

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(390f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 780f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 780f, size, colour);
        }
    }
}
