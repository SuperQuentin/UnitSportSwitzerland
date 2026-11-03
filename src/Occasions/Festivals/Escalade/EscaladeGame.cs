using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// The Escalade, the player's side (#1351, <c>docs/notes/occasions/festival-escalade.md</c>): three things to do, all on the interact key (E / the pad's Y /
/// a VR grip, which <c>XrPad</c> replays), so no new binding exists. At the Marmite E smashes it (the party's youngest and oldest both press, the first
/// holds it); at the soup pot E ladles a bowl for the whole party; at the start line E begins the Course de l'Escalade. A bottom-centre panel says what
/// is at hand; while the Course runs it shows the clock, the next gate and its bearing. The server decides everything. Nothing is built per frame:
/// strings are cached on the keys that change, and the panel redraws on change.
/// </summary>
public partial class EscaladeGame : CanvasLayer
{
    public const string NodeName = "EscaladeGame";

    public static EscaladeGame? Instance { get; private set; }

    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", VolumeDb = -6f };
    private readonly Random _rng = new(1351);
    private FestivalService? _svc;
    private bool _hooked;
    private int _target = -1;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, int, int) _promptKey = ((InputDevice)(-1), -1, -1);
    private string _runLine = "";
    private (int, int, int, int) _runKey = (-1, -1, -1, -1);
    private bool _running;
    private int _next;
    private double _runStart;

    /// <summary>What is at hand (<see cref="FestivalService.EscaladeMarmite"/> and the others), or -1.</summary>
    internal int NearTarget => _target;

    /// <summary>The Course is running for this player and the gate it heads for next (0 to 6, 6 is the line).</summary>
    internal bool Running => _running;

    internal int NextGate => _next;

    internal string Message => _message;

    public EscaladeGame(Func<FootPlayer?> local)
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
            _svc.EscaladeTold -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.EscaladeTold += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnTold(FestivalService.EscaladeEvent what, int a, int ms, string text)
    {
        switch (what)
        {
            case FestivalService.EscaladeEvent.CourseStart:
                _running = true;
                _next = 0;
                _runStart = GameClock.Now;
                break;
            case FestivalService.EscaladeEvent.Gate:
                _next = a;
                break;
            case FestivalService.EscaladeEvent.CourseDone:
            case FestivalService.EscaladeEvent.CourseLost:
                _running = false;
                break;
        }
        Say(text);
        var bank = what switch
        {
            FestivalService.EscaladeEvent.Smashed => EscaladeSounds.MarmiteSmash,
            FestivalService.EscaladeEvent.Ladled => EscaladeSounds.PotLadle,
            FestivalService.EscaladeEvent.CourseStart => EscaladeSounds.CourseHorn,
            FestivalService.EscaladeEvent.Gate or FestivalService.EscaladeEvent.CourseDone => EscaladeSounds.GateBell,
            _ => EscaladeSounds.GateBell,
        };
        if (what == FestivalService.EscaladeEvent.CourseLost) return;
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = -6f + jitter;
        _sfx.Play();
    }

    private void OnRefused(string why)
    {
        // only the Escalade's own refusals reach this screen: the ones said at the Marmite, the pot or the line
        if (_svc?.EscaladeNow() == null || _target < 0) return;
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
        int target = -1;
        if (_svc?.EscaladeNow() is { } m && _local() is { } p)
        {
            var g = p.Global;
            target = EscaladeRules.NearMarmite(m.Site, g.E, g.N) ? FestivalService.EscaladeMarmite
                : EscaladeRules.NearPot(m.Site, g.E, g.N) ? FestivalService.EscaladePot
                : EscaladeRules.NearStart(m.Site, g.E, g.N) ? FestivalService.EscaladeStart : -1;
        }
        else if (_running) _running = false;   // the festival ended, or this body is gone
        bool shown = target >= 0 || _running || GameClock.Now - _messageAt < 4;
        if (target != _target || _running) _view.QueueRedraw();
        _target = target;
        if (_view.Visible != shown) _view.Visible = shown;
    }

    // ---- the interaction -------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here, or null away from the Marmite, the pot and the line.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.EscaladeNow() == null || game._target < 0) return null;
        var device = PlayerInput.HintDevice;
        var key = (device, game._target, game._running ? 1 : 0);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, game._target switch
            {
                FestivalService.EscaladeMarmite => "Smash the Marmite (the party's youngest and oldest together)",
                FestivalService.EscaladePot => "Ladle the soupe (feeds the party)",
                _ => game._running ? "Start the Course again" : "Start the Course de l'Escalade",
            });
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: smashes, ladles or starts. False when none of them is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.EscaladeNow() == null || game._target < 0) return false;
        svc.AskEscalade(game._target);
        return true;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    /// <summary>The run's line: the clock, the gate and its bearing and distance, rebuilt only when a shown value changes.</summary>
    private void RunLine(ResolvedSite site, FootPlayer p)
    {
        var path = EscaladeRules.Path(site);
        var g = p.Global;
        var to = path[Math.Clamp(_next, 0, EscaladeRules.Gates)];
        double dx = to.E - g.E, dn = to.N - g.N;
        int dist = (int)(Math.Sqrt(dx * dx + dn * dn) / 5) * 5;
        int bearing = (int)Math.Round(Math.Atan2(dx, dn) / (Math.PI / 4));   // 0 = north, clockwise
        int secs = (int)(GameClock.Now - _runStart);
        var key = (secs, _next, dist, ((bearing % 8) + 8) % 8);
        if (key == _runKey) return;
        _runKey = key;
        string where = _next >= EscaladeRules.Gates ? "the line" : $"gate {_next + 1}/{EscaladeRules.Gates}";
        _runLine = $"{secs / 60}:{secs % 60:00}   -   {where}: {dist} m {Compass[key.Item4]}";
    }

    private sealed partial class View : Control
    {
        public EscaladeGame Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.EscaladeNow() is not { } m) return;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            bool message = g._message.Length > 0 && GameClock.Now - g._messageAt < 4;
            if (g._running && g._local() is { } p) g.RunLine(m.Site, p);
            float height = g._running ? 76 : g._target >= 0 ? 76 : 0;
            if (height > 0) Panel(c, height);
            if (g._running)
            {
                Text("Course de l'Escalade", c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text(g._runLine, c + new Vector2(0, 12), UiTheme.FontBody, Gold);
                if (message) Text(g._message, c + new Vector2(0, 34), UiTheme.FontSmall, Amber);
                return;
            }
            if (g._target >= 0)
            {
                (string title, string line) = g._target switch
                {
                    FestivalService.EscaladeMarmite => ("La Marmite", "Youngest and oldest of the party smash it together."),
                    FestivalService.EscaladePot => ("Soupe de l'Escalade", "One ladle feeds everyone of the party near the pot."),
                    _ => ("Start line", "Six gates through the old town, then back here. Par is a good jog."),
                };
                Text(title, c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text(line, c + new Vector2(0, 12), UiTheme.FontBody, Gold);
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
