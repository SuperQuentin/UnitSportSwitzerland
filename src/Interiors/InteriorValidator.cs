using System.Globalization;
using System.Text;

namespace UnitSport.Interiors;

/// <summary>
/// Checks the properties that make a plan walkable rather than merely drawn: rooms inside the
/// building and not overlapping, every doorway cut on both sides, every room reachable from the
/// front door, a flight between every pair of floors, furniture inside its room and out of the
/// doorways. The generator is built to satisfy all of these; this is how that claim is tested.
/// </summary>
public static class InteriorValidator
{
    public static List<string> Validate(InteriorLayout l)
    {
        var errors = new List<string>();
        float hw = l.Width / 2 + 0.02f, hd = l.Depth / 2 + 0.02f;
        if (l.Floors.Count == 0) { errors.Add("no floors"); return errors; }
        if (!l.Floors[0].Rooms.Any(r => r.Openings.Any(o => o.Kind == OpeningKind.Entry && o.Side == Side.Front)))
            errors.Add("no entry door on the front wall");

        for (int f = 0; f < l.Floors.Count; f++)
        {
            var rooms = l.Floors[f].Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r.X0 < -hw || r.X1 > hw || r.Z0 < -hd || r.Z1 > hd)
                    errors.Add($"floor {f} room {i} {r.Type} outside the footprint");
                if (Math.Min(r.Width, r.Depth) < 1.0f)
                    errors.Add($"floor {f} room {i} {r.Type} is {r.Width:F1}x{r.Depth:F1} m");
                for (int j = i + 1; j < rooms.Count; j++)
                    if (new RectPlan(r.X0, r.Z0, r.X1, r.Z1).Overlaps(new RectPlan(rooms[j].X0, rooms[j].Z0, rooms[j].X1, rooms[j].Z1), 0.02f))
                        errors.Add($"floor {f} rooms {i} and {j} overlap");
                foreach (var o in r.Openings.Where(o => o.Kind == OpeningKind.Door))
                {
                    if (o.Other < 0 || o.Other >= rooms.Count
                        || !rooms[o.Other].Openings.Any(q => q.Other == i && Math.Abs(q.Center - o.Center) < 0.01f))
                        errors.Add($"floor {f} room {i}: doorway without a matching cut on the other side");
                }
            }

