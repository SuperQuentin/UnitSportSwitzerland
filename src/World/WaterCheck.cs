using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Fixture;

namespace UnitSport.World;

/// <summary>
/// <c>--watercheck [net]</c> on <c>--chunks fixture:lake</c> (#299), headless.
/// <list type="bullet">
/// <item>Offline: the water layer (dry beach, shelf, deep lake, river), the wave field at calm and
/// gamey through <c>/seastate</c> (small on the river, big on the lake), <see cref="WaterField.IsUnderwater"/>,
/// and a car: wading in the shallows it keeps driving, driven into the deep it floats, sinks and is
/// wrecked with the driver out on foot.</item>
/// <item><c>net</c> (a client of a loopback server started with <c>--sea-state gamey
/// --admin-password test</c>, both on the lake): the sea state arrives on join; <c>/water E N</c>
/// answered by the server matches this peer's own field at the server's wave time to the millimetre;
/// an admin <c>/seastate storm</c> reaches this peer.</item>
/// </list>
/// Prints <c>[watercheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class WaterCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--watercheck") >= 0;

    private static bool NetMode
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--watercheck");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "net";
        }
    }

    private readonly Func<FootPlayer?> _local;
    private readonly List<string> _heard = new();
    private int _failures;

    public WaterCheck(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "WaterCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[watercheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[watercheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (done()) return true;
            await Wait(0.1);
        }
        return done();
    }

    private UnitSport.Net.ChatManager? Chat =>
        GetTree().Root.FindChild(UnitSport.Net.ChatManager.NodeName, true, false) as UnitSport.Net.ChatManager;

    /// <summary>A point of the course (metres from the start, X east, Y north) in world space, at altitude y.</summary>
    private static Vector3 At(double x, double y, float alt = 0f)
    {
        var (e, n) = Start;
        WaterField.TryWorld(e + x, n + y, out var w);
        return w with { Y = alt };
    }

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            // offline the world starts on the free camera: the player comes with the mode key
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        if (Chat is { } chat) chat.LineReceived += (line, _) => _heard.Add(line);

        var deep = At(Lake.ShoreX + 400, 0);
        if (!await Until(() => WaterField.TryGetStill(deep, out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }

        if (NetMode) await NetChecks();
        else
        {
            Layer();
            await Waves();
            await Cars(me);
        }
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { _failures++; GD.PrintErr($"[watercheck] FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    // ---- offline ---------------------------------------------------------------------------

    private void Layer()
    {
        var beach = At(0, 0);
        Expect(!WaterField.TryGetStill(beach, out _, out _), "the start, on the beach, is dry");

        var shelf = At(Lake.ShoreX + 100, 0);
        Expect(WaterField.TryGetStill(shelf, out float still, out float shelfScale) && Mathf.Abs(still - (float)Lake.Level) < 0.01f,
            $"the shelf has the lake's level ({still:F3})");
        float bed = WaterField.TryLv95(shelf, out double se, out double sn) ? (float)Lake.Ground(se - Start.E, sn - Start.N) : float.NaN;
        Expect(Mathf.Abs(still - bed - 1.67f) < 0.15f, $"the shelf is 1.7 m deep ({still - bed:F2})");

        var deep = At(Lake.ShoreX + 400, 0);
        WaterField.TryGetStill(deep, out _, out float deepScale);
        Expect(deepScale > 0.8f && deepScale < 0.95f, $"the deep lake takes the sea state ({deepScale:F3})");
        Expect(shelfScale < deepScale * 0.2f, $"the shelf is calmer ({shelfScale:F3})");

        var river = At(-600, Lake.RiverY);
        Expect(WaterField.TryGetStill(river, out float riverLevel, out float riverScale) && riverLevel > Lake.Level + 3,
            $"the river runs above the lake ({riverLevel:F2})");
        Expect(riverScale < 0.02f, $"the river is flat ({riverScale:F4})");

        Expect(WaterField.IsUnderwater(deep with { Y = (float)Lake.Level - 4 }), "4 m down in the lake is under water");
        Expect(!WaterField.IsUnderwater(deep with { Y = (float)Lake.Level + 3 }), "3 m up is not");
    }

    /// <summary>The largest wave height above or below the still level at a point over a minute of wave time.</summary>
    private static float Swing(Vector3 p)
    {
        WaterField.TryGetStill(p, out float still, out _);
        float max = 0f;
        for (double t = 0; t < 60; t += 0.37)
            if (WaterField.TryLevelAt(p, t, out float level)) max = Mathf.Max(max, Mathf.Abs(level - still));
        return max;
    }

    private async Task Waves()
    {
        var deep = At(Lake.ShoreX + 400, 0);
        var river = At(-600, Lake.RiverY);

        Chat?.Send("/seastate calm");
        await Until(() => WaterField.SeaState == 0f, 3);
        float calm = Swing(deep);
        Expect(calm < 0.08f, $"calm: a ripple on the lake ({calm:F3} m)");

        Chat?.Send("/seastate gamey");
        Expect(await Until(() => WaterField.SeaState == 1f, 3), "/seastate gamey sets the sea state offline");
        float gamey = Swing(deep), onRiver = Swing(river);
        Expect(gamey > 0.6f && gamey < 1.6f, $"gamey: a swell on the lake ({gamey:F2} m above or below still)");
        Expect(onRiver < 0.05f, $"gamey: the river stays flat ({onRiver:F3} m)");

        // the surface moves with the clock: two moments a second apart differ
        WaterField.TryLevelAt(deep, 10.0, out float a);
        WaterField.TryLevelAt(deep, 11.0, out float b);
        Expect(Mathf.Abs(a - b) > 0.01f, $"the waves move ({a:F3} -> {b:F3})");
        // floating-origin safe: the field is a function of LV95, so a point and the same point
        // named from another frame agree (exercised for real by --originstress)
        var n = WaterField.Normal(deep.X, deep.Z, 10.0);
        Expect(n.Y > 0.7f && Mathf.Abs(n.Length() - 1f) < 1e-3f, $"the normal is up-ish ({n})");
        Chat?.Send("/seastate calm");
        await Until(() => WaterField.SeaState == 0f, 3);
    }

    private async Task Cars(FootPlayer me)
    {
        var kind = CarCatalog.All[0].Kind;
        Expect(me.SetRide(kind), "in a car on the beach");
        await Wait(0.5);

        // the shallows: 0.4 m of water over the gravel, under the car's 0.55 m wading depth
        var wade = At(Lake.ShoreX + 24, 0);
        WaterField.TryLv95(wade, out double we, out double wn);
        float wadeGround = (float)Lake.Ground(we - Start.E, wn - Start.N);
        me.GlobalPosition = wade with { Y = wadeGround + 0.8f };
        me.Velocity = Vector3.Zero;
        await Wait(4);
        Expect(me.Ride == kind && me.Sinking == 0f, $"wading 0.4 m deep: still a car, not sinking ({me.Ride}, {me.Sinking:F1} s)");

        // the deep: dropped onto the water past the drop-off
        var drop = At(Lake.ShoreX + 400, 0, (float)Lake.Level + 1f);
        me.GlobalPosition = drop;
        me.Velocity = Vector3.Zero;
        float highest = float.MinValue;
        bool sank = false;
        for (double t = 0; t < 12 && !sank; t += 0.1)
        {
            if (t > 0.5 && t < 2.4) highest = Mathf.Max(highest, me.GlobalPosition.Y);
            sank = me.Ride == RideKind.OnFoot;
            await Wait(0.1);
        }
        Expect(highest > (float)Lake.Level - 1.5f, $"it floats first ({highest - (float)Lake.Level:F2} m from the surface)");
        Expect(sank, $"it sinks and the driver is out on foot ({me.Ride})");
        await Wait(1);
        bool wreck = false;
        if (UnitSport.Vehicles.VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is UnitSport.Vehicles.VehicleBody { Wrecked: true } v && v.GlobalPosition.DistanceTo(drop) < 30f) wreck = true;
        Expect(wreck, "a wreck is left where it went down");
    }

    // ---- net -------------------------------------------------------------------------------

    private async Task NetChecks()
    {
        Expect(await Until(() => Permissions.Online, 60), "online");
        Expect(await Until(() => WaterField.SeaState == 1f, 20), $"the server's gamey sea state arrived on join ({WaterField.SeaState})");

        foreach (var (x, y, what) in new[] { (Lake.ShoreX + 400, 0.0, "deep"), (Lake.ShoreX + 300, 150.0, "deep, off axis"), (Lake.ShoreX + 100, 0.0, "shelf") })
        {
            var (e, n) = Start;
            string ask = string.Create(CultureInfo.InvariantCulture, $"/water {e + x:F2} {n + y:F2}");
            int before = _heard.Count;
            Chat?.Send(ask);
            if (!await Until(() => _heard.Skip(before).Any(l => l.StartsWith("water at")), 10)) { Expect(false, $"{what}: the server answered /water"); continue; }
            string line = _heard.Skip(before).First(l => l.StartsWith("water at"));
            double here = WaterField.Now;
            if (!TryField(line, "t", out double t) || !TryField(line, "level", out double level)
                || !TryField(line, "still", out double still) || !TryField(line, "scale", out double scale))
            {
                Expect(false, $"{what}: the server has water there ({line})");
                continue;
            }
            var p = At(x, y);
            WaterField.TryGetStill(p, out float myStill, out float myScale);
            WaterField.TryLevelAt(p, t, out float mine);
            Log($"{what}: server {line}");
            Expect(Math.Abs(mine - level) < 0.002, $"{what}: the surface at the server's time t {t:F3} agrees ({mine:F4} vs {level:F4})");
            Expect(Math.Abs(myStill - still) < 0.001 && Math.Abs(myScale - scale) < 0.01, $"{what}: still level and wave scale agree");
            // the server's wave time, as this peer's synchronized clock reads it
            double dt = Math.Abs(here - t);
            dt = Math.Min(dt, WaveSpectrum.LoopS - dt);
            Expect(dt < 1.0, $"{what}: one clock (server t {t:F2}, here {here:F2})");
        }

        Chat?.Send("/login test");
        await Wait(1.5);
        Chat?.Send("/seastate storm");
        Expect(await Until(() => Mathf.Abs(WaterField.SeaState - SeaStateCommand.Storm) < 1e-4f, 10),
            $"an admin's /seastate storm reached this peer ({WaterField.SeaState})");
    }

    private static bool TryField(string line, string name, out double value)
    {
        value = 0;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < words.Length; i++)
            if (words[i] == name)
                return double.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return false;
    }
}
