using System;
using System.Collections.Generic;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>What a colony module is (docs/plans/moon-surface.md section 4).</summary>
public enum ModuleKind : byte
{
    /// <summary>The core: a vertical cylinder of two storeys, doors all round.</summary>
    Hub,
    /// <summary>A horizontal cylinder 4.5 m across on legs, a door at each end.</summary>
    Can,
    /// <summary>A vertical cylinder of two or three storeys (the ESA village's look).</summary>
    Stack,
    /// <summary>A hemisphere covered in moondust, a short tunnel to its door.</summary>
    Dome,
    /// <summary>A large glass dome with a garden inside.</summary>
    Biodome,
    /// <summary>A long half-cylinder with a wide vehicle door at its outer end.</summary>
    Hangar,
    /// <summary>A short can with a door at each end that never both open: the way out at a spine's end (#1941).</summary>
    Airlock,
}

/// <summary>What a module's room is for, inside (#1941, docs/plans/moon-surface.md section 5).</summary>
public enum RoomKind : byte
{
    Hall, Quarters, Galley, Lab, Greenhouse, Control, Medbay, Gym, Workshop, Storage, Garage, Airlock,
}

/// <summary>An airlock: its module, its outer door (to the outside) and its inner door (to the colony).</summary>
public readonly record struct ColonyAirlock(int Module, int Outer, int Inner);

/// <summary>
/// A module in its colony's frame (x east, z south, metres from the centre, on the plateau): its long axis along
/// <see cref="Yaw"/> (radians from north, clockwise seen from above), its length along it, its width across, its height.
/// </summary>
public readonly record struct ColonyModule(int Id, ModuleKind Kind, double X, double Z, double Yaw, double Length, double Width, double Height, int Storeys);

/// <summary>A door on a module, where a tube or a road meets it: its place and the way out of it (radians from north).</summary>
public readonly record struct ColonyDoor(int Module, double X, double Z, double Out);

/// <summary>A pressurised tube (or an open alley where the theme prefers it) between two doors.</summary>
public readonly record struct ColonyLink(int DoorA, int DoorB, bool Alley);

/// <summary>What an industrial piece is (#1942, docs/plans/moon-surface.md section 4).</summary>
public enum IndustryKind : byte
{
    /// <summary>A solar array on a mast, its wing turned to the sun (a node).</summary>
    SolarArray,
    /// <summary>A bucket-wheel regolith digger driving a loop round its pit on the shared clock; Size is the loop's radius.</summary>
    Digger,
    /// <summary>A heap of dug regolith; Size is its radius.</summary>
    SpoilHeap,
    /// <summary>The ice mine's headframe over its shaft: a lattice tower, its sheave wheel turning.</summary>
    Headframe,
    /// <summary>A small pressurised can on legs (an outpost's control room), Size its length.</summary>
    Hut,
    /// <summary>A row of ore bins.</summary>
    OreBins,
    /// <summary>A flood light mast lighting the dark (a real light), Size its height.</summary>
    FloodLight,
    /// <summary>A radio dish on its pedestal, Size its diameter.</summary>
    Dish,
    /// <summary>The mass driver's rail on pylons from here along Yaw, Size its length; the bucket runs it on the shared clock.</summary>
    DriverTrack,
    /// <summary>A runway from here along Yaw, Size its length (#1943): sintered, painted, edge lights.</summary>
    Runway,
    /// <summary>A lander standing on a pad (#1943), Size its pad's radius.</summary>
    Lander,
    /// <summary>The Cold War base's radar (#1944): a big lattice dish on a pedestal, turning; Size its width.</summary>
    Radar,
    /// <summary>A lattice radio mast with its guys and a red light on top (#1944), Size its height.</summary>
    Mast,
}

/// <summary>An industrial piece in its colony's (or outpost's) frame (x east, z south): kind, place, heading (radians from north), size.</summary>
public readonly record struct ColonyIndustry(IndustryKind Kind, double X, double Z, double Yaw, double Size);

/// <summary>A landing pad: centre, radius, its painted number.</summary>
public readonly record struct ColonyPad(double X, double Z, double Radius, int Number);

