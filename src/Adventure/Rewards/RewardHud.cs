using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The rewards' HUD (#1227): one line of news above the hotbar (a refusal, a seal's progress, an edelweiss picked,
/// an offering, a photo judged), 3 s, from a queue; the trial's panel at the right (its name, the clock, the next
/// gate, the par); the treasure detector's readout and the ping's arrow; the offering's confirmation. Texts are set
/// only when they change; the clock only when its tenth does.
/// </summary>
public partial class RewardHud : CanvasLayer
{
    private const double ToastSeconds = 3.0, SenseEvery = 0.25;

    public static RewardHud? Instance { get; private set; }

    private readonly Func<FootPlayer?> _me;
    private Control _root = null!;
    private PanelContainer _toastPanel = null!, _trialPanel = null!, _detectorPanel = null!;
    private Label _toast = null!, _trialName = null!, _trialClock = null!, _trialNext = null!, _trialPar = null!, _detector = null!;
    private AudioStreamPlayer _beep = null!;
    private readonly Queue<string> _queue = new();
    private double _toastUntil, _nextSense, _nextBeep, _pingUntil, _pingReady;
    private float? _interval;
    private int _shownTenths = -1;
    private string _detectorText = "";
    private Vector3 _pingDir;
    private static readonly string[] Bars = { "Detector", "Detector |", "Detector ||", "Detector |||", "Detector ||||", "Detector |||||", "Detector ||||||" };
    private float _pingDistance;

    /// <summary>The last line shown (checks).</summary>
    public string LastToast { get; private set; } = "";
    /// <summary>The detector's beep interval now (checks); null out of range or not held.</summary>
    public float? Interval => _interval;

    public RewardHud(Func<FootPlayer?> me) => _me = me;
    public RewardHud() : this(() => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (RewardService.Instance is not { } r) return;
        r.Refused -= OnRefused;
        r.SealChanged -= OnSeal;
        r.Picked -= OnPicked;
        r.Offered -= OnOffered;
        r.PhotoJudged -= OnPhoto;
        r.TrialStarted -= OnTrialStarted;
        r.TrialEnded -= OnTrialEnded;
        r.TrialGate -= OnGate;
    }

    public override void _Ready()
    {
        Name = "RewardHud";
        Layer = 9;
        _root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // the news line, above the hotbar and the gather prompt
        var bottom = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1, OffsetTop = -262, OffsetBottom = -222 };
        _root.AddChild(bottom);
        _toastPanel = Glass();
        _toastPanel.Visible = false;
        bottom.AddChild(_toastPanel);
        _toast = UiKit.Text("", UiTheme.FontBody, UiTheme.Text);
        _toastPanel.AddChild(_toast);

