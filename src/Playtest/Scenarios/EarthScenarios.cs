using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Playtest.Scenarios;

/// <summary>
/// The earth world (G2, #1500) as a player meets it: the <c>--frame earth</c> sections of
/// <c>tests/TO-TEST-BY-HUMAN.md</c> turned into scenarios, played offline on the region's built leaves
/// (B4) on D:. Each relaunches the game at its place with its leaves (<see cref="PlaytestScenario.Flags"/>);
/// the date is always given, so a winter scenario never leaves snow on the next one. The online rows
/// (two clients, bots, the generated world's sites) stay in the list: the playtest server runs offline.
/// </summary>
public static class EarthScenarios
{
    private const string Category = "Earth world (G2)";
    private const string B4Leaves = "D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube";
    private const string Row7Leaves = "D:/UnitSportSwitzerland/test_b3/row7/cube";
    private const string Chunks = "D:/UnitSportSwitzerland/terrain_chunks";
    private const string B4Chunks = "D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks";
    private const string Summer = "2027-07-15";
    private const string Winter = "2027-01-15";

    private static readonly string[] Earth = ["src/Terrain/Earth/**", "src/Terrain/Leaves/**", "!**/*Check.cs", "!**/*Probe.cs"];

    private static string[] At(string leaves, string chunks, string at, string date) =>
        ["--frame", "earth", "--leaves", leaves, "--chunks", chunks, "--at", at, "--date", date];

    private static string Key(string action) => InputHints.Label(action);

    /// <summary>On foot where the launch put the player, or at the wheel of a car there.</summary>
    private static async Task Here(PlaytestContext c, bool car = false)
    {
        var p = await c.OnFoot();
        if (car) await c.Drive(CollisionScenarios.Car, p.GlobalPosition, p.Rotation.Y);
    }

    [PlaytestScenario("earth-1-geneva-streets", "Geneva's streets on leaves", Category)]
    private static PlaytestScenario GenevaStreets() => new()
    {
        Instructions = "Geneva's centre on the earth world. Walk the streets for a minute, then drive the car you sit in "
            + "through them for a few km.",
        Checklist =
        [
            "Roads, paint, signals, buildings, trees and the lake are drawn as in the flat world",
            "You stand and drive on the roads, never sunk into them",
            "The ground never shows above a road; no holes, seams or steps where leaves meet",
        ],
        Covers = [.. Earth, "src/Terrain/Earth/EarthWorld.Furnish.cs"],
        Flags = At(B4Leaves, Chunks, "2500956,1117569", Summer),
        Setup = async c => await Here(c, car: true),
    };

    [PlaytestScenario("earth-2-geneva-doors", "Doors, garages and stoops on leaves", Category)]
    private static PlaytestScenario GenevaDoors() => new()
    {
        Instructions = "Walk to a house's door and press E (try the pad too), go in, come out. Then find a garage door "
            + "with an access road and a door with a stoop: walk up the steps, and drive up the access road "
            + "(Quick menu > Vehicles for a car).",
        Checklist =
        [
            "The door opens, the interior stands behind it, the way out lands on the street",
            "The steps of a stoop and the access road both collide: you climb and drive up them",
        ],
        Covers = [.. Earth, "src/Interiors/InteriorLayout.cs"],
        Flags = At(B4Leaves, Chunks, "2500956,1117569", Summer),
        Setup = async c => await Here(c),
    };

    [PlaytestScenario("earth-3-reanchor-drive", "Driving across a re-anchor", Category)]
    private static PlaytestScenario ReanchorDrive() => new()
    {
        Instructions = "The ground frame re-anchors every 3 km. Drive the car north-east for 4 km out of Geneva "
            + "(the log prints [origin] shift at each one), then get out and walk a little.",
        Checklist =
        [
            "No jump or hitch in the view or the steering at the re-anchor",
            "The car and the body stay on the ground, the horizon stays level",
            "Houses, trees and signs keep their places across it",
        ],
        Covers = [.. Earth, "src/Core/OriginShifter.cs"],
        Flags = At(B4Leaves, Chunks, "2500956,1117569", Summer),
        Setup = async c => await Here(c, car: true),
    };

