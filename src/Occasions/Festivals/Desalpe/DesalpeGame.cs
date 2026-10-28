using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The Désalpe, the player's side (#1269 part 7, <c>docs/notes/occasions/festival-desalpe.md</c>): three things to do, all on the interact key
/// (E / the pad's Y / a VR grip, which <c>XrPad</c> replays) and the use key (LMB / X / the trigger), so no new binding exists.
/// <list type="bullet">
/// <item>Within 4.5 m of a cow of the herd on the alp (the dress game, from day 1): E puts on the next headdress shape, Use the next colour.</item>
/// <item>At the alp sign (the drive, from day 3): E starts the Alpabzug; stand behind the herd and keep it together, fetch the strays.</item>
/// <item>In the fair's ring (the vote, from day 4): Use picks a herd, E gives it the one vote.</item>
/// </list>
/// A bottom-centre panel says what the keys do, how the drive goes (cows with the herd, strays, time against par) and what it paid; the herd
/// itself is <see cref="DesalpeHerd"/>. The server decides everything (the dress, the start, the vote, the pay).
/// </summary>
public partial class DesalpeGame : CanvasLayer
{
    public const string NodeName = "DesalpeGame";

    /// <summary>A cow this near is dressed with E and Use.</summary>
    public const double CowReach = 4.5, SignReach = 6, RingReach = 14;

    public static DesalpeGame? Instance { get; private set; }

    /// <summary>Whether Use belongs to this screen now (dressing a cow, choosing a herd to vote for): the items let go of it.</summary>
    public static bool TakesUse => Instance is { _dressing: true } or { _voting: true };

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private FestivalService? _svc;
    private bool _dressing, _voting, _wasUse, _hooked;
    private int _cow = -1, _voteIndex;
    private bool _atSign;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity, _resultAt = double.NegativeInfinity;
    private (int Score, int Batzen, int Arrived, int Total, int Seconds, int Dressed) _result;
    private string _votedInstance = "";
    private int _lastPhase = -1;
    private string _hint = "";
    private (InputDevice, byte, int, int) _hintKey = ((InputDevice)(-1), 0, -1, -1);
    private string _promptCow = "", _promptSign = "", _promptVote = "";
    private (InputDevice, byte, int) _promptCowKey = ((InputDevice)(-1), 0, -1);
    private (InputDevice, int, int, int) _promptVoteKey = ((InputDevice)(-1), -1, -1, -1);
    private string _driveLine = "";
    private int _driveKey = -1;

    internal bool Dressing => _dressing;
    internal bool Voting => _voting;
    internal int NearCow => _cow;
    internal (int Score, int Batzen, int Arrived, int Total, int Seconds, int Dressed) LastResult => _result;
    internal int VoteIndex => _voteIndex;

    /// <summary>The use key, replaced by a check.</summary>
    internal Func<bool> UseHeld = () => PlayerInput.Held(PlayerInput.UseItem);