/// <summary>
/// A colony's plan (#1940, docs/plans/moon-surface.md section 4): its modules, the doors on them, the tubes between the
/// doors, its pads and the road out to them, all in the colony's frame. A pure function of the colony's record (theme and
/// seed): every peer plans the same colony.
///
/// <para>
/// <b>The layout</b>: a hub at the centre; spines out of it along the heading and at right angles; along each spine a
/// few cans end to end (a tube between each pair of doors); a moondust dome off a can's side door now and then; the
/// hangar at the head of the spine toward the pads; the pads out past it on a road. The spacing keeps every tube 6-14 m
/// and every module clear of the others by 4 m.
/// </para>
/// </summary>
public sealed partial class ColonyPlan
{
    public readonly List<ColonyModule> Modules = new();

    /// <summary>Each module's room, by its id.</summary>
    public readonly List<RoomKind> Rooms = new();

    /// <summary>The ways in and out: an airlock at the end of every spine but the hangar's.</summary>
    public readonly List<ColonyAirlock> Airlocks = new();

    /// <summary>The hangar as a vehicle airlock (#1943): its module, its vehicle door (Outer) and the door at its inner doorway (Inner); null with no hangar.</summary>
    public ColonyAirlock? Hangar { get; private set; }
    public readonly List<ColonyDoor> Doors = new();
    public readonly List<ColonyLink> Links = new();
    public readonly List<ColonyPad> Pads = new();

    /// <summary>The colony's own industry (#1942): solar arrays, the regolith digger and its spoil heap.</summary>
    public readonly List<ColonyIndustry> Industry = new();

    /// <summary>The road from the hangar's door to the pads, as points (x, z).</summary>
    public readonly List<(double X, double Z)> Road = new();

    /// <summary>How far the farthest thing is from the centre, metres.</summary>
    public double Extent
    {
        get
        {
            double m = 0;
            foreach (var mod in Modules) m = Math.Max(m, Math.Sqrt(mod.X * mod.X + mod.Z * mod.Z) + Math.Max(mod.Length, mod.Width) / 2);
            foreach (var p in Pads) m = Math.Max(m, Math.Sqrt(p.X * p.X + p.Z * p.Z) + p.Radius);
            foreach (var i in Industry) m = Math.Max(m, Math.Sqrt(i.X * i.X + i.Z * i.Z) + IndustryReach(i));
            return m;
        }
    }

    /// <summary>The unit vector along a heading in the frame (x east, z south).</summary>
    public static (double X, double Z) Along(double yaw) => (Math.Sin(yaw), -Math.Cos(yaw));

    /// <summary>The Cold War base's biodome's radius, metres (#1944): the largest, 64 m across.</summary>
    public const double BiodomeR = 32;

