using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The colony modules' insides in the model viewer ("Moon rooms", #1941, #2000): each room kind in a 12 m can, a can with a
/// side doorway, the hub's control room, a greenhouse dome, the hangar, a tube; the kit's shell round the interior the
/// interiors system plans, furnishes and draws (<see cref="InteriorGenerator.Colony"/>), as <see cref="ColonyBuilder"/>
/// builds them.
/// </summary>
public static class ColonyRooms
{
    [Showcase("Moon rooms")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Rooms()
    {
        foreach (RoomKind r in Enum.GetValues<RoomKind>())
        {
            if (r is RoomKind.Hall or RoomKind.Control or RoomKind.Garage or RoomKind.Greenhouse) continue;
            var rr = r;
            var kind = r == RoomKind.Airlock ? ModuleKind.Airlock : ModuleKind.Can;
            double len = r == RoomKind.Airlock ? 4 : 12;
            yield return ($"Can: {r}", () => Show(kind, rr, len, 4.6, 5.5));
        }
        // a can whose side a dome's tube comes in at (#1976): the furniture clear of the doorway
        yield return ("Can: quarters, side door", () => Show(ModuleKind.Can, RoomKind.Quarters, 12, 4.6, 5.5, 1));
        yield return ("Can: lab, side door", () => Show(ModuleKind.Can, RoomKind.Lab, 12, 4.6, 5.5, 1));
        yield return ("Hub: control", () => Show(ModuleKind.Hub, RoomKind.Control, 10, 10, 7.5, storeys: 2));
        yield return ("Stack: quarters", () => Show(ModuleKind.Stack, RoomKind.Quarters, 9, 9, 10.5, storeys: 3));
        yield return ("Dome: greenhouse", () => Show(ModuleKind.Dome, RoomKind.Greenhouse, 18, 18, 9));
        yield return ("Dome: storage", () => Show(ModuleKind.Dome, RoomKind.Storage, 18, 18, 9));
        yield return ("Hangar: garage", () => Show(ModuleKind.Hangar, RoomKind.Garage, 26, 14, 8));
        yield return ("Tube", () => Tube(9));
    }

    private static Node3D Show(ModuleKind kind, RoomKind room, double l, double w, double h, int sideDoor = 0, int storeys = 1)
    {
        var root = new Node3D();
        root.AddChild(ColonyKit.Instance(ColonyKit.Module(kind, l, w, h, storeys, ColonyTheme.Artemis, sideDoor)));
        var m = new ColonyModule(0, kind, 0, 0, 0, l, w, h, storeys);
        var layout = InteriorGenerator.Colony(m, room, ColonyTheme.Artemis, sideDoor, "showcase");
        root.AddChild(InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior),
            new Transform3D(Basis.Identity, new Vector3(0, InteriorGenerator.FloorOf(kind), 0))));
        return root;
    }

    private static Node3D Tube(float length)
    {
        var root = new Node3D();
        var a = new Vector3(0, 0, -length / 2);
        var b = new Vector3(0, 0, length / 2);
        root.AddChild(ColonyKit.Instance(ColonyKit.Links([(a, b, false)], ColonyTheme.Artemis)));
        var layout = InteriorGenerator.ColonyLink(length, false, ColonyTheme.Artemis, "showcase");
        root.AddChild(InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior),
            new Transform3D(Basis.Identity, new Vector3(0, ColonyKit.FloorY + InteriorGenerator.ColonyFloorLift, 0))));
        return root;
    }
}