    public DesalpeGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (_svc != null)
        {
            _svc.DesalpeChanged -= OnChanged;
            _svc.DesalpeScored -= OnScored;
            _svc.DesalpeVoted -= OnVoted;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.DesalpeChanged += OnChanged;
        svc.DesalpeScored += OnScored;
        svc.DesalpeVoted += OnVoted;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnChanged()
    {
        int phase = _svc?.DesalpeNow?.Phase ?? -1;
        // the drive starts: the herder's call from where this player stands
        if (_lastPhase == (int)DesalpePhase.Grazing && phase == (int)DesalpePhase.Driving && _local() is { } p && DesalpeHerd.Instance is { } herd && p.Origin is { } o)
            herd.Call(o.ToWorld(p.Global.E, p.Global.N, p.Global.Alt + 1.4f));
        _lastPhase = phase;
        _driveKey = -1;
        _clockAtView = GameClock.Now;
    }

    private void OnScored(int score, int batzen, int arrived, int total, int seconds, int dressed)
    {
        _result = (score, batzen, arrived, total, seconds, dressed);
        _resultAt = GameClock.Now;
    }

    private void OnVoted(int entry)
    {
        _votedInstance = _svc?.DesalpeNow?.Instance ?? "";
        Say(entry > 0 ? "Your vote is in." : "");
    }

    private void OnRefused(string why)
    {
        // only the Désalpe's own refusals reach this screen: the ones about the herd, the sign and the ring
        if (_svc?.DesalpeNow == null || _local() is not { } p || !(NearHerdOrSign(p) || InRing(p))) return;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
    }

    // ---- what is near ---------------------------------------------------------------------------------

    private RunningFestival? Run => _svc?.RunningOf(DesalpeRules.FestivalId);

    private bool Open(string game) => Run is { } r && FestivalCatalog.Get(r.Id) is { } def && def.IsOpen(game, r.Day);

    private bool NearHerdOrSign(FootPlayer p) => _cow >= 0 || _atSign;

    private bool InRing(FootPlayer p) => Run?.Site is { } site && site.DistanceTo(p.Global.E, p.Global.N) <= RingReach;

    private DesalpeRoute? Route => _svc?.DesalpeRouteNow();

    public override void _Process(double delta)
    {
        Hook();
        _dressing = _voting = _atSign = false;
        _cow = -1;
        var view = _svc?.DesalpeNow;
        if (view == null || Run is not { Site: { } site } || _local() is not { } p || Route is not { } route)
        {
            if (_view.Visible) _view.Visible = false;
            _wasUse = false;
            return;
        }
        _view.Visible = true;
        var g = p.Global;
        bool grazing = view.Phase == (int)DesalpePhase.Grazing;
        if (grazing && DesalpeHerd.Instance is { Count: > 0 } herd)
        {
            var (cow, d) = herd.Nearest(g.E, g.N);
            if (cow >= 0 && d <= CowReach && Open(DesalpeRules.DressGame)) { _cow = cow; _dressing = true; }
        }
        var (se, sn) = DesalpeGround.Sign(site, route);
        _atSign = grazing && Open(DesalpeRules.DriveGame) && !_dressing && Math.Sqrt((g.E - se) * (g.E - se) + (g.N - sn) * (g.N - sn)) <= SignReach;
        _voting = !_dressing && !_atSign && Open(DesalpeRules.VoteGame) && view.Ring.Length > 0 && _votedInstance != view.Instance && InRing(p);

        bool use = (_dressing || _voting) && UseHeld();
        if (use && !_wasUse)
        {
            if (_dressing && DesalpeHerd.Instance is { } h && _cow >= 0) _svc!.AskDesalpeDress(_cow, DesalpeRules.NextColour(h.DressOf(_cow)));
            else if (_voting) _voteIndex = (_voteIndex + 1) % view.Ring.Length;
        }
        _wasUse = use;
        if (_voteIndex >= view.Ring.Length) _voteIndex = 0;
        if (_view.Visible) _view.QueueRedraw();
    }

    // ---- the interaction -------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here: dress the cow, start the drive, vote. Null away from all three.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.DesalpeNow is not { } view) return null;
        var device = PlayerInput.HintDevice;
        if (game._dressing && DesalpeHerd.Instance is { } herd && game._cow >= 0)
        {
            byte dress = herd.DressOf(game._cow);
            var key = (device, dress, game._cow);
            if (key != game._promptCowKey)
            {
                game._promptCowKey = key;
                byte next = DesalpeRules.NextShape(dress);
                game._promptCow = InputHints.Prompt(PlayerInput.InteractMount, game._cow == 0 ? $"Dress the Leitkuh: {DesalpeModels.ShapeNames[DesalpeRules.ShapeOf(next)]}"
                    : $"Dress the cow: {DesalpeModels.ShapeNames[DesalpeRules.ShapeOf(next)]}");
            }
            var at = herd.CowAt(game._cow);
            var outline = Outline.Box(null, Vector3.Zero, Vector3.Zero);
            if (p.Origin is { } origin && at is { } c)
            {
                var w = origin.ToWorld(c.E, c.N, p.Global.Alt);
                outline = Outline.Box(null, w + new Vector3(-1.3f, -0.4f, -1.3f), w + new Vector3(1.3f, 2.2f, 1.3f));
            }
            return new Interaction(game._promptCow, outline);
        }
        if (game._atSign)
        {
            if (game._promptSign.Length == 0 || game._promptCowKey.Item1 != device) game._promptSign = InputHints.Prompt(PlayerInput.InteractMount, "Start the Alpabzug");
            return new Interaction(game._promptSign, default);
        }
        if (game._voting && view.Ring.Length > 0)
        {
            var e = view.Ring[game._voteIndex % view.Ring.Length];
            var key = (device, e.Id, e.Votes, view.Ring.Length);
            if (key != game._promptVoteKey)
            {
                game._promptVoteKey = key;
                game._promptVote = InputHints.Prompt(PlayerInput.InteractMount, $"Vote for {e.Label} ({e.Score} points, {e.Votes} votes)");
            }
            return new Interaction(game._promptVote, default);
        }
        return null;
    }

