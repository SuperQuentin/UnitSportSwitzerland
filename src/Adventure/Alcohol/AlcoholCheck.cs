using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Fixture;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// <c>--alcoholcheck --chunks fixture:crag --systems ui,physics,adventure</c> (quick tier, #1271): offline, this client plays
/// the server's part. Three beers and an alcohol-free one are drunk from the pack (the drink log: 39 g + 1.3 g); the curve
/// from the log (rising to a peak between 30 and 90 min, back to 0 within five hours); the clock skipped 45 min, the level
/// and the glass icon; the effects at set levels: the lag on a car's steering (0.2 s at 1.0 per mille), on the walk, the climb
/// refused at 1.6 ("Not in this state") and granted sober, passing out at 2.6 for a minute and waking; the electronic reading
/// (two decimals, mg/L, the limit line), mouth alcohol ("WAIT"), the single-use tube used up with its colour; mild feels less
/// and never passes out; off logs nothing and reads 0.00. Progress in <c>test_output/alcoholcheck</c>, a scratch bank.
/// </summary>
public partial class AlcoholCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--alcoholcheck");

    private readonly Func<FootPlayer?> _me;
    private readonly ItemStack[] _kept = new ItemStack[Inventory.Size];
    private Vector3 _wish;
    private float _steer;

    public AlcoholCheck(ItemController items, Func<FootPlayer?> me) : base(items, "alcoholcheck", "", "alcohol_") => _me = me;
    public AlcoholCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static AlcoholService Svc => AlcoholService.Instance!;
    private AlcoholHud? Hud => GetParent().GetNodeOrNull<AlcoholHud>("AlcoholHud");
    private static List<Shot> DrinkLog => AdventureService.Instance!.Mine.Get(AlcoholProgress.Section).Drinks;

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    private Vector3 At(FootPlayer me, double x, double y, double above = 0)
    {
        var (e, n) = Start;
        return me.Origin!.ToWorld(e + x, n + y, Crag.Ground(x, y) + above);
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        AlcoholClock.Reset();
        AlcoholService.ModeOverride = AlcoholMode.Real;
        AlcoholService.LevelOverride = null;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => AlcoholService.Instance != null && AdventureService.Instance != null && Me is { } m && m.IsOnFloor() && m.Origin != null, 90))
        {
            Fail($"no alcohol service ({AlcoholService.Instance != null}) or no player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var me = Me!;
        if (!await Until(() => me.Terrain != null && me.Terrain.HasCollisionAt(At(me, Crag.WallX + 10, 0)), 90)) { Fail("the crag never loaded"); return; }
        for (int i = 0; i < Inventory.Size; i++) _kept[i] = _items.Inventory[i];
        me.WalkControls = () => (_wish, false);
        // the scratch folder keeps the last run's log: start from a sober player
        DrinkLog.Clear();
        AdventureService.Instance!.Mine.Touch(AlcoholProgress.Section);

        await Drinks(me);
        Curve();
        await Feel(me);
        await Lag(me);
        await Climb(me);
        await PassOut(me);
        await Readings(me);
        await Modes(me);

        AlcoholService.ModeOverride = null;
        AlcoholService.LevelOverride = null;
        AlcoholClock.Reset();
        me.WalkControls = null;
        me.RideControls = null;
        for (int i = 0; i < Inventory.Size; i++) _items.Inventory.Put(i, _kept[i]);
        await Finish(0.5);
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private async Task UseOne(FootPlayer me, int slot, ItemId id, double seconds = 8)
    {
        _items.Inventory.Select(slot);
        int before = Count(id);
        _items.UseSlot(me, slot);
        Expect(await Until(() => Count(id) < before, seconds), $"{id} used ({before} -> {Count(id)})");
        await Until(() => !_items.UseBusy, 5);
    }

    private async Task Drinks(FootPlayer me)
    {
        Expect(Svc.Mode == AlcoholMode.Real && DrinkLog.Count == 0 && Svc.LevelOf(Svc.Self) == 0, "a fresh player is sober, alcohol is real");
        _items.Inventory.Put(0, new ItemStack(ItemId.BeerBottle, 3));
        for (int i = 0; i < 3; i++) await UseOne(me, 0, ItemId.BeerBottle);
        Expect(DrinkLog.Count == 3, $"three beers in the drink log ({DrinkLog.Count})");
        double grams = DrinkLog.Sum(s => (double)s.G);
        Expect(Math.Abs(grams - 39.0) < 0.6, $"a beer is 13 g of ethanol: {grams:F1} g for three");
        _items.Inventory.Put(1, new ItemStack(ItemId.BeerAlcoholFree, 1));
        await UseOne(me, 1, ItemId.BeerAlcoholFree);
        Expect(DrinkLog.Count == 4 && Math.Abs(DrinkLog.Sum(s => (double)s.G) - 40.3) < 0.7, $"the alcohol-free beer adds 1.3 g ({DrinkLog.Sum(s => (double)s.G):F1} g)");
        double now = Svc.LevelOf(Svc.Self);
        Expect(now < 0.1, $"just drunk, almost nothing is absorbed yet ({now:F2})");
    }

    private void Curve()
    {
        double t0 = AlcoholClock.Now;
        var levels = new List<(int Min, double L)>();
        foreach (int min in new[] { 0, 10, 30, 45, 60, 90, 180, 300 }) levels.Add((min, Svc.LevelOf(Svc.Self, t0 + min * 60)));
        GD.Print($"{base.Log} curve " + string.Join(" ", levels.Select(l => FormattableString.Invariant($"{l.Min}min={l.L:F2}"))));
        double peak = levels.Max(l => l.L);
        int peakAt = levels.First(l => l.L == peak).Min;
        Expect(peak is > 0.5 and < 0.95 && peakAt is >= 30 and <= 90, $"the peak is {peak:F2} per mille at {peakAt} min");
        Expect(levels[1].L > levels[0].L && levels[2].L > levels[1].L, "it rises after the drinks");
        Expect(levels[^3].L < peak && levels[^2].L < levels[^3].L, "then it falls");
        double gone = Svc.LevelOf(Svc.Self, t0 + 9 * 3600);
        Expect(levels[^1].L < levels[^2].L && gone == 0, "and is gone within nine hours (three beers and a half take about that)");
    }

    private async Task Feel(FootPlayer me)
    {
        AlcoholClock.Skip(45 * 60);
        Svc.Resample();
        Expect(Svc.Level is > 0.45 and < 0.95, $"45 min on: the level is {Svc.Level:F2}");
        Expect(me.Drunk == Svc.Body, "the local body wears the filter");
        await Seconds(0.3);
        Expect(Hud?.IconShown == true, "the glass icon shows above 0.3 per mille");
        Expect(Svc.Body.Fx.Lag >= 0.1f && Svc.Body.Fx.SwayDegrees > 0, $"lag {Svc.Body.Fx.Lag:F1} s and sway {Svc.Body.Fx.SwayDegrees:F1} deg");
        Expect(Svc.Body.Sway(0.016f) != Vector2.Zero || Svc.Body.Sway(0.5f) != Vector2.Zero, "the camera sways");
        GameSettings.Current.AlcoholViewEffects = false;
        Svc.Resample();
        Expect(Svc.Body.Sway(0.5f) == Vector2.Zero && Svc.Body.Blur == 0f && Svc.Body.Fx.Lag > 0f, "the comfort setting drops the sway and blur and keeps the lag");
        GameSettings.Current.AlcoholViewEffects = true;
        AlcoholService.LevelOverride = 1.0;
        Svc.Resample();
        await Until(() => Hud?.BlurStrength > 0f, 2);
        Expect(Hud?.BlurStrength > 0f, "above 0.8 the edges blur");
        AlcoholService.LevelOverride = 0.0;
        Svc.Resample();
        await Until(() => Hud?.IconShown == false && Hud?.BlurStrength == 0f, 2);
        Expect(Hud?.IconShown == false && Hud?.BlurStrength == 0f, "sober: no icon, no blur");
    }

    private async Task Lag(FootPlayer me)
    {
        // a car's steering: the time the steer reaches half after the input jumps to full
        async Task<double> Response(double level)
        {
            AlcoholService.LevelOverride = level;
            Svc.Resample();
            _steer = 0f;
            await Seconds(0.6);
            double t0 = GameClock.Now;
            _steer = 1f;
            await Until(() => me.LastRideInput.Steer > 0.5f, 2);
            return GameClock.Now - t0;
        }
        Expect(me.SetRide((RideKind)8), "mounted a car");
        me.RideControls = () => new RideInput(0f, 0f, _steer, false);
        await Seconds(0.5);
        double sober = await Response(0.0);
        double lag1 = await Response(0.6);
        double lag2 = await Response(1.0);
        double lag3 = await Response(1.8);
        GD.Print(FormattableString.Invariant($"{base.Log} steer response sober {sober:F2} s, 0.6: {lag1:F2} s, 1.0: {lag2:F2} s, 1.8: {lag3:F2} s"));
        Expect(sober < 0.12, $"sober, the steering answers at once ({sober:F2} s)");
        Expect(lag1 - sober is > 0.06 and < 0.2, $"at 0.6 per mille the input lags 100 ms ({lag1 - sober:F2} s)");
        Expect(lag2 - sober is > 0.14 and < 0.3, $"at 1.0 it lags 200 ms ({lag2 - sober:F2} s)");
        Expect(lag3 - sober is > 0.22 and < 0.42, $"at 1.8 it lags 300 ms ({lag3 - sober:F2} s)");
        me.RideControls = null;
        me.SetRide(RideKind.OnFoot);
        await Until(() => me.Ride == RideKind.OnFoot && me.IsOnFloor(), 5);

        // on foot: the time to a 1 m/s walk
        async Task<double> Walk(double level)
        {
            AlcoholService.LevelOverride = level;
            Svc.Resample();
            _wish = Vector3.Zero;
            await Seconds(0.8);
            double t0 = GameClock.Now;
            _wish = Vector3.Right;
            await Until(() => new Vector2(me.Velocity.X, me.Velocity.Z).Length() > 1f, 3);
            double t = GameClock.Now - t0;
            _wish = Vector3.Zero;
            return t;
        }
        double w0 = await Walk(0.0), w1 = await Walk(1.0);
        GD.Print(FormattableString.Invariant($"{base.Log} walk onset sober {w0:F2} s, 1.0: {w1:F2} s"));
        Expect(w1 - w0 > 0.1, $"the walk lags too ({w1 - w0:F2} s later)");
        AlcoholService.LevelOverride = null;
    }

    private async Task Climb(FootPlayer me)
    {
        var east = (At(me, 100, 0) - At(me, 0, 0)) with { Y = 0 };
        east = east.Normalized();
        async Task Stand()
        {
            me.GlobalPosition = At(me, Crag.WallX - 1.5, 0, 0.3);
            me.Velocity = Vector3.Zero;
            me.LookYaw = Mathf.Atan2(-east.X, -east.Z);
            await Until(() => me.IsOnFloor() && !me.IsClimbing, 5);
            me.DebugSetStamina(me.StaminaMeter.Max);
            await Seconds(0.2);
        }
        AlcoholService.LevelOverride = 1.6;
        Svc.Resample();
        await Stand();
        _wish = east;
        bool grabbed = await Until(() => me.IsClimbing, 3);
        _wish = Vector3.Zero;
        Expect(!grabbed && me.ClimbRefused == "Not in this state", $"at 1.6 per mille the wall is refused ({me.ClimbRefused})");
        AlcoholService.LevelOverride = 0.0;
        Svc.Resample();
        await Stand();
        _wish = east;
        Expect(await Until(() => me.IsClimbing, 4), "sober, the same wall is grabbed");
        _wish = Vector3.Zero;
        Input.ActionPress(PlayerInput.CrouchSlide);
        await Seconds(0.2);
        Input.ActionRelease(PlayerInput.CrouchSlide);
        await Until(() => !me.IsClimbing, 3);
        await Until(() => me.IsOnFloor(), 6);
        AlcoholService.LevelOverride = null;
    }

    private async Task PassOut(FootPlayer me)
    {
        await Until(() => me.IsOnFloor() && !me.IsClimbing, 8);
        AlcoholService.LevelOverride = 2.2;
        Svc.Resample();
        await Seconds(1.0);
        Expect(!me.PassedOut, "2.2 per mille: stumbling, not out");
        AlcoholService.LevelOverride = 2.6;
        Svc.Resample();
        Expect(await Until(() => me.PassedOut && me.Down == 1, 3), "at 2.6 per mille the player passes out (down, still)");
        double t0 = GameClock.Now;
        AlcoholService.LevelOverride = 1.0;
        Svc.Resample();
        await Seconds(10);
        Expect(me.PassedOut, "a minute is a minute: a sober level does not wake it early");
        Expect(await Until(() => !me.PassedOut && me.Down == 0, 80), "then it wakes");
        double out_ = GameClock.Now - t0;
        Expect(out_ is >= 59 and < 70, $"out for {out_:F0} s");
        await Until(() => me.IsOnFloor(), 5);
        AlcoholService.LevelOverride = null;
    }

    private async Task Readings(FootPlayer me)
    {
        Svc.Resample();
        var hud = Hud!;
        _items.Inventory.Put(2, new ItemStack(ItemId.Breathalyzer, 1));
        _items.Inventory.Select(2);
        double server = Svc.LevelOf(Svc.Self);
        _items.UseSlot(me, 2);
        Expect(await Until(() => hud.Shown.Contains('‰'), 10), $"the electronic reading shows after the blow ('{hud.Shown}')");
        Expect(Count(ItemId.Breathalyzer) == 1, "the electronic device is kept");
        var r = Svc.LastResult;
        Expect(r is { Kind: BreathKind.Electronic } && Math.Abs(r.Value.Level - server) < 0.03, $"it reads the server's number: {r?.Level:F2} vs {server:F2}");
        Expect(System.Text.RegularExpressions.Regex.IsMatch(hud.Shown, @"^\d\.\d\d ‰ · \d\.\d\d mg/L$"), $"two decimals, both units ('{hud.Shown}')");
        Expect(hud.ShownSub == (Math.Round(server, 2) > 0.5 ? "> 0.5 ‰: do not drive" : "under the 0.5 ‰ limit"), $"and the limit line ('{hud.ShownSub}')");
        await Until(() => !_items.UseBusy, 5);

        // a drink just now: mouth alcohol, the device says wait
        Svc.ServeDrink(Svc.Self, 330, 0.05, false, "check");
        _items.UseSlot(me, 2);
        Expect(await Until(() => hud.Shown.StartsWith("WAIT"), 10), $"mouth alcohol: '{hud.Shown}'");
        Expect(hud.Shown is "WAIT 15 min" or "WAIT 14 min", "about a quarter of an hour to wait");
        await Until(() => !_items.UseBusy, 5);

        // the single-use test: one reading, then used up; an early reading says so
        _items.Inventory.Put(3, new ItemStack(ItemId.BreathTestSingle, 2));
        _items.Inventory.Select(3);
        _items.UseSlot(me, 3);
        Expect(await Until(() => hud.TubeShown != default, 10), "the tube shows its colour");
        Expect(Count(ItemId.BreathTestSingle) == 1, "one test used up");
        Expect(hud.ShownSub.Contains("unreliable"), $"too soon after a drink it says so ('{hud.ShownSub}')");
        await Until(() => !_items.UseBusy, 5);
        AlcoholClock.Skip(20 * 60);
        double level = Svc.LevelOf(Svc.Self);
        _items.UseSlot(me, 3);
        Expect(await Until(() => Count(ItemId.BreathTestSingle) == 0, 10), "the second test is used up too");
        await Until(() => Svc.LastResult is { Kind: BreathKind.Tube, WaitSeconds: 0 }, 5);
        bool green = hud.TubeShown.G > hud.TubeShown.R;
        Expect(green == level < 0.5, $"the tube is {(green ? "green" : "amber")} at {level:F2} per mille");
        Expect(!hud.ShownSub.Contains("unreliable"), "a clean reading after the wait");
        Expect(_items.Inventory[3].IsEmpty, "nothing left in the slot");
    }

    private async Task Modes(FootPlayer me)
    {
        AlcoholService.ModeOverride = AlcoholMode.Mild;
        AlcoholService.LevelOverride = 2.6;
        Svc.Resample();
        Expect(!Svc.Body.Fx.PassesOut && Svc.Body.Fx.Lag == AlcoholRules.Effects(1.3, AlcoholMode.Real).Lag, "mild: half the effects, no passing out");
        await Seconds(0.5);
        Expect(!me.PassedOut, "mild at 2.6: still on the feet");
        AlcoholService.LevelOverride = null;

        AlcoholService.ModeOverride = AlcoholMode.Off;
        int logged = DrinkLog.Count;
        Expect(!Svc.ServeDrink(Svc.Self, 330, 0.05, false, "off") && DrinkLog.Count == logged, "off: a drink carries no alcohol");
        Svc.Resample();
        Expect(!Svc.Body.Fx.Any && Svc.Level == 0 || Svc.Body.Fx.Lag == 0f, "off: no effects");
        _items.Inventory.Put(4, new ItemStack(ItemId.Breathalyzer, 1));
        _items.Inventory.Select(4);
        _items.UseSlot(me, 4);
        Expect(await Until(() => Hud!.Shown.StartsWith("0.00"), 10), $"off: the device reads 0.00 ('{Hud!.Shown}')");
        await Until(() => !_items.UseBusy, 5);
    }
}
