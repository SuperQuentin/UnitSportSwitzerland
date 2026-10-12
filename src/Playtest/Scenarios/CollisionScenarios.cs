using Godot;
using UnitSport.Player;

namespace UnitSport.Playtest.Scenarios;

/// <summary>
/// Crashes only a person can judge (#751): whether a hit looks and feels right. The automated
/// collision matrix checks that bodies collide at all; these check how it plays. Most run on any
/// open ground, built ahead of the stage (the spot the session started on); the junction ones need
/// that fixture course. The player drives the moving vehicle, so every run is a real hit.
/// </summary>
public static class CollisionScenarios
{
    /// <summary>The files every vehicle crash goes through: the bodies, their collision, damage and the shared ride code.</summary>
    private static readonly string[] Crash =
    [
        "src/Vehicles/VehicleBody*.cs", "src/Vehicles/VehicleManager.cs", "src/Vehicles/Explosion.cs",
        "src/Player/FootPlayer.Crash.cs", "src/Player/Rideable.cs",
        // the automated checks and probes beside them are not what the player judges
        "!**/*Check.cs", "!**/*Probe.cs",
    ];

    private static string[] With(params string[] more) => [.. Crash, .. more];

    internal static RideKind Car => CarCatalog.All[0].Kind;
    internal static RideKind Heavy(string label) => HeavyCatalog.All.First(h => h.Label.StartsWith(label, StringComparison.Ordinal)).Kind;
    internal static RideKind CityBus => HeavyCatalog.All.First(h => h.Class == HeavyClass.CityBus).Kind;