    /// <summary>E / Y / a VR grip: puts on the next headdress, starts the drive, or gives the vote. False when none of the three is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.DesalpeNow is not { } view) return false;
        if (game._dressing && DesalpeHerd.Instance is { } herd && game._cow >= 0)
        {
            svc.AskDesalpeDress(game._cow, DesalpeRules.NextShape(herd.DressOf(game._cow)));
            return true;
        }
        if (game._atSign)
        {
            svc.AskDesalpeStart();
            return true;
        }
        if (game._voting && view.Ring.Length > 0)
        {
            svc.AskDesalpeVote(view.Ring[game._voteIndex % view.Ring.Length].Id);
            return true;
        }
        return false;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    private string DriveLine(DesalpeView view, DesalpeHerd herd)
    {
        int strays = 0, lost = 0, with = 0;
        for (int i = 0; i < herd.Count; i++)
            switch (herd.StateOf(i))
            {
                case CowState.Stray: strays++; break;
                case CowState.Lost: lost++; break;
                default: with++; break;
            }
        int key = with * 1000000 + strays * 10000 + lost * 100 + (int)(herd.Progress * 99);
        if (key == _driveKey) return _driveLine;
        _driveKey = key;
        string line = $"{with}/{herd.Count} cows with the herd";
        if (strays > 0) line += $"  -  {strays} wandered off: go round them from the field side";
        if (lost > 0) line += $"  -  {lost} lost";
        return _driveLine = line;
    }

