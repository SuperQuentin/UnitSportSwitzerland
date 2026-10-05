using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Items.Fishing;

/// <summary>
/// The rod in the local player's hand (#493, docs/notes/items/fishing.md), stepped by
/// <see cref="ItemController"/> every frame and told when Use is pressed:
/// <list type="number">
/// <item>Hold Use: the cast winds up (a power bar); let go: the float flies 3-25 m along the view.</item>
/// <item>The float lands on water (the water layer, or a mapped stream from its bank) or on dry ground,
/// which winds it back in. Where it lies decides the fish (<see cref="FishWaters.Spot"/>).</item>
/// <item>A bite comes after a random wait (<see cref="FishRules.BiteSeconds"/>): the float dips, the pad
/// rumbles, and Use within <see cref="StrikeWindow"/> hooks the fish; too late, dough is eaten.</item>
/// <item>The fight (<see cref="FishFight"/>): hold Use to reel, let go when the line strains or a surge
/// is coming. Landed: kept into the pack, or released by the rules (<see cref="FishRules.Judge"/>).</item>
/// </list>
/// Aim winds the line in at any time but the fight. Everything is local but the float and line, which
/// go out as item events (<see cref="FishingVisuals"/>).
/// </summary>
public partial class FishingRod : Node
{
    public enum Phase { Idle, Charging, Waiting, Bite, Fighting }

    public const float ChargeSeconds = 1.0f, MinCast = 3f, MaxCast = 25f, StrikeWindow = 1.1f;

    public Phase State { get; private set; }
    /// <summary>The wind-up, 0..1 while charging.</summary>
    public float Power { get; private set; }
    public FishFight? Fight { get; private set; }
    public FishSpot Spot { get; private set; }
    public Bait Bait { get; private set; }
    /// <summary>The fish on the line, decided at the strike.</summary>
    public Catch? Hooked { get; private set; }
    /// <summary>Seconds until the next bite (Waiting), or left to strike (Bite).</summary>
    public double Timer => _timer;

    /// <summary>Raised when a fish is on the bank, kept or not (for probes).</summary>
    public event Action<Catch>? Landed;
    /// <summary>Raised when the line comes in with nothing: "", "snap", "dry", "missed".</summary>
    public event Action<string>? Lost;

    private readonly ItemController _items;
    private readonly Inventory _inventory;
    private readonly WorldOrigin _origin;
    private readonly Random _rng = new();
    private static readonly RayQuery Ray = new();
    private double _timer, _waited, _clickT;
    private bool _toldEmpty;
    private Vector3 _float, _drag;

    private CanvasLayer _hud = null!;
    private Label _label = null!;
    private ProgressBar _bar = null!;
    private StyleBoxFlat _fill = null!;
    private Color _shownFill;

    public FishingRod(ItemController items, Inventory inventory, WorldOrigin origin)
    {
        _items = items;
        _inventory = inventory;
        _origin = origin;
    }

    public FishingRod() : this(null!, null!, null!) { }

