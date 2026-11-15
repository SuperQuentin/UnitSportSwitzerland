using UnitSport.Terrain.Bodies;
using Kit = UnitSport.World.Moon.ColonyKit;

namespace UnitSport.Interiors;

/// <summary>
/// A Moon colony module's fittings (#2000), ported from the kit-era <c>ColonyInteriors</c> (#1941) with its sizes,
/// spacing and side-door rules: furniture by room along a can's walls (its aisle down the middle), the hub's ring of
/// consoles round the map table, a dome's racks in rows, the hangar's benches and bay paint, and the ceiling lamps the
/// kit drew. Placed in the module's node frame (x across, z along, y = 0 its floor); drawn by
/// <c>InteriorMeshBuilder.ColonyPiece</c>.
/// <para>
/// A wall piece stands against +X with <see cref="FurniturePlan.Turns"/> 0, against -X with 2, where it is drawn as the
/// mirror image (its ends stay where the kit drew them, a bunk's ladder toward -Z on both walls). Turns' bit 4 picks a
/// piece's second colour: an amber screen or lamp, a dark crate (the rotation reads only the low two bits).
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>The bit of <see cref="FurniturePlan.Turns"/> that picks a colony piece's second colour.</summary>
    public const int ColonyAlt = 4;

    /// <summary>Half the length kept clear in front of a can's side doorway, metres (the kit's).</summary>
    private const float ColonyClearHalf = Kit.DoorW / 2 + 0.55f;

    /// <summary>Furnishes a colony module's room by its kind (<paramref name="sideDoor"/>: a can's side doorway, +1 on +X).</summary>
    private static void FurnishColony(InteriorLayout l, RoomPlan room, ColonyModule m, RoomKind kind, int sideDoor)
    {
        var f = new ColonyFitter(l, sideDoor);
        float len = (float)m.Length, w = (float)m.Width;
        switch (m.Kind)
        {
            case ModuleKind.Can:
            case ModuleKind.Airlock:
            {
                f.Can(kind, len);
                // the kit's lamp strips at the crown, one per 2.6 m; an airlock's amber while it has power
                int n = Math.Max(1, (int)(len / 2.6f));
                float crown = BarrelCrown(room, 0.2f);
                for (int i = 0; i < n; i++)
                    f.Put(FurnitureType.ColonyLamp, 0, -(-len / 2 + (i + 0.5f) * len / n), 0.4f, 1.2f, 0.01f,
                        m.Kind == ModuleKind.Airlock ? ColonyAlt : 0, crown);
                break;
            }
            case ModuleKind.Hub:
            case ModuleKind.Stack:
                f.Round(w / 2);
                // a ring of eight panes just under the ceiling, square to the axes as the kit drew them
                for (int i = 0; i < 8; i++)
                {
                    float a = MathF.Tau * i / 8;
                    f.Put(FurnitureType.ColonyLamp, -MathF.Sin(a) * w / 2 * 0.55f, -MathF.Cos(a) * w / 2 * 0.55f, 0.8f, 0.3f, 0.01f, 0,
                        room.Clear - 0.01f);
                }
                break;
            case ModuleKind.Dome:
            {
                f.Dome(kind, w / 2);
                // six panes on a ring of 0.3 r, under the shell where their outer corners reach it
                float ring = w / 2 * 0.3f, R = room.ShellR, Ry = room.ShellRy > 0 ? room.ShellRy : R, reach = ring + 0.36f;
                float y = room.ShellY + Ry * MathF.Sqrt(Math.Max(0, 1 - reach * reach / (R * R))) - 0.05f;
                for (int i = 0; i < 6; i++)
                {
                    float a = MathF.Tau * i / 6;
                    f.Put(FurnitureType.ColonyLamp, -MathF.Sin(a) * ring, -MathF.Cos(a) * ring, 0.5f, 0.5f, 0.01f, 0, y);
                }
                break;
            }
            case ModuleKind.Hangar:
            {
                f.Hangar(len, w);
                float crown = BarrelCrown(room, 0.6f);
                for (int k = 0; k < 4; k++)
                    f.Put(FurnitureType.ColonyLamp, 0, -(-len / 2 + (k + 0.5f) * len / 4), 1.2f, 0.4f, 0.01f, 0, crown);
                break;
            }
        }
    }

    /// <summary>
    /// The height a flat lamp <paramref name="halfWidth"/> either side of a barrel's crown hangs at: its edges 3 cm under
    /// the curve, so the shell's facets never cut through it.
    /// </summary>
    private static float BarrelCrown(RoomPlan r, float halfWidth)
    {
        float R = r.ShellR, Ry = r.ShellRy > 0 ? r.ShellRy : R;
        return r.ShellY + Ry * MathF.Sqrt(Math.Max(0, 1 - halfWidth * halfWidth / (R * R))) - 0.03f;
    }

    /// <summary>
    /// Lays one module's pieces. Coordinates are written from the kit's authored frame (+Z along, its pieces on -X
    /// mirrored here to +X): every authored (x, z) is placed at (-x, -z), the half turn the kit's mesh took.
    /// </summary>
    private sealed class ColonyFitter(InteriorLayout l, int sideDoor)
    {
        private const float CH = ColonyClearHalf;

        /// <summary>One piece, W across (x) by D along (z) by H, its base <paramref name="lift"/> over the floor.</summary>
        public void Put(FurnitureType t, float x, float z, float w, float d, float h, int turns = 0, float lift = 0, float angle = 0) =>
            l.Furniture.Add(new FurniturePlan { Type = t, Floor = 0, X = x, Z = z, W = w, D = d, H = h, Turns = turns, Lift = lift, Angle = angle });

        /// <summary>The wall turn of a piece standing at <paramref name="x"/>: 0 against +X, 2 against -X.</summary>
        private static int WallTurns(float x) => x > 0 ? 0 : 2;

        /// <summary>Whether a piece <paramref name="len"/> long along the side doorway's wall stands in front of it (the kit's test).</summary>
        private bool Blocks(float x, float z, float len) =>
            sideDoor != 0 && x * sideDoor > 0.3f && z - len / 2 < CH && z + len / 2 > -CH;

        /// <summary>The kit's test for a lit pane <paramref name="w"/> wide.</summary>
        private bool BlocksPane(float x, float z, float w) => sideDoor != 0 && x * sideDoor > 0.3f && MathF.Abs(z) < CH + w / 2;

        /// <summary>A piece that goes when it stands in front of the side doorway.</summary>
        private void Piece(FurnitureType t, float x, float z, float w, float d, float h, int turns)
        {
            if (!Blocks(x, z, d)) Put(t, x, z, w, d, h, turns);
        }

        /// <summary>
        /// A run along a wall, centred on the can's middle: cut either side of the side doorway (pieces under 0.1 m left
        /// out), gone when under 2.5 m. Its fittings are laid along the whole run by the drawer, so a cut keeps them put.
        /// </summary>
        private void Run(FurnitureType t, float x, float w, float len, float h, int turns)
        {
            if (!Blocks(x, 0, len)) { Put(t, x, 0, w, len, h, turns); return; }
            if (len < 2.5f) return;
            float z0 = -len / 2, z1 = len / 2;
            if (-CH - z0 > 0.1f) Put(t, x, (z0 - CH) / 2, w, -CH - z0, h, turns);
            if (z1 - CH > 0.1f) Put(t, x, (z1 + CH) / 2, w, z1 - CH, h, turns);
        }

        /// <summary>A screen on a wall: its face at <paramref name="x"/>, its middle <paramref name="y"/> up, facing the aisle.</summary>
        private void Screen(float x, float z, float y, float w, float h, bool amber)
        {
            if (BlocksPane(x, z, w)) return;
            Put(FurnitureType.ColonyScreen, x, z, 0, w, h, WallTurns(x) | (amber ? ColonyAlt : 0), y - h / 2);
        }

        // ---- cans: an aisle down the middle, things along both walls ---------------------------------------------------

        public void Can(RoomKind room, float len)
        {
            float z0 = -len / 2 + 1.0f, z1 = len / 2 - 1.0f;
            switch (room)
            {
                case RoomKind.Quarters:
                    for (float z = z0 + 1.0f; z + 1.0f <= z1 + 0.01f; z += 2.2f)
                        foreach (float ax in new[] { -1.05f, 1.05f })
                        {
                            float x = -ax;
                            Piece(FurnitureType.ColonyBunk, x, -z, 0.85f, 2.0f, 1.53f, WallTurns(x));
                            Piece(FurnitureType.ColonyLocker, x * 1.05f, -(z + 1.15f), 0.45f, 0.2f, 1.8f, WallTurns(x));
                        }
                    break;
                case RoomKind.Galley:
                    Run(FurnitureType.GalleyCounter, 1.15f, 0.6f, len - 2.4f, 0.9f, 0);
                    for (float z = -len / 2 + 1.6f; z < len / 2 - 1.6f; z += 1.2f) Screen(1.44f, -z, 1.45f, 0.45f, 0.3f, amber: true);
                    for (float z = z0 + 0.8f; z + 0.8f <= z1; z += 2.4f) Piece(FurnitureType.MessTable, -0.95f, -z, 0.8f, 2.25f, 0.78f, 0);
                    break;
                case RoomKind.Lab:
                    foreach (float ax in new[] { -1.1f, 1.1f })
                        Run(FurnitureType.LabBench, -ax, 0.65f, len - 2.4f, 1.45f, WallTurns(-ax));
                    break;
                case RoomKind.Medbay:
                    Piece(FurnitureType.MedBed, 1.0f, len / 4, 0.8f, 2.0f, 0.66f, 0);
                    Piece(FurnitureType.MedBed, 1.0f, -len / 4, 0.8f, 2.0f, 0.66f, 0);
                    Run(FurnitureType.MedCabinet, -1.15f, 0.5f, len - 3f, 1.8f, 2);
                    Screen(1.45f, len / 4, 1.5f, 0.5f, 0.35f, amber: false);
                    break;
                case RoomKind.Gym:
                    // the treadmill with its bar and the harness that holds a runner down at a sixth of a g
                    Piece(FurnitureType.GymMat, 1.0f, len / 4, 0.7f, 1.8f, 1.93f, 0);
                    Piece(FurnitureType.GymMachine, -1.0f, len / 4, 0.4f, 1.0f, 1.0f, 2);
                    Piece(FurnitureType.GymFrame, -1.0f, -len / 4, 0.3f, 0.3f, 1.8f, 2);
                    break;
                case RoomKind.Workshop:
                    Run(FurnitureType.ColonyWorkbench, 1.1f, 0.7f, len - 2.4f, 1.85f, 0);
                    for (float z = z0 + 0.5f; z <= z1 - 0.5f; z += 1.6f) Piece(FurnitureType.ColonyCrates, -1.05f, -z, 0.7f, 0.9f, 0.7f, 2);
                    break;
                case RoomKind.Airlock:
                    // the suits waiting on their racks either side, helmets over them
                    foreach (float ax in new[] { -1.15f, 1.15f })
                        Piece(FurnitureType.SuitRack, -ax, 0, 0.5f, 0.9f, 1.98f, WallTurns(-ax));
                    break;
                default:
                    // storage: racks of crates along both walls
                    foreach (float ax in new[] { -1.1f, 1.1f })
                        for (float z = z0 + 0.5f; z <= z1 - 0.4f; z += 1.1f)
                            Piece(FurnitureType.StoreRack, -ax, -z, 0.7f, 1.0f, 1.8f, WallTurns(-ax));
                    break;
            }
        }

        // ---- the hub: the control room in the round -------------------------------------------------------------------

        /// <summary>Eight consoles 30 degrees either side of the diagonals (clear of the four doorways) facing the map table.</summary>
        public void Round(float r)
        {
            for (int i = 0; i < 8; i++)
            {
                float a = (45 + 90 * (i / 2) + (i % 2 == 0 ? -15 : 15)) * MathF.PI / 180;
                // the kit drew it at dir * (r - 0.9) and turned it half round: here on the other side, facing in along dir
                Put(FurnitureType.ColonyConsole, -MathF.Sin(a) * (r - 0.9f), -MathF.Cos(a) * (r - 0.9f), 1.08f, 0.6f, 1.4f,
                    i % 2 == 0 ? 0 : ColonyAlt, 0, a);
            }
            Put(FurnitureType.MapTable, 0, 0, 2.2f, 2.2f, 0.91f);
        }

        // ---- a dome: hydroponic racks or storage in rows ---------------------------------------------------------------

        public void Dome(RoomKind room, float r)
        {
            for (float x = -r * 0.5f; x <= r * 0.5f + 0.01f; x += 2.2f)
            {
                float half = MathF.Sqrt(Math.Max(0, r * r * 0.55f - x * x));
                if (half < 1.2f) continue;
                if (MathF.Abs(x) < 0.8f) continue;   // the middle aisle from the door
                if (room == RoomKind.Greenhouse)
                    Put(FurnitureType.HydroRack, -x, 0, 0.9f, half * 2, 2.3f);
                else
                    for (float z = -half + 0.6f; z <= half - 0.6f; z += 1.2f)
                        Put(FurnitureType.CrateStack, -x, -z, 0.9f, 1.0f, 1.0f, (int)(z * 3) % 2 == 0 ? 0 : ColonyAlt);
            }
        }

        // ---- the hangar: the workshop along its sides, the bay free for rovers -------------------------------------------

        public void Hangar(float len, float w)
        {
            foreach (float ax in new[] { -w / 2 + 1.0f, w / 2 - 1.0f })
                Put(FurnitureType.HangarBench, -ax, 0, 0.8f, len * 0.6f, 1.5f, WallTurns(-ax));
            // the bay's paint: a box outline where a rover parks
            foreach (float z in new[] { -len * 0.2f, len * 0.2f })
                Put(FurnitureType.BayPaint, 0, -z, 4.52f, 5.12f, 0.01f);
        }
    }
}
