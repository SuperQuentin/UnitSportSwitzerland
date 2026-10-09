using Godot;
using UnitSport.Player;

namespace UnitSport.Playtest.Scenarios;

/// <summary>How the core ways of getting about feel (#751): on foot, on wheels, in the air. Feel is the one thing no probe can measure.</summary>
public static class MovementScenarios
{
    private static readonly string[] NotChecks = ["!**/*Check.cs", "!**/*Probe.cs"];

    [PlaytestScenario("move-foot", "Walk, run, jump and climb", "Movement")]
    private static PlaytestScenario Foot() => new()
    {
        Instructions = "You stand before four blocks, 0.3, 0.6, 1.0 and 1.5 m high. Walk, run and sprint around, step and jump onto each, and jump off.",
        Checklist =
        [
            "Walk and run speeds feel natural, starting and stopping included",
            "0.3 m is a step you walk up; 1.0 m needs a jump; 1.5 m is out of reach",
            "Landing from a jump looks and sounds right, with no bounce",
            "The camera stays smooth stepping up and down",
        ],
        Covers = ["src/Player/FootPlayer.cs", "src/Avatar/HumanMeshBuilder.cs", "src/Avatar/Limb.cs", .. NotChecks],
        Setup = async c =>
        {
            float[] heights = [0.3f, 0.6f, 1.0f, 1.5f];
            for (int i = 0; i < heights.Length; i++)
            {
                var size = new Vector3(2.5f, heights[i], 2.5f);
                var block = new StaticBody3D { Name = $"PlaytestStep{i}" };
                block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                block.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = size },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f + 0.1f * i, 0.5f, 0.45f) },
                });
                c.Own(block);
                block.GlobalPosition = c.At(8, (i - 1.5f) * 4f) + Vector3.Up * (heights[i] / 2f);
                block.Rotation = new Vector3(0, c.Heading(0), 0);
            }
            await c.PutPlayer(c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("move-car-hairpin", "Car down the hairpins", "Movement")]
    private static PlaytestScenario CarHairpin() => new()
    {
        Instructions = "On the hairpin course: drive the car down the 7% slope and through the 15 m hairpins. Brake late, try the handbrake.",
        Checklist =
        [
            "Steering and braking feel progressive, not twitchy",
            "Downhill the car gains speed believably; engine braking works",
            "In the hairpins it understeers before it spins",
            "Tyre sounds follow the grip",
        ],
        Covers = ["src/Player/Car.cs", "src/Player/CarCatalog.cs", "src/Player/Rideable.cs", "src/Core/SteeringWheel.cs"],
        Course = "hairpin",
        Setup = async c =>
        {
            var start = c.Course(0, 0);
            await c.Drive(CollisionScenarios.Car, start, PlaytestContext.Facing(start, c.Course(100, -14)));
        },
    };

    [PlaytestScenario("move-artic-bus-junction", "Articulated bus through the junctions", "Movement")]
    private static PlaytestScenario ArticBus() => new()
    {
        Instructions = "On the junction course: drive the articulated bus east, turn left at the T junction (700 m), come back, then turn right at the crossroads (1400 m).",
        Checklist =
        [
            "The rear section tracks inside the front through the turns",
            "The turns are possible within the road and its kerbs",
            "Reversing with the joint is hard but controllable",
        ],
        Covers = ["src/Player/Truck.cs", "src/Player/HeavyTrain.cs", "src/Player/HeavyDriveline.cs", "src/Player/HeavyCatalog.cs", "src/Avatar/HeavyRig.cs"],
        Course = "junction",
        Setup = async c =>
        {
            var start = c.Course(20, 0);
            await c.Drive(CollisionScenarios.Heavy("Citaro G"), start, PlaytestContext.Facing(start, c.Course(200, 0)));
        },
    };

    [PlaytestScenario("move-motorbike", "Motorbike: lean, brake, wheelie", "Movement")]
    private static PlaytestScenario Moto() => new()
    {
        Instructions = "Ride the superbike: accelerate hard, lean into long and tight turns, brake from 150 km/h.",
        Checklist = ["The bike leans into turns, more with speed", "Hard acceleration lifts the front, no flip", "Braking hard dips the front, no instant stop"],
        Covers = ["src/Player/Motorbike.cs", "src/Player/MotorbikeCatalog*.cs", "src/Avatar/MotorbikeMeshBuilder.cs", .. NotChecks],
        Setup = async c => await c.Drive(MotorbikeCatalog.All[0].Kind, c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("move-road-bike", "Road bike: pedal and coast", "Movement")]
    private static PlaytestScenario RoadBike() => new()
    {
        Instructions = "Ride the road bike: pedal up to speed, coast, turn and brake.",
        Checklist = ["Pedalling cadence matches the speed", "Coasting slows down gradually", "The rider leans with the bike"],
        Covers = ["src/Player/Bicycle.cs", "src/Avatar/BikeMeshBuilder.cs", "src/Avatar/Cyclist.cs", .. NotChecks],
        Setup = async c => await c.Drive(RideKind.RoadBike, c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("move-plane", "Light plane: fly and land", "Movement")]
    private static PlaytestScenario Plane() => new()
    {
        Instructions = "You start flying the light plane at 300 m, 180 km/h. Climb, turn, stall it once, then land on open ground.",
        Checklist =
        [
            "Pitch, roll and yaw answer smoothly",
            "A stall drops the nose, and recovering from it works",
            "The landing is possible and forgiving at a gentle sink rate",
        ],
        Covers = ["src/Player/Flight.cs", "src/Avatar/AircraftMeshBuilder.cs", "src/Avatar/AircraftCockpit.cs", .. NotChecks],
        Setup = async c =>
        {
            var me = await c.Drive(RideKind.Plane, c.At(0), c.Heading(0));
            me.DebugLaunch(c.At(0) + Vector3.Up * 300f, PlaytestContext.Forward(c.Heading(0)) * 50f);
        },
    };

    [PlaytestScenario("move-helicopter", "Helicopter: hover and land", "Movement")]
    private static PlaytestScenario Heli() => new()
    {
        Instructions = "Take off in the helicopter, hold a hover at 10 m, fly forward, then land softly where you started.",
        Checklist = ["A hover can be held without fighting it", "Forward flight tilts the nose down", "Touchdown is gentle at low sink"],
        Covers = ["src/Player/Flight.cs", "src/Avatar/AircraftMeshBuilder.cs", .. NotChecks],
        Setup = async c => await c.Drive(RideKind.Helicopter, c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("move-paraglider", "Paraglider from 400 m", "Movement")]
    private static PlaytestScenario Paraglider() => new()
    {
        Instructions = "You hang under a paraglider 400 m up. Turn both ways, speed up, and land on the field.",
        Checklist = ["The glide is slow and steady", "Turns bank the wing", "Landing flares instead of crashing"],
        Covers = ["src/Player/Flight.cs", .. NotChecks],
        Setup = async c =>
        {
            var me = await c.Drive(RideKind.Paraglider, c.At(0), c.Heading(0));
            me.DebugLaunch(c.At(0) + Vector3.Up * 400f, PlaytestContext.Forward(c.Heading(0)) * 10f);
        },
    };
}
