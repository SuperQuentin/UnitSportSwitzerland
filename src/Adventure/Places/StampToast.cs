using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The stamp toast (#1224, places plan 3): a place found is a stamp pressed in with a thump, "Rigi Kulm ·
/// 1 797 m · SZ", below the quest toasts, 3 s, one at a time from a queue. A minor place (tier 3) is a
/// small line instead of a banner, and a burst of them (a walk past ten bus stops) folds into
/// "+4 places". A synced viewpoint says the map round it is revealed. Built once; text set only on a new toast.
/// </summary>
public partial class StampToast : CanvasLayer
{
    private const double ShowSeconds = 3.0;

    private static AudioStreamWav? _thump;

    /// <summary>A rubber stamp pressed onto paper: a low knock and a short papery burst.</summary>
    public static AudioStreamWav Thump => _thump ??= Dsp.OneShot(0.35f, 1224, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float knock = Mathf.Sin(Mathf.Tau * 95f * t * (1f - t)) * Mathf.Exp(-t * 28f);
            float paper = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 60f) * 0.35f;
            s[i] = (knock * 0.8f + paper) * Mathf.Min(1f, t * 800f);
        }
        return s;
    });

    private readonly Queue<(string Head, string Line, bool Minor)> _queue = new();
    private int _minorBurst;
    private PanelContainer _panel = null!;
    private Label _head = null!, _line = null!;
    private AudioStreamPlayer _player = null!;
    private double _until;
    private PlacesService? _places;

    /// <summary>The last toast's lines (checks).</summary>
    public string Last { get; private set; } = "";

    public override void _Ready()
    {
        Name = "StampToast";
        Layer = 9;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var top = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AnchorRight = 1f;
        top.OffsetTop = 168f;
        top.OffsetBottom = 240f;
        root.AddChild(top);
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 12));
        top.AddChild(_panel);
        var col = UiKit.VBox(2);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(col);
        _head = UiKit.Text("", UiTheme.FontBody, UiTheme.Amber, bold: true, align: HorizontalAlignment.Center);
        _line = UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, align: HorizontalAlignment.Center);
        col.AddChild(_head);
        col.AddChild(_line);
        _player = new AudioStreamPlayer { Name = "Thump", Bus = SfxBus.Name, VolumeDb = -4f };
        AddChild(_player);
        SetProcess(false);
        Callable.From(Hook).CallDeferred();
    }

    private void Hook()
    {
        _places = PlacesService.Instance;
        if (_places == null) return;
        _places.StampedPlace += OnStamped;
        _places.SyncedView += OnSynced;
    }

    public override void _ExitTree()
    {
        if (_places == null) return;
        _places.StampedPlace -= OnStamped;
        _places.SyncedView -= OnSynced;
    }

    /// <summary>"Rigi Kulm · 1 797 m · SZ".</summary>
    public static string LineOf(Poi poi)
    {
        string text = poi.Name;
        if (poi.Kind is not (PoiKind.BusStop or PoiKind.Lake) && poi.Elevation > 0) text += $" · {AtlasFacts.Num(poi.Elevation)} m";
        if (poi.Canton.Length > 0) text += " · " + poi.Canton;
        return text;
    }

    private void OnStamped(Poi poi)
    {
        bool minor = poi.Tier >= 3 && !(_places?.Catalogue.IsLandmark(poi) ?? false);
        if (minor && _queue.Count > 0 && _queue.Peek().Minor)
        {
            // a burst of minor places folds into one line
            _minorBurst++;
            return;
        }
        _queue.Enqueue(minor ? ("", $"Found {LineOf(poi)}", true)
            : ($"New stamp: {AtlasFacts.KindName(poi.Kind)}", LineOf(poi), false));
        SetProcess(true);
    }

    private void OnSynced(Poi poi)
    {
        _queue.Enqueue(("View synced", $"The map round {poi.Name} is revealed", false));
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now >= _until && _queue.Count > 0 && !Permissions.InMatch)
        {
            var (head, line, minor) = _queue.Dequeue();
            if (minor && _minorBurst > 0 && _queue.Count == 0)
            {
                line += $"  +{_minorBurst} place{(_minorBurst == 1 ? "" : "s")}";
                _minorBurst = 0;
            }
            _head.Text = head;
            _head.Visible = head.Length > 0;
            _line.Text = line;
            Last = $"{head} | {line}";
            _until = now + (minor ? ShowSeconds * 0.6 : ShowSeconds);
            if (!minor)
            {
                _player.Stream = Thump;
                _player.Play();
            }
        }
        bool show = now < _until;
        var m = _panel.Modulate;
        float want = show ? 1f : 0f;
        if (Mathf.IsEqualApprox(m.A, want))
        {
            if (!show && _queue.Count == 0) SetProcess(false);
            return;
        }
        _panel.Modulate = new Color(1, 1, 1, Mathf.MoveToward(m.A, want, (float)delta * (show ? 5f : 1f)));
    }
}