            // reachability from the core (room 0) through doorways
            var seen = new bool[rooms.Count];
            var queue = new Queue<int>();
            seen[0] = true;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int a = queue.Dequeue();
                foreach (var o in rooms[a].Openings)
                    if (o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count && !seen[o.Other])
                    {
                        seen[o.Other] = true;
                        queue.Enqueue(o.Other);
                    }
            }
            for (int i = 0; i < rooms.Count; i++)
                if (!seen[i]) errors.Add($"floor {f} room {i} {rooms[i].Type} unreachable");

            if (f < l.Floors.Count - 1)
            {
                var fl = l.Floors[f].Flight;
                if (fl == null) errors.Add($"no stairs from floor {f}");
                else
                {
                    var core = rooms[0];
                    if (fl.X0 < core.X0 - 0.01f || fl.X1 > core.X1 + 0.01f
                        || Math.Min(fl.ZBottom, fl.ZTop) < core.Z0 || Math.Max(fl.ZBottom, fl.ZTop) > core.Z1)
                        errors.Add($"floor {f} stairs outside the core");
                    if (!l.Floors[f + 1].Holes.Any(h => h.X0 <= fl.X0 + 0.01f && h.X1 >= fl.X1 - 0.01f))
                        errors.Add($"floor {f + 1} has no opening over the stairs from below");
                }
            }
        }

        // furniture
        var byFloor = l.Furniture.GroupBy(p => p.Floor);
        foreach (var group in byFloor)
        {
            if (group.Key < 0 || group.Key >= l.Floors.Count) { errors.Add("furniture on a missing floor"); continue; }
            var floor = l.Floors[group.Key];
            var rects = new List<(FurniturePlan P, RectPlan R)>();
            foreach (var p in group)
            {
                bool odd = p.Turns % 2 == 1;
                float w = (odd ? p.D : p.W) / 2, d = (odd ? p.W : p.D) / 2;
                var rect = new RectPlan(p.X - w, p.Z - d, p.X + w, p.Z + d);
                var room = floor.Rooms.FirstOrDefault(r => p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1);
                if (room == null) { errors.Add($"{p.Type} on floor {p.Floor} is in no room"); continue; }
                if (rect.X0 < room.X0 || rect.X1 > room.X1 || rect.Z0 < room.Z0 || rect.Z1 > room.Z1)
                    errors.Add($"{p.Type} on floor {p.Floor} pokes through a wall");
                if (p.Type != FurnitureType.Rug)
                {
                    foreach (var o in room.Openings.Where(o => o.Kind is OpeningKind.Door or OpeningKind.Entry))
                        if (Doorway(room, o).Overlaps(rect)) errors.Add($"{p.Type} on floor {p.Floor} blocks a doorway");
                    foreach (var (q, r) in rects)
                        if (q.Type != FurnitureType.Rug && r.Overlaps(rect, 0.02f))
                            errors.Add($"{p.Type} and {q.Type} on floor {p.Floor} overlap");
                    rects.Add((p, rect));
                }
            }
        }
        return errors;
    }

    /// <summary>The strip just inside a doorway, which must stay clear.</summary>
    private static RectPlan Doorway(RoomPlan r, OpeningPlan o)
    {
        float half = o.Width / 2, deep = 0.6f;
        return o.Side switch
        {
            Side.Front => new RectPlan(o.Center - half, r.Z0, o.Center + half, r.Z0 + deep),
            Side.Back => new RectPlan(o.Center - half, r.Z1 - deep, o.Center + half, r.Z1),
            Side.Left => new RectPlan(r.X0, o.Center - half, r.X0 + deep, o.Center + half),
            _ => new RectPlan(r.X1 - deep, o.Center - half, r.X1, o.Center + half),
        };
    }

    /// <summary>Plan view of every floor side by side, the front wall at the bottom.</summary>
    public static string ToSvg(InteriorLayout l)
    {
        var inv = CultureInfo.InvariantCulture;
        const float S = 24f, Gap = 30f;
        float fw = l.Width * S, fd = l.Depth * S;
        var sb = new StringBuilder();
        string N(float v) => v.ToString("0.#", inv);
        float width = l.Floors.Count * (fw + Gap) + Gap, height = fd + 2 * Gap + 20;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" font-family=\"sans-serif\" font-size=\"10\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#f4f1ea\"/>");
        sb.Append($"<text x=\"{Gap}\" y=\"16\" font-size=\"13\">{l.Key} {l.Kind} {l.Width:F1}x{l.Depth:F1} m, {l.Floors.Count} floor(s) of {l.StoreyHeight:F2} m</text>");
        for (int f = 0; f < l.Floors.Count; f++)
        {
            float ox = Gap + f * (fw + Gap) + fw / 2, oy = Gap + 20 + fd / 2;
            // Z grows toward the back; draw the back at the top
            float X(float x) => ox + x * S;
            float Y(float z) => oy - z * S;
            var floor = l.Floors[f];
            foreach (var r in floor.Rooms)
            {
                string fill = r.Type switch
                {
                    RoomType.Hall or RoomType.Landing or RoomType.Lobby => "#e8dcc4",
                    RoomType.Living or RoomType.Dining => "#f2c98a",
                    RoomType.Kitchen => "#c9e2b8",
                    RoomType.Bedroom => "#b8cde2",
                    RoomType.Bathroom or RoomType.WC => "#a8e0e0",
                    _ => "#ddd",
                };
                sb.Append($"<rect x=\"{N(X(r.X0))}\" y=\"{N(Y(r.Z1))}\" width=\"{N(r.Width * S)}\" height=\"{N(r.Depth * S)}\" fill=\"{fill}\" stroke=\"#333\" stroke-width=\"2\"/>");
                sb.Append($"<text x=\"{N(X((r.X0 + r.X1) / 2))}\" y=\"{N(Y((r.Z0 + r.Z1) / 2))}\" text-anchor=\"middle\">{r.Type}</text>");
                foreach (var o in r.Openings)
                {
                    bool along = o.Side is Side.Front or Side.Back;
                    float a = o.Center - o.Width / 2, b = o.Center + o.Width / 2;
                    float fixedC = o.Side switch { Side.Front => r.Z0, Side.Back => r.Z1, Side.Left => r.X0, _ => r.X1 };
                    string col = o.Kind switch { OpeningKind.Window => "#4aa3ff", OpeningKind.Entry => "#d11", _ => "#fff" };
                    if (along)
                        sb.Append($"<line x1=\"{N(X(a))}\" y1=\"{N(Y(fixedC))}\" x2=\"{N(X(b))}\" y2=\"{N(Y(fixedC))}\" stroke=\"{col}\" stroke-width=\"4\"/>");
                    else
                        sb.Append($"<line x1=\"{N(X(fixedC))}\" y1=\"{N(Y(a))}\" x2=\"{N(X(fixedC))}\" y2=\"{N(Y(b))}\" stroke=\"{col}\" stroke-width=\"4\"/>");
                }
            }
            foreach (var h in floor.Holes)
                sb.Append($"<rect x=\"{N(X(h.X0))}\" y=\"{N(Y(h.Z1))}\" width=\"{N((h.X1 - h.X0) * S)}\" height=\"{N((h.Z1 - h.Z0) * S)}\" fill=\"#333\" fill-opacity=\"0.5\"/>");
            if (floor.Flight is { } fl)
            {
                float z0 = Math.Min(fl.ZBottom, fl.ZTop), z1 = Math.Max(fl.ZBottom, fl.ZTop);
                sb.Append($"<rect x=\"{N(X(fl.X0))}\" y=\"{N(Y(z1))}\" width=\"{N((fl.X1 - fl.X0) * S)}\" height=\"{N((z1 - z0) * S)}\" fill=\"#a0784c\"/>");
                sb.Append($"<line x1=\"{N(X((fl.X0 + fl.X1) / 2))}\" y1=\"{N(Y(fl.ZBottom))}\" x2=\"{N(X((fl.X0 + fl.X1) / 2))}\" y2=\"{N(Y(fl.ZTop))}\" stroke=\"#fff\" stroke-width=\"2\" marker-end=\"none\"/>");
            }
            foreach (var p in l.Furniture.Where(p => p.Floor == f))
            {
                bool odd = p.Turns % 2 == 1;
                float w = odd ? p.D : p.W, d = odd ? p.W : p.D;
                sb.Append($"<rect x=\"{N(X(p.X - w / 2))}\" y=\"{N(Y(p.Z + d / 2))}\" width=\"{N(w * S)}\" height=\"{N(d * S)}\" fill=\"#8a6\" fill-opacity=\"0.6\" stroke=\"#453\"/>");
            }
            sb.Append($"<text x=\"{N(ox - fw / 2)}\" y=\"{N(oy + fd / 2 + 14)}\">floor {f}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