    [PlaytestScenario("earth-4-reanchor-flight", "Flying high across re-anchors", Category)]
    private static PlaytestScenario ReanchorFlight() => new()
    {
        Instructions = $"Press {Key(PlayerInput.Spectator)} for the free camera, climb to about 2 km and fly 6 km or more "
            + "(two re-anchors), looking at the far ground; then fly 3 km away low over the town and back.",
        Checklist =
        [
            "The far ground falls away smoothly, no step at a re-anchor",
            "The furniture (houses, trees, signs) follows the re-anchor without a jump",
            "The globe beyond the region meets the leaves without a gap or a wall",
        ],
        Covers = [.. Earth, "src/Core/OriginShifter.cs"],
        Flags = At(B4Leaves, Chunks, "2500956,1117569", Summer),
        Setup = async c => await Here(c),
    };

    [PlaytestScenario("earth-5-ground-look", "The ground and the lake's look", Category)]
    private static PlaytestScenario GroundLook() => new()
    {
        Instructions = $"At the Jardin Anglais. Walk the lawns and the Rhône's banks; then press {Key(PlayerInput.Spectator)} "
            + "and fly about 150 m over the lake for 5 km. Cartoon only.",
        Checklist =
        [
            "Grass, paving and stone banks look as in the flat world, never pale mint or white",
            "No straight light lines across the water at the leaves' joins",
            "No brighter square of water with a straight edge (seen from 600 m at the start)",
        ],
        Covers = [.. Earth, "shaders/cartoon_terrain.gdshader", "shaders/cartoon_water.gdshader"],
        Flags = At(B4Leaves, Chunks, "2500650,1117640", Summer),
        Setup = async c => await Here(c),
    };

    [PlaytestScenario("earth-6-nyon-pier", "Nyon's pier and jetties", Category)]
    private static PlaytestScenario NyonPier() => new()
    {
        Instructions = "At Nyon. Walk out along the footbridge to the steamer landing (the pier head); then take a speedboat (Quick menu > Vehicles), "
            + "drive it along Port de Nyon's jetties and step out onto one.",
        Checklist =
        [
            "The pier and the jetty decks are drawn and solid, as on flat",
            "Nothing falls through the footbridge or a deck",
            "The boat floats at the water's level beside the decks",
        ],
        Covers = [.. Earth],
        Flags = At(B4Leaves, B4Chunks, "2507900,1137600", Summer),
        Setup = async c => await Here(c),
    };

    [PlaytestScenario("earth-7-region-edge", "The region's edge and the fill", Category)]
    private static PlaytestScenario RegionEdge() => new()
    {
        Instructions = "North of Riddes, on the slope where the built region ends and the generated fill begins. "
            + "Walk across into the real ground and back, then drive across (Quick menu > Vehicles). "
            + $"Then press {Key(PlayerInput.Spectator)} and fly 10 km out over the fill.",
        Checklist =
        [
            "No step, ledge or hole where the generated ground meets the real one (a bank of a few metres over ~60 m is the hand-over)",
            "Over the fill the ground keeps streaming under you (each generated leaf takes ~2 s)",
            "No real relief stands up through the generated ground",
        ],
        Covers = [.. Earth, "src/Terrain/Earth/GameLeaves.cs"],
        Flags = At(B4Leaves, Chunks, "2578113,1115406", Summer),
        Setup = async c => await Here(c),
    };

    [PlaytestScenario("earth-8-tzoumaz-skis", "Skiing at La Tzoumaz on leaves", Category)]
    private static PlaytestScenario TzoumazSkis() => new()
    {
        Instructions = "La Tzoumaz in January, on skis. Ski down a run; open the ski map; ski across where two leaves meet.",
        Checklist =
        [
            "The HUD names the run; the snow grips as corduroy or ice as on flat",
            "Poles line both edges; none stands doubled where two leaves meet; the corduroy has no seam at a leaf's edge",
            "The ski map shows the runs round you, north up, with no seam where leaves meet",
        ],
        Covers = [.. Earth, "src/World/Snow/PisteLayer.cs", "src/World/Snow/PisteIndex.cs", "src/World/Snow/PisteCourse.cs"],
        Flags = At(Row7Leaves, B4Chunks, "2583954,1109157", Winter),
        Setup = async c =>
        {
            var p = await c.OnFoot();
            await c.Drive(RideKind.Skis, p.GlobalPosition, p.Rotation.Y);
        },
    };
}
