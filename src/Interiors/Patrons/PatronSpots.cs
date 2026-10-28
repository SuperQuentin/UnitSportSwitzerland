using Godot;

namespace UnitSport.Interiors.Patrons;

/// <summary>How a patron holds itself on its spot.</summary>
public enum PatronPose
{
    /// <summary>On a chair, a bench or a cinema seat: <see cref="PatronSpot.At"/> is the hip.</summary>
    Chair,
    /// <summary>On a bar stool, feet on its ring: <see cref="PatronSpot.At"/> is the hip.</summary>
    Stool,
    /// <summary>On its feet: <see cref="PatronSpot.At"/> is the feet.</summary>
    Stand,
}

/// <summary>
/// One place a patron can be (#1244), in the frame its node is built in (an interior's, a terrace's): where (the hip
/// seated, the feet standing), facing <see cref="Yaw"/> (a figure looks down its node's −Z, so the facing direction
/// <c>f</c> is <c>atan2(−f.x, −f.z)</c>), on a floor at <see cref="Floor"/>. Places of one <see cref="Group"/> (a
/// table, a pair at a pool table, a queue) come and go together; <see cref="Place"/> numbers them in facing pairs.
/// </summary>
public readonly record struct PatronSpot(uint Group, int Place, int Places, Vector3 At, float Yaw, float Floor,
    PatronPose Pose, PatronActivity Activity, PatronVenue Venue, int Piece, int Room);

/// <summary>
/// Where patrons go (#1244), read off the furniture as its meshes draw it (<see cref="InteriorMeshBuilder"/>'s dining,
/// venue and arcade pieces, <see cref="TerraceProps"/>), so a figure sits on the chair that is there. Pure: the same
/// plan gives every peer the same spots.
/// </summary>
public static class PatronSpots
{
    /// <summary>A hip over a chair's or a bench's seat, m (seat 0.45-0.47 high).</summary>
    public const float ChairHip = 0.52f;
    /// <summary>A hip on a bar stool (seat 0.78 high).</summary>
    public const float StoolHip = 0.84f;
    /// <summary>How far apart a queue stands, m.</summary>
    public const float QueuePitch = 0.75f;
    /// <summary>The most a counter's queue holds.</summary>
    public const int QueueMost = 5;

    /// <summary>The venue a room keeps the hours of, or null for a room nobody comes to (a kitchen, a flat).</summary>
    public static PatronVenue? VenueOf(RoomType room) => room switch
    {
        RoomType.Gaststube => PatronVenue.Beiz,
        RoomType.Pizzeria => PatronVenue.Pizzeria,
        RoomType.CafeRoom => PatronVenue.Cafe,
        RoomType.FastFoodRoom or RoomType.KebabRoom => PatronVenue.FastFood,
        RoomType.BarRoom or RoomType.BilliardHall => PatronVenue.Bar,
        RoomType.PubRoom => PatronVenue.Pub,
        RoomType.ClubRoom => PatronVenue.Club,
        RoomType.BowlingLanes or RoomType.Concourse or RoomType.Mezzanine => PatronVenue.Bowling,
        RoomType.ArcadeHall => PatronVenue.Arcade,
        RoomType.CinemaLobby or RoomType.Auditorium => PatronVenue.Cinema,
        _ => null,
    };

    /// <summary>What a party at a table does there, by the venue.</summary>
    public static PatronActivity TableActivity(PatronVenue v) => v switch
    {
        PatronVenue.Beiz or PatronVenue.Pizzeria => PatronActivity.Dine,
        PatronVenue.Cafe => PatronActivity.Coffee,
        PatronVenue.FastFood => PatronActivity.Snack,
        _ => PatronActivity.Drink,
    };

