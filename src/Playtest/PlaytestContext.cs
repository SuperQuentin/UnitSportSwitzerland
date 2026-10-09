using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Playtest;

/// <summary>
/// What a scenario's <see cref="PlaytestScenario.Setup"/> builds with: the player, the vehicles, the
/// ground, and a stage to place things on. Everything placed through it is tracked and removed by
/// <see cref="Clear"/>, which runs before the next scenario, so scenarios never leave each other
/// debris. Offline only (vehicles placed here are this client's own).
/// </summary>
public sealed class PlaytestContext
{
    private readonly Node _host;
    private readonly Func<FootPlayer?> _local;
    private readonly Action<string> _command;
    private readonly List<string> _vehicles = [];
    private readonly List<Node> _nodes = [];
    /// <summary>Numbers every placed vehicle of the session: a name never comes back while the last one is still being freed.</summary>
    private int _serial;

    /// <summary>The scenario's knobs, as Claude last set them (<c>set_param</c>), else the defaults.</summary>
    internal Dictionary<string, double> Params { get; set; } = [];

    /// <summary>The fixture course's start in LV95 (the spawn), when this run is on one.</summary>
    internal (double E, double N)? CourseStart { get; set; }

    /// <summary>Where scenarios that need no course are built: open ground found once per session, facing <see cref="StageYaw"/>.</summary>
    public Vector3 Stage { get => _stage; internal set { _stage = value; _stageSet = true; } }

    private Vector3 _stage;
    private bool _stageSet;

    public float StageYaw { get; internal set; }

    internal PlaytestContext(Node host, Func<FootPlayer?> local, Action<string> command)
    {
        _host = host;
        _local = local;
        _command = command;
    }

    /// <summary>The local player, once there is one (see <see cref="OnFoot"/>).</summary>
    public FootPlayer? Player => _local();

    public VehicleManager? Vehicles => VehicleManager.Instance;

    public double Param(string name) => Params.TryGetValue(name, out double v) ? v : 0;

    /// <summary>Runs a chat command (<c>/time 18:00</c>, <c>/give ...</c>), offline its own admin.</summary>
    public void Command(string line) => _command(line);

