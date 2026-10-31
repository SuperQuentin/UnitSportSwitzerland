using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Playtest.Scenarios;

/// <summary>
/// Recently merged features that only a player can sign off (#751): the category is the PR. Covers
/// are the PR's own files, so the verdict holds until that feature's code changes again. Add one
/// per player-facing PR whose look or feel no check can judge.
/// </summary>
public static class FeatureScenarios
{
    [PlaytestScenario("pr749-dance-page", "Emote wheel: the dance page", "PR #749")]
    private static PlaytestScenario Dances() => new()
    {
        Instructions = $"Hold {InputHints.Label(PlayerInput.EmoteWheel)} for the emote wheel, turn to its fourth page and play every dance. Watch from the third-person camera.",
        Checklist =
        [
            "Every dance on page 4 plays and loops cleanly",
            "No limb pops or bends the wrong way",
            "A tap stops the dance; moving stops it too",
        ],
        Covers = ["src/Player/EmoteWheel.cs", "src/Avatar/HumanMeshBuilder.Emotes.cs", "src/Avatar/HumanMeshBuilder.MoreMoves.cs",
            "src/Avatar/HumanMeshBuilder.Break.cs", "src/Avatar/DancePick.cs", "src/Avatar/Face/DanceFace.cs"],
        Setup = async c => await c.PutPlayer(c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("pr726-clothing-patterns", "Clothing patterns stay on the cloth", "PR #726")]
    private static PlaytestScenario Clothing() => new()
    {
        Instructions = "In third person, run, jump, crouch and dance. Look closely at the patterned clothes (stripes, checks, prints).",
        Checklist = ["Patterns move with the cloth, never slide across it", "No pattern flickers when a limb bends"],
        Covers = ["src/Avatar/HumanMeshBuilder.Body.cs", "src/Avatar/HumanMeshBuilder.Clothing.cs", "src/Avatar/Garments.cs"],
        Setup = async c => await c.PutPlayer(c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("pr722-go-kart", "Go-kart: the rental kart", "PR #722")]
    private static PlaytestScenario Kart() => new()
    {
        Instructions = "Drive the 270 cc rental kart: full throttle, tight slaloms, and a spin.",
        Checklist = ["It feels light and darty, not like a car", "The engine sound is a small single", "The driver sits low and in proportion"],
        Covers = ["src/Avatar/KartMeshBuilder.cs", "src/Player/CarCatalog.cs", "src/Avatar/CarRig.cs"],
        Setup = async c => await c.Drive(CarCatalog.Kart.Kind, c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("pr723-army-vehicles", "Swiss Army vehicles", "PR #723")]
    private static PlaytestScenario Army() => new()
    {
        Instructions = "You drive the Mowag Duro; the G 300 and the Trakker stand on your right. Drive each one (get out, walk over, get in) off-road.",
        Checklist =
        [
            "Each feels heavy in its own way: the G nimble, the Trakker slow",
            "Doors, seats and the canvas bed line up with the bodies",
            "Matt olive, no insignia, black tyres and bumpers",
        ],
        Covers = ["src/Avatar/ArmyMeshBuilder.cs", "src/Player/HeavyCatalog.cs", "src/Avatar/HeavyRig.cs", "src/Avatar/TruckMeshBuilder.cs"],
        Setup = async c =>
        {
            await c.Place(CollisionScenarios.Heavy("Mercedes-Benz G"), c.At(0, 8), c.Heading(0));
            await c.Place(CollisionScenarios.Heavy("Iveco Trakker"), c.At(0, 16), c.Heading(0));
            await c.Drive(CollisionScenarios.Heavy("Mowag Duro"), c.At(0), c.Heading(0));
        },
    };

    [PlaytestScenario("pr693-mini-dumper", "Tracked mini dumper", "PR #693")]
    private static PlaytestScenario MiniDumper() => new()
    {
        Instructions = "Drive the tracked mini dumper, turn on the spot, and tip its skip forward and back.",
        Checklist = ["The tracks counter-rotate when turning on the spot", "The skip tips smoothly to its stop", "The controls card explains every key"],
        Covers = ["src/Player/MiniDumper.cs", "src/Avatar/MiniDumperLayout.cs", "src/Avatar/MiniDumperMeshBuilder.cs"],
        Setup = async c => await c.Drive(RideKind.MiniDumper, c.At(0), c.Heading(0)),
    };

    [PlaytestScenario("pr691-tipper-mixer", "Site trucks: tipper and mixer", "PR #691")]
    private static PlaytestScenario SiteTrucks() => new()
    {
        Instructions = "Drive the tipper and raise its body; then take the concrete mixer parked on your right and turn its drum.",
        Checklist = ["The tipper body lifts with its tailgate swinging", "The mixer drum turns, faster when discharging", "Both feel loaded and heavy"],
        Covers = ["src/Avatar/TruckMeshBuilder.cs", "src/Player/Truck.cs", "src/Player/HeavyCatalog.cs"],
        Setup = async c =>
        {
            await c.Place(CollisionScenarios.Heavy("Arocs 3240"), c.At(0, 8), c.Heading(0));
            await c.Drive(CollisionScenarios.Heavy("Arocs 3245"), c.At(0), c.Heading(0));
        },
    };
    /// <summary>Played at Trois Verres, Geneva: <c>--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499794.3,1117466.5</c>.</summary>
    [PlaytestScenario("pr1327-venue-own-door", "Venues and shops: their own door and a glazed front", "PR #1327")]
    private static PlaytestScenario VenueOwnDoor() => new()
    {
        Instructions = "Launched at Trois Verres (Geneva): its door is ahead of you. Look at its front, walk in by its own door, "
            + "then walk the streets round it past the shops under the blocks of flats. Type /time set 22:00 in the chat for the night look.",
        Checklist =
        [
            "Trois Verres: its own door among glass bays, a stone plinth, the fascia or awning with its name",
            "In by that door: straight into the restaurant, no lobby; its windows inside where the bays are outside",
            "The panes show a room behind them, tinted to the venue; warm-lit at night",
            "Shops under flats: each its own door and glass front, about 8 m apart; no facade window peeking round the glass",
            "No flicker where the frame, the fascia, a sign or an awning meet",
        ],
        Covers = ["src/Interiors/VenueFront.cs", "shaders/shop_glass.gdshader", "shaders/common/room_behind.gdshaderinc",
            "src/Interiors/VenueSigns.cs", "src/Interiors/VenueTerraces.cs"],
        // the door of Trois Verres is 10.8 m east and 5.3 m north of the launch point
        Setup = async c => await c.PutPlayer(c.At(0), PlaytestContext.Facing(c.At(0), c.At(0) + new Godot.Vector3(10.8f, 0, -5.3f))),
    };
}
