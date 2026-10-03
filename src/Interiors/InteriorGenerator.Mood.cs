using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// How a building is kept (#434, <see cref="InteriorMood"/>): the roll, the fancy house's
/// double-height living room and its extra pieces, and the clutter of a messy or abandoned one.
/// Every roll here has its own seed (<c>key|mood</c>, <c>key|tall</c>, <c>key|clutter</c>), so a
/// house's rooms and furniture stay where they were whatever its mood.
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>
    /// The building's mood: homes are mostly lived in, a few fancy, some messy, a few abandoned
    /// (more of those in the countryside, fewer fancy flats). What is not a home is just in use.
    /// </summary>
    public static InteriorMood MoodOf(string key, BuildingKind kind, bool rural, bool business)
    {
        if (business) return InteriorMood.Lived;
        double u = Core.Fnv.Unit(key + "|mood");
        // (fancy, messy, abandoned); lived in is the rest
        var (fancy, messy, abandoned) = kind switch
        {
            BuildingKind.House => rural ? (0.10, 0.22, 0.14) : (0.17, 0.16, 0.07),
            BuildingKind.Apartment => (0.06, 0.16, 0.04),
            BuildingKind.Other => (0.08, 0.16, 0.08),
            BuildingKind.Agricultural => (0.0, 0.30, 0.12),
            BuildingKind.Annex or BuildingKind.Garage => (0.0, 0.25, 0.12),
            _ => (0.0, 0.0, 0.0),
        };
        if (u < fancy) return InteriorMood.Fancy;
        if (u < fancy + messy) return InteriorMood.Messy;
        if (u < fancy + messy + abandoned) return InteriorMood.Abandoned;
        return InteriorMood.Lived;
    }

    /// <summary>Whether a fancy house gets its double-height living room: three in four with two storeys or more.</summary>
    private static bool Tall(InteriorLayout l, BuildingKind kind, int above) =>
        l.Mood == InteriorMood.Fancy && above >= 2 && kind is BuildingKind.House or BuildingKind.Other
        && Core.Fnv.Unit(l.Key + "|tall") < 0.75;

    /// <summary>
    /// The ground floor's living room (else its dining room) made two storeys tall, when it spans
    /// the whole width of its side (so the floor above can be planned round it in rectangles):
    /// its rectangle, or null.
    /// </summary>
    private static RectPlan? Lofty(FloorPlan floor, List<RectPlan> sides)
    {
        foreach (var type in new[] { RoomType.Living, RoomType.Dining })
            foreach (var r in floor.Rooms)
            {
                if (r.Type != type || r.Area < 12f) continue;
                if (!sides.Any(sd => Math.Abs(sd.X0 - r.X0) < 0.01f && Math.Abs(sd.X1 - r.X1) < 0.01f)) continue;
                r.Span = 2;
                return new RectPlan(r.X0, r.Z0, r.X1, r.Z1);
            }
        return null;
    }

    /// <summary>The sides of the storey above a double-height room, without its rectangle: the parts before and behind it 2.4 m deep or more.</summary>
    private static List<RectPlan> AroundLofty(List<RectPlan> sides, RectPlan v)
    {
        var left = new List<RectPlan>();
        foreach (var sd in sides)
        {
            if (v.X0 >= sd.X1 - 0.01f || v.X1 <= sd.X0 + 0.01f) { left.Add(sd); continue; }
            if (v.Z0 - sd.Z0 >= 2.4f) left.Add(new RectPlan(sd.X0, sd.Z0, sd.X1, v.Z0));
            if (sd.Z1 - v.Z1 >= 2.4f) left.Add(new RectPlan(sd.X0, v.Z1, sd.X1, sd.Z1));
        }
        return left;
    }

    /// <summary>What a fancy house adds to a room, after its own pieces: art, a mirror, a fireplace, a big rug, plants.</summary>
    private static IEnumerable<Piece> MoodPieces(InteriorMood mood, RoomType t, RoomPlan r)
    {
        if (mood != InteriorMood.Fancy) return Array.Empty<Piece>();
        // a picture or a mirror is drawn high on the wall but planned 2 m tall, so it keeps out of windows
        var painting = new Piece(FurnitureType.Painting, Math.Clamp(r.Width * 0.3f, 0.7f, 1.4f), 0.06f, 2.0f, true);
        return t switch
        {
            RoomType.Living => new[]
            {
                new Piece(FurnitureType.Fireplace, 1.5f, 0.55f, 1.15f, true),
                painting, painting with { W = 0.8f },
                new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.5f, true),
                new Piece(FurnitureType.Rug, Math.Min(2.8f, r.Width - 1.2f), Math.Min(2.0f, r.Depth - 1.2f), 0.02f, false),
            },
            RoomType.Dining => new[] { painting, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.5f, true) },
            RoomType.Bedroom or RoomType.GuestRoom => new[] { painting with { W = 0.9f }, new Piece(FurnitureType.Mirror, 0.6f, 0.05f, 2.0f, true) },
            RoomType.Hall or RoomType.Lobby or RoomType.Landing => new[] { new Piece(FurnitureType.Mirror, 0.7f, 0.05f, 2.0f, true), painting with { W = 0.8f } },
            RoomType.Study or RoomType.Office => new[] { painting with { W = 0.9f } },
            RoomType.Bathroom => new[] { new Piece(FurnitureType.Mirror, 0.6f, 0.05f, 2.0f, true) },
            _ => Array.Empty<Piece>(),
        };
    }

    /// <summary>What an abandoned house keeps whatever happens: the big pieces, what is built in, and what is locked or sold.</summary>
    private static bool Kept(FurnitureType t) => t is FurnitureType.Bed or FurnitureType.SingleBed or FurnitureType.BunkBed
        or FurnitureType.Sofa or FurnitureType.Table or FurnitureType.Counter or FurnitureType.Stove or FurnitureType.Fridge
        or FurnitureType.Toilet or FurnitureType.Sink or FurnitureType.Bathtub or FurnitureType.Wardrobe or FurnitureType.Piano
        or FurnitureType.Workbench or FurnitureType.Car or FurnitureType.HayBale or FurnitureType.WashingMachine
        or FurnitureType.GunLocker or FurnitureType.Safe or FurnitureType.VaultSafe or FurnitureType.TellerDesk
        or FurnitureType.ShopCounter or FurnitureType.VendingMachine or FurnitureType.WaterTank or FurnitureType.CinemaScreen
        || t >= FurnitureType.Bell && t <= FurnitureType.FrontPew || t == FurnitureType.ChurchRadio;

    /// <summary>
    /// A messy or abandoned house, after it is furnished: an abandoned one loses about half of what
    /// is not <see cref="Kept"/> (taken, or carted off), and every room gets its clutter.
    /// </summary>
    private static void Neglect(InteriorLayout l, List<(int Floor, RoomPlan Room, List<RectPlan> Placed, List<RectPlan> Blocked)> rooms)
    {
        if (l.Mood is not (InteriorMood.Messy or InteriorMood.Abandoned)) return;
        var rng = new Random(StableHash(l.Key + "|clutter"));
        bool abandoned = l.Mood == InteriorMood.Abandoned;
        if (abandoned)
        {
            var gone = new HashSet<FurniturePlan>();
            foreach (var f in l.Furniture)
                if (!Kept(f.Type) && rng.NextDouble() < 0.5) gone.Add(f);
            l.Furniture.RemoveAll(gone.Contains);
            // the rooms' placed lists still hold what went: the clutter may lie where it stood
            foreach (var c in rooms) c.Placed.Clear();
            foreach (var f in l.Furniture)
                foreach (var c in rooms)
                    if (c.Floor == f.Floor && f.X > c.Room.X0 && f.X < c.Room.X1 && f.Z > c.Room.Z0 && f.Z < c.Room.Z1)
                        c.Placed.Add(Footprint(f));
        }
        foreach (var (f, r, placed, blocked) in rooms)
        {
            if (r.Type is RoomType.Nave or RoomType.Belfry or RoomType.Vault or RoomType.BankHall or RoomType.Shop) continue;
            float clear = l.ClearOf(r);
            int Count(int min, int max) => rng.Next(min, max + 1);
            if (abandoned)
            {
                // cobwebs up in the corners, dust and rubbish on the floor
                Cobwebs(l, f, r, clear, rng);
                Scatter(l, f, r, FurnitureType.DirtPatch, Count(2, 4), placed, blocked, rng, onTop: true);
                Scatter(l, f, r, FurnitureType.Trash, Count(1, 3), placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.Papers, Count(0, 2), placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.Bottles, Count(0, 2), placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.Plank, rng.NextDouble() < 0.4 ? 1 : 0, placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.FallenChair, rng.NextDouble() < 0.35 ? 1 : 0, placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.ClothesPile, rng.NextDouble() < 0.3 ? 1 : 0, placed, blocked, rng);
            }
            else
            {
                bool clothes = r.Type is RoomType.Bedroom or RoomType.GuestRoom or RoomType.Bathroom or RoomType.Living
                    or RoomType.Playroom or RoomType.Laundry or RoomType.Hall or RoomType.Landing;
                bool papers = r.Type is RoomType.Study or RoomType.Office or RoomType.Living or RoomType.Bedroom or RoomType.Playroom;
                bool bottles = r.Type is RoomType.Kitchen or RoomType.Living or RoomType.Carnotzet or RoomType.Cellar or RoomType.HomeCinema;
                if (clothes) Scatter(l, f, r, FurnitureType.ClothesPile, Count(1, 4), placed, blocked, rng);
                if (papers) Scatter(l, f, r, FurnitureType.Papers, Count(0, 2), placed, blocked, rng);
                if (bottles) Scatter(l, f, r, FurnitureType.Bottles, Count(1, 3), placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.Trash, Count(0, 2), placed, blocked, rng);
                Scatter(l, f, r, FurnitureType.DirtPatch, Count(0, 1), placed, blocked, rng, onTop: true);
                if (rng.NextDouble() < 0.25) Cobwebs(l, f, r, clear, rng, 1);
            }
        }
    }

    /// <summary>A piece's footprint on its floor, turned.</summary>
    private static RectPlan Footprint(FurniturePlan f)
    {
        bool odd = (f.Turns & 1) == 1;
        float w = (odd ? f.D : f.W) / 2, d = (odd ? f.W : f.D) / 2;
        return new RectPlan(f.X - w, f.Z - d, f.X + w, f.Z + d);
    }

    /// <summary>Clutter's size: (W, D, H).</summary>
    private static (float W, float D, float H) SizeOf(FurnitureType t, Random rng) => t switch
    {
        FurnitureType.DirtPatch => ((float)(0.6 + rng.NextDouble() * 1.0), (float)(0.5 + rng.NextDouble() * 0.9), 0.006f),
        FurnitureType.ClothesPile => ((float)(0.45 + rng.NextDouble() * 0.35), (float)(0.35 + rng.NextDouble() * 0.3), 0.16f),
        FurnitureType.Papers => (0.5f, 0.4f, 0.02f),
        FurnitureType.Bottles => (0.35f, 0.3f, 0.3f),
        FurnitureType.Plank => ((float)(1.2 + rng.NextDouble() * 0.8), 0.18f, 0.05f),
        FurnitureType.FallenChair => (0.5f, 0.95f, 0.45f),
        _ => (0.45f, 0.4f, 0.2f),   // trash
    };

    /// <summary>
    /// Drops <paramref name="count"/> pieces of clutter anywhere on the room's free floor (a few
    /// tries each), turned any way, never in a doorway's path. <paramref name="onTop"/>: a dirt
    /// patch lies under anything, and nothing keeps clear of it.
    /// </summary>
    private static void Scatter(InteriorLayout l, int f, RoomPlan r, FurnitureType type, int count,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng, bool onTop = false)
    {
        const float inset = WallInset + 0.05f;
        for (int n = 0; n < count; n++)
        {
            var (w, d, h) = SizeOf(type, rng);
            int turns = rng.Next(4);
            bool odd = (turns & 1) == 1;
            float rw = odd ? d : w, rd = odd ? w : d;
            if (r.Width - 2 * inset < rw || r.Depth - 2 * inset < rd) continue;
            for (int tryAt = 0; tryAt < 8; tryAt++)
            {
                float x = r.X0 + inset + rw / 2 + (float)rng.NextDouble() * (r.Width - 2 * inset - rw);
                float z = r.Z0 + inset + rd / 2 + (float)rng.NextDouble() * (r.Depth - 2 * inset - rd);
                var rect = new RectPlan(x - rw / 2, z - rd / 2, x + rw / 2, z + rd / 2);
                if (blocked.Any(b => b.Overlaps(rect))) continue;
                if (!onTop && placed.Any(p => p.Overlaps(rect))) continue;
                if (!onTop) placed.Add(rect);
                l.Furniture.Add(new FurniturePlan { Type = type, Floor = f, X = x, Z = z, Turns = turns, W = w, D = d, H = h });
                break;
            }
        }
    }

    /// <summary>Cobwebs in the room's upper corners: up to <paramref name="most"/> of the four, hung from the ceiling.</summary>
    private static void Cobwebs(InteriorLayout l, int f, RoomPlan r, float clear, Random rng, int most = 4)
    {
        const float t = WallInset + 0.02f;
        // a corner's two walls are its piece's back (-Z) and left (-X), turned to face into the
        // room; the piece is square, centred half its size out of the corner (sx, sz into the room)
        var corners = new (float X, float Z, int Sx, int Sz, int Turns)[]
        {
            (r.X0 + t, r.Z0 + t, 1, 1, 0), (r.X1 - t, r.Z0 + t, -1, 1, 3), (r.X1 - t, r.Z1 - t, -1, -1, 2), (r.X0 + t, r.Z1 - t, 1, -1, 1),
        };
        int made = 0;
        foreach (var (x, z, sx, sz, turns) in corners)
        {
            if (made >= most || rng.NextDouble() < 0.3) continue;
            float size = (float)(0.35 + rng.NextDouble() * 0.35);
            l.Furniture.Add(new FurniturePlan
            {
                Type = FurnitureType.Cobweb, Floor = f, X = x + sx * size / 2, Z = z + sz * size / 2, Turns = turns,
                W = size, D = size, H = size * 0.8f, Lift = clear - size * 0.8f,
            });
            made++;
        }
    }
}
