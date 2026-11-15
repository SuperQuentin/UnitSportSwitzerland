using System;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

public sealed partial class ColonyPlan
{
    /// <summary>
    /// The sealed airlock of the abandoned colony (#1944): a module set into the pit's wall at the mouth of the lava tube, its
    /// outer door shut for good. Null elsewhere.
    /// </summary>
    public int? Sealed { get; private set; }

    /// <summary>The stair tower up the pit's wall to its rim (#1944): its foot's centre and the way its bridge leaves (radians from north).</summary>
    public (double X, double Z, double Yaw)? Tower { get; private set; }

    /// <summary>The stair tower's footprint half side, metres.</summary>
    public const double TowerHalf = 3;

    /// <summary>
    /// The abandoned colony on its pit's floor (#1944, docs/plans/moon-surface.md section 4): a compact plan inside the floor's
    /// radius, short tubes (3 m). A hub; along the heading a lab can, then a 13 m tube to an airlock set into the pit's wall at
    /// the lava tube's mouth, sealed (<see cref="Sealed"/>); a quarters can and the airlock the crew used (its doors gone: no
    /// air anywhere now) with the stair tower up the wall beyond it (<see cref="Tower"/>); a galley can whose end is open; a
    /// moondust dome off the hub. No pads, no road, no industry.
    /// </summary>
    /// <summary>
    /// The Cold War base's outdoor works (#1944): the big radar dish on its pedestal turning, three lattice radio masts, and
    /// the quarry with its excavators parked for good and their spoil heaps. Each only where it is clear of every module.
    /// </summary>
    private void ColdWarIndustry(double h0, Random rng)
    {
        void Try(IndustryKind k, double yaw, double dist, double size, double facing)
        {
            var (x, z) = Along(yaw);
            if (Clear(x * dist, z * dist, ColonyPlan.IndustryReach(new ColonyIndustry(k, 0, 0, 0, size)) + 4))
                Industry.Add(new ColonyIndustry(k, x * dist, z * dist, facing, size));
        }
        Try(IndustryKind.Radar, h0 + 3 * Math.PI / 4, 120, 18, h0);
        for (int i = 0; i < 3; i++) Try(IndustryKind.Mast, h0 + Math.PI / 4 + (i - 1) * 0.35, 95 + 25 * i, 30, h0);
        double quarry = h0 + 5 * Math.PI / 4 + (rng.NextDouble() - 0.5) * 0.2;
        for (int i = 0; i < 2; i++) Try(IndustryKind.Digger, quarry + (i - 0.5) * 0.25, 230, 0, quarry + 1.2 + i);
        for (int i = 0; i < 3; i++) Try(IndustryKind.SpoilHeap, quarry + (i - 1) * 0.3, 270 + 10 * (i % 2), 9 + 3 * i, quarry);
    }

    private static ColonyPlan Pit(MoonColony c)
    {
        var plan = new ColonyPlan();
        double h0 = c.Heading * Math.PI / 180;
        const double tube = 3;
        int hub = plan.Add(ModuleKind.Hub, 0, 0, h0, 10, 10, 7.5, 2, RoomKind.Control);

        // the heading: the lab, then the sealed airlock in the wall
        {
            double yaw = h0;
            var (dx, dz) = Along(yaw);
            int from = plan.Door(hub, 5, yaw);
            double at = 5 + tube;
            int lab = plan.Add(ModuleKind.Can, dx * (at + 6), dz * (at + 6), yaw, 12, 4.6, 5.5, 1, RoomKind.Lab);
            plan.Links.Add(new ColonyLink(from, plan.Door(lab, 6, yaw + Math.PI), false));
            int labOut = plan.Door(lab, 6, yaw);
            // its outer door at the foot of the wall, the wall rising right behind it
            double sealedMid = c.PlateauM - 1.6;
            int sealedLock = plan.Add(ModuleKind.Airlock, dx * sealedMid, dz * sealedMid, yaw, 4, 4.6, 5.5, 1, RoomKind.Airlock);
            plan.Links.Add(new ColonyLink(labOut, plan.Door(sealedLock, 2, yaw + Math.PI), false));
            plan.Door(sealedLock, 2, yaw);
            plan.Sealed = sealedLock;
        }
        // a quarter turn: quarters, the airlock the crew used, the stair tower
        {
            double yaw = h0 + Math.PI / 2;
            var (dx, dz) = Along(yaw);
            int from = plan.Door(hub, 5, yaw);
            double at = 5 + tube;
            int q = plan.Add(ModuleKind.Can, dx * (at + 5), dz * (at + 5), yaw, 10, 4.6, 5.5, 1, RoomKind.Quarters);
            plan.Links.Add(new ColonyLink(from, plan.Door(q, 5, yaw + Math.PI), false));
            int qOut = plan.Door(q, 5, yaw);
            double mid = at + 10 + tube + 2;
            int al = plan.Add(ModuleKind.Airlock, dx * mid, dz * mid, yaw, 4, 4.6, 5.5, 1, RoomKind.Airlock);
            int inner = plan.Door(al, 2, yaw + Math.PI);
            plan.Links.Add(new ColonyLink(qOut, inner, false));
            plan.Airlocks.Add(new ColonyAirlock(al, plan.Door(al, 2, yaw), inner));
            // the tower past the airlock's steps, its bridge out to the rim along the same way
            double towerAt = mid + 2 + 2.4 + 1.2 + TowerHalf;
            plan.Tower = (dx * towerAt, dz * towerAt, yaw);
        }
        // a half turn: the galley, its far end open
        {
            double yaw = h0 + Math.PI;
            var (dx, dz) = Along(yaw);
            int from = plan.Door(hub, 5, yaw);
            double at = 5 + tube;
            int g = plan.Add(ModuleKind.Can, dx * (at + 6), dz * (at + 6), yaw, 12, 4.6, 5.5, 1, RoomKind.Galley);
            plan.Links.Add(new ColonyLink(from, plan.Door(g, 6, yaw + Math.PI), false));
            plan.Door(g, 6, yaw);
        }
        // three quarters: a moondust dome right off the hub
        {
            double yaw = h0 + 3 * Math.PI / 2;
            var (dx, dz) = Along(yaw);
            const double r = 6;
            int from = plan.Door(hub, 5, yaw);
            double centre = 5 + tube + r;
            int dome = plan.Add(ModuleKind.Dome, dx * centre, dz * centre, yaw, 2 * r, 2 * r, r, 1, RoomKind.Greenhouse);
            plan.Links.Add(new ColonyLink(from, plan.Door(dome, r, yaw + Math.PI), false));
        }
        return plan;
    }
}