    public async Task Wait(double seconds) =>
        await _host.ToSignal(_host.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    /// <summary>The ground height under <paramref name="at"/>, or its own Y where there is none (yet).</summary>
    public float Ground(Vector3 at) =>
        TestWorld.TryGround(Player?.Terrain ?? Vehicles?.Terrain, at, out float y) ? y : at.Y;

    /// <summary>A point on the ground relative to the stage: <paramref name="ahead"/> m along its heading, <paramref name="right"/> m to its right.</summary>
    public Vector3 At(float ahead, float right = 0f)
    {
        var basis = new Basis(Vector3.Up, StageYaw);
        var p = Stage + basis * new Vector3(right, 0, -ahead);
        return p with { Y = Ground(p) };
    }

    /// <summary>A yaw relative to the stage's heading, degrees (0: the way the stage faces, 90: to its right).</summary>
    public float Heading(float degreesRight) => StageYaw - Mathf.DegToRad(degreesRight);

    /// <summary>A point on the fixture course by its own coordinates (m east, m north of the start), on the ground.</summary>
    public Vector3 Course(double x, double y)
    {
        if (CourseStart is not { } s || (Player?.Origin ?? Vehicles?.Origin) is not { } origin)
            throw new InvalidOperationException("not on a fixture course");
        var p = origin.ToWorld(s.E + x, s.N + y, 0);
        return p with { Y = Ground(p) };
    }

    /// <summary>The yaw that faces from <paramref name="from"/> to <paramref name="to"/> (0 faces north, -Z).</summary>
    public static float Facing(Vector3 from, Vector3 to) => Mathf.Atan2(-(to.X - from.X), -(to.Z - from.Z));

    /// <summary>The unit vector a yaw points along.</summary>
    public static Vector3 Forward(float yaw) => new Basis(Vector3.Up, yaw) * Vector3.Forward;

    /// <summary>
    /// Off whatever the player rides. Getting out leaves the vehicle parked where it stands; that one is
    /// the scenario's too, so it goes with the rest.
    /// </summary>
    private async Task Dismount(FootPlayer me)
    {
        me.RideControls = null;
        if (me.Ride == RideKind.OnFoot) return;
        var before = Vehicles?.GetChildren().OfType<VehicleBody>().ToHashSet() ?? [];
        me.ExitVehicle();
        if (me.Ride != RideKind.OnFoot) me.SetRide(RideKind.OnFoot);
        await Wait(0.1);
        if (Vehicles is { } vehicles)
            foreach (var left in vehicles.GetChildren().OfType<VehicleBody>().Where(v => !before.Contains(v)))
                left.QueueFree();
    }

    /// <summary>Makes sure there is a player and that it stands on its feet, out of any vehicle.</summary>
    public async Task<FootPlayer> OnFoot()
    {
        for (int i = 0; i < 600; i++)
        {
            var me = _local();
            if (me != null)
            {
                await Dismount(me);
                if (me.Ride == RideKind.OnFoot && me.IsOnFloor()) return me;
                // still in the air after a second (the last scenario flew, or jumped out up there): back on the stage
                if (i % 10 == 9)
                {
                    if (me.Ride != RideKind.OnFoot) me.SetRide(RideKind.OnFoot);
                    var at = _stageSet ? Stage : me.GlobalPosition;
                    me.DebugLaunch(at with { Y = Ground(at) + 0.5f }, Vector3.Zero);
                }
            }
            // offline the world starts on the free camera: the player comes with the mode key (as ExitProbe does)
            else if (i % 50 == 10)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        throw new InvalidOperationException("the player never came down on its feet");
    }

    /// <summary>The player on foot at <paramref name="at"/>, facing <paramref name="yaw"/>.</summary>
    public async Task<FootPlayer> PutPlayer(Vector3 at, float yaw)
    {
        var me = await OnFoot();
        me.PlaceAt(at + Vector3.Up * 0.3f, yaw);
        me.Velocity = Vector3.Zero;
        for (int i = 0; i < 50 && !me.IsOnFloor(); i++) await Wait(0.1);
        return me;
    }

    /// <summary>The player at the wheel (or on the saddle) of a fresh <paramref name="kind"/> at <paramref name="at"/>, engine running.</summary>
    public async Task<FootPlayer> Drive(RideKind kind, Vector3 at, float yaw)
    {
        var me = await PutPlayer(at, yaw);
        if (!me.SetRide(kind)) throw new InvalidOperationException($"could not get on {Label(kind)} here");
        await Wait(0.3);
        me.PlaceAt(at + Vector3.Up * 0.5f, yaw);
        return me;
    }

    /// <summary>
    /// A vehicle standing (or rolling, with <paramref name="kmh"/>) at <paramref name="at"/>, facing
    /// <paramref name="yaw"/>. Driverless: given a speed it coasts and slows down (about 3 m/s² on
    /// the ground), so put a rolling one close to what it should hit. Returns the body once it is in the world.
    /// </summary>
    public async Task<VehicleBody> Place(RideKind kind, Vector3 at, float yaw, float kmh = 0f)
    {
        if (Vehicles is not { } vehicles || (Player?.Origin ?? vehicles.Origin) is not { } origin)
            throw new InvalidOperationException("no vehicle manager in this world");
        var ride = Rideable.Create(kind) ?? throw new ArgumentException($"no ride {kind}");
        var velocity = Forward(yaw) * (kmh / 3.6f);
        var state = new VehicleState(kind, origin.ToGlobal(at with { Y = Ground(at) }), yaw, velocity, ride.MaxHealth,
            EngineOn: kmh > 0, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
        string name = vehicles.Place(state, $"playtest_{++_serial}_{(int)kind}")
            ?? throw new InvalidOperationException($"{ride.Label} was refused");
        _vehicles.Add(name);
        // Posed is only ever set by the bodies whose frame stands on its own (trucks, buses, aircraft, boats):
        // wait for it a moment, for a walkable deck, then take a car as it is
        for (int i = 0; i < 100; i++)
        {
            if (vehicles.GetNodeOrNull<VehicleBody>(name) is { } body && (body.Posed || i >= 20)) return body;
            await Wait(0.05);
        }
        throw new InvalidOperationException($"{ride.Label} never appeared");
    }

    /// <summary>Adds a node to the world for this scenario only.</summary>
    public T Own<T>(T node) where T : Node
    {
        // into the world (the director's parent), with the vehicles and the player
        (_host.GetParent() ?? _host).AddChild(node);
        _nodes.Add(node);
        return node;
    }

    public static string Label(RideKind kind) => Rideable.Create(kind)?.Label ?? kind.ToString();

    /// <summary>Removes what the last scenario placed and puts the player back on foot.</summary>
    internal async Task Clear()
    {
        if (Player is { } me) await Dismount(me);
        if (Vehicles is { } vehicles)
            foreach (string name in _vehicles)
                vehicles.GetNodeOrNull<VehicleBody>(name)?.QueueFree();
        foreach (var node in _nodes)
            if (GodotObject.IsInstanceValid(node)) node.QueueFree();
        _vehicles.Clear();
        _nodes.Clear();
    }

    /// <summary>The vehicles the current scenario placed that are still in the world.</summary>
    public IEnumerable<VehicleBody> Placed() =>
        Vehicles is { } vehicles ? _vehicles.Select(n => vehicles.GetNodeOrNull<VehicleBody>(n)).OfType<VehicleBody>() : [];
}