    /// <summary>The calendar month, as the bird seasons have it (<c>--birdmonth N</c> pretends another).</summary>
    public static int Month
    {
        get
        {
            int month = DateTime.Now.Month;
            var args = OS.GetCmdlineUserArgs();
            int at = Array.IndexOf(args, "--birdmonth");
            if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int m) && m is >= 1 and <= 12) month = m;
            return month;
        }
    }

    public override void _Ready()
    {
        Name = "FishingRod";
        _hud = new CanvasLayer { Layer = 9 };
        AddChild(_hud);
        _label = UiTheme.Prompt(-214);
        _hud.AddChild(_label);
        _fill = new StyleBoxFlat { BgColor = new Color(0.95f, 0.78f, 0.25f) };
        _bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, ShowPercentage = false, Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) });
        _bar.AddThemeStyleboxOverride("fill", _fill);
        _bar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _bar.Position = new Vector2(-110, -180);
        _bar.Size = new Vector2(220, 10);
        _hud.AddChild(_bar);
    }

    private ItemEvents? Events => ItemEvents.Instance;
    private FishingVisuals? Visuals => Events is { } n ? FishingVisuals.Of(n) : null;

    // ---- input -------------------------------------------------------------------------------------

    /// <summary>Use pressed with the rod in hand (<c>ItemController.UseSlot</c>).</summary>
    public void Press(FootPlayer player)
    {
        switch (State)
        {
            case Phase.Idle:
                State = Phase.Charging;
                Power = 0;
                break;
            case Phase.Waiting:
                // striking at nothing: the fish nearby are put off a little
                _timer += 2;
                _items.Toast("Too early: the float has not moved.");
                break;
            case Phase.Bite:
                Strike(player);
                break;
        }
    }

    /// <summary>
    /// Every frame. <paramref name="player"/>: the usable player with the rod in hand, else null (which
    /// winds any line in: the rod put away, a vehicle, a swim).
    /// </summary>
    public void Step(FootPlayer? player, bool useHeld, bool aimHeld, float dt)
    {
        if (player == null)
        {
            if (State != Phase.Idle) End("");
            ShowHud(null);
            return;
        }
        switch (State)
        {
            case Phase.Charging:
                Power = Math.Min(1f, Power + dt / ChargeSeconds);
                if (!useHeld) Cast(player);
                else if (aimHeld) State = Phase.Idle;
                break;
            case Phase.Waiting:
                if (aimHeld) { End(""); break; }
                _waited += dt;
                if (!_toldEmpty && _waited > 40 && FishRules.Odds(Spot, Bait, World.WorldClock.CurrentHour).Count == 0)
                {
                    _toldEmpty = true;
                    _items.Toast("Nothing seems to live here.");
                }
                if ((_timer -= dt) <= 0) Bite();
                break;
            case Phase.Bite:
                if (aimHeld) { End(""); break; }
                if ((_timer -= dt) <= 0) Missed();
                break;
            case Phase.Fighting:
                StepFight(player, useHeld, dt);
                break;
        }
        ShowHud(player);
    }

    // ---- the cast ----------------------------------------------------------------------------------

    private void Cast(FootPlayer player)
    {
        float power = Power;
        Power = 0;
        var fwd = -player.Camera.GlobalTransform.Basis.Z;
        fwd.Y = 0;
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        var feet = player.GlobalPosition;
        var target = feet + fwd * (MinCast + (MaxCast - MinCast) * power);
        Bait = Inventory.CountPlain(ItemId.Spinner) > 0 ? Bait.Spinner : Inventory.CountPlain(ItemId.DoughBait) > 0 ? Bait.Dough : Bait.None;
        ItemController.Kick(player);
        _items.PlaySound(SfxSynth.WhooshBank.Variants[0], Mathf.Lerp(0.8f, 1.3f, power));

        // the ground under the float: a lake's bed, a river's, or dry land
        var space = player.GetWorld3D().DirectSpaceState;
        var hit = Ray.Cast(space, target + Vector3.Up * 20f, target + Vector3.Down * 60f, uint.MaxValue, player.SelfExclude);
        float ground = hit.Count > 0 ? hit["position"].AsVector3().Y : float.NegativeInfinity;
        // water: a surface over that ground (the layer runs on a little under a beach, to hide its edge),
        // below the eye and not far down a cliff
        bool onLayer = World.WaterField.TryGetStill(target, out float still, out _)
                       && still > ground + MinDepth
                       && still < player.Camera.GlobalPosition.Y + 0.5f && still > feet.Y - 30f;
        bool stream = false;
        if (onLayer) target.Y = still;
        else
        {
            if (hit.Count > 0) target.Y = ground;
            stream = Loot.Gathering.Instance?.StreamAt(target) == true;
            if (stream) target.Y += 0.05f;
        }
        _float = target;
        GD.Print(FormattableString.Invariant(
            $"[fishing] cast power {power:F2}, {MathX.FlatLength(target - feet):F1} m: {(onLayer ? $"water {still - ground:F1} m deep" : stream ? "a stream" : "dry")}"));
        Events?.Send(ItemEventKind.FishCast, target, Vector3.Zero);
        if (!onLayer && !stream)
        {
            _items.Toast("The float lands on dry ground. Cast at water: a lake, a river, a stream.");
            End("dry");
            return;
        }

        var at = _origin.ToGlobal(target);
        // a lake lies flat, a river's surface falls downstream: compare the wet samples across the float
        double slope = onLayer ? Math.Max(Fall(target, new Vector3(6, 0, 0)), Fall(target, new Vector3(0, 0, 6))) : 0;
        Spot = FishWaters.Spot(onLayer, at.E, at.N, at.Alt, slope);
        State = Phase.Waiting;
        _waited = 0;
        _toldEmpty = false;
        _timer = FishRules.BiteSeconds(Spot, Bait, World.WorldClock.CurrentHour, World.WaterField.SeaState, _rng);
        string bait = Bait switch { Bait.Spinner => "a spinner", Bait.Dough => "dough", _ => "a bare hook" };
        _items.Toast($"Cast into {Spot.Describe()}, with {bait}.");
    }

    /// <summary>Water shallower than this is a wet beach, not somewhere a float lies.</summary>
    public const float MinDepth = 0.15f;

    /// <summary>The still surface's fall per metre across <paramref name="at"/> along <paramref name="half"/>; 0 unless both ends are wet.</summary>
    private static double Fall(Vector3 at, Vector3 half) =>
        World.WaterField.TryGetStill(at - half, out float a, out _) && World.WaterField.TryGetStill(at + half, out float b, out _)
            ? Math.Abs(b - a) / (2 * half.Length()) : 0;

    // ---- the bite ----------------------------------------------------------------------------------

    private FishSpecies? _biter;

    private void Bite()
    {
        _biter = FishRules.Pick(Spot, Bait, World.WorldClock.CurrentHour, _rng);
        if (_biter == null)
        {
            _timer = 30;   // nothing lives here: the float just sits
            return;
        }
        State = Phase.Bite;
        _timer = StrikeWindow;
        Visuals?.Dip(0.07f);
        _items.PlaySound(FishingVisuals.Plop, 1.4f);
        PlayerInput.Rumble(0.5f, 0.3f, 0.25f);
    }

    private void Missed()
    {
        string text = "Missed: the float bobbed back up.";
        if (Bait == Bait.Dough)
        {
            Inventory.TakePlain(ItemId.DoughBait, 1);
            text = "Missed: something ate your dough.";
            if (Inventory.CountPlain(ItemId.DoughBait) == 0) Bait = Bait.None;
        }
        _items.Toast(text);
        State = Phase.Waiting;
        _timer = FishRules.BiteSeconds(Spot, Bait, World.WorldClock.CurrentHour, World.WaterField.SeaState, _rng);
    }

    private void Strike(FootPlayer player)
    {
        if (_biter == null) return;
        var c = FishRules.Land(_biter, Spot, Month, _rng);
        Hooked = c;
        float distance = MathX.FlatLength(_float - player.GlobalPosition);
        Fight = new FishFight(c.Kg, distance, _rng);
        _drag = _float;
        State = Phase.Fighting;
        PlayerInput.Rumble(0.3f, 0.8f, 0.3f);
        _items.Toast(InputHints.Format("Hooked! Hold {use_item} to reel in; let go when the line strains."));
    }

    // ---- the fight ---------------------------------------------------------------------------------

    private void StepFight(FootPlayer player, bool useHeld, float dt)
    {
        if (Fight is not { } fight || Hooked is not { } c) { End(""); return; }
        bool reeling = useHeld;
        fight.Step(dt, reeling);
        if (reeling && (_clickT -= dt) <= 0)
        {
            _clickT = 0.07;
            _items.PlaySound(FishingVisuals.Click, 0.9f + (float)_rng.NextDouble() * 0.2f);
        }
        if (fight.SurgeComing || fight.Surging) PlayerInput.Rumble(fight.Surging ? 0.2f : 0.6f, fight.Surging ? 0.9f : 0.2f, 0.06f);

        // the float follows the fish: along the line from the angler, wandering side to side
        var from = player.GlobalPosition;
        var dir = _float - from;
        dir.Y = 0;
        dir = dir.LengthSquared() > 1e-4f ? dir.Normalized() : Vector3.Forward;
        var side = dir.Cross(Vector3.Up);
        float wander = Mathf.Sin((float)Time.GetTicksMsec() / 700f) * Mathf.Min(3f, fight.Distance * 0.25f) * fight.Pull;
        var want = from + dir * fight.Distance + side * wander;
        want.Y = _float.Y;
        _drag = _drag.Lerp(want, Math.Min(1f, dt * 4f));
        Visuals?.Drag(_drag);

        if (fight.Snapped) Snap();
        else if (fight.Landed) Land(c);
    }

    private void Snap()
    {
        string lost = "";
        if (Bait == Bait.Spinner && Inventory.TakePlain(ItemId.Spinner, 1) > 0) lost = " Your spinner went with it.";
        else if (Bait == Bait.Dough) Inventory.TakePlain(ItemId.DoughBait, 1);
        _items.Toast($"The line snapped!{lost}");
        PlayerInput.Rumble(0.9f, 0.9f, 0.3f);
        End("snap");
    }

    private void Land(Catch c)
    {
        if (Bait == Bait.Dough) Inventory.TakePlain(ItemId.DoughBait, 1);
        string name = c.Species.Item == ItemId.Whitefish && Spot.Lake is { } lake ? $"{c.Species.Name} ({lake.Whitefish})" : c.Species.Name;
        string size = FormattableString.Invariant($"{c.Cm:0} cm, {(c.Kg < 1 ? $"{c.Kg * 1000:0} g" : $"{c.Kg:0.0} kg")}");
        bool best = FishJournal.Record(c, Spot.Describe());
        string record = best ? " A new best!" : "";
        if (c.Kept)
        {
            _items.Give(new ItemStack(c.Species.Item, 1));
            _items.Toast($"{name}, {size}.{record}");
            End(((int)c.Species.Item).ToString(System.Globalization.CultureInfo.InvariantCulture), landed: true);
        }
        else
        {
            _items.Toast($"{name}, {size}: {FishRules.Why(c)}.{record}");
            End("", landed: true);
        }
        Landed?.Invoke(c);
    }

    /// <summary>
    /// Winds the line in: the float goes on every peer (<see cref="FishingVisuals.OnEnd"/>). <paramref name="extra"/>:
    /// "" wound in, "snap", "dry" (the float never reached water), or the item id of a fish kept.
    /// </summary>
    private void End(string extra, bool landed = false)
    {
        bool lineOut = State is Phase.Waiting or Phase.Bite or Phase.Fighting || extra == "dry";
        if (lineOut)
        {
            Events?.Send(ItemEventKind.FishEnd, Visuals?.LocalFloat ?? _float, Vector3.Zero, extra == "dry" ? "" : extra);
            if (!landed) Lost?.Invoke(extra);
        }
        State = Phase.Idle;
        Fight = null;
        Hooked = null;
        _biter = null;
        Power = 0;
    }

    private Inventory Inventory => _inventory;

    // ---- the HUD -----------------------------------------------------------------------------------

    private (Phase, int, bool) _hudKey = (Phase.Idle, -1, false);

    private static readonly Color Calm = new(0.35f, 0.85f, 0.4f), Strained = new(0.95f, 0.78f, 0.25f), Danger = new(0.95f, 0.25f, 0.18f);

    /// <summary>The prompt over the hotbar and a bar: the wind-up, or the line's tension. Text and colour only change on change.</summary>
    private void ShowHud(FootPlayer? player)
    {
        var phase = player == null ? Phase.Idle : State;
        var f = phase == Phase.Fighting ? Fight : null;
        bool run = f is { SurgeComing: true } or { Surging: true };
        var key = (phase, f == null ? -1 : (int)f.Distance, run);
        if (key != _hudKey)
        {
            _hudKey = key;
            string text = phase switch
            {
                Phase.Charging => "Let go to cast",
                Phase.Waiting => "Waiting for a bite…",
                Phase.Bite => InputHints.Format("A bite! {use_item}"),
                Phase.Fighting when run => "It runs! Let go!",
                Phase.Fighting when f != null => FormattableString.Invariant($"Reel in: {(int)f.Distance} m"),
                _ => "",
            };
            _label.Text = text;
            _label.Visible = text != "";
        }
        bool bar = phase == Phase.Charging || f != null;
        _bar.Visible = bar;
        if (!bar) return;
        _bar.Value = f != null ? Mathf.Clamp(f.Tension, 0, 1) : Power;
        var fill = f == null ? Strained : run || f.Tension > 0.8f ? Danger : Calm.Lerp(Strained, Mathf.Clamp(f.Tension / 0.8f, 0, 1));
        // a step of colour, not a new shade every frame: the style box redraws on change only
        fill = new Color(Mathf.Snapped(fill.R, 0.05f), Mathf.Snapped(fill.G, 0.05f), Mathf.Snapped(fill.B, 0.05f));
        if (fill != _shownFill)
        {
            _fill.BgColor = fill;
            _shownFill = fill;
        }
    }
}