        // the trial, at the right under the tracker's place
        _trialPanel = Glass();
        _trialPanel.Visible = false;
        _trialPanel.AnchorLeft = _trialPanel.AnchorRight = 1;
        _trialPanel.OffsetLeft = -300;
        _trialPanel.OffsetRight = -16;
        _trialPanel.OffsetTop = 120;
        _root.AddChild(_trialPanel);
        var t = UiKit.VBox(2);
        _trialPanel.AddChild(t);
        _trialName = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, bold: true, wrap: true);
        t.AddChild(_trialName);
        _trialClock = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        t.AddChild(_trialClock);
        _trialNext = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        t.AddChild(_trialNext);
        _trialPar = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        t.AddChild(_trialPar);

        // the detector's readout, bottom left of centre
        var det = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1, OffsetTop = -310, OffsetBottom = -270 };
        _root.AddChild(det);
        _detectorPanel = Glass();
        _detectorPanel.Visible = false;
        det.AddChild(_detectorPanel);
        _detector = UiKit.Text("", UiTheme.FontBody, UiTheme.Text);
        _detectorPanel.AddChild(_detector);

        _beep = new AudioStreamPlayer { Bus = SfxBus.Name, VolumeDb = -10, Stream = RewardSounds.Beep };
        AddChild(_beep);

        if (RewardService.Instance is not { } r) return;
        r.Refused += OnRefused;
        r.SealChanged += OnSeal;
        r.Picked += OnPicked;
        r.Offered += OnOffered;
        r.PhotoJudged += OnPhoto;
        r.TrialStarted += OnTrialStarted;
        r.TrialEnded += OnTrialEnded;
        r.TrialGate += OnGate;
    }

    private static PanelContainer Glass()
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 10));
        return p;
    }

    /// <summary>Queues a line of news.</summary>
    public void Say(string line)
    {
        _queue.Enqueue(line);
        if (GameClock.Now >= _toastUntil) Next();
    }

    private void Next()
    {
        if (!_queue.TryDequeue(out var line))
        {
            _toastPanel.Visible = false;
            return;
        }
        _toast.Text = LastToast = line;
        _toastPanel.Visible = true;
        _toastUntil = GameClock.Now + ToastSeconds;
    }

    private void OnRefused(string id, string why) => Say(why);

    private void OnSeal(string chestId, int mask)
    {
        if ((mask & Seals.DoneBit) != 0) { Say("The seal is broken: the chest opens for you."); return; }
        int n = 0;
        for (int b = 0; b < 30; b++) if ((mask & (1 << b)) != 0) n++;
        var chest = RewardWorld.Instance?.Chests.GetValueOrDefault(chestId)?.Spot;
        string what = chest?.Seal switch
        {
            SealKind.Bonfire => $"Fire {n} of 3 lit",
            SealKind.Rings => $"Ring {n} passed",
            SealKind.Bells => $"Bell {n} of 4 right",
            SealKind.Guide => "The guide runs on",
            _ => "",
        };
        if (what.Length > 0 && n > 0) Say(what);
    }

    private void OnPicked(string id)
    {
        var c = AdventureService.Instance?.Mine.Get(CollectProgress.Section);
        if (id.StartsWith(RewardRules.CairnPrefix + ":", StringComparison.Ordinal)) Say("You added a stone to the cairn.");
        else Say($"Edelweiss picked: {(c?.Held ?? 0) + 1} held. Five make an offering at a summit cross or a chapel.");
    }

    private void OnOffered(string shrine, int steps, int offered) =>
        Say($"Offered {steps * Offering.PerStep} edelweiss: stamina +{steps} step{(steps == 1 ? "" : "s")} ({offered} offered in all).");

    private void OnPhoto(string spot, bool taken, string why)
    {
        var s = PhotoSpots.Resolve(spot, PoiCatalogueHost.Current);
        Say(taken ? $"Photo spot: {s?.Name ?? spot}" : why);
    }

    // ---- offering --------------------------------------------------------------------------------

    /// <summary>E at an offering box: how many edelweiss it would take, confirmed (keyboard, pad, the VR ray).</summary>
    public void Offer(Poi shrine)
    {
        var c = AdventureService.Instance?.Mine.Get(CollectProgress.Section);
        int held = c?.Held ?? 0;
        if (held < Offering.PerStep)
        {
            Say($"Five edelweiss make an offering (you hold {held}).");
            return;
        }
        int fives = held / Offering.PerStep * Offering.PerStep;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        Modal.Confirm(_root, $"Offer at {shrine.Name}",
            $"You hold {held} edelweiss. Every five offered raise your stamina a step (ten steps at most); every fifth step brings a gift too.",
            $"Offer {fives}", () => { MouseCapture.Capture(); RewardService.Instance?.AskOffer(shrine.Id); },
            () => MouseCapture.Capture(), cancelText: "Not now");
    }

    // ---- trials ----------------------------------------------------------------------------------

    private void OnTrialStarted(ActiveTrial t)
    {
        var def = Trials.Resolve(t.Id, PoiCatalogueHost.Current);
        _trialName.Text = def?.Name ?? t.Id;
        _trialPar.Text = $"Gold {Trials.Clock(t.Par.Gold)} · Silver {Trials.Clock(t.Par.Silver)} · Bronze {Trials.Clock(t.Par.Bronze)}";
        _trialPanel.Visible = true;
        _shownTenths = -1;
        Say("Go! Through every gate to the finish.");
    }

    private void OnGate(string id, int gate)
    {
        if (RewardService.Instance?.Trial is { } t) _trialNext.Text = gate + 1 >= t.Gates.Length ? "Finish!" : $"Gate {gate + 2} of {t.Gates.Length}";
    }

    private void OnTrialEnded(string id, double seconds, int medal, bool pb, int rank)
    {
        _trialPanel.Visible = false;
        if (medal < 0) { Say(id.Length > 0 ? "Out of the trial." : "You left the trial."); return; }
        Say($"{Trials.Clock(seconds)}: {Trials.MedalName(medal)}{(pb ? ", a personal best" : "")}{(rank >= 0 ? $", #{rank + 1} on this server" : "")}");
    }

    // ---- the detector --------------------------------------------------------------------------

    /// <summary>Use with the detector in hand: one strong ping that shows the direction for 3 s (10 s to recharge).</summary>
    public void Ping()
    {
        double now = GameClock.Now;
        if (now < _pingReady) { Say("The detector is recharging."); return; }
        _pingReady = now + Detector.Cooldown;
        if (Nearest() is not { } n)
        {
            Say("Nothing within 250 m.");
            return;
        }
        _pingDir = n.Dir;
        _pingDistance = n.Distance;
        _pingUntil = now + Detector.PingSeconds;
        _nextSense = 0;
        _beep.PitchScale = 0.7f;
        _beep.Play();
    }

    private (Vector3 Dir, float Distance)? Nearest()
    {
        var me = _me();
        if (me == null || !IsInstanceValid(me) || RewardWorld.Instance is not { } w) return null;
        var mine = AdventureService.Instance?.Mine;
        var opened = mine?.Get(ChestsProgress.Section).Opened;
        var taken = mine?.Get(CollectProgress.Section).Taken;
        int edition = RewardRules.Edition(DateTime.UtcNow);
        var at = me.Global;
        double best = double.MaxValue;
        double be = 0, bn = 0;
        foreach (var s in w.Near)
        {
            if (s.Kind == SpotKind.Cairn) continue;
            if (s.Kind == SpotKind.Chest ? opened?.Contains(RewardRules.OpenId(s, edition)) == true : taken?.Contains(s.Id) == true) continue;
            double d = (s.E - at.E) * (s.E - at.E) + (s.N - at.N) * (s.N - at.N);
            if (d < best) (best, be, bn) = (d, s.E - at.E, s.N - at.N);
        }
        if (best == double.MaxValue || Math.Sqrt(best) > Detector.RangeM) return null;
        return (new Vector3((float)be, 0, (float)-bn).Normalized(), (float)Math.Sqrt(best));
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (_toastPanel.Visible && now >= _toastUntil) Next();
        if (RewardService.Instance?.Trial is { } t)
        {
            int tenths = (int)((Net.ClockSync.ServerNow - t.Since) * 10);
            if (tenths != _shownTenths)
            {
                _shownTenths = tenths;
                _trialClock.Text = Trials.Clock(tenths / 10.0);
                if (_trialNext.Text.Length == 0 || t.Next == 0) _trialNext.Text = $"Gate {t.Next + 1} of {t.Gates.Length}";
            }
        }
        var me = _me();
        bool held = me != null && IsInstanceValid(me) && me.HeldItemId == (int)ItemId.TreasureDetector;
        if (!held)
        {
            _interval = null;
            if (_detectorPanel.Visible) _detectorPanel.Visible = false;
            return;
        }
        // the readout is worked out four times a second, never per frame
        if (now >= _nextSense)
        {
            _nextSense = now + SenseEvery;
            _interval = Nearest() is { } n ? Detector.Interval(n.Distance) : null;
            string text;
            if (now < _pingUntil && me!.Camera is { } cam)
            {
                // the arrow turns with the view: the ping's direction against where the camera faces
                var fwd = -cam.GlobalTransform.Basis.Z;
                float a = Mathf.Atan2(fwd.X * _pingDir.Z - fwd.Z * _pingDir.X, fwd.X * _pingDir.X + fwd.Z * _pingDir.Z);
                string arrow = Mathf.Abs(a) < 0.4f ? "↑" : Mathf.Abs(a) > 2.7f ? "↓" : a > 0 ? (a < 1.2f ? "↗" : "→") : (a > -1.2f ? "↖" : "←");
                text = $"{arrow} {Mathf.RoundToInt(_pingDistance / 10f) * 10} m";
            }
            else text = _interval is { } i ? Bars[Math.Clamp(6 - (int)(i / 0.27f), 1, 6)] : "Detector: nothing near";
            if (text != _detectorText)
            {
                _detectorText = text;
                _detector.Text = text;
            }
        }
        if (!_detectorPanel.Visible) _detectorPanel.Visible = true;
        if (_interval is { } every && now >= _nextBeep)
        {
            _nextBeep = now + every;
            _beep.PitchScale = 1f;
            _beep.Play();
        }
    }
}
