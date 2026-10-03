using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--holdcheck [shots]</c> offline on <c>--world fixture</c> (#418): a car driven into a hold and
/// carried, on the real mechanism (<c>docs/notes/vehicles/vehicles-in-holds.md</c>). The carrier is a
/// parked A320 given the checks' test hold (<see cref="TestHold"/>, under its belly, a ramp at the
/// back behind its aft left door), moved by hand as if flown.
/// <list type="bullet">
/// <item>what fits: a car fits the test hold, a city bus does not (hull bounds);</item>
/// <item>up the ramp into the hold, stopped, the handbrake: carried, tied down;</item>
/// <item>the carrier moved, turned, pitched and rolled for seconds: the car stays on its spot;</item>
/// <item>got out: the parked car is carried (its <c>VehicleState.Carrier</c>), and moves with the carrier;</item>
/// <item>got back in: tied down where it stood; reversed down the ramp, out of the hold, on the ground.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/progress/418-*.png</c>. RESULT line at the end.
/// </summary>
public partial class HoldCheck : Node
{
    public static bool Requested => CmdArgs.Has("--holdcheck");
    private static bool Shots => CmdArgs.Value("--holdcheck") == "shots";

    /// <summary>The A320 door whose opening is the test hold's ramp (aft left).</summary>
    public const int RampDoor = 3;

    /// <summary>The test hold's floor: its top over the ground, m; the bay's middle along the aircraft, and its length.</summary>
    public const float FloorTop = 0.12f, BayLength = 16f, BayWidth = 3.4f, BayHeight = 1.85f;

    /// <summary>
    /// The checks' hold for the A320 (#418, <see cref="Airliner.ProbeHold"/>): a floor under the belly
    /// between the main gear legs, side walls, a front wall, at the back a wall while the aft left door
    /// is shut and a ramp down to the ground while it is open. Its aboard box is the hold, so one can
    /// walk in it too. Not the game's: an A320 carries no vehicles.
    /// </summary>
    public static VehicleDeck TestHold()
    {
        static float At(float z) => -z;   // authored z (+ forward) to the builder's station (cg 0)
        var dk = new DeckBuilder(0f);
        float half = BayLength * 0.5f, top = FloorTop + BayHeight, wall = 0.1f, w = BayWidth + 0.2f;
        dk.Along(At(half + 0.1f), At(-half), 0f, FloorTop, w);                                  // the floor
        foreach (float x in new[] { 1f, -1f })
            dk.Along(At(half + 0.1f), At(-half), FloorTop, top, wall, x * (BayWidth * 0.5f + wall * 0.5f));
        dk.Along(At(half + 0.1f), At(half), FloorTop, top, w);                                  // the front wall
        dk.Along(At(-half), At(-half - 0.1f), FloorTop, top, w, 0f, DeckPart.DoorShut, RampDoor);  // the tailgate
        dk.RampAlong(At(-half - 3f), 0f, At(-half), FloorTop, BayWidth, 0f, DeckPart.DoorStep, RampDoor);
        dk.CargoBay(new Vector3(0f, FloorTop + BayHeight * 0.5f, 0f), new Vector3(BayWidth, BayHeight, BayLength));
        return dk.Build(0, new Aabb(new Vector3(-BayWidth * 0.5f, 0f, -half), new Vector3(BayWidth, top, BayLength)));
    }

    private readonly System.Func<FootPlayer?> _player;
    private int _failures;

    /// <summary>Run in this frame's <c>_Process</c>, last of all (<c>ProcessPriority</c>): after every vehicle was put where it is drawn.</summary>
    private System.Action? _onFrame;

    public override void _Process(double delta) => _onFrame?.Invoke();

    public HoldCheck(System.Func<FootPlayer?> player)
    {
        _player = player;
        ProcessPriority = 100;
        Airliner.ProbeHold = TestHold();
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[hold] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private int _shot;

    /// <summary>The carrier the pictures are taken of, from a camera of their own beside it (the chase camera ends up in the fuselage).</summary>
    private VehicleBody? _filmed;
    private Camera3D? _spectator;

    private async Task Shot(string name)
    {
        if (!Shots) return;
        Camera3D? was = null;
        if (_filmed != null && IsInstanceValid(_filmed))
        {
            // off the left side and behind, a little above the hold: the ramp, the car in it and the aircraft over it
            was = GetViewport().GetCamera3D();
            _spectator ??= new Camera3D { Fov = 60f, Far = 20000f };
            if (_spectator.GetParent() == null) AddChild(_spectator);
            var eye = Point(_filmed, 13f, 4.5f, -22f);
            _spectator.GlobalPosition = eye;
            _spectator.LookAt(Point(_filmed, 0f, 0.8f, -2f), Vector3.Up);
            _spectator.MakeCurrent();
        }
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/progress");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"418-{++_shot:00}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[hold] wrote {path}");
        if (was != null && IsInstanceValid(was)) was.MakeCurrent();
    }

    private static VehicleBody? Carrier() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.A320 && !v.Wrecked && !v.IsQueuedForDeletion());

    /// <summary>The car parked in the hold (the real map has vehicles placed of its own: the one carried, or else the nearest of its kind).</summary>
    private VehicleBody? ParkedCar() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .Where(v => v.Kind == (RideKind)CarCatalog.First && !v.IsQueuedForDeletion())
        .OrderBy(v => v.InHold ? 0 : 1).ThenBy(v => _player() is { } p ? v.GlobalPosition.DistanceTo(p.GlobalPosition) : 0f).FirstOrDefault();

    /// <summary>The carrier's frame now (its drawn rig, or headless its own node).</summary>
    private static Transform3D Frame(VehicleBody carrier) => FootPlayer.HoldFrame(carrier, 0);

    /// <summary>An authored point of the A320 (x left, z forward) in the world as the carrier stands now.</summary>
    private static Vector3 Point(VehicleBody carrier, float x, float y, float z) => Frame(carrier) * AircraftMeshBuilder.Flip(new Vector3(x, y, z));

    /// <summary>A point of the world in the carrier's authored frame.</summary>
    private static Vector3 Local(VehicleBody carrier, Vector3 world) => AircraftMeshBuilder.Flip(Frame(carrier).AffineInverse() * world);

    private static string F(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})");

    /// <summary>Moves the carrier by hand for <paramref name="seconds"/>: ahead at a speed, turning, pitched and rolled; checks <paramref name="each"/> every step.</summary>
    private async Task Fly(VehicleBody carrier, double seconds, float speed, float turnRate, float pitch, float roll, System.Action each, string? shot = null)
    {
        var start = carrier.GlobalTransform;
        float yaw = start.Basis.GetEuler().Y;
        var at = start.Origin;
        double t = 0;
        while (t < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            float dt = (float)GetPhysicsProcessDeltaTime();
            t += dt;
            float k = Mathf.Sin(Mathf.Pi * (float)(t / seconds));   // eases in and out of the attitude
            yaw += turnRate * dt;
            var basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch * k) * new Basis(Vector3.Back, roll * k);
            at += basis * Vector3.Forward * speed * dt;
            at.Y = start.Origin.Y + 30f * k;
            carrier.GlobalTransform = new Transform3D(basis, at);
            // measured as it is drawn: after the carrier and its cargo have moved this frame
            _onFrame = each;
            if (shot != null && t > seconds * 0.45)
            {
                await Shot(shot);
                shot = null;
            }
        }
        _onFrame = null;
        // landed back where it took off, the circuit closed (the fixture's ground is one tile: never off its edge)
        carrier.GlobalTransform = start;
        await Seconds(0.5);
    }

    public override async void _Ready()
    {
        await Seconds(2);
        // offline the world starts on the free camera: the player comes with the mode key
        for (int i = 0; i < 1800 && (_player() is null || !_player()!.IsOnFloor()); i++)
        {
            if (_player() == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor()) { Finish("no player"); return; }
        if (VehicleManager.Instance is not { } vehicles || me.Origin is not { } origin) { Finish("no vehicles"); return; }

        // the carrier: an A320 parked 40 m ahead of the player, its ramp (aft left door) down
        float yaw = me.Rotation.Y;
        var ahead = new Basis(Vector3.Up, yaw) * Vector3.Forward;
        var spot = me.GlobalPosition + ahead * 45f;
        var jet = new Airliner(RideKind.A320, AirlinerCatalog.A320);
        jet.DoorsOpen = 1 << RampDoor;
        vehicles.Park(new VehicleState(RideKind.A320, origin.ToGlobal(spot), yaw, Vector3.Zero, 400f, false, false, 0f, VehicleState.Now,
            Flags: jet.PackFlags()));
        if (!await Until(() => Carrier() is { Asleep: true, Posed: true }, 20)) { Finish("the carrier never came to rest"); return; }
        var carrier = Carrier()!;
        _filmed = carrier;
        Expect(carrier.Ride.Decks.Any(d => d.CargoBays.Length > 0) && carrier.BusDoors == 1 << RampDoor,
            $"the carrier has the test hold, its ramp down (doors {carrier.BusDoors})");

        // what fits, from hull bounds
        var car = Rideable.Create((RideKind)CarCatalog.First)!;
        var bus = Rideable.Create((RideKind)(HeavyCatalog.First + System.Array.FindIndex(HeavyCatalog.All.ToArray(), h => h.Class == HeavyClass.CityBus)))!;
        var middle = Point(carrier, 0f, FloorTop, 0f);
        Expect(FootPlayer.CarrierAt(this, middle, car.ParkedBox.Size, null) != null,
            $"a {car.Label} ({F(car.ParkedBox.Size)}) fits the hold ({BayWidth} x {BayHeight} x {BayLength})");
        Expect(FootPlayer.CarrierAt(this, middle, bus.ParkedBox.Size, null) == null,
            $"a {bus.Label} ({F(bus.ParkedBox.Size)}) does not");

        // the car, behind the ramp, nose to the hold
        Expect(me.SetRide(car.Kind), $"in a {car.Label}");
        await Seconds(1);
        me.PlaceAt(Point(carrier, 0f, 0.3f, -BayLength * 0.5f - 8f), carrier.Rotation.Y);
        await Seconds(1.5);
        await Shot("car-behind-the-hold-cartoon");
        float throttle = 0.35f, brake = 0f;
        bool handbrake = false;
        me.RideControls = () => new RideInput(throttle, brake, 0f, false, handbrake);
        bool inside = await Until(() =>
        {
            if (me.GroundSpeed > 3f) throttle = 0f; else if (me.GroundSpeed < 1.5f) throttle = 0.35f;
            return me.DeckOn == FootPlayer.KeyOf(carrier) && Local(carrier, me.GlobalPosition).Z > -1f;
        }, 40);
        var l = Local(carrier, me.GlobalPosition);
        Expect(inside, $"drove up the ramp into the hold: carried by '{me.DeckOn}' at {F(l)}");
        throttle = 0f;
        brake = 1f;
        await Until(() => me.GroundSpeed < 0.2f, 10);
        // not tied yet, nothing pressed: taxiing, the car's own physics runs on the hold's floor, in its frame
        brake = 0f;
        var rolling = Local(carrier, me.GlobalPosition);
        float drift = 0f;
        bool aboard = true;
        await Fly(carrier, 3, 6f, 0.12f, 0f, 0f, () =>
        {
            drift = Mathf.Max(drift, Local(carrier, me.GlobalPosition).DistanceTo(rolling));
            aboard &= me.DeckOn == FootPlayer.KeyOf(carrier) && !me.TiedDown;
        });
        Expect(aboard && drift < 0.5f, $"taxied 18 m, turning, not tied: still in the hold, {drift:F2} m from where it stopped");
        brake = 1f;
        await Until(() => me.GroundSpeed < 0.2f, 10);
        handbrake = true;
        brake = 0f;
        Expect(await Until(() => me.TiedDown, 5), $"stopped with the handbrake: tied down ({me.GroundSpeed:F2} m/s)");
        handbrake = false;
        l = Local(carrier, me.GlobalPosition);
        Expect(Mathf.Abs(l.Y - FloorTop) < 0.15f && Mathf.Abs(l.X) < 1f, $"on the hold's floor at {F(l)}");
        await Shot("car-in-hold-cartoon");

        // flown: ahead at 60 m/s, turning, pitched up 12° and rolled 20°
        var tied = l;
        float worst = 0f;
        bool carried = true;
        await Fly(carrier, 6, 60f, 0.15f, 0.21f, 0.35f, () =>
        {
            var now = Local(carrier, me.GlobalPosition);
            worst = Mathf.Max(worst, now.DistanceTo(tied));
            carried &= me.DeckOn == FootPlayer.KeyOf(carrier) && me.TiedDown;
        }, "car-carried-in-flight-cartoon");
        Expect(carried && worst < 0.05f, $"carried 360 m, turning, pitched and rolled: never off its spot by more than {worst * 100f:F1} cm");

        // out of it: the parked car is carried
        me.RideControls = null;
        me.ExitVehicle();
        if (!await Until(() => ParkedCar() is { InHold: true }, 5)) { Expect(false, "the parked car is in the hold"); Finish(""); return; }
        var parked = ParkedCar()!;
        await Seconds(0.5);
        Expect(parked.Carrier == FootPlayer.KeyOf(carrier) && parked.Capture().Carrier == parked.Carrier,
            $"got out: the parked car stands in the hold, carried by '{parked.Carrier}' at {F(parked.CarrierPos)}");
        var standing = Local(carrier, parked.GlobalPosition);
        await Shot("car-parked-in-hold-cartoon");
        worst = 0f;
        await Fly(carrier, 3, 30f, -0.2f, 0.1f, -0.2f, () => worst = Mathf.Max(worst, Local(carrier, parked.GlobalPosition).DistanceTo(standing)));
        Expect(worst < 0.05f, $"the parked car goes where the carrier goes (off its spot by {worst * 100f:F1} cm at most)");

        // back in: tied where it stood, then reversed down the ramp
        bool back = false;
        vehicles.Claim(parked, state => { me.TakeVehicle(state, 0); back = true; });
        Expect(await Until(() => back && me.Vehicle is Car, 5) && me.TiedDown && me.DeckOn == FootPlayer.KeyOf(carrier),
            $"back in: tied down in the hold ('{me.DeckOn}', tied {me.TiedDown})");
        await Seconds(0.5);
        l = Local(carrier, me.GlobalPosition);
        Expect(l.DistanceTo(standing) < 0.1f, $"where it was parked ({F(l)}, parked at {F(standing)})");
        throttle = 0f;
        brake = 0.4f;
        me.RideControls = () => new RideInput(throttle, brake, 0f, false, false);
        bool outside = await Until(() =>
        {
            brake = me.GroundSpeed > 3f ? 0f : 0.4f;
            return me.DeckOn == "" && Local(carrier, me.GlobalPosition).Z < -BayLength * 0.5f - 6f;
        }, 40);
        brake = 0f;
        throttle = 0f;
        me.RideControls = () => new RideInput(0f, 1f, 0f, false, true);
        await Seconds(2);
        l = Local(carrier, me.GlobalPosition);
        Expect(outside && me.IsOnFloor() && Mathf.Abs(l.Y) < 0.3f, $"reversed down the ramp and out: on the ground behind it at {F(l)}, not carried ('{me.DeckOn}')");
        await Shot("car-out-of-the-hold-cartoon");
        me.RideControls = null;
        Finish("");
    }

    private void Finish(string why)
    {
        if (why != "") Expect(false, why);
        GD.Print(_failures == 0 ? "[hold] RESULT: ok" : $"[hold] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
