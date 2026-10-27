using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Restaurants, cafés, bars and clubs (#1242, part 5 of #1232): every dining venue a solid holds takes
/// a ground-floor room the building's own planner made (a shop unit under flats, a house's living room,
/// a commercial ground floor) and makes it its guest room, by its kind; the kitchen or store off it
/// becomes its kitchen, a house's dining room a second guest room (the Säli). Numbers are in
/// <c>docs/notes/terrain/venue-dining.md</c>, the rules in <c>docs/notes/terrain/dining.md</c>.
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>
    /// Set while a block of flats holding a dining venue is planned: its ground floor is shop units, as
    /// a mixed block's, for the venues to take. Per thread: tiles are planned on several.
    /// </summary>
    [ThreadStatic] internal static bool DiningShops;

    /// <summary>A dining venue's guest room (not its kitchen).</summary>
    internal static bool DiningRoom(RoomType t) => t is RoomType.Gaststube or RoomType.Pizzeria or RoomType.CafeRoom
        or RoomType.FastFoodRoom or RoomType.KebabRoom or RoomType.BarRoom or RoomType.PubRoom or RoomType.ClubRoom;

    /// <summary>The guest room a venue makes, from its kind and its cuisine.</summary>
    internal static RoomType DiningRoomOf(VenueRecord v)
    {
        // the cuisine, else the name: many a pizzeria is tagged only by what it is called
        string c = (v.Cuisine + " " + v.Name).ToLowerInvariant().Replace("döner", "kebab").Replace("doner", "kebab").Replace("pizz", "pizza");
        return v.Type switch
        {
            VenueType.Restaurant when c.Contains("pizza") || c.Contains("italian") => RoomType.Pizzeria,
            VenueType.Restaurant or VenueType.FastFood when c.Contains("kebab") => RoomType.KebabRoom,
            VenueType.Restaurant when c.Contains("burger") => RoomType.FastFoodRoom,
            VenueType.Restaurant => RoomType.Gaststube,
            VenueType.Cafe => RoomType.CafeRoom,
            VenueType.FastFood => RoomType.FastFoodRoom,
            VenueType.Bar => RoomType.BarRoom,
            VenueType.Nightclub => RoomType.ClubRoom,
            _ => RoomType.PubRoom,   // pub, beer garden
        };
    }

    /// <summary>A guest room smaller than this is no venue's: a kebab fits in less than a Beiz.</summary>
    private static float DiningMinArea(RoomType t) => t is RoomType.KebabRoom or RoomType.FastFoodRoom or RoomType.CafeRoom ? 9f : 14f;

    /// <summary>
    /// How much a room is wanted as a guest room, lower first: a shop, then a living room, a dining room,
    /// an office, a classroom; a hall last (it is the house's stair hall, where few tables fit).
    /// </summary>
    private static int GuestRank(RoomType t) => t switch
    {
        RoomType.Shop => 0,
        RoomType.Living => 1,
        RoomType.Dining => 2,
        RoomType.Office => 3,
        RoomType.Classroom => 4,
        _ => 5,
    };

    /// <summary>
    /// Each dining venue of the solid takes a ground-floor room, in list order, one room each: the best
    /// kind (<see cref="GuestRank"/>), then the nearest its point, then the biggest. Shop units only in a
    /// block of flats (its living rooms are flats'), any public or living room elsewhere. A venue with no
    /// room left, or none big enough, is not drawn.
    /// </summary>
    private static void DiningUnits(InteriorLayout l, Footprint fp, IReadOnlyList<VenueRecord>? dining)
    {
        if (dining is not { Count: > 0 } || l.Floors.Count == 0) return;
        if (l.Type is not (BuildingType.None or BuildingType.Apartments or BuildingType.MixedUse)) return;
        bool apt = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
        var rooms = l.GroundFloor.Rooms;
        var tile = new TileId(fp.Key.TileE, fp.Key.TileN);
        var taken = new HashSet<int>();
        foreach (var v in dining)
        {
            // the venue's point in the plan's frame: +X along AxisU, +Z along AxisV, from the centre
            var d = new Vector2((float)(v.E - tile.MinE), (float)(tile.MaxN - v.N)) - fp.Center;
            float px = d.Dot(fp.AxisU), pz = d.Dot(fp.AxisV);
            var guest = DiningRoomOf(v);
            int best = -1;
            (int Rank, float Dist) bestD = (int.MaxValue, float.MaxValue);
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (taken.Contains(i) || r.Area < DiningMinArea(guest)) continue;
                bool fits = apt ? r.Type == RoomType.Shop
                    : r.Type is RoomType.Shop or RoomType.Living or RoomType.Dining or RoomType.Office
                        or RoomType.Classroom or RoomType.Hall;
                if (!fits) continue;
                float dx = Math.Max(0, Math.Max(r.X0 - px, px - r.X1)), dz = Math.Max(0, Math.Max(r.Z0 - pz, pz - r.Z1));
                var score = (GuestRank(r.Type), dx * dx + dz * dz - r.Area * 1e-4f);
                if (score.CompareTo(bestD) < 0) (best, bestD) = (i, score);
            }
            if (best < 0) continue;
            rooms[best].Type = guest;
            taken.Add(best);
            // through its doors: the first kitchen, store or pantry becomes its kitchen, a dining room its Säli
            bool kitchen = false;
            foreach (var o in rooms[best].Openings)
            {
                if (o.Kind != OpeningKind.Door || o.Other < 0 || o.Other >= rooms.Count || taken.Contains(o.Other)) continue;
                var next = rooms[o.Other];
                if (!kitchen && next.Type is RoomType.Kitchen or RoomType.Storage or RoomType.Pantry && next.Area >= 5f)
                {
                    next.Type = RoomType.GastroKitchen;
                    taken.Add(o.Other);
                    kitchen = true;
                }
                else if (!apt && next.Type == RoomType.Dining && next.Area >= DiningMinArea(guest))
                {
                    next.Type = guest;
                    taken.Add(o.Other);
                }
            }
            // a house's kitchen across its hall is the restaurant's all the same
            if (!kitchen && !apt)
            {
                var g = rooms[best];
                int k = Enumerable.Range(0, rooms.Count).Where(i => !taken.Contains(i) && rooms[i].Type == RoomType.Kitchen)
                    .OrderBy(i => Math.Abs((rooms[i].X0 + rooms[i].X1 - g.X0 - g.X1) / 2) + Math.Abs((rooms[i].Z0 + rooms[i].Z1 - g.Z0 - g.Z1) / 2))
                    .DefaultIfEmpty(-1).First();
                if (k >= 0)
                {
                    rooms[k].Type = RoomType.GastroKitchen;
                    taken.Add(k);
                }
            }
        }
        // the shop counter a shop building is promised goes to its shop, never into a restaurant
        if (taken.Count > 0 && l.Shop != Loot.ShopType.None && !l.Floors.Any(fl => fl.Rooms.Any(r => r.Type == RoomType.Shop)))
            l.Shop = Loot.ShopType.None;
    }

    /// <summary>
    /// A guest room by its kind: what stands at its walls (the bar or the buffet, the pizza oven, the
    /// pastry case, the order counter, the DJ booth, banquettes), the games, then tables in a grid over
    /// what is left. A kitchen: the cook line under its hood, prep tables, fridges, the sink.
    /// </summary>
    private static void DiningLayout(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var rooms = l.Floors[f].Rooms;
        string key = $"{l.Key}|{f}|{rooms.IndexOf(r)}";
        double Odds(string what) => Core.Fnv.Unit(key + "|" + what);
        // the way in stays clear 2 m into the room from the street door, 1.2 m from an inner one
        foreach (var o in r.Openings)
            if (o.Kind is OpeningKind.Entry or OpeningKind.Door) blocked.Add(Walk(r, o, o.Kind == OpeningKind.Entry ? 2.0f : 1.2f));

        if (r.Type == RoomType.GastroKitchen)
        {
            var guest = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count && DiningRoom(rooms[o.Other].Type));
            var back = guest != null ? Opposite(guest.Side) : Side.Back;
            var line = new[] { back, Turn(back), Opposite(Turn(back)), guest?.Side ?? Side.Front };
            // the line under its hood keeps out of the windows: shorter, on an inner wall, when the long one finds none
            foreach (float len in new[] { Math.Clamp(r.Width * 0.6f, 1.6f, 4.5f), 1.6f, 1.2f })
            {
                int before = l.Furniture.Count;
                TryPlace(l, f, r, new Piece(FurnitureType.CookLine, len, 0.9f, 2.3f, true), placed, blocked, rng, line);
                if (l.Furniture.Count > before) break;
            }
            TryPlace(l, f, r, new Piece(FurnitureType.Sink, 1.2f, 0.65f, 0.9f, true), placed, blocked, rng, line);
            TryPlace(l, f, r, new Piece(FurnitureType.Fridge, 0.7f, 0.75f, 2.0f, true), placed, blocked, rng, line);
            TryPlace(l, f, r, new Piece(FurnitureType.Counter, 1.6f, 0.7f, 0.9f, true), placed, blocked, rng, line);
            if (r.Area >= 12f) TryPlace(l, f, r, new Piece(FurnitureType.Fridge, 0.7f, 0.75f, 2.0f, true), placed, blocked, rng, line);
            if (r.Area >= 16f) TryPlace(l, f, r, new Piece(FurnitureType.Counter, 1.6f, 0.7f, 0.9f, false), placed, blocked, rng);
            TryPlace(l, f, r, new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true), placed, blocked, rng, line);
            return;
        }

        var entry = r.Openings.Where(o => o.Kind is OpeningKind.Entry or OpeningKind.Door)
            .OrderByDescending(o => o.Kind == OpeningKind.Entry).FirstOrDefault();
        var toKitchen = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count
            && rooms[o.Other].Type == RoomType.GastroKitchen);
        var inSide = entry?.Side ?? Side.Front;
        // the bar stands by the kitchen's door (the Buffet's swing door), else facing the way in
        var barSide = toKitchen?.Side ?? Opposite(inSide);
        float barAt = toKitchen?.Center ?? float.NaN;
        var far = new[] { Opposite(inSide), Turn(inSide), Opposite(Turn(inSide)) };
        var walls = new[] { Opposite(inSide), Turn(inSide), Opposite(Turn(inSide)), inSide };
        var barSides = new[] { barSide }.Concat(walls.Where(s => s != barSide)).ToArray();

        void Bar(float len, bool stools)
        {
            int before = l.Furniture.Count;
            foreach (float n in new[] { len, Math.Min(len, 2.4f), 1.8f })
            {
                TryPlace(l, f, r, new Piece(FurnitureType.BarCounter, n, 2.2f, 2.2f, true), placed, blocked, rng, barSides, barAt);
                if (l.Furniture.Count > before) break;
            }
            if (stools && l.Furniture.Count > before) Stools(l, f, r, l.Furniture[^1], placed, blocked);
        }
        void Along(Piece p, int max, Side[] sides)
        {
            for (int i = 0; i < max; i++)
            {
                int before = l.Furniture.Count;
                TryPlace(l, f, r, p, placed, blocked, rng, sides);
                if (l.Furniture.Count == before) break;
            }
        }
        // the games by the research's odds: what hangs or stands at a wall before the tables, what
        // stands free (table football, pool) in the floor the tables leave
        void WallGames(double pinball, double darts, double jukebox)
        {
            if (Odds("darts") < darts) TryPlace(l, f, r, new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), placed, blocked, rng, far);
            if (Odds("pinball") < pinball) TryPlace(l, f, r, new Piece(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true), placed, blocked, rng, walls);
            if (Odds("jukebox") < jukebox) TryPlace(l, f, r, new Piece(FurnitureType.Jukebox, 0.8f, 0.6f, 1.5f, true), placed, blocked, rng, walls);
        }
        void FloorGames(double foosball, double pool)
        {
            if (Odds("foosball") < foosball) TryPlace(l, f, r, new Piece(FurnitureType.TableFootball, 1.5f, 0.8f, 0.92f, false), placed, blocked, rng);
            if (Odds("pool") < pool && Math.Min(r.Width, r.Depth) >= 4.0f && r.Area >= 36f)
                TryPlace(l, f, r, new Piece(FurnitureType.PoolTable, 1.4f, 2.7f, 0.8f, false), placed, blocked, rng);
        }

        switch (r.Type)
        {
            case RoomType.Gaststube:
                // the Buffet, the Stammtisch near it, a tiled stove sometimes, benches round the walls
                Bar(Math.Clamp(r.Width * 0.3f, 2.0f, 4.0f), stools: r.Area >= 40f);
                if (Odds("stove") < 0.35) TryPlace(l, f, r, new Piece(FurnitureType.Kachelofen, 1.2f, 0.9f, 1.7f, true), placed, blocked, rng, far);
                if (r.Area >= 30f) TryPlace(l, f, r, new Piece(FurnitureType.TableSet, 1.9f, 1.9f, 0.76f, false), placed, blocked, rng);
                WallGames(0.03, 0.10, 0.02);
                FloorGames(0.15, 0.03);
                Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.7f, 1.7f, 2.5f, 2.5f);
                break;
            case RoomType.Pizzeria:
                TryPlace(l, f, r, new Piece(FurnitureType.PizzaOven, 1.8f, 1.6f, 2.4f, true), placed, blocked, rng, barSides, barAt);
                Bar(2.4f, stools: false);
                TryPlace(l, f, r, new Piece(FurnitureType.WineRack, 1.2f, 0.4f, 1.9f, true), placed, blocked, rng, walls);
                Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.7f, 1.7f, 2.5f, 2.5f);
                break;
            case RoomType.CafeRoom:
                // the pastry case and the coffee bar by the door, little tables for two
                // counter-high in the plan, so a shopfront's windows do not keep it off the wall
                TryPlace(l, f, r, new Piece(FurnitureType.PastryCase, Math.Clamp(r.Width * 0.4f, 2.2f, 4.0f), 1.8f, 1.0f, true),
                    placed, blocked, rng, new[] { Turn(inSide), Opposite(Turn(inSide)), Opposite(inSide) });
                Along(new Piece(FurnitureType.BanquetteSet, 1.2f, 1.45f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.0f, 1.5f, 1.8f, 2.2f);
                TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
                break;
            case RoomType.FastFoodRoom:
            case RoomType.KebabRoom:
                // the order counter facing the door, fixed booths round the walls, tables for two
                var counter = r.Type == RoomType.KebabRoom ? FurnitureType.KebabCounter : FurnitureType.OrderCounter;
                // counter-high in the plan (its menu boards hang over the back line at their own height), so
                // a shopfront's windows do not keep it off the wall
                TryPlace(l, f, r, new Piece(counter, Math.Clamp(r.Width * 0.5f, 2.2f, 5.0f), 2.0f, 1.05f, true), placed, blocked, rng, walls);
                Along(new Piece(FurnitureType.BanquetteSet, 1.2f, 1.45f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.0f, 1.5f, 1.9f, 2.2f);
                break;
            case RoomType.BarRoom:
            case RoomType.PubRoom:
            {
                bool pub = r.Type == RoomType.PubRoom;
                Bar(Math.Clamp(r.Width * 0.45f, 3.0f, 8.0f), stools: true);
                // a small stage on the wall facing the bar, in a room big enough for a crowd before it
                if (r.Area >= 60f && Odds("stage") < (pub ? 0.25 : 0.4))
                    TryPlace(l, f, r, new Piece(FurnitureType.Stage, Math.Clamp(r.Width * 0.4f, 3.0f, 6.0f), 2.5f, 0.4f, true),
                        placed, blocked, rng, new[] { Opposite(barSide), Turn(barSide), Opposite(Turn(barSide)) });
                WallGames(pub ? 0.20 : 0.40, pub ? 0.55 : 0.35, pub ? 0.20 : 0.15);
                if (pub) Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 4, far);
                Tables(l, f, r, placed, blocked, 1.4f, 1.4f, 2.2f, 2.2f, FurnitureType.HighTable, 1.05f);
                FloorGames(pub ? 0.40 : 0.55, pub ? 0.45 : 0.35);
                break;
            }
            case RoomType.ClubRoom:
            {
                // the dance floor in the middle, the DJ at one end of it, the bar along a side, sofas round
                float fw = Math.Max(2.4f, r.Width * 0.45f), fd = Math.Max(2.4f, r.Depth * 0.45f);
                var floor = new RectPlan((r.X0 + r.X1 - fw) / 2, (r.Z0 + r.Z1 - fd) / 2, (r.X0 + r.X1 + fw) / 2, (r.Z0 + r.Z1 + fd) / 2);
                if (Free(r, floor, placed, blocked, 0f))
                {
                    Put(l, f, FurnitureType.DanceFloor, (r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2, 0, fw, fd, 0.02f, placed);
                    blocked.Add(floor);
                }
                Bar(Math.Clamp(r.Width * 0.4f, 3.0f, 10.0f), stools: true);
                TryPlace(l, f, r, new Piece(FurnitureType.DjBooth, 2.4f, 1.3f, 1.3f, true), placed, blocked, rng,
                    new[] { Opposite(barSide), Turn(barSide), Opposite(Turn(barSide)) });
                Along(new Piece(FurnitureType.Settee, 2.0f, 0.9f, 1.0f, true), 6, walls);
                break;
            }
        }
        TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
    }

    /// <summary>The next side round: front, right, back, left.</summary>
    private static Side Turn(Side s) => (Side)(((int)s + 1) % 4);

    /// <summary>The strip in front of an opening, <paramref name="deep"/> m into the room, a hand wider than it.</summary>
    private static RectPlan Walk(RoomPlan r, OpeningPlan o, float deep)
    {
        float a = o.Center - o.Width / 2 - 0.2f, b = o.Center + o.Width / 2 + 0.2f;
        return (o.Side switch
        {
            Side.Front => new RectPlan(a, r.Z0, b, r.Z0 + deep),
            Side.Back => new RectPlan(a, r.Z1 - deep, b, r.Z1),
            Side.Left => new RectPlan(r.X0, a, r.X0 + deep, b),
            _ => new RectPlan(r.X1 - deep, a, r.X1, b),
        }).Clip(r);
    }

    /// <summary>
    /// Tables (with their chairs or stools) in a grid over the room, <paramref name="pitchX"/> by
    /// <paramref name="pitchZ"/> apart, each where it is free and leaves a way through. The grid is tried
    /// centred and shifted half a pitch each way, and laid where it seats the most: a centred row can fall
    /// right on a bar's stools.
    /// </summary>
    private static void Tables(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked,
        float w, float d, float pitchX, float pitchZ, FurnitureType type = FurnitureType.TableSet, float h = 0.76f)
    {
        const float margin = 0.5f;
        List<(float X, float Z)> Lay(float sx, float sz, List<RectPlan> taken)
        {
            var spots = new List<(float, float)>();
            int nx = (int)((r.Width - 2 * margin - w - Math.Abs(sx)) / pitchX) + 1, nz = (int)((r.Depth - 2 * margin - d - Math.Abs(sz)) / pitchZ) + 1;
            if (nx < 1 || nz < 1) return spots;
            float x0 = (r.X0 + r.X1) / 2 + sx / 2 - (nx - 1) * pitchX / 2, z0 = (r.Z0 + r.Z1) / 2 + sz / 2 - (nz - 1) * pitchZ / 2;
            for (int i = 0; i < nx; i++)
                for (int k = 0; k < nz; k++)
                {
                    float x = x0 + i * pitchX, z = z0 + k * pitchZ;
                    var rect = new RectPlan(x - w / 2, z - d / 2, x + w / 2, z + d / 2);
                    if (!Free(r, rect, taken, blocked, 0.3f) || !KeepsWay(r, taken, rect)) continue;
                    taken.Add(rect);
                    spots.Add((x, z));
                }
            return spots;
        }
        var best = new List<(float X, float Z)>();
        foreach (var (sx, sz) in new[] { (0f, 0f), (pitchX / 2, 0f), (0f, pitchZ / 2), (pitchX / 2, pitchZ / 2), (-pitchX / 2, 0f), (0f, -pitchZ / 2) })
        {
            var spots = Lay(sx, sz, new List<RectPlan>(placed));
            if (spots.Count > best.Count) best = spots;
        }
        foreach (var (x, z) in best) Put(l, f, type, x, z, 0, w, d, h, placed);
    }

    /// <summary>The dining pieces at the sizes this file places them, for the model viewer.</summary>
    private static readonly Piece[] DiningPieces =
    {
        new(FurnitureType.TableSet, 1.7f, 1.7f, 0.76f, false),
        new(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true),
        new(FurnitureType.HighTable, 1.4f, 1.4f, 1.05f, false),
        new(FurnitureType.Kachelofen, 1.2f, 0.9f, 1.7f, true),
        new(FurnitureType.PizzaOven, 1.8f, 1.6f, 2.4f, true),
        new(FurnitureType.PastryCase, 3.0f, 1.8f, 1.0f, true),
        new(FurnitureType.OrderCounter, 3.6f, 2.0f, 1.05f, true),
        new(FurnitureType.KebabCounter, 3.0f, 2.0f, 1.05f, true),
        new(FurnitureType.TableFootball, 1.5f, 0.8f, 0.92f, false),
        new(FurnitureType.Jukebox, 0.8f, 0.6f, 1.5f, true),
        new(FurnitureType.Stage, 4.0f, 2.5f, 0.4f, true),
        new(FurnitureType.DjBooth, 2.4f, 1.3f, 1.3f, true),
        new(FurnitureType.DanceFloor, 4.0f, 4.0f, 0.02f, false),
        new(FurnitureType.CookLine, 3.0f, 0.9f, 2.3f, true),
    };

    /// <summary>Whole venues in the model viewer: a hand-made building holding one of each kind.</summary>
    [Core.Showcase("Interiors")]
    private static IEnumerable<(string, Func<Godot.Node3D>)> ShowcaseDining()
    {
        foreach (var (name, type, cuisine) in new[]
        {
            ("Gaststube", VenueType.Restaurant, "regional"), ("Pizzeria", VenueType.Restaurant, "pizza"),
            ("Café", VenueType.Cafe, ""), ("Kebab", VenueType.FastFood, "kebab"), ("Bar", VenueType.Bar, ""),
            ("Pub", VenueType.Pub, ""), ("Club", VenueType.Nightclub, ""),
        })
            yield return ($"Dining - {name}", () =>
            {
                var id = new TileId(2600, 1200);
                var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, 12f, 16f, 4f, BuildingKind.Commercial) } };
                Venues.Override = new List<VenueRecord> { new("n1", type, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, cuisine, name) };
                InteriorLayout layout;
                try { layout = Generate(tile, 0, null, null)!; }
                finally { Venues.Override = null; }
                var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
                return node;
            });
    }
}