    private sealed partial class View : Control
    {
        public DesalpeGame Game = null!;
        private string _timeLine = "";
        private int _timeKey = -1;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.DesalpeNow is not { } view) return;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            double now = GameClock.Now;
            if (now - g._resultAt < 7)
            {
                var r = g._result;
                Panel(c, 116);
                Text($"{r.Score} points", c + new Vector2(0, -6), UiTheme.FontTitle, Gold, true);
                Text($"{r.Arrived}/{r.Total} cows at the fair  -  {r.Dressed} dressed  -  {r.Seconds / 60}:{r.Seconds % 60:00}  -  +{r.Batzen} Batzen", c + new Vector2(0, 30), UiTheme.FontBody, UiTheme.Text);
                return;
            }
            if (g._dressing && DesalpeHerd.Instance is { } herd && g._cow >= 0)
            {
                byte dress = herd.DressOf(g._cow);
                Panel(c, 76);
                Text(g._cow == 0 ? "The Leitkuh" : $"Cow {g._cow + 1}", c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text($"Wearing: {DesalpeModels.Describe(dress)}", c + new Vector2(0, 12), UiTheme.FontBody, dress == 0 ? UiTheme.TextDim : DesalpeModels.Accent(DesalpeRules.ColourOf(dress)));
                Text(Hint(dress), c + new Vector2(0, 34), UiTheme.FontSmall, UiTheme.TextDim);
                return;
            }
            if (g._voting && view.Ring.Length > 0)
            {
                var e = view.Ring[g._voteIndex % view.Ring.Length];
                Panel(c, 96);
                Text("The finest herd", c + new Vector2(0, -22), UiTheme.FontHeading, UiTheme.Text, true);
                Text($"{e.Label}: {e.Arrived} cows, {e.Dressed} dressed, {e.Score} points  -  {e.Votes} votes", c + new Vector2(0, 4), UiTheme.FontBody, Gold);
                Text(InputHints.Format("{use_item} next herd   {interact_mount} give your vote"), c + new Vector2(0, 30), UiTheme.FontSmall, UiTheme.TextDim);
                return;
            }
            if (view.Phase == (int)DesalpePhase.Driving && DesalpeHerd.Instance is { Count: > 0 } drive && NearHerd(g, drive))
            {
                Panel(c, 96);
                DrawDrive(g, view, drive, c);
                if (g._message.Length > 0 && now - g._messageAt < 4) Text(g._message, c + new Vector2(0, 62), UiTheme.FontSmall, Amber);
                return;
            }
            if (g._message.Length > 0 && now - g._messageAt < 4)
                Text(g._message, c, UiTheme.FontBody, Amber);
        }

        private static bool NearHerd(DesalpeGame g, DesalpeHerd herd)
        {
            if (g._local() is not { } p || g.Run?.Site is not { } site || herd.CowAt(0) is not { } at) return false;
            double de = p.Global.E - at.E, dn = p.Global.N - at.N;
            return de * de + dn * dn <= 300 * 300;
        }

        private void DrawDrive(DesalpeGame g, DesalpeView view, DesalpeHerd herd, Vector2 c)
        {
            Text("Alpabzug", c + new Vector2(0, -30), UiTheme.FontHeading, UiTheme.Text, true);
            Text(g.DriveLine(view, herd), c + new Vector2(0, -6), UiTheme.FontSmall, UiTheme.Text);
            // the Leitkuh's progress along the route, against par
            const float W = 360f;
            var bar = new Rect2(c.X - W / 2, c.Y + 8, W, 10);
            DrawRect(bar, new Color(0, 0, 0, 0.5f), true);
            DrawRect(new Rect2(bar.Position, new Vector2(W * herd.Progress, 10)), Green, true);
            double spent = view.Elapsed + (GameClock.Now - g._clockAtView);
            int secs = (int)spent;
            if (secs != _timeKey)
            {
                _timeKey = secs;
                int par = (int)view.Par;
                _timeLine = $"{secs / 60}:{secs % 60:00} of {par / 60}:{par % 60:00} par";
            }
            Text(_timeLine, c + new Vector2(0, 38), UiTheme.FontSmall, secs > view.Par ? Amber : UiTheme.TextDim);
        }

        private string Hint(byte dress)
        {
            var key = (PlayerInput.HintDevice, dress, 0, 0);
            if (key == Game._hintKey) return Game._hint;
            Game._hintKey = key;
            return Game._hint = InputHints.Format("{interact_mount} next headdress   {use_item} next colour");
        }

        private void Panel(Vector2 c, float height) =>
            DrawRect(new Rect2(c.X - 300, c.Y - height / 2 - 12, 600, height), new Color(0, 0, 0, 0.45f), true);

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(300f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 600f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 600f, size, colour);
        }
    }

    /// <summary>The game clock when the view last changed: the HUD's seconds run on from the server's <see cref="DesalpeView.Elapsed"/> by it.</summary>
    private double _clockAtView;
}