    /// <summary>Every spot of a plan's venue rooms, in the interior's frame.</summary>
    public static List<PatronSpot> Of(InteriorLayout l)
    {
        var spots = new List<PatronSpot>();
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var p = l.Furniture[i];
            int room = RoomAt(l, p.Floor, p.X, p.Z);
            if (room < 0) continue;
            var type = l.Floors[p.Floor].Rooms[room].Type;
            if (VenueOf(type) is not { } venue) continue;
            Piece(l, i, room, venue, spots);
        }
        return spots;
    }

    /// <summary>The index of the room on <paramref name="floor"/> holding a point, or -1.</summary>
    public static int RoomAt(InteriorLayout l, int floor, float x, float z)
    {
        if (floor < 0 || floor >= l.Floors.Count) return -1;
        var rooms = l.Floors[floor].Rooms;
        for (int r = 0; r < rooms.Count; r++)
        {
            var rp = rooms[r];
            if (x >= rp.X0 && x <= rp.X1 && z >= rp.Z0 && z <= rp.Z1) return r;
        }
        return -1;
    }

    private static void Piece(InteriorLayout l, int index, int room, PatronVenue venue, List<PatronSpot> spots)
    {
        var p = l.Furniture[index];
        float w = p.W / 2, d = p.D / 2, floorY = l.FloorY(p.Floor) + p.Lift;
        var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
        var origin = new Vector3(p.X, floorY, p.Z);
        uint group = PatronSchedule.KeyOf((int)(p.X * 100), (int)(p.Z * 100), p.Floor, (uint)p.Type);
        int first = spots.Count;

        // a place in the piece's own frame (+Z its front), facing a direction of that frame
        void Add(float x, float y, float z, float fx, float fz, PatronPose pose, PatronActivity a, uint g = 0)
        {
            var at = origin + basis * new Vector3(x, y, z);
            if (pose == PatronPose.Stand && !Clear(l, index, room, at)) return;
            var f = basis * new Vector3(fx, 0, fz);
            spots.Add(new PatronSpot(g == 0 ? group : g, 0, 0, at, Mathf.Atan2(-f.X, -f.Z), floorY, pose, a, venue, index, room));
        }

        var table = TableActivity(venue);
        switch (p.Type)
        {
            case FurnitureType.TableSet:
            {
                // InteriorMeshBuilder.Dining: chairs on the long sides, on the ends when wide enough, two a side when wider
                const float chair = 0.42f;
                bool ends = p.W >= 1.6f;
                float hw = ends ? w - chair : w - 0.17f;
                int per = p.W >= 1.85f ? 2 : 1;
                for (int i = 0; i < per; i++)
                {
                    float off = per == 1 ? 0 : (i - 0.5f) * hw;
                    Add(off, ChairHip, d - 0.15f, 0, -1, PatronPose.Chair, table);
                    Add(off, ChairHip, -d + 0.15f, 0, 1, PatronPose.Chair, table);
                }
                if (ends)
                    for (int i = 0; i < per; i++)
                    {
                        float off = per == 1 ? 0 : (i - 0.5f) * hw;
                        Add(w - 0.15f, ChairHip, off, -1, 0, PatronPose.Chair, table);
                        Add(-w + 0.15f, ChairHip, off, 1, 0, PatronPose.Chair, table);
                    }
                break;
            }
            case FurnitureType.BanquetteSet:
            {
                // the bench along the back, chairs across the table
                int n = p.W >= 1.4f ? 2 : 1;
                for (int i = 0; i < n; i++)
                {
                    float x = n == 1 ? 0 : (i - 0.5f) * (p.W - 0.6f);
                    Add(x, ChairHip, -d + 0.32f, 0, 1, PatronPose.Chair, table);
                    Add(x, ChairHip, d - 0.15f, 0, -1, PatronPose.Chair, table);
                }
                break;
            }
            case FurnitureType.HighTable:
                Add(0, StoolHip, d - 0.14f, 0, -1, PatronPose.Stool, PatronActivity.Drink);
                Add(0, StoolHip, -d + 0.14f, 0, 1, PatronPose.Stool, PatronActivity.Drink);
                Add(w - 0.14f, StoolHip, 0, -1, 0, PatronPose.Stool, PatronActivity.Drink);
                Add(-w + 0.14f, StoolHip, 0, 1, 0, PatronPose.Stool, PatronActivity.Drink);
                break;
            case FurnitureType.BarStool:
            {
                // facing the bar it stands at
                var face = BarFacing(l, p, origin);
                spots.Add(new PatronSpot(group, 0, 0, origin + Vector3.Up * StoolHip, Mathf.Atan2(-face.X, -face.Z), floorY,
                    PatronPose.Stool, PatronActivity.Drink, venue, index, room));
                break;
            }
            case FurnitureType.SeatingBank:
            {
                // InteriorGenerator.RowsOf, as CinemaSeats sits a player: the screen at the bank's +Z
                var rows = InteriorGenerator.RowsOf(p);
                for (int k = 0; k < rows.Rows; k++)
                {
                    var (z0, _, y) = rows.Row(k);
                    for (int i = 0; i < rows.SeatsIn(k); i++)
                        Add(rows.SeatX(k, i), y + 0.45f, z0 + 0.42f, 0, 1, PatronPose.Chair, PatronActivity.Watch);
                }
                break;
            }
            case FurnitureType.ArcadeCabinet or FurnitureType.Pinball:
                Add(0, 0, d + 0.32f, 0, -1, PatronPose.Stand, PatronActivity.Arcade);
                break;
            case FurnitureType.ClawMachine or FurnitureType.BasketballGame:
                Add(0, 0, d + 0.42f, 0, -1, PatronPose.Stand, PatronActivity.Arcade);
                break;
            case FurnitureType.DrumGame:
                Add(0, 0, d + 0.25f, 0, -1, PatronPose.Stand, PatronActivity.Arcade);
                break;
            case FurnitureType.DanceMachine:
            {
                // two players on the platform's arrows (0.15 m up)
                float z = (-d + 0.8f + d - 0.2f) / 2;
                Add(-w / 2, 0.15f, z, 0, -1, PatronPose.Stand, PatronActivity.Dance);
                Add(w / 2, 0.15f, z, 0, -1, PatronPose.Stand, PatronActivity.Dance);
                break;
            }
            case FurnitureType.Racer:
                // the bucket seats, facing the screens
                Add(-w / 2, 0.55f, d - 0.4f, 0, -1, PatronPose.Chair, PatronActivity.Arcade);
                Add(w / 2, 0.55f, d - 0.4f, 0, -1, PatronPose.Chair, PatronActivity.Arcade);
                break;
            case FurnitureType.AirHockey:
                Add(0, 0, d + 0.3f, 0, -1, PatronPose.Stand, PatronActivity.Arcade);
                Add(0, 0, -d - 0.3f, 0, 1, PatronPose.Stand, PatronActivity.Arcade);
                break;
            case FurnitureType.TableFootball:
                Add(0, 0, d + 0.2f, 0, -1, PatronPose.Stand, PatronActivity.TableFootball);
                Add(0, 0, -d - 0.2f, 0, 1, PatronPose.Stand, PatronActivity.TableFootball);
                break;
            case FurnitureType.PoolTable:
                // one on the shot at the foot, the other waiting by the side
                Add(0, 0, d + 0.45f, 0, -1, PatronPose.Stand, PatronActivity.Pool);
                Add(w + 0.55f, 0, -0.4f, -1, 0, PatronPose.Stand, PatronActivity.Pool);
                break;
            case FurnitureType.DartBoard:
                // the oche 2.37 m out [S], the other one a step behind
                Add(0, 0, -d + 2.37f, 0, -1, PatronPose.Stand, PatronActivity.Darts);
                Add(0.7f, 0, -d + 2.9f, 0, -1, PatronPose.Stand, PatronActivity.Darts);
                break;
            case FurnitureType.Settee:
            {
                // InteriorMeshBuilder.Venues: the back bench and its two wings, round the scoring table
                float wing = Math.Min(1.2f, p.D - 0.9f);
                for (float x = -w + 0.9f; x <= w - 0.9f + 1e-3f; x += 0.6f)
                    Add(x, ChairHip, -d + 0.32f, 0, 1, PatronPose.Chair, PatronActivity.Settee);
                if (wing > 0.5f)
                {
                    Add(-w + 0.3f, ChairHip, -d + 0.6f + wing / 2, 1, 0, PatronPose.Chair, PatronActivity.Settee);
                    Add(w - 0.3f, ChairHip, -d + 0.6f + wing / 2, -1, 0, PatronPose.Chair, PatronActivity.Settee);
                }
                break;
            }
            case FurnitureType.DanceFloor:
            {
                // one dancer in most 1.2 m cells, each on its own
                const float cell = 1.2f;
                int nx = Math.Max(1, (int)(p.W / cell)), nz = Math.Max(1, (int)(p.D / cell));
                for (int i = 0; i < nx; i++)
                    for (int k = 0; k < nz; k++)
                    {
                        uint g = PatronSchedule.Mix(group, (uint)i, (uint)k);
                        float x = -w + (i + 0.3f + 0.4f * (float)PatronSchedule.Unit(g, 1)) * (p.W / nx);
                        float z = -d + (k + 0.3f + 0.4f * (float)PatronSchedule.Unit(g, 2)) * (p.D / nz);
                        float turn = (float)PatronSchedule.Unit(g, 3) * Mathf.Tau;
                        Add(x, 0, z, Mathf.Sin(turn), Mathf.Cos(turn), PatronPose.Stand, PatronActivity.Dance, g);
                    }
                break;
            }
            case FurnitureType.OrderCounter or FurnitureType.KebabCounter or FurnitureType.ConcessionStand
                or FurnitureType.BoxOffice or FurnitureType.ShoeCounter or FurnitureType.PastryCase or FurnitureType.PrizeCounter:
            {
                // a line out from the counter's front, as long as the floor allows
                for (int i = 0; i < QueueMost; i++)
                {
                    int before = spots.Count;
                    Add(0, 0, d + 0.45f + i * QueuePitch, 0, -1, PatronPose.Stand, PatronActivity.Queue);
                    if (spots.Count == before) break;
                }
                break;
            }
        }
        Number(spots, first);
    }

    /// <summary>
    /// The places of each group just added, numbered in the order they came (the facing pairs above), the group's
    /// count on each. A dance floor's cells are each their own group.
    /// </summary>
    private static void Number(List<PatronSpot> spots, int first)
    {
        for (int i = first; i < spots.Count; i++)
        {
            int place = 0, places = 0;
            for (int j = first; j < spots.Count; j++)
            {
                if (spots[j].Group != spots[i].Group) continue;
                if (j < i) place++;
                places++;
            }
            spots[i] = spots[i] with { Place = place, Places = places };
        }
    }

    /// <summary>
    /// A standing place is clear: in its room (a hair inside its walls), off every other piece but those lying on the
    /// floor (a rug, a dance floor, the carpet's print).
    /// </summary>
    private static bool Clear(InteriorLayout l, int own, int room, Vector3 at)
    {
        var r = l.Floors[l.Furniture[own].Floor].Rooms[room];
        const float body = 0.22f;
        if (at.X < r.X0 + body || at.X > r.X1 - body || at.Z < r.Z0 + body || at.Z > r.Z1 - body) return false;
        int floor = l.Furniture[own].Floor;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var q = l.Furniture[i];
            if (i == own || q.Floor != floor || Flat(q.Type)) continue;
            var local = new Basis(Vector3.Up, q.Turns * Mathf.Pi / 2).Inverse() * (at - new Vector3(q.X, at.Y, q.Z));
            if (Math.Abs(local.X) < q.W / 2 + body && Math.Abs(local.Z) < q.D / 2 + body) return false;
        }
        return true;
    }

    private static bool Flat(FurnitureType t) => t is FurnitureType.Rug or FurnitureType.Confetti or FurnitureType.DanceFloor
        or FurnitureType.FloorMarking or FurnitureType.Stanchions or FurnitureType.WallSconce or FurnitureType.PosterFrame
        or FurnitureType.ExitSign or FurnitureType.Loudspeaker or FurnitureType.DartBoard;

    /// <summary>Which way a stool's sitter looks: at the nearest bar counter's front, else at the stool's own −Z.</summary>
    private static Vector3 BarFacing(InteriorLayout l, FurniturePlan stool, Vector3 at)
    {
        FurniturePlan? best = null;
        float bestD = 3f;
        foreach (var q in l.Furniture)
        {
            if (q.Type != FurnitureType.BarCounter || q.Floor != stool.Floor) continue;
            float dist = new Vector2(q.X - at.X, q.Z - at.Z).Length() - Math.Max(q.W, q.D) / 2;
            if (dist < bestD) (best, bestD) = (q, dist);
        }
        // a counter's customers stand at its front (+Z) and look back into it
        return best is { } b ? new Basis(Vector3.Up, b.Turns * Mathf.Pi / 2) * Vector3.Forward : Vector3.Forward;
    }

    /// <summary>A terrace's places (<see cref="TerraceProps"/>), in the terrace root's frame; empty for its other pieces.</summary>
    public static void OfTerrace(TerraceItem it, PatronVenue venue, int piece, List<PatronSpot> spots)
    {
        var basis = new Basis(Vector3.Up, it.Yaw);
        uint group = PatronSchedule.KeyOf((int)(it.At.X * 100), (int)(it.At.Z * 100), 0, 0x7E77u + (uint)it.Kind);
        var table = TableActivity(venue);
        int first = spots.Count;
        void Add(float x, float z, float fx, float fz)
        {
            var f = basis * new Vector3(fx, 0, fz);
            spots.Add(new PatronSpot(group, 0, 0, it.At + basis * new Vector3(x, ChairHip - 0.04f, z), Mathf.Atan2(-f.X, -f.Z),
                it.At.Y, PatronPose.Chair, table, venue, piece, -1));
        }
        switch (it.Kind)
        {
            case TerraceKind.BistroSet:
                // a rattan chair each side, facing the table
                Add(0.6f, 0, -1, 0);
                Add(-0.6f, 0, 1, 0);
                break;
            case TerraceKind.BeerBench:
                // three a bench, the benches facing each other
                foreach (float x in new[] { -0.7f, 0f, 0.7f })
                {
                    Add(x, 0.62f, 0, -1);
                    Add(x, -0.62f, 0, 1);
                }
                break;
        }
        Number(spots, first);
    }
}
