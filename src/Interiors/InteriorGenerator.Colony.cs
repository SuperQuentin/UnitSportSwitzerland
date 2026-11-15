using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using Kit = UnitSport.World.Moon.ColonyKit;

namespace UnitSport.Interiors;

/// <summary>
/// A Moon colony's modules as interiors (#2000): each module of a <see cref="ColonyPlan"/> one <see cref="InteriorLayout"/>
/// built where it stands (<see cref="InteriorLayout.InPlace"/>), its room a curved shell (<see cref="ShellKind"/>) with the
/// doorways its tubes meet, furnished by its <see cref="RoomKind"/>; each tube or alley one more. The plan's frame is the
/// module's node frame (x across, z along its axis, the way out of its first door at -Z), y = 0 its floor:
/// <see cref="FloorOf"/> over the plateau. Drawn, lit and given collision by <see cref="InteriorMeshBuilder"/>, as any
/// building's rooms; the outside shell stays the kit's (<c>ColonyKit</c>).
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>How far the floor is lifted over the kit's deck at the sill, metres: the two never fight.</summary>
    public const float ColonyFloorLift = 0.02f;

    /// <summary>A module's floor over the plateau, metres (the kit's deck at the doorways' sill; the hangar's slab; the garden).</summary>
    public static float FloorOf(ModuleKind kind) => kind switch
    {
        ModuleKind.Hangar => Kit.HangarSlab,
        ModuleKind.Biodome => BiodomeFloor,
        _ => Kit.FloorY,
    } + ColonyFloorLift;

    /// <summary>A biodome's garden over the plateau, metres.</summary>
    public const float BiodomeFloor = 1.25f;

    /// <summary>The room type a colony room plays.</summary>
    public static RoomType TypeOf(RoomKind room) => room switch
    {
        RoomKind.Quarters => RoomType.CrewQuarters,
        RoomKind.Galley => RoomType.Galley,
        RoomKind.Lab => RoomType.ColonyLab,
        RoomKind.Greenhouse => RoomType.Greenhouse,
        RoomKind.Control => RoomType.ColonyControl,
        RoomKind.Medbay => RoomType.Medbay,
        RoomKind.Gym => RoomType.Gym,
        RoomKind.Workshop => RoomType.ColonyWorkshop,
        RoomKind.Garage => RoomType.Hangar,
        RoomKind.Airlock => RoomType.AirlockChamber,
        _ => RoomType.ColonyStore,
    };

    /// <summary>
    /// A module's interior: its shell room, the doorways where its tubes meet it, its fittings. <paramref name="sideDoor"/> is
    /// <see cref="ColonyPlan.SideDoor"/>'s (+1 a doorway on the node's +X, -1 on its -X).
    /// </summary>
    public static InteriorLayout Colony(ColonyModule m, RoomKind kind, ColonyTheme theme, int sideDoor, string key)
    {
        var l = new InteriorLayout
        {
            Key = key, InPlace = true, Colony = (int)theme, Kind = BuildingKind.Other, Type = BuildingType.None,
            Width = (float)m.Width, Depth = (float)m.Length, Yaw = (float)m.Yaw,
        };
        var floor = new FloorPlan();
        l.Floors.Add(floor);
        float len = (float)m.Length, w = (float)m.Width, h = (float)m.Height, r = w / 2;
        float lift = FloorOf(m.Kind);
        float door = Kit.DoorH - ColonyFloorLift;
        var room = new RoomPlan { Type = TypeOf(kind) };
        switch (m.Kind)
        {
            case ModuleKind.Can:
            case ModuleKind.Airlock:
            {
                // the kit's shell: radius r round an axis r + 0.9 over the plateau; ours 5 cm inside it
                float axis = r + 0.9f - lift, rr = r - 0.05f;
                float half = Chord(rr, rr, axis), end = len / 2 - 0.12f;
                Rect(room, -half, -end, half, end);
                Barrel(room, rr, rr, axis);
                room.Openings.Add(Arch(Side.Front, 0, Kit.DoorW, 0, door));
                room.Openings.Add(Arch(Side.Back, 0, Kit.DoorW, 0, door));
                if (sideDoor != 0) room.Openings.Add(Arch(sideDoor > 0 ? Side.Right : Side.Left, 0, Kit.DoorW, 0, door));
                break;
            }
            case ModuleKind.Hub:
            case ModuleKind.Stack:
            {
                // the drum's panels are 0.2 m thick at r; its ceiling's band hangs 0.15 m under h
                float rr = r - 0.13f;
                Round(room, rr * 0.7f, rr, 24);
                room.Shell = ShellKind.Drum;
                room.ShellR = rr;
                room.Clear = h - 0.2f - lift;
                for (int i = 0; i < 4; i++) room.Openings.Add(Arch(Side.Front, 0, Kit.DoorW, 0, door, MathF.PI / 2 * i));
                break;
            }
            case ModuleKind.Dome:
            {
                // the kit's moondust skin: r across, 0.92 r up, round a centre FloorY / 2 over the plateau (its rings jitter 2 %)
                float rr = r * 0.93f, ry = rr * 0.92f, cy = Kit.FloorY / 2 - lift;
                float fr = rr * MathF.Sqrt(Math.Max(0.1f, 1 - cy * cy / (ry * ry)));
                Round(room, fr * 0.7f, fr, 24);
                room.Shell = ShellKind.Dome;
                room.ShellR = rr;
                room.ShellRy = ry;
                room.ShellY = cy;
                room.Clear = cy + ry;
                room.Openings.Add(Arch(Side.Front, 0, Kit.DoorW, 0, door, MathF.PI));
                // the entrance's short passage through the arch out to the tube (its door is r out)
                var hall = new RoomPlan { Type = RoomType.Tube, Clear = door + 0.3f };
                Rect(hall, -Kit.DoorW / 2 - WallInset, -r - 0.3f, Kit.DoorW / 2 + WallInset, -fr + 0.25f);
                hall.Openings.Add(Arch(Side.Front, 0, Kit.DoorW, 0, door));
                hall.Openings.Add(Arch(Side.Back, 0, Kit.DoorW, 0, door));
                floor.Rooms.Add(hall);
                break;
            }
            case ModuleKind.Hangar:
            {
                // a half cylinder flattened to h over the slab, its panels 0.25 m thick; the vehicle door at -Z, the walkers' at +Z
                float dh = Kit.HangarDoorHalf(w), sill = Kit.FloorY - Kit.HangarSlab - ColonyFloorLift;
                float end = len / 2 - 0.2f;
                Rect(room, -(r - 0.25f), -end, r - 0.25f, end);
                Barrel(room, r - 0.2f, h - 0.2f, 0);
                room.Openings.Add(Arch(Side.Front, 0, 2 * dh, 0, Kit.HangarDoorH - ColonyFloorLift));
                room.Openings.Add(Arch(Side.Back, 0, Kit.DoorW, sill, sill + Kit.DoorH));
                // the landing at the sill and its steps, drawn by the kit: walked on
                Solid(l, 0, end - 0.8f, 2.4f, 2.0f, 0, sill);
                const int steps = 5;
                float rise = sill / steps;
                for (int k = 1; k < steps; k++) Solid(l, 0, end - 1.8f - (k - 0.5f) * 0.3f, 2.4f, 0.3f, 0, sill - k * rise);
                break;
            }
            case ModuleKind.Biodome:
            {
                // glass over a garden: the kit draws both; here the low ring wall, the floor, the landing, the trunks
                float band = 1.2f + r * 0.3f - BiodomeFloor;
                Round(room, r * 0.6f, r + 0.1f, 28);
                room.Shell = ShellKind.Glass;
                room.ShellY = band;
                room.Clear = r;
                room.Openings.Add(Arch(Side.Front, 0, 2.4f, 0, band, MathF.PI));
                float sill = Kit.FloorY - BiodomeFloor;
                Solid(l, 0, -(r - 0.4f), 2.2f, 1.6f, 0, sill);
                foreach (var (at, th, _) in Kit.Trees(r, w >= 60))
                    Solid(l, -at.X, -at.Z, 0.4f, 0.4f, at.Y - BiodomeFloor + ColonyFloorLift, th);
                break;
            }
        }
        floor.Rooms.Insert(0, room);
        l.StoreyHeight = Math.Max(2.4f, room.Clear) + Slab;
        if (m.Kind != ModuleKind.Biodome) FurnishColony(l, room, m, kind, sideDoor);
        return l;
    }

    /// <summary>
    /// A tube (or a glazed alley) <paramref name="length"/> m from door to door, as an interior in its own frame: z along it,
    /// its middle at the origin, the floor at the doors' sill.
    /// </summary>
    public static InteriorLayout ColonyLink(float length, bool alley, ColonyTheme theme, string key)
    {
        var l = new InteriorLayout { Key = key, InPlace = true, Colony = (int)theme, Kind = BuildingKind.Other, Width = 2.8f, Depth = length };
        var floor = new FloorPlan();
        l.Floors.Add(floor);
        var room = new RoomPlan { Type = RoomType.Tube };
        // past each door into the module's doorway, so no gap is left at the sill
        float end = length / 2 + 0.25f;
        if (alley)
        {
            Rect(room, -1.15f, -end, 1.15f, end);
            room.Shell = ShellKind.Glass;
            room.ShellY = 2.6f;
            room.ShellRy = 2.5f;
            room.Clear = 2.5f;
        }
        else
        {
            float axis = Kit.DoorH / 2 - ColonyFloorLift, rr = Kit.TubeR - 0.05f;
            float half = Chord(rr, rr, axis);
            Rect(room, -half, -end, half, end);
            Barrel(room, rr, rr, axis);
            // open at both ends: an opening wider than the disc leaves no end wall
            room.Openings.Add(Arch(Side.Front, 0, 4 * rr, -1, 2 * rr + 1));
            room.Openings.Add(Arch(Side.Back, 0, 4 * rr, -1, 2 * rr + 1));
        }
        floor.Rooms.Add(room);
        l.StoreyHeight = room.Clear + Slab;
        return l;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>Half a barrel's floor at <paramref name="axis"/> under its axis.</summary>
    private static float Chord(float r, float ry, float axis) => r * MathF.Sqrt(Math.Max(0, 1 - axis * axis / (ry * ry)));

    private static void Rect(RoomPlan r, float x0, float z0, float x1, float z1) { r.X0 = x0; r.Z0 = z0; r.X1 = x1; r.Z1 = z1; }

    private static void Barrel(RoomPlan room, float r, float ry, float axis)
    {
        room.Shell = ShellKind.Barrel;
        room.ShellR = r;
        room.ShellRy = ry;
        room.ShellY = axis;
        room.Clear = axis + ry;
    }

    /// <summary>A round room: its core a square of half <paramref name="core"/>, its shape an <paramref name="n"/>-gon of <paramref name="radius"/>.</summary>
    private static void Round(RoomPlan room, float core, float radius, int n)
    {
        Rect(room, -core, -core, core, core);
        var c = new Godot.Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = MathF.Tau * (i + 0.5f) / n;
            c[i] = new Godot.Vector2(MathF.Sin(a) * radius, MathF.Cos(a) * radius);
        }
        // counter-clockwise seen from above in the plan's (x, z), as every shape
        if (RoomShapes.Area(c) < 0) Array.Reverse(c);
        room.SetShape(c);
    }

    private static OpeningPlan Arch(Side side, float center, float width, float bottom, float top, float? angle = null) =>
        new() { Side = side, Center = center, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Arch, Angle = angle };

    /// <summary>Something the building draws itself, solid here: a box <paramref name="w"/> x <paramref name="d"/> from <paramref name="lift"/> up <paramref name="h"/>.</summary>
    private static void Solid(InteriorLayout l, float x, float z, float w, float d, float lift, float h) =>
        l.Furniture.Add(new FurniturePlan { Type = FurnitureType.Solid, X = x, Z = z, W = w, D = d, H = h, Lift = lift });
}