    [PlaytestScenario("collide-car-parked", "Car into a parked car", "Collisions")]
    private static PlaytestScenario CarIntoParked() => new()
    {
        Instructions = "You sit in a car with a parked car 60 m ahead. Drive into its back at about 30 km/h, then try 60 and 100 (Reset puts it back).",
        Checklist =
        [
            "Both cars react with plausible force: no launch into the air, no sinking into the ground",
            "No car passes through the other",
            "Damage shows on both, and grows with speed",
            "The impact sound fits the speed",
        ],
        Covers = With("src/Player/Car.cs", "src/Player/CarCatalog.cs"),
        Setup = async c =>
        {
            await c.Place(Car, c.At(60), c.Heading(0));
            await c.Drive(Car, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-car-bus-side", "Car into the side of a bus", "Collisions")]
    private static PlaytestScenario CarIntoBus() => new()
    {
        Instructions = "A city bus stands across your road 70 m ahead. T-bone it with the car at different speeds.",
        Checklist =
        [
            "The bus barely moves; the car stops hard or bounces back",
            "The car does not climb onto the bus or slip under it",
            "Getting out after the crash works",
        ],
        Covers = With("src/Player/Car.cs", "src/Player/CarCatalog.cs", "src/Player/Truck*.cs", "src/Player/Heavy*.cs"),
        Setup = async c =>
        {
            await c.Place(CityBus, c.At(70), c.Heading(90));
            await c.Drive(Car, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-bus-into-cars", "Bus through a row of parked cars", "Collisions")]
    private static PlaytestScenario BusIntoCars() => new()
    {
        Instructions = "You drive a city bus. Three cars stand across the road 80 m ahead, side by side. Push through them.",
        Checklist =
        [
            "The cars get shoved aside, not launched",
            "The bus slows noticeably for each car",
            "No car ends up inside the bus",
        ],
        Covers = With("src/Player/Truck*.cs", "src/Player/Heavy*.cs"),
        Setup = async c =>
        {
            for (int i = -1; i <= 1; i++) await c.Place(Car, c.At(80, i * 2.6f), c.Heading(90));
            await c.Drive(CityBus, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-pileup", "Truck into the back of a queue", "Collisions")]
    private static PlaytestScenario Pileup() => new()
    {
        Instructions = "Five cars queue nose to tail 100 m ahead. Hit the last one with the rigid truck at 50 km/h.",
        Checklist =
        [
            "The push goes down the queue, car by car",
            "Nothing jitters or explodes apart while the cars are squeezed together",
            "The frame rate holds during the pile-up",
        ],
        Covers = With("src/Player/Truck*.cs", "src/Player/Heavy*.cs"),
        Setup = async c =>
        {
            for (int i = 0; i < 5; i++) await c.Place(Car, c.At(100 + i * 5.2f), c.Heading(0));
            await c.Drive(Heavy("MAN"), c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-headon-truck", "Car head-on into a truck", "Collisions")]
    private static PlaytestScenario HeadOn() => new()
    {
        Instructions = "A parked tipper faces you 80 m ahead. Drive the car into its front.",
        Checklist = ["The car crumples and stops dead", "The truck stays put", "The camera does not go inside either vehicle"],
        Covers = With("src/Player/Car.cs", "src/Player/CarCatalog.cs", "src/Player/Truck*.cs"),
        Setup = async c =>
        {
            await c.Place(Heavy("Arocs 3245"), c.At(80), c.Heading(180));
            await c.Drive(Car, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-wall", "Car into a concrete wall", "Collisions")]
    private static PlaytestScenario Wall() => new()
    {
        Instructions = "A concrete wall stands 60 m ahead. Hit it straight, then at an angle, at different speeds.",
        Checklist =
        [
            "A straight hit stops the car; an angled one scrapes along the wall",
            "The car never goes through, even at full speed",
            "It does not stick to the wall afterwards",
        ],
        Covers = With("src/Player/Car.cs", "src/Player/CarCatalog.cs"),
        Params = new() { ["thickness"] = 0.6, ["length"] = 24 },
        Setup = async c =>
        {
            float thick = (float)c.Param("thickness"), length = (float)c.Param("length");
            var at = c.At(60);
            var wall = new StaticBody3D { Name = "PlaytestWall" };
            var size = new Vector3(length, 3f, thick);
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            wall.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.62f, 0.6f) },
            });
            c.Own(wall);
            wall.GlobalPosition = at + Vector3.Up * 1.5f;
            wall.Rotation = new Vector3(0, c.Heading(0), 0);
            await c.Drive(Car, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-bus-hits-walker", "Rolling bus into a person on foot", "Collisions")]
    private static PlaytestScenario BusHitsWalker() => new()
    {
        Instructions = "You stand on foot. A driverless city bus rolls at you from the left. Stand still and get hit (Reset sends it again; Claude can change its speed).",
        Checklist =
        [
            "You get knocked over (ragdoll), not teleported or stuck inside the bus",
            "Health drops in line with the speed",
            "Getting up afterwards works",
        ],
        Covers = With("src/Player/Ragdoll*.cs", "src/Combat/**"),
        Params = new() { ["kmh"] = 35, ["distance"] = 14 },
        Setup = async c =>
        {
            var me = await c.PutPlayer(c.At(0), c.Heading(0));
            float d = (float)c.Param("distance");
            await c.Place(CityBus, c.At(0, -d), c.Heading(90), (float)c.Param("kmh"));
        },
    };

    [PlaytestScenario("collide-moto-car", "Motorbike into the side of a car", "Collisions")]
    private static PlaytestScenario MotoIntoCar() => new()
    {
        Instructions = "A car stands across your road 60 m ahead. Ride the motorbike into its side.",
        Checklist = ["You fly off the bike", "The bike falls over rather than standing", "The car gets nudged, not launched"],
        Covers = With("src/Player/Motorbike*.cs", "src/Player/Ragdoll*.cs"),
        Setup = async c =>
        {
            await c.Place(Car, c.At(60), c.Heading(90));
            await c.Drive(MotorbikeCatalog.All[0].Kind, c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("collide-bus-bus-junction", "Bus into a bus at a crossroads", "Collisions")]
    private static PlaytestScenario BusBusJunction() => new()
    {
        Instructions = "On the junction course: a city bus stands in the middle of the crossroads. Come up the south side road in an articulated bus and hit it.",
        Checklist =
        [
            "Two heavy bodies collide with weight: slow pushes, no bouncing",
            "The articulated bus's rear section follows and folds sensibly",
            "The crossroads' kerbs and islands do not throw either bus",
        ],
        Covers = With("src/Player/Truck*.cs", "src/Player/Heavy*.cs"),
        Course = "junction",
        Setup = async c =>
        {
            var centre = c.Course(1400, 0);
            await c.Place(CityBus, centre, PlaytestContext.Facing(c.Course(1300, 0), centre));
            var start = c.Course(1400, -120);
            await c.Drive(Heavy("Citaro G"), start, PlaytestContext.Facing(start, centre));
        },
    };
}
