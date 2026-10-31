using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;
using UnitSport.Ui;

namespace UnitSport.World.Fun;

/// <summary>
/// The fun world's air rings (#1396), client only: neon hoops in flying order (<see cref="FunCourse.Rings"/>), from the
/// runway out over the grid and back. The next one burns pink and large, the rest wait cyan, the ones flown through go
/// dark. Flying through the first ring starts the clock (again, whenever), the last stops it; a small panel on the
/// right says which ring is next and the time, and the best time of the session. Nothing new to press: flying a plane
/// (or a wingsuit, or anything else) through them is the whole game, on any device. Only this player's run: no
/// network, no bots.
/// </summary>
public partial class FunRings : Node3D, IOriginContainer
{
    private readonly WorldOrigin _origin;
    private readonly Func<Player.FootPlayer?> _me;
    private readonly MeshInstance3D[] _rings = new MeshInstance3D[FunCourse.Rings.Length];
    private StandardMaterial3D _waiting = null!, _next = null!, _done = null!;
    private Label? _label;
    private PanelContainer? _panel;
    private int _nextRing;
    private double _clock, _startedAt = -1, _best = double.MaxValue, _hideAt;
    private long _shownTenths = -1;
    private int _shownRing = -1;

    public FunRings(WorldOrigin origin, Func<Player.FootPlayer?> me)
    {
        Name = "FunRings";
        _origin = origin;
        _me = me;
    }

    public FunRings() : this(null!, () => null) { }

    /// <summary>How near a ring's middle a body must pass, m: its radius and a little.</summary>
    private const float PassRadius = FunCourse.RingRadius + 2f;

    public override void _Ready()
    {
        _waiting = Neon(FunBackdrop.Cyan, 1.4f);
        _next = Neon(FunBackdrop.Pink, 2.4f);
        _done = Neon(new Color(0.25f, 0.1f, 0.3f), 0.6f);
        var torus = new TorusMesh { InnerRadius = FunCourse.RingRadius - 0.7f, OuterRadius = FunCourse.RingRadius + 0.7f, Rings = 48, RingSegments = 8 };
        for (int i = 0; i < _rings.Length; i++)
        {
            var at = Position(i);
            // the ring faces along the course: from the one before to the one after
            var from = Position(Math.Max(0, i - 1));
            var to = Position(Math.Min(_rings.Length - 1, i + 1));
            var along = (to - from) with { Y = 0 };
            var basis = along.LengthSquared() < 1 ? Basis.Identity : Basis.LookingAt(along.Normalized(), Vector3.Up);
            // a torus lies in XZ: stood up across the line of flight
            _rings[i] = new MeshInstance3D
            {
                Name = $"Ring{i + 1}", Mesh = torus, Transform = new Transform3D(basis * new Basis(Vector3.Right, Mathf.Pi / 2), at),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_rings[i]);
        }
        BuildPanel();
        Paint();
    }

    private Vector3 Position(int i)
    {
        var (x, y, h) = FunCourse.Rings[i];
        return _origin.ToWorld(FunWorld.Start.E + x, FunWorld.Start.N + y, FunCourse.Flat + h);
    }

    private static StandardMaterial3D Neon(Color c, float energy) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(c.R * energy, c.G * energy, c.B * energy),
    };

    private void BuildPanel()
    {
        var layer = new CanvasLayer { Name = "RingsHud", Layer = 5 };
        _panel = new PanelContainer { Visible = false };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.7f, 10, 12));
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        _panel.Position = new Vector2(-260, -40);
        _panel.CustomMinimumSize = new Vector2(230, 0);
        _label = UiKit.Text("", UiTheme.FontBody, UiTheme.Text);
        _panel.AddChild(_label);
        layer.AddChild(_panel);
        AddChild(layer);
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_me() is not { } me || me.Origin is null) return;
        var here = _origin.ToWorld(me.Global);
        // the first ring (re)starts a run whenever it is flown through
        if (_nextRing != 1 && Through(0, here)) Start();
        else if (_startedAt >= 0 && _nextRing < _rings.Length && Through(_nextRing, here)) Passed();
        Show();
    }

    private bool Through(int ring, Vector3 here) => here.DistanceSquaredTo(_rings[ring].Position) < PassRadius * PassRadius;

    private void Start()
    {
        _startedAt = _clock;
        _nextRing = 1;
        _hideAt = 0;
        Paint();
    }

    private void Passed()
    {
        _nextRing++;
        if (_nextRing == _rings.Length)
        {
            double time = _clock - _startedAt;
            bool best = time < _best;
            _best = Math.Min(_best, time);
            _startedAt = -1;
            _hideAt = _clock + 12;
            _shownTenths = -1;
            _label!.Text = $"AIR RINGS done in {Time(time)}{(best ? "  best!" : $"\nbest {Time(_best)}")}";
            _nextRing = 0;
        }
        Paint();
    }

    /// <summary>The rings' colours: the next one hot and a third larger, the flown ones dark.</summary>
    private void Paint()
    {
        for (int i = 0; i < _rings.Length; i++)
        {
            bool running = _startedAt >= 0;
            bool next = running ? i == _nextRing : i == 0;
            _rings[i].MaterialOverride = next ? _next : running && i < _nextRing ? _done : _waiting;
            _rings[i].Scale = Vector3.One * (next ? 1.3f : 1f);
        }
    }

    /// <summary>The panel, written only when its text changes (a tenth of a second at most).</summary>
    private void Show()
    {
        if (_panel == null || _label == null) return;
        if (_startedAt < 0)
        {
            _panel.Visible = _clock < _hideAt;
            return;
        }
        _panel.Visible = true;
        long tenths = (long)((_clock - _startedAt) * 10);
        if (tenths == _shownTenths && _nextRing == _shownRing) return;
        _shownTenths = tenths;
        _shownRing = _nextRing;
        _label.Text = $"AIR RINGS  {_nextRing} / {_rings.Length - 1}\n{Time(tenths / 10.0)}{(_best < double.MaxValue ? $"   best {Time(_best)}" : "")}";
    }

    private static string Time(double s) => $"{(int)(s / 60)}:{s % 60:00.0}";
}
