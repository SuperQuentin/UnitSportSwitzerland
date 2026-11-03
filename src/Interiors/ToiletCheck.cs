namespace UnitSport.Interiors;

/// <summary>
/// What the generators' probes ask of a plan's public toilets (#1285): the set the footprint should have,
/// and that it is whole: anterooms with basins and mirrors and no line of sight, stalls with a bowl and a
/// leaf, urinals with their screens, the accessible WC's rails, a sign over every way in, every doorway
/// of every toilet room reachable among its pieces.
/// </summary>
public static class ToiletCheck
{
    /// <summary>A line describing the plan's toilets, for the log.</summary>
    public static string Describe(InteriorLayout l)
    {
        int Rooms(RoomType t) => l.Floors.Sum(f => f.Rooms.Count(r => r.Type == t));
        int Count(FurnitureType t) => l.Furniture.Count(f => f.Type == t);
        return $"{InteriorGenerator.ToiletSetOf(l)}: {Rooms(RoomType.ToiletStall)} stalls, {Count(FurnitureType.Urinal)} urinals, "
            + $"{Count(FurnitureType.Mirror)} mirrors, {Rooms(RoomType.AccessibleToilet)} accessible, {l.InnerDoors.Count} leaves";
    }

    /// <summary>Everything wrong with the plan's toilets for a building that should have <paramref name="want"/>; empty when right.</summary>
    public static List<string> Problems(InteriorLayout l, ToiletSet want, bool accessible = false)
    {
        var bad = new List<string>();
        var got = InteriorGenerator.ToiletSetOf(l);
        if (got != want) bad.Add($"{got} toilets, not {want}");
        int Count(FurnitureType t) => l.Furniture.Count(f => f.Type == t);
        for (int fi = 0; fi < l.Floors.Count; fi++)
        {
            var rooms = l.Floors[fi].Rooms;
            List<FurniturePlan> In(RoomPlan r) => l.Furniture.Where(p => p.Floor == fi && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1).ToList();
            bool Leaf(int ri, OpeningPlan o) => l.InnerDoors.Any(d => d.Floor == fi && Math.Abs(d.Center - o.Center) < 0.01f
                && (d.Room == ri && d.Side == o.Side || d.Room == o.Other && d.Side == Opposite(o.Side)));
            // the room with the basins: in from the host by a door, the basins and a mirror, every other doorway a stall's
            void Basins(RoomPlan r, List<FurniturePlan> pieces, int ri, string what)
            {
                if (!pieces.Any(p => p.Type == FurnitureType.SinkRow)) bad.Add($"{what} without basins");
                if (!pieces.Any(p => p.Type == FurnitureType.Mirror)) bad.Add($"{what} without a mirror");
                var ways = r.Openings.Where(o => o.Kind == OpeningKind.Door && o.Other >= 0).ToList();
                var host = ways.FirstOrDefault(o => !InteriorGenerator.ToiletRoom(rooms[o.Other].Type) && rooms[o.Other].Type != RoomType.Storage);
                if (host == null) bad.Add($"{what} with no way in");
                else if (!Leaf(ri, host)) bad.Add($"{what} with no door");
                if (ways.Any(o => o != host && !Leaf(ri, o))) bad.Add($"{what} with a doorway but no door");
            }
            for (int ri = 0; ri < rooms.Count; ri++)
            {
                var r = rooms[ri];
                var pieces = In(r);
                switch (r.Type)
                {
                    case RoomType.ToiletStall:
                    case RoomType.AccessibleToilet:
                    {
                        if (!pieces.Any(p => p.Type == FurnitureType.Toilet)) bad.Add($"a {r.Type} without a bowl");
                        var door = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door);
                        if (door == null || !Leaf(ri, door)) bad.Add($"a {r.Type} without a door leaf");
                        if (r.Type == RoomType.AccessibleToilet)
                        {
                            if (r.Width < ToiletSizing.AccessW - 0.01f && r.Depth < ToiletSizing.AccessW - 0.01f || Math.Min(r.Width, r.Depth) < 1.65f)
                                bad.Add($"an accessible WC of {r.Width:F2} x {r.Depth:F2} m, under SIA 500's");
                            if (pieces.Count(p => p.Type == FurnitureType.GrabRail) < 2) bad.Add("an accessible WC without its two rails");
                        }
                        break;
                    }
                    case RoomType.ToiletLobby:
                    {
                        Basins(r, pieces, ri, "an anteroom");
                        break;
                    }
                    case RoomType.WomensToilet:
                        Basins(r, pieces, ri, "a women's room");
                        break;
                    case RoomType.MensToilet:
                    {
                        Basins(r, pieces, ri, "a men's room");
                        int urinals = pieces.Count(p => p.Type == FurnitureType.Urinal), screens = pieces.Count(p => p.Type == FurnitureType.UrinalScreen);
                        if (urinals == 0) bad.Add("a men's room without urinals");
                        if (screens != urinals + 1) bad.Add($"{urinals} urinals and {screens} screens");
                        break;
                    }
                }
                if (InteriorGenerator.ToiletRoom(r.Type) || r.Type == RoomType.WC)
                {
                    var rects = pieces.Where(p => p.Lift < 1.5f).Select(p =>
                    {
                        bool odd = p.Turns % 2 == 1;
                        float w = (odd ? p.D : p.W) / 2, d = (odd ? p.W : p.D) / 2;
                        return new RectPlan(p.X - w, p.Z - d, p.X + w, p.Z + d);
                    }).ToList();
                    int shut = InteriorGenerator.ShutDoorways(r, rects);
                    if (shut > 0) bad.Add($"a {r.Type} with {shut} doorway(s) its pieces shut off");
                }
            }
        }
        switch (want)
        {
            case ToiletSet.Gendered:
                if (Count(FurnitureType.WcSignWomen) < 1 || Count(FurnitureType.WcSignMen) < 1) bad.Add("no women's or men's sign");
                if (accessible != (Count(FurnitureType.WcSignAccessible) > 0)) bad.Add(accessible ? "no accessible WC" : "an accessible WC where none was wanted");
                break;
            case ToiletSet.Shared:
                if (Count(FurnitureType.WcSignAll) < 1 || Count(FurnitureType.WcSignWomen) < 1 || Count(FurnitureType.WcSignMen) < 1) bad.Add("the shared anteroom's signs are missing");
                break;
            case ToiletSet.Pair:
                if (Count(FurnitureType.WcSignWomen) < 1 || Count(FurnitureType.WcSignMen) < 1) bad.Add("the pair of WCs has no women's or men's sign");
                break;
            case ToiletSet.Single:
                if (Count(FurnitureType.WcSignAll) < 1) bad.Add("the WC has no sign");
                break;
        }
        return bad;
    }

    private static Side Opposite(Side s) => (Side)(((int)s + 2) % 4);
}
