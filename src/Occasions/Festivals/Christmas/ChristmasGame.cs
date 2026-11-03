using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>
/// The Christmas market, the player's side (#1348, <c>docs/notes/occasions/festival-christmas.md</c>): one thing to do, on the interact key (E / the pad's Y /
/// a VR grip, which <c>XrPad</c> replays), so no new binding exists. Within <see cref="ChristmasRules.StallReach"/> of a stall, E takes the day's free taste
/// there (the stall-hop's stamp) and, once tasted, buys its ware for Batzen; at the Samichlaus, E takes his bag on 6 December. A bottom-centre panel says
/// what the stall sells and costs, how many stalls today's hop has stamped and what the last use paid. The server decides everything.
/// </summary>
public partial class ChristmasGame : CanvasLayer
{
    public const string NodeName = "ChristmasGame";

    public static ChristmasGame? Instance { get; private set; }

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", VolumeDb = -6f };
    private readonly Random _rng = new(1348);
    private FestivalService? _svc;
    private bool _hooked;
    private int _stall = -1;
    private bool _chlaus;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, int, int, int, int) _promptKey = ((InputDevice)(-1), -1, -1, -1, -1);
    private string _wareLine = "", _stampLine = "";
    private (int, int, int) _lineKey = (-1, -1, -1);

    /// <summary>The stall within reach (0 to 7), or -1.</summary>
    internal int NearStall => _stall;

    internal bool AtSamichlaus => _chlaus;

    internal string Message => _message;

    public ChristmasGame(Func<FootPlayer?> local)
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
            _svc.MarketTold -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.MarketTold += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnTold(int day, int mask, FestivalService.MarketEvent what, int stall, string text)
    {
        if (what == FestivalService.MarketEvent.Sync) { _view.QueueRedraw(); return; }
        Say(text);
        var bank = what switch
        {
            FestivalService.MarketEvent.Bought => ChristmasSounds.MugClink,
            FestivalService.MarketEvent.Bag => ChristmasSounds.SleighBells,
            _ => ChristmasSounds.StallTing,
        };
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = -6f + jitter;
        _sfx.Play();
    }

    private void OnRefused(string why)
    {
        // only the market's own refusals reach this screen: the ones said at a stall or the Samichlaus
        if (_svc?.MarketNow() == null || !(_stall >= 0 || _chlaus)) return;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
        _view.QueueRedraw();
    }

    // ---- what is near ---------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        Hook();
        int stall = -1;
        bool chlaus = false;
        if (_svc?.MarketNow() is { } m && _local() is { } p)
        {
            var g = p.Global;
            stall = ChristmasRules.NearestStall(m.Site, g.E, g.N);
            chlaus = stall < 0 && ChristmasRules.NearSamichlaus(m.Site, g.E, g.N);
        }
        bool shown = stall >= 0 || chlaus || GameClock.Now - _messageAt < 4;
        if (stall != _stall || chlaus != _chlaus) _view.QueueRedraw();
        _stall = stall;
        _chlaus = chlaus;
        if (_view.Visible != shown) _view.Visible = shown;
    }

    // ---- the interaction -------------------------------------------------------------------------------

    private int Held() =>
        _svc?.MarketNow() is { } m ? _svc.Mine.Instances.GetValueOrDefault(m.Run.Instance)?.Batzen ?? 0 : 0;

    /// <summary>The prompt of what E does here: the taste, the purchase or the Samichlaus's bag. Null away from all of them.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.MarketNow() is not { } m) return null;
        var device = PlayerInput.HintDevice;
        if (game._stall >= 0)
        {
            var st = ChristmasRules.Layout(m.Site)[game._stall];
            bool tasted = ChristmasRules.Stamped(game._svc.StampsToday(m.Run.Day), game._stall);
            var key = (device, game._stall, tasted ? 1 : 0, tasted ? game.Held() : 0, 0);
            if (key != game._promptKey)
            {
                game._promptKey = key;
                game._prompt = InputHints.Prompt(PlayerInput.InteractMount, tasted
                    ? $"Buy {ChristmasRules.WareName(st.Kind)} ({ChristmasRules.PriceOf(st.Kind)} Batzen)"
                    : $"Taste at the {ChristmasRules.StallName(st.Kind)} (free, +{ChristmasRules.StampBatzen} Batzen)");
            }
            var outline = Outline.Box(null, Vector3.Zero, Vector3.Zero);
            if (p.Origin is { } origin)
            {
                var w = origin.ToWorld(st.E, st.N, p.Global.Alt);
                outline = Outline.Box(null, w + new Vector3(-1.8f, -0.4f, -1.3f), w + new Vector3(1.8f, 2.8f, 1.3f));
            }
            return new Interaction(game._prompt, outline);
        }
        if (game._chlaus)
        {
            var key = (device, -2, m.Run.Day, 0, 0);
            if (key != game._promptKey)
            {
                game._promptKey = key;
                game._prompt = InputHints.Prompt(PlayerInput.InteractMount, m.Run.Day == ChristmasRules.SamichlausDay ? "Take the Samichlaus's bag" : "Greet the Samichlaus");
            }
            return new Interaction(game._prompt, default);
        }
        return null;
    }

    /// <summary>E / Y / a VR grip: tastes, buys or takes the bag. False when no stall and no Samichlaus is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.MarketNow() == null) return false;
        if (game._stall >= 0) { svc.AskMarket(game._stall); return true; }
        if (game._chlaus) { svc.AskMarket(FestivalService.MarketSamichlaus); return true; }
        return false;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    private void Lines(MarketStall st, int day, int held)
    {
        int stamps = ChristmasRules.Count(_svc!.StampsToday(day));
        var key = (_stall, held, stamps);
        if (key == _lineKey) return;
        _lineKey = key;
        _wareLine = $"{ChristmasRules.WareName(st.Kind)}: {ChristmasRules.PriceOf(st.Kind)} Batzen   -   you hold {held}";
        _stampLine = $"Stall-hop today: {stamps}/{ChristmasRules.Stalls} stalls tasted";
    }

    private sealed partial class View : Control
    {
        public ChristmasGame Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.MarketNow() is not { } m) return;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            double now = GameClock.Now;
            bool message = g._message.Length > 0 && now - g._messageAt < 4;
            if (g._stall >= 0)
            {
                var st = ChristmasRules.Layout(m.Site)[g._stall];
                g.Lines(st, m.Run.Day, g.Held());
                Panel(c, 96);
                Text(ChristmasRules.StallName(st.Kind), c + new Vector2(0, -22), UiTheme.FontHeading, UiTheme.Text, true);
                Text(g._wareLine, c + new Vector2(0, 4), UiTheme.FontBody, Gold);
                Text(g._stampLine, c + new Vector2(0, 28), UiTheme.FontSmall, UiTheme.TextDim);
                if (message) Text(g._message, c + new Vector2(0, 52), UiTheme.FontSmall, Amber);
                return;
            }
            if (g._chlaus)
            {
                Panel(c, 76);
                Text("Samichlaus", c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text(m.Run.Day == ChristmasRules.SamichlausDay ? "6 December: he has a bag for you." : "He comes on 6 December.", c + new Vector2(0, 12), UiTheme.FontBody, Gold);
                if (message) Text(g._message, c + new Vector2(0, 34), UiTheme.FontSmall, Amber);
                return;
            }
            if (message) Text(g._message, c, UiTheme.FontBody, Amber);
        }

        private void Panel(Vector2 c, float height) =>
            DrawRect(new Rect2(c.X - 380, c.Y - height / 2 - 12, 760, height), new Color(0, 0, 0, 0.45f), true);

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(380f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 760f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 760f, size, colour);
        }
    }
}
