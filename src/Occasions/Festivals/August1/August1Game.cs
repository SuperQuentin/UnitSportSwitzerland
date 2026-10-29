using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// The 1 August, the player's side (#1355, <c>docs/notes/occasions/festival-august1.md</c>): the lampion stand, the fireworks stand and the lampion walk's post are
/// all on the interact key (E / the pad's Y / a VR grip, which <c>XrPad</c> replays), so no new binding exists; a box of fireworks is lit with the use key like any
/// item in hand (<see cref="ItemUse.Firework"/>: LMB / X / the trigger). A bottom-centre panel says what is at hand; while the walk runs it shows the clock, the next
/// stop and its bearing and how many walk. The server decides everything. Nothing is built per frame: strings are cached on the keys that change.
/// </summary>
public partial class August1Game : CanvasLayer
{
    public const string NodeName = "August1Game";

    public static August1Game? Instance { get; private set; }

    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", VolumeDb = -6f };
    private readonly Random _rng = new(1355);
    private FestivalService? _svc;
    private bool _hooked;
    private int _target = -1;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity, _lastLight = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, int, int) _promptKey = ((InputDevice)(-1), -1, -1);
    private string _runLine = "";
    private (int, int, int, int) _runKey = (-1, -1, -1, -1);
    private bool _walking;
    private int _next, _walkers;
    private double _runStart;

    /// <summary>What is at hand (<see cref="FestivalService.August1LampionStand"/> and the others), or -1.</summary>
    internal int NearTarget => _target;

    /// <summary>The walk is running for this player, the stop it heads for next (0 to 10, 10 is the post) and how many walk (people and children).</summary>
    internal bool Walking => _walking;

    internal int NextStop => _next;

    internal int Walkers => _walkers;

    internal string Message => _message;

    public August1Game(Func<FootPlayer?> local)
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
            _svc.August1Told -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.August1Told += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnTold(FestivalService.August1Event what, int a, int ms, string text)
    {
        switch (what)
        {
            case FestivalService.August1Event.WalkStart:
                _walking = true;
                _next = 0;
                _walkers = a + ms;
                _runStart = GameClock.Now;
                break;
            case FestivalService.August1Event.Waypoint:
                _next = a;
                break;
            case FestivalService.August1Event.WalkDone:
            case FestivalService.August1Event.WalkLost:
                _walking = false;
                break;
        }
        Say(text);
        SfxBank? bank = what switch
        {
            FestivalService.August1Event.Lampion => August1Sounds.LampionRustle,
            FestivalService.August1Event.Bought or FestivalService.August1Event.WalkStart or FestivalService.August1Event.Waypoint => August1Sounds.StopChime,
            FestivalService.August1Event.WalkDone => August1Sounds.WalkFanfare,
            FestivalService.August1Event.Lit => August1Sounds.FuseHiss,
            _ => null,
        };
        if (bank == null) return;
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = -6f + jitter;
        _sfx.Play();
    }

    private void OnRefused(string why)
    {
        // only the 1 August's own refusals reach this screen: the ones said at a stand or the post, or by a box
        if (_svc?.August1Now() == null || (_target < 0 && GameClock.Now - _lastLight > 2)) return;
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
        if (_svc?.August1Now() is { } m && _local() is { } p)
        {
            var g = p.Global;
            target = August1Rules.NearLampionStand(m.Site, g.E, g.N) ? FestivalService.August1LampionStand
                : August1Rules.NearFireworkStand(m.Site, g.E, g.N) ? FestivalService.August1FireworkStand
                : August1Rules.NearPost(m.Site, g.E, g.N) ? FestivalService.August1Post : -1;
        }
        else if (_walking) _walking = false;   // the festival ended, or this body is gone
        bool shown = target >= 0 || _walking || GameClock.Now - _messageAt < 4;
        if (target != _target || _walking) _view.QueueRedraw();
        _target = target;
        if (_view.Visible != shown) _view.Visible = shown;
    }

    // ---- the interaction -------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here, or null away from the stands and the post.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.August1Now() == null || game._target < 0) return null;
        var device = PlayerInput.HintDevice;
        var key = (device, game._target, game._walking ? 1 : 0);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, game._target switch
            {
                FestivalService.August1LampionStand => "Take a lampion (one a day)",
                FestivalService.August1FireworkStand => $"Buy a box of fireworks ({August1Rules.BoxPrice} Batzen)",
                _ => game._walking ? "Start the lampion walk again" : "Start the lampion walk (your party, at night)",
            });
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: takes a lampion, buys a box or starts the walk. False when none of them is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.August1Now() == null || game._target < 0) return false;
        svc.AskAugust1(game._target);
        return true;
    }

    /// <summary>
    /// The use key with a box of fireworks in hand (<see cref="ItemUse.Firework"/>): when the 1 August is on, it is night and the last box was lit 25 s ago or
    /// more, the server is asked to light it and true is returned (the caller uses the box up). Otherwise false, and <paramref name="why"/> says why.
    /// </summary>
    public static bool TryLight(FootPlayer p, out string? why)
    {
        why = null;
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.August1Now() == null) { why = "The fireworks are for the 1 August."; return false; }
        if (!August1Rules.IsNight(svc.Hour())) { why = "Fireworks are for the night: after 20:30."; return false; }
        double wait = August1Rules.OwnCooldown - (GameClock.Now - game._lastLight);
        if (wait > 0) { why = $"Wait {wait:F0} s before the next box."; return false; }
        game._lastLight = GameClock.Now;
        svc.AskAugust1(FestivalService.August1Light);
        return true;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    /// <summary>The walk's line: the clock, the stop and its bearing and distance, rebuilt only when a shown value changes.</summary>
    private void RunLine(ResolvedSite site, FootPlayer p)
    {
        var path = August1Rules.Path(site);
        var g = p.Global;
        var to = path[Math.Clamp(_next, 0, August1Rules.Waypoints)];
        double dx = to.E - g.E, dn = to.N - g.N;
        int dist = (int)(Math.Sqrt(dx * dx + dn * dn) / 5) * 5;
        int bearing = (int)Math.Round(Math.Atan2(dx, dn) / (Math.PI / 4));   // 0 = north, clockwise
        int secs = (int)(GameClock.Now - _runStart);
        var key = (secs, _next, dist, ((bearing % 8) + 8) % 8);
        if (key == _runKey) return;
        _runKey = key;
        string where = _next >= August1Rules.Waypoints ? "the post" : $"stop {_next + 1}/{August1Rules.Waypoints}";
        _runLine = $"{secs / 60}:{secs % 60:00}   -   {where}: {dist} m {Compass[key.Item4]}   -   {_walkers} walking";
    }

    private sealed partial class View : Control
    {
        public August1Game Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.August1Now() is not { } m) return;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            bool message = g._message.Length > 0 && GameClock.Now - g._messageAt < 4;
            if (g._walking && g._local() is { } p) g.RunLine(m.Site, p);
            float height = g._walking || g._target >= 0 ? 76 : 0;
            if (height > 0) Panel(c, height);
            if (g._walking)
            {
                Text("Lampion walk", c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text(g._runLine, c + new Vector2(0, 12), UiTheme.FontBody, Gold);
                if (message) Text(g._message, c + new Vector2(0, 34), UiTheme.FontSmall, Amber);
                return;
            }
            if (g._target >= 0)
            {
                (string title, string line) = g._target switch
                {
                    FestivalService.August1LampionStand => ("Lampion stand", "A paper lantern for the walk, free, one a day."),
                    FestivalService.August1FireworkStand => ("Fireworks stand", $"A box for {August1Rules.BoxPrice} Batzen: light it at night with the use key."),
                    _ => ("Lampion post", "At night, with your lampions: ten stops round the village, then back here."),
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
