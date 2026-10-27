using System.Runtime.CompilerServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Amusement arcades (#1240, part 4 of #1232): what is inside the solids <see cref="Venues"/> names
/// <c>leisure=amusement_arcade</c>. Numbers are in <c>docs/notes/terrain/venue-arcade.md</c>.
///
/// <para>
/// One dark hall on the ground floor, in the bowling centre's frame (<see cref="BowlFrame"/>): by the
/// door the prize counter with its clerk, the change machine and the claw machines; along the side
/// walls the racers, simulators, the dance and drum games and the basketball; along the back a row of
/// pinballs; in the middle islands of upright cabinets back to back with 1.8 m aisles between; air
/// hockey tables in the open floor; a strip of public toilets (#1285), store and office across the back. 14-16 machines to
/// 100 m2 at most, and never more than <see cref="MaxMachines"/>: a mall's arcade keeps to 32 m a side
/// and the rest of the solid is a plain hall.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Bumped when these rules change, so stored arcades are planned again.</summary>
    public const int ArcadeRules = 6;

    /// <summary>A billiard hall's longest side, m (#1250).</summary>
    private const float BilliardMaxSide = 22f;

    /// <summary>The most pool tables a billiard hall has.</summary>
    private const int MaxPoolTables = 12;

    private const float ArcadeMinSide = 6f, ArcadeMaxSide = 32f, ArcadeService = 3.0f, ArcadeAisle = 1.8f;
    /// <summary>Machines in one arcade at most: an upright is ~400 vertices, a racer ~600.</summary>
    private const int MaxMachines = 110;

    private sealed class ArcadePlan
    {
        public BowlFrame Frame;
        public float DoorU, DoorV;
    }

    private static readonly ConditionalWeakTable<InteriorLayout, ArcadePlan> ArcadePlans = new();

    /// <summary>Plans an arcade into <paramref name="l"/>; false gives the layout back as it was.</summary>
    internal static bool TryArcade(InteriorLayout l, Footprint fp, Building b, float doorHeight, Random rng, string name = "")
    {
        bool whole = Venues.WholeBuilding(b);
        float wall = b.MaxY - b.MinY;
        float h = whole ? Math.Clamp(wall * 0.85f, 3.0f, 5.0f) : Storeys(b).Height;
        var (storey, entryX, entryW, type, shop) = (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop);
        l.StoreyHeight = h;
        if (PlanArcade(l, doorHeight, whole, IsBilliard(name)) && InteriorValidator.Validate(l).Count == 0) return true;
        (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop) = (storey, entryX, entryW, type, shop);
        l.Floors.Clear();
        l.InnerDoors.Clear();
        l.Below = 0;
        ArcadePlans.Remove(l);
        return false;
    }

    /// <summary>An arcade whose name says it is a billiard hall (#1250): OSM tags Geneva's Bluff Billard an amusement arcade.</summary>
    public static bool IsBilliard(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"billard|billiard|snooker|\bpool\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool PlanArcade(InteriorLayout l, float doorHeight, bool whole, bool billiard)
    {
        float W = l.Width, D = l.Depth;
        var frame = D >= W ? new BowlFrame(true, 1, W, D) : new BowlFrame(false, l.EntryX <= 0 ? 1 : -1, W, D);
        // a billiard hall with its door far along a long wall keeps to its size from that wall (#1250)
        if (billiard && !frame.AlongZ && frame.V(l.EntryX, -D / 2) + 4f > BilliardMaxSide && D >= ArcadeMinSide + 1f)
            frame = new BowlFrame(true, 1, W, D);
        float along = frame.Along, across = frame.Across;
        if (along < ArcadeMinSide + 1f || across < ArcadeMinSide) return false;
        float doorU = frame.U(l.EntryX, -D / 2), doorV = frame.V(l.EntryX, -D / 2);

        // a billiard hall is smaller than an arcade's hangar [S: Bluff Billard Geneva, 8 tables]; the rest a plain hall
        float maxSide = billiard ? BilliardMaxSide : ArcadeMaxSide;
        float progV = Math.Min(along, maxSide);
        // a door in a long wall past the programme along X: the programme reaches it
        if (!frame.AlongZ) progV = Math.Min(along, Math.Max(progV, doorV + 4f));
        if (along - progV < 3f) progV = along;
        float progW = Math.Min(across, maxSide);
        if (across - progW < 3f) progW = across;
        float uL = frame.AlongZ ? Fit(doorU - progW / 2, -across / 2, across / 2 - progW) : -across / 2;
        float uR = uL + progW;

        l.Type = BuildingType.Arcade;
        l.Shop = Loot.ShopType.None;
        l.Below = 0;
        l.Floors.Clear();
        l.Lifts.Clear();
        l.InnerDoors.Clear();
        var ground = new FloorPlan();
        l.Floors.Add(ground);
        float clear = l.StoreyHeight - Slab, top = Math.Min(2.05f, clear - 0.2f);

        // the service strip across the back: the public toilets (#1285) at the end away from the door, a store
        // and an office beside them, each off the hall
        bool service = progV >= 9f && progW >= 5.2f;
        float hallV1 = service ? progV - ArcadeService : progV;
        var hall = new RoomPlan { Type = billiard ? RoomType.BilliardHall : RoomType.ArcadeHall };
        SetRect(hall, frame.Rect(uL, 0, uR, hallV1));
        ground.Rooms.Add(hall);
        int storeRoom = -1;
        if (service)
        {
            // players: about one to 4 m2 of the hall
            int people = (int)(progW * hallV1 / 4f);
            bool farR = doorU <= (uL + uR) / 2;
            // a mall's plain hall behind is reached through the store: keep a store's width at the door's end
            float keep = along - progV >= 3f ? 1.6f : 0f;
            float t0 = farR ? uL + keep : uL, t1 = farR ? uR : uR - keep;
            var (_, rest) = PublicToilets(l, ground, 0, frame.Rect(t0, hallV1, t1, progV), frame.VMinus, frame.Point(farR ? uR : uL, progV), people);
            if (keep > 0)
            {
                var kept = frame.Rect(farR ? uL : uR - keep, hallV1, farR ? uL + keep : uR, progV);
                rest = rest == null ? kept : new RectPlan(Math.Min(rest.X0, kept.X0), Math.Min(rest.Z0, kept.Z0), Math.Max(rest.X1, kept.X1), Math.Max(rest.Z1, kept.Z1));
            }
            if (rest != null)
            {
                // a store, and an office beside it where the rest takes both
                bool alongX = frame.VMinus is Side.Front or Side.Back;
                float r0 = alongX ? rest.X0 : rest.Z0, r1 = alongX ? rest.X1 : rest.Z1;
                var program = r1 - r0 >= 6.4f ? new[] { RoomType.Storage, RoomType.Office } : new[] { RoomType.Storage };
                float each = (r1 - r0) / program.Length;
                for (int i = 0; i < program.Length; i++)
                {
                    float a = r0 + each * i, b = i == program.Length - 1 ? r1 : a + each;
                    var room = new RoomPlan { Type = program[i] };
                    SetRect(room, alongX ? new RectPlan(a, rest.Z0, b, rest.Z1) : new RectPlan(rest.X0, a, rest.X1, b));
                    ground.Rooms.Add(room);
                    int ri = ground.Rooms.Count - 1;
                    if (program[i] == RoomType.Storage) storeRoom = ri;
                    Opening(ground.Rooms, ri, frame.VMinus, 0, (a + b) / 2, InnerDoor, top, OpeningKind.Door);
                }
            }
        }

        // a plain hall past the programme in a mall: behind it, through the store (or the hall), and beside it
        if (along - progV >= 3f)
        {
            var back = new RoomPlan { Type = RoomType.Hall };
            SetRect(back, frame.Rect(uL, progV, uR, along));
            ground.Rooms.Add(back);
            int bi = ground.Rooms.Count - 1;
            int from = storeRoom >= 0 ? storeRoom : 0;
            var r = ground.Rooms[from];
            float c = (Math.Min(frame.U(r.X0, r.Z0), frame.U(r.X1, r.Z1)) + Math.Max(frame.U(r.X0, r.Z0), frame.U(r.X1, r.Z1))) / 2;
            r.Openings.Add(new OpeningPlan { Side = frame.VPlus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = bi });
            back.Openings.Add(new OpeningPlan { Side = frame.VMinus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = from });
        }
        if (progW < across)
        {
            bool right = uL <= -across / 2 + 0.01f;
            var side = new RoomPlan { Type = RoomType.Hall };
            SetRect(side, right ? frame.Rect(uR, 0, across / 2, along) : frame.Rect(-across / 2, 0, uL, along));
            ground.Rooms.Add(side);
            int si = ground.Rooms.Count - 1;
            var wallSide = right ? frame.UPlus : frame.UMinus;
            float c = frame.OnUWall(hallV1 / 2);
            hall.Openings.Add(new OpeningPlan { Side = wallSide, Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = si });
            side.Openings.Add(new OpeningPlan { Side = Opposite(wallSide), Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = 0 });
        }

        // ---- the way in --------------------------------------------------------------------------------
        l.EntryWidth = Math.Min(Math.Max(l.DoorWidth, 1.8f), W - 1.2f);
        l.EntryX = Fit(l.EntryX, -W / 2 + l.EntryWidth / 2 + 0.3f, W / 2 - l.EntryWidth / 2 - 0.3f);
        if (RoomAt(ground, l.EntryX, -D / 2 + 0.1f) != 0) return false;
        hall.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = l.EntryWidth, Bottom = 0,
            Top = Math.Min(Math.Max(doorHeight, 2.1f), l.ClearOf(hall) - 0.3f), Kind = OpeningKind.Entry,
        });
        // the hall is dark on purpose: no windows on it, whatever the facade; its back rooms may have some
        if (!whole) AddWindows(l, ground, 0);
        hall.Openings.RemoveAll(o => o.Kind == OpeningKind.Window);

        ArcadePlans.AddOrUpdate(l, new ArcadePlan { Frame = frame, DoorU = doorU, DoorV = doorV });
        return true;
    }

    /// <summary>
    /// The hall: carpet; by the door the prize counter, the change machine, the claw machines; the big
    /// machines along the side walls, pinballs along the back; islands of uprights in the middle; air hockey.
    /// </summary>
    private static void ArcadeLayout(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        if (!ArcadePlans.TryGetValue(l, out var p)) return;
        if (r.Type == RoomType.BilliardHall) { BilliardLayout(l, f, r, p, placed, blocked, rng); return; }
        var fr = p.Frame;
        Carpet(l, f, r, placed);
        var (dx, dz) = fr.Point(p.DoorU, p.DoorV);
        blocked.Add(new RectPlan(dx - 1.6f, dz - 1.6f, dx + 1.6f, dz + 1.6f).Clip(r));
        var near = Sides(fr, l, r);
        int budget = Math.Min(MaxMachines, (int)(r.Area * 0.15f));
        int Count() => l.Furniture.Count(x => x.Floor == f && IsMachine(x.Type));

        TryPlace(l, f, r, new Piece(FurnitureType.PrizeCounter, Math.Min(3.4f, Math.Max(2.2f, r.Width * 0.25f)), 2.2f, 2.3f, true),
            placed, blocked, rng, near.Door, near.DoorAt);
        TryPlace(l, f, r, new Piece(FurnitureType.ChangeMachine, 0.7f, 0.6f, 1.8f, true), placed, blocked, rng, near.Door, near.DoorAt);
        for (int i = 0; i < Math.Clamp((int)(r.Width / 4f), 2, 5); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.ClawMachine, 1.0f, 1.0f, 2.1f, true), placed, blocked, rng, near.Door, near.DoorAt);

        // the big machines along the sides, one wall then the other: racers, simulators, the dance
        // machine, the hoops, a drum game
        var sides = new[] { fr.UMinus, fr.UPlus };
        var big = new List<Piece>
        {
            new(FurnitureType.Racer, 1.6f, 2.3f, 2.0f, true), new(FurnitureType.Racer, 1.6f, 2.3f, 2.0f, true),
            new(FurnitureType.SimPod, 1.6f, 2.1f, 2.2f, true), new(FurnitureType.DanceMachine, 2.0f, 2.4f, 2.4f, true),
            new(FurnitureType.BasketballGame, 1.2f, 3.0f, 2.6f, true), new(FurnitureType.DrumGame, 1.2f, 1.4f, 1.9f, true),
            new(FurnitureType.Racer, 1.6f, 2.3f, 2.0f, true), new(FurnitureType.SimPod, 1.6f, 2.1f, 2.2f, true),
            new(FurnitureType.BasketballGame, 1.2f, 3.0f, 2.6f, true),
        };
        for (int i = 0; i < big.Count && Count() < budget; i++)
            TryPlace(l, f, r, big[i], placed, blocked, rng, new[] { sides[i % 2], sides[1 - i % 2] });
        // a row of pinballs along the back, an eighth of the machines at most
        int pinballs = Math.Clamp(Math.Min((int)(r.Width / 0.9f), budget / 8), 2, 12);
        for (int i = 0; i < pinballs && Count() < budget; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true), placed, blocked, rng, new[] { fr.VPlus });
        TryPlace(l, f, r, new Piece(FurnitureType.Bench, 1.6f, 0.5f, 0.45f, true), placed, blocked, rng);
        // the islands, leaving room in the budget for air hockey in the open floor between them
        int hockey = Math.Clamp((int)(r.Area / 150f), 1, 3);
        ArcadeIslands(l, f, r, fr, placed, blocked, budget - hockey - Count());
        // what the islands leave lines the walls, uprights side by side
        for (int i = 0; i < 40 && Count() < budget - hockey; i++)
        {
            int before = Count();
            TryPlace(l, f, r, new Piece(FurnitureType.ArcadeCabinet, 0.78f, 0.9f, 1.85f, true), placed, blocked, rng,
                new[] { sides[i % 2], sides[1 - i % 2], fr.VPlus });
            if (Count() == before) break;
        }
        for (int i = 0; i < hockey && Count() < budget; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.AirHockey, 1.2f, 2.1f, 0.9f, false), placed, blocked, rng);
        // a snack corner in a big hall: a counter, its stools (#1250)
        if (r.Area >= 150f) SnackBar(l, f, r, near.Far, placed, blocked, rng, 4f);
        // and what the islands left of the floor: game tables and high tables, so it is no empty hangar
        FillFloor(l, f, r, placed, blocked, 3.6f, 3.6f, 1.6f, (i, k) => ((i * 7 + k * 3) % 4) switch
        {
            0 when Count() < budget => GameTable(hockey: true),
            1 => HighTables,
            2 => GameTable(hockey: false),
            _ => null,
        });
    }

    /// <summary>A bar counter <paramref name="length"/> m long (shorter where that does not fit) with its stools, against <paramref name="sides"/>.</summary>
    private static void SnackBar(InteriorLayout l, int f, RoomPlan r, Side[] sides,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng, float length)
    {
        int before = l.Furniture.Count;
        foreach (float len in new[] { length, length * 0.7f, 2.4f })
        {
            TryPlace(l, f, r, new Piece(FurnitureType.BarCounter, len, 2.0f, 1.1f, true), placed, blocked, rng, sides);
            if (l.Furniture.Count > before) break;
        }
        if (l.Furniture.Count == before) return;
        Stools(l, f, r, l.Furniture[^1], placed, blocked);
    }

    /// <summary>
    /// A billiard hall [S: Bluff Billard Geneva, 8 pool tables]: pool tables in bays with the cue's swing
    /// round them, each under its lamp; the bar with its stools; lounges of armchairs and high tables in
    /// what the tables leave; darts, table football, a few machines and cue racks at the walls; plants.
    /// </summary>
    private static void BilliardLayout(InteriorLayout l, int f, RoomPlan r, ArcadePlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        var (dx, dz) = fr.Point(p.DoorU, p.DoorV);
        blocked.Add(new RectPlan(dx - 1.6f, dz - 1.6f, dx + 1.6f, dz + 1.6f).Clip(r));
        var near = Sides(fr, l, r);
        SnackBar(l, f, r, near.Far, placed, blocked, rng, Math.Clamp(r.Width * 0.3f, 3f, 6f));
        // the tables first, they are what the hall is for
        // at most a dozen [S: Bluff has 8]; the floor past them takes lounges
        int pools = 0;
        if (FillFloor(l, f, r, placed, blocked, PoolBay.W + 0.2f, PoolBay.D + 0.2f, 0.4f, (_, _) => pools++ < MaxPoolTables ? PoolBay : null, gap: 0.05f) == 0)
        {
            pools = 0;
            FillFloor(l, f, r, placed, blocked, PoolBay.W, PoolBay.D, 0.3f, (_, _) => pools++ < MaxPoolTables ? PoolBay : null, gap: 0f);
        }
        foreach (var piece in new[]
                 {
                     new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true),
                     new Piece(FurnitureType.TableFootball, 0.75f, 1.4f, 0.9f, false),
                     new Piece(FurnitureType.ArcadeCabinet, 0.78f, 0.9f, 1.85f, true), new Piece(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true),
                     new Piece(FurnitureType.Jukebox, 0.9f, 0.6f, 1.6f, true),
                 })
            TryPlace(l, f, r, piece, placed, blocked, rng);
        // the lounges and high tables in what the tables leave
        FillFloor(l, f, r, placed, blocked, 3.2f, 3.4f, 1.0f, (i, k) => (i + k) % 2 == 0 ? LoungeGroup : HighTables);
        for (int i = 0; i < Math.Clamp((int)(r.Area / 40f), 2, 8); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.CueRack, 0.6f, 0.12f, 1.6f, true), placed, blocked, rng);
        for (int i = 0; i < Math.Clamp((int)(r.Area / 60f), 2, 6); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
    }

    /// <summary>
    /// Islands of uprights back to back, three to eight a side, in a grid over the middle with 1.8 m
    /// aisles round each; <paramref name="left"/> machines at most, spread evenly over the grid, never
    /// bunched at the door. An island is placed whole where it and 0.9 m round it are free, or not at all.
    /// </summary>
    private static void ArcadeIslands(InteriorLayout l, int f, RoomPlan r, BowlFrame fr, List<RectPlan> placed, List<RectPlan> blocked, int left)
    {
        float u0 = Math.Min(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1)), u1 = Math.Max(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1));
        float v0 = Math.Min(fr.V(r.X0, r.Z0), fr.V(r.X1, r.Z1)), v1 = Math.Max(fr.V(r.X0, r.Z0), fr.V(r.X1, r.Z1));
        const float cabW = 0.78f, cabD = 0.9f;
        // the grid over the floor clear of the walls; the free test keeps the machines along them
        float iu0 = u0 + 1.0f, iu1 = u1 - 1.0f, iv0 = v0 + 3.0f, iv1 = v1 - 1.0f;
        if (left < 6 || iu1 - iu0 < 3 * cabW || iv1 - iv0 < 2 * cabD) return;
        float pitchV = 2 * cabD + ArcadeAisle;
        int rows = Math.Max(1, (int)((iv1 - iv0 + ArcadeAisle) / pitchV));
        // the island length that seats most of what the budget leaves
        int Segs(int n) => Math.Max(1, (int)((iu1 - iu0 + ArcadeAisle) / (n * cabW + ArcadeAisle)));
        int Wanted(int n) => Math.Min(Segs(n) * rows, Math.Max(1, left / (2 * n)));
        // tried in that order: a length that fits nowhere gives way to the next
        foreach (int per in Enumerable.Range(3, Math.Max(1, Math.Min(8, (int)((iu1 - iu0) / cabW)) - 2))
            .OrderByDescending(n => Wanted(n) * n).ThenByDescending(n => n))
            if (Islands(per) > 0) return;

        int Islands(int per)
        {
            int segs = Segs(per);
            float pitchU = per * cabW + ArcadeAisle;
            float su = iu0 + (iu1 - iu0 - (segs * pitchU - ArcadeAisle)) / 2, sv = iv0 + (iv1 - iv0 - (rows * pitchV - ArcadeAisle)) / 2;
            // the slots of a checkerboard first, then the others, each set spread evenly; any slot after those
            // when one of them is taken
            int slots = segs * rows, wanted = Wanted(per), done = 0;
            var first = Enumerable.Range(0, slots).Where(i => (i % segs + i / segs) % 2 == 0).ToList();
            var then = Enumerable.Range(0, slots).Where(i => (i % segs + i / segs) % 2 == 1).ToList();
            static IEnumerable<int> Spread(List<int> from, int n) => Enumerable.Range(0, Math.Clamp(n, 0, from.Count)).Select(k => from[(int)((k + 0.5f) * from.Count / Math.Min(n, from.Count))]);
            foreach (int slot in Spread(first, wanted).Concat(Spread(then, wanted - first.Count)).Concat(Enumerable.Range(0, slots)).Distinct())
            {
                if (done == wanted) break;
                float ua = su + slot % segs * pitchU, va = sv + slot / segs * pitchV;
                var (ax, az) = fr.Point(ua, va);
                var (bx, bz) = fr.Point(ua + per * cabW, va + 2 * cabD);
                var island = new RectPlan(Math.Min(ax, bx), Math.Min(az, bz), Math.Max(ax, bx), Math.Max(az, bz));
                if (!Free(r, island, placed, blocked, ArcadeAisle / 2)) continue;
                done++;
                for (int side = 0; side < 2; side++)
                    for (int c = 0; c < per; c++)
                    {
                        var (x, z) = fr.Point(ua + (c + 0.5f) * cabW, va + cabD / 2 + side * cabD);
                        int turns = side == 0 ? fr.FaceVMinus : fr.FaceVPlus;
                        Put(l, f, FurnitureType.ArcadeCabinet, x, z, turns, cabW, cabD, 1.85f, placed);
                    }
            }
            return done;
        }
    }

    private static bool IsMachine(FurnitureType t) => t is FurnitureType.ArcadeCabinet or FurnitureType.Pinball or FurnitureType.ClawMachine
        or FurnitureType.Racer or FurnitureType.SimPod or FurnitureType.DanceMachine or FurnitureType.DrumGame
        or FurnitureType.BasketballGame or FurnitureType.AirHockey;

    /// <summary>The arcade pieces at the sizes this file places them, for the model viewer.</summary>
    private static readonly Piece[] ArcadePieces =
    {
        new(FurnitureType.PrizeCounter, 3.0f, 2.2f, 2.3f, true),
        new(FurnitureType.ChangeMachine, 0.7f, 0.6f, 1.8f, true),
        new(FurnitureType.ClawMachine, 1.0f, 1.0f, 2.1f, true),
        new(FurnitureType.Racer, 1.6f, 2.3f, 2.0f, true),
        new(FurnitureType.SimPod, 1.6f, 2.1f, 2.2f, true),
        new(FurnitureType.DanceMachine, 2.0f, 2.4f, 2.4f, true),
        new(FurnitureType.DrumGame, 1.2f, 1.4f, 1.9f, true),
        new(FurnitureType.BasketballGame, 1.2f, 3.0f, 2.6f, true),
        new(FurnitureType.AirHockey, 1.2f, 2.1f, 0.9f, false),
        new(FurnitureType.CueRack, 0.6f, 0.12f, 1.6f, true),
    };

    /// <summary>A whole arcade in the model viewer.</summary>
    [Core.Showcase("Interiors", "Arcade")]
    private static Godot.Node3D ShowcaseArcade()
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, 18f, 24f, 5f, BuildingKind.Commercial) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Arcade, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", "Arcade"),
        };
        var layout = Generate(tile, 0, null, null)!;
        Venues.Override = null;
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        return node;
    }
}