    public static ColonyPlan For(MoonColony c)
    {
        // the abandoned colony on its pit's floor (#1944): a compact plan of its own
        if (c.Theme == ColonyTheme.Abandoned) return Pit(c);
        var plan = new ColonyPlan();
        var rng = new Random(c.Seed);
        double h0 = c.Heading * Math.PI / 180;
        bool alleys = c.Theme == ColonyTheme.MoonVillage;

        // the hub: a two-storey drum, 10 m across
        int hub = plan.Add(ModuleKind.Hub, 0, 0, h0, 10, 10, 7.5, 2, RoomKind.Control);
        // the cans' rooms: a colony's worth, shuffled by its seed
        var rooms = new List<RoomKind> { RoomKind.Quarters, RoomKind.Galley, RoomKind.Lab, RoomKind.Quarters, RoomKind.Medbay,
            RoomKind.Gym, RoomKind.Workshop, RoomKind.Storage, RoomKind.Quarters, RoomKind.Lab, RoomKind.Galley, RoomKind.Storage };
        for (int i = rooms.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (rooms[i], rooms[j]) = (rooms[j], rooms[i]);
        }
        int nextRoom = 0;

        var cansForDomes = new List<int>();

        // four spines; the first (along the heading) leads to the hangar and the pads
        for (int k = 0; k < 4; k++)
        {
            double yaw = h0 + k * Math.PI / 2;
            var (dx, dz) = Along(yaw);
            int last = plan.Door(hub, 5, yaw);
            double at = 5 + 9;
            int cans = k == 0 ? 1 : 1 + rng.Next(3);
            for (int i = 0; i < cans; i++)
            {
                bool stack = c.Theme == ColonyTheme.MoonVillage && i == cans - 1;
                double len = stack ? 9 : 10 + 2 * rng.Next(4);
                double mid = at + len / 2;
                int m = stack
                    ? plan.Add(ModuleKind.Stack, dx * mid, dz * mid, yaw, 9, 9, 10.5, 3, RoomKind.Quarters)
                    : plan.Add(ModuleKind.Can, dx * mid, dz * mid, yaw, len, 4.6, 4.6 + 0.9, 1, rooms[nextRoom++ % rooms.Count]);
                int inner = plan.Door(m, len / 2, yaw + Math.PI);
                plan.Links.Add(new ColonyLink(last, inner, alleys));
                if (!stack) cansForDomes.Add(m);
                last = plan.Door(m, len / 2, yaw);
                at += len + 9;
            }
            if (k == 0)
            {
                // the hangar at the head of the first spine: 26 m long, 14 m wide, its vehicle door at the far end
                double len = 26;
                double mid = at + len / 2;
                int hangar = plan.Add(ModuleKind.Hangar, dx * mid, dz * mid, yaw, len, 14, 8, 1, RoomKind.Garage);
                int hangarInner = plan.Door(hangar, len / 2, yaw + Math.PI);
                plan.Links.Add(new ColonyLink(last, hangarInner, alleys));
                int vehicle = plan.Door(hangar, len / 2, yaw);
                // the hangar is a vehicle airlock (#1943): its rolling door out, a door at its inner doorway
                plan.Hangar = new ColonyAirlock(hangar, vehicle, hangarInner);
                // the road on out to the pads, 220 m past the hangar, three pads across it
                var d = plan.Doors[vehicle];
                plan.Road.Add((d.X, d.Z));
                double padAt = at + len + 220;
                plan.Road.Add((dx * padAt, dz * padAt));
                var (cx, cz) = Along(yaw + Math.PI / 2);
                for (int p = -1; p <= 1; p++)
                    plan.Pads.Add(new ColonyPad(dx * (padAt + 30) + cx * p * 75, dz * (padAt + 30) + cz * p * 75, 20, p + 2));
            }
            else if (c.Theme == ColonyTheme.ColdWar && k == 2)
            {
                // the Cold War base's big glass biodome at the end of its third spine (#1944), its door back toward the hub:
                // the mesh's door is at its node's -Z, along the module's yaw, so the yaw looks back down the spine
                const double r = BiodomeR;
                double centre = at - 9 + 6 + r;
                int bio = plan.Add(ModuleKind.Biodome, dx * centre, dz * centre, yaw + Math.PI, 2 * r, 2 * r, r, 1, RoomKind.Greenhouse);
                plan.Links.Add(new ColonyLink(last, plan.Door(bio, r + 0.3, yaw + Math.PI), alleys));
            }
            else
            {
                // the way out at the spine's end: a 4 m airlock 5 m on from the last door, its outer door to the ground
                double mid = at - 9 + 5 + 2;
                int al = plan.Add(ModuleKind.Airlock, dx * mid, dz * mid, yaw, 4, 4.6, 5.5, 1, RoomKind.Airlock);
                int inner = plan.Door(al, 2, yaw + Math.PI);
                plan.Links.Add(new ColonyLink(last, inner, alleys));
                plan.Airlocks.Add(new ColonyAirlock(al, plan.Door(al, 2, yaw), inner));
            }
        }
        // moondust domes off the cans' sides, now and then, where one fits clear of everything else
        foreach (int m in cansForDomes)
        {
            if (rng.NextDouble() >= 0.45) continue;
            var can = plan.Modules[m];
            double r = 7 + rng.Next(4);
            double first = rng.Next(2) == 0 ? 1 : -1;
            foreach (double side in new[] { first, -first })
            {
                double sy = can.Yaw + side * Math.PI / 2;
                var (sx, sz) = Along(sy);
                double reach = can.Width / 2;
                double cx = can.X + sx * (reach + 8 + r), cz = can.Z + sz * (reach + 8 + r);
                if (!plan.Clear(cx, cz, r + 4)) continue;
                int sideDoor = plan.Door(m, reach, sy);
                int dome = plan.Add(ModuleKind.Dome, cx, cz, sy, 2 * r, 2 * r, r, 1, rng.Next(2) == 0 ? RoomKind.Greenhouse : RoomKind.Storage);
                plan.Links.Add(new ColonyLink(sideDoor, plan.Door(dome, r, sy + Math.PI), alleys));
                break;
            }
        }
        plan.AddIndustry(c, h0, rng);
        return plan;
    }

