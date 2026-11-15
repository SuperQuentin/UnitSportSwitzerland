using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Bodies;
using UnitSport.Vehicles;

namespace UnitSport.World.Moon;

/// <summary>
/// The rovers parked at a staffed colony (#1943): two open rovers beside the road outside the hangar, the pressurised
/// rover inside it facing its rolling door. Each is a real vehicle put down once through <see cref="VehicleManager.Place"/>
/// under a fixed name (<c>moonrover_&lt;colony&gt;_&lt;slot&gt;</c>): offline it is simply added, online the server places it
/// (a client does nothing), and a second visit finds it there. One driven away and left elsewhere is a vehicle like any
/// other; its slot fills again the next time the colony is built near the camera. Placed only once the camera is within
/// <see cref="NearM"/> of the colony, where the Moon's ground square (with its collision) is under the slots.
/// </summary>
public static class MoonRoverDepot
{
    /// <summary>The camera's ground point must be this near the colony's centre to park its rovers, metres.</summary>
    public const double NearM = 200;

    /// <summary>A depot rover's name begins so (<c>moonrover_&lt;colony&gt;_&lt;slot&gt;</c>); parked again it keeps it (<c>VehicleManager.Kept</c>).</summary>
    public const string Prefix = "moonrover_";

    /// <summary>A hangar lane's offset from its axis, metres: a rover parks in one, the other is the way out.</summary>
    public const float HangarLane = 2.5f;

    /// <summary>A parking slot in the colony's frame (x east, y up, z south): what stands there, where, facing which way (unit, horizontal).</summary>
    public readonly record struct Slot(RideKind Kind, Vector3 At, Vector3 Facing);

    /// <summary>The slots of a staffed colony; none for the empty ones.</summary>
    public static List<Slot> Slots(MoonColony c, ColonyPlan plan)
    {
        var list = new List<Slot>();
        if (!c.Staffed || plan.Hangar is not { } h || plan.Road.Count < 2) return list;
        var (rx, rz) = plan.Road[0];
        var (ex, ez) = plan.Road[^1];
        var along = new Vector3((float)(ex - rx), 0, (float)(ez - rz)).Normalized();
        var side = Vector3.Up.Cross(along).Normalized();
        var road = new Vector3((float)rx, 0, (float)rz);
        // past the ramp, either side of the road, nose out toward the pads
        foreach (float sd in new[] { -8f, 8f })
            list.Add(new Slot(MoonRovers.Open.Kind, road + along * (ColonyKit.RampLen + 18) + side * sd, along));
        // in the hangar on its slab, facing its rolling door, in the right-hand lane (the left one stays free to drive out)
        var m = plan.Modules[h.Module];
        list.Add(new Slot(MoonRovers.Cabin.Kind, new Vector3((float)m.X, ColonyKit.HangarSlab, (float)m.Z) + side * HangarLane, along));
        return list;
    }

    /// <summary>Parks the colony's rovers that are not there yet; <paramref name="frame"/> is the colony node's world transform.</summary>
    public static int Park(MoonColony c, ColonyPlan plan, Transform3D frame, Core.WorldOrigin origin)
    {
        if (VehicleManager.Instance is not { } vehicles) return 0;
        int placed = 0;
        var slots = Slots(c, plan);
        for (int i = 0; i < slots.Count; i++)
        {
            string name = $"{Prefix}{c.Id}_{i}";
            if (vehicles.GetNodeOrNull(name) != null) continue;
            var s = slots[i];
            var world = frame * (s.At + Vector3.Up * 0.4f);
            var dir = (frame.Basis * s.Facing) with { Y = 0 };
            float yaw = Mathf.Atan2(-dir.X, -dir.Z);
            var ride = Rideable.Create(s.Kind);
            // a record keeps its heading on the LV95 grid at its place (WorldOrigin.GridYawOf), whatever the body
            var state = new VehicleState(s.Kind, origin.ToGlobal(world), origin.Frame.GridYawOf(world, yaw), Vector3.Zero, ride?.MaxHealth ?? 400f,
                EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: VehicleState.Now);
            if (vehicles.Place(state, name) != null) placed++;
        }
        return placed;
    }
}