    /// <summary>How far an industrial piece reaches from its point, metres.</summary>
    public static double IndustryReach(ColonyIndustry i) => i.Kind switch
    {
        IndustryKind.SolarArray => 6, IndustryKind.Digger => i.Size + 8, IndustryKind.DriverTrack or IndustryKind.Runway => i.Size,
        IndustryKind.Lander => 6, IndustryKind.Radar => i.Size / 2 + 2, IndustryKind.Mast => i.Size * 0.4,
        IndustryKind.Dish => i.Size / 2, IndustryKind.Hut => i.Size / 2 + 1, _ => Math.Max(i.Size, 4),
    };

    /// <summary>
    /// The staffed colonies' industry (#1942), on the back of the colony away from the pads: a field of solar arrays on
    /// the diagonal between the second and third spines (rows across it, 16 m apart, each clear of every module), and the
    /// regolith digger's pit with its spoil heap on the other back diagonal.
    /// </summary>
    private void AddIndustry(MoonColony c, double h0, Random rng)
    {
        if (c.Theme == ColonyTheme.ColdWar)
        {
            ColdWarIndustry(h0, rng);
            return;
        }
        if (c.Theme is not (ColonyTheme.Artemis or ColonyTheme.MoonVillage or ColonyTheme.Ilrs)) return;
        double fieldYaw = h0 + 3 * Math.PI / 4;
        var (fx, fz) = Along(fieldYaw);
        var (sx, sz) = Along(fieldYaw + Math.PI / 2);
        int rows = 3, cols = c.Theme == ColonyTheme.Artemis ? 5 : 4;
        for (int r = 0; r < rows; r++)
            for (int k = 0; k < cols; k++)
            {
                double along = 150 + r * 16, across = (k - (cols - 1) / 2.0) * 16;
                double x = fx * along + sx * across, z = fz * along + sz * across;
                if (!Clear(x, z, 8)) continue;
                Industry.Add(new ColonyIndustry(IndustryKind.SolarArray, x, z, fieldYaw, 10));
            }
        double digYaw = h0 + 5 * Math.PI / 4 + (rng.NextDouble() - 0.5) * 0.3;
        var (dx, dz) = Along(digYaw);
        double loop = 20 + rng.Next(6);
        Industry.Add(new ColonyIndustry(IndustryKind.Digger, dx * 250, dz * 250, digYaw, loop));
        var (hx, hz) = Along(digYaw + Math.PI / 2);
        Industry.Add(new ColonyIndustry(IndustryKind.SpoilHeap, dx * 250 + hx * (loop + 22), dz * 250 + hz * (loop + 22), digYaw, 11));
        // a lander on the first pad (#1943); the others stay free for the shuttle (MoonPads)
        if (Pads.Count > 0) Industry.Add(new ColonyIndustry(IndustryKind.Lander, Pads[0].X, Pads[0].Z, h0 + rng.NextDouble() * Math.Tau, Pads[0].Radius));
    }

    /// <summary>Whether a circle is clear of every module's footprint (a rectangle along its axis, or its circle).</summary>
    public bool Clear(double x, double z, double radius)
    {
        foreach (var m in Modules)
        {
            double dx = x - m.X, dz = z - m.Z;
            if (m.Kind is ModuleKind.Hub or ModuleKind.Stack or ModuleKind.Dome or ModuleKind.Biodome)
            {
                if (dx * dx + dz * dz < Math.Pow(m.Width / 2 + radius, 2)) return false;
                continue;
            }
            var (ax, az) = Along(m.Yaw);
            double v = dx * ax + dz * az, u = -dx * az + dz * ax;
            // the nearest point of the rectangle to the circle's centre
            double nv = Math.Clamp(v, -m.Length / 2, m.Length / 2), nu = Math.Clamp(u, -m.Width / 2, m.Width / 2);
            if ((v - nv) * (v - nv) + (u - nu) * (u - nu) < radius * radius) return false;
        }
        return true;
    }

    private int Add(ModuleKind kind, double x, double z, double yaw, double length, double width, double height, int storeys, RoomKind room)
    {
        int id = Modules.Count;
        Modules.Add(new ColonyModule(id, kind, x, z, yaw, length, width, height, storeys));
        Rooms.Add(room);
        return id;
    }

    /// <summary>A door on a module <paramref name="reach"/> metres from its centre toward <paramref name="out"/>.</summary>
    private int Door(int module, double reach, double @out)
    {
        var m = Modules[module];
        var (dx, dz) = Along(@out);
        Doors.Add(new ColonyDoor(module, m.X + dx * reach, m.Z + dz * reach, @out));
        return Doors.Count - 1;
    }
}
