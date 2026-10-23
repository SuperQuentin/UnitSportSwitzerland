namespace UnitSport.Terrain.Format;

/// <summary>A rectangle of one room after <see cref="RoomStrips.Cut"/>.</summary>
public readonly record struct RoomPiece(int Room, double X0, double Z0, double X1, double Z1)
{
    public double Width => X1 - X0;
    public double Depth => Z1 - Z0;
}

/// <summary>
/// Where two pieces meet: along x at z = <see cref="At"/> (<see cref="AlongX"/>) or along z at x = <see cref="At"/>,
/// from <see cref="Lo"/> to <see cref="Hi"/>. <see cref="A"/> is the piece on the lower side (smaller z, or smaller x).
/// </summary>
public readonly record struct PieceContact(int A, int B, bool AlongX, double At, double Lo, double Hi)
{
    public double Length => Hi - Lo;
}

/// <summary>
/// Cuts a floor's polygon rooms into axis-aligned rectangles (#1186): the game's interiors are built from rectangles,
/// and the web lets people draw any shape. The floor is sliced into horizontal strips at every corner's z, shared by
/// all its rooms, so two rooms that share a wall share it after the cut too; each room's span across a strip,
/// measured at the strip's middle, is one piece. A right-angled room comes out exact (an L is two pieces). Strips
/// crossed by an angled wall are cut every <see cref="Step"/> or so, which turns that wall into steps. Pieces of one
/// room that keep the same span are merged again. A few centimetres of overlap or gap between two rooms, a drawing's
/// slop, is split or closed at the middle. A stair's rectangle is carved out as a piece of its own, so the game's
/// flight stands inside one room.
/// </summary>
public static class RoomStrips
{
    public const double Step = 0.5;
    /// <summary>Strip lines closer than this are one line: walls drawn a few centimetres apart meet.</summary>
    public const double Merge = 0.15;
    /// <summary>A gap between two rooms up to this wide is closed at its middle.</summary>
    public const double Gap = 0.15;
    /// <summary>Slivers thinner than this (an acute corner's tip) are dropped.</summary>
    public const double MinPiece = 0.2;

    public static (List<RoomPiece> Pieces, List<PieceContact> Contacts) Cut(
        IReadOnlyList<(double X, double Z)[]> rooms, IReadOnlyList<(double X0, double Z0, double X1, double Z1)> stairs)
    {
        // --- strip lines: corners and stair ends, merged when close, angled walls cut finer ---
        var lines = rooms.SelectMany(r => r.Select(p => p.Z)).Concat(stairs.SelectMany(s => new[] { s.Z0, s.Z1 }))
            .Select(z => Math.Round(z, 3)).OrderBy(z => z).ToList();
        var kept = new List<double>();
        foreach (var z in lines)
            if (kept.Count == 0 || z - kept[^1] >= Merge) kept.Add(z);
        var all = new List<double>();
        for (int i = 0; i < kept.Count; i++)
        {
            all.Add(kept[i]);
            if (i + 1 == kept.Count) break;
            double a = kept[i], b = kept[i + 1];
            if (b - a <= Step + 0.3 || !Angled(rooms, a, b)) continue;
            int n = (int)Math.Ceiling((b - a) / Step);
            for (int k = 1; k < n; k++) all.Add(a + (b - a) * k / n);
        }

        // --- each strip: every room's spans at the strip's middle ---
        var pieces = new List<RoomPiece>();
        var open = new Dictionary<(int Room, long X0, long X1), int>(); // a piece that may still grow down
        for (int s = 0; s + 1 < all.Count; s++)
        {
            double za = all[s], zb = all[s + 1], zm = (za + zb) / 2;
            var spans = new List<(int Room, double X0, double X1)>();
            for (int r = 0; r < rooms.Count; r++)
                foreach (var (x0, x1) in Spans(rooms[r], zm))
                    spans.Add((r, x0, x1));
            spans.Sort((p, q) => p.X0.CompareTo(q.X0));
            // a drawing's slop between neighbours: overlap split, small gap closed, at the middle
            for (int i = 0; i + 1 < spans.Count; i++)
            {
                var (ra, a0, a1) = spans[i];
                var (rb, b0, b1) = spans[i + 1];
                if (ra == rb || b0 - a1 > Gap) continue;
                double mid = Math.Clamp((a1 + b0) / 2, a0, b1);
                spans[i] = (ra, a0, mid);
                spans[i + 1] = (rb, mid, b1);
            }
            // a stair's lane is a piece of its own
            foreach (var st in stairs.Where(st => st.Z0 < zm && zm < st.Z1))
                for (int i = 0; i < spans.Count; i++)
                {
                    var (r, x0, x1) = spans[i];
                    if (x0 >= st.X1 || x1 <= st.X0) continue;
                    var parts = new[] { (x0, Math.Min(st.X0, x1)), (Math.Max(st.X0, x0), Math.Min(st.X1, x1)), (Math.Max(st.X1, x0), x1) }
                        .Where(p => p.Item2 - p.Item1 > 1e-6).Select(p => (r, p.Item1, p.Item2)).ToList();
                    spans.RemoveAt(i);
                    spans.InsertRange(i, parts);
                    i += parts.Count - 1;
                }

            var next = new Dictionary<(int, long, long), int>();
            foreach (var (r, x0, x1) in spans)
            {
                if (x1 - x0 < MinPiece || zb - za < 1e-6) continue;
                var key = (r, (long)Math.Round(x0 * 1000), (long)Math.Round(x1 * 1000));
                if (open.TryGetValue(key, out int at) && Math.Abs(pieces[at].Z1 - za) < 1e-6)
                {
                    pieces[at] = pieces[at] with { Z1 = zb };
                    next[key] = at;
                }
                else
                {
                    pieces.Add(new RoomPiece(r, x0, za, x1, zb));
                    next[key] = pieces.Count - 1;
                }
            }
            open = next;
        }
        // thin strips (two corners nearly level) leave pieces too shallow to stand in: dropped
        pieces = pieces.Where(p => p.Depth >= MinPiece / 2).ToList();
        return (pieces, Contacts(pieces));
    }

    /// <summary>Every place two pieces touch, with how long they touch.</summary>
    public static List<PieceContact> Contacts(IReadOnlyList<RoomPiece> pieces)
    {
        const double eps = 1e-4;
        var c = new List<PieceContact>();
        for (int i = 0; i < pieces.Count; i++)
            for (int j = 0; j < pieces.Count; j++)
            {
                if (i == j) continue;
                var a = pieces[i];
                var b = pieces[j];
                if (Math.Abs(a.Z1 - b.Z0) < eps)
                {
                    double lo = Math.Max(a.X0, b.X0), hi = Math.Min(a.X1, b.X1);
                    if (hi - lo > 0.05) c.Add(new PieceContact(i, j, true, a.Z1, lo, hi));
                }
                if (Math.Abs(a.X1 - b.X0) < eps)
                {
                    double lo = Math.Max(a.Z0, b.Z0), hi = Math.Min(a.Z1, b.Z1);
                    if (hi - lo > 0.05) c.Add(new PieceContact(i, j, false, a.X1, lo, hi));
                }
            }
        return c;
    }

    /// <summary>
    /// The parts of one piece's side not touching any other piece: where a window or a door to the outside can go.
    /// Side 0 = z0, 1 = x1, 2 = z1, 3 = x0 (the game's front, right, back, left). Each as (lo, hi) along it.
    /// </summary>
    public static List<(double Lo, double Hi)> FreeAlong(IReadOnlyList<RoomPiece> pieces, IReadOnlyList<PieceContact> contacts, int piece, int side)
    {
        var p = pieces[piece];
        bool alongX = side is 0 or 2;
        double at = side switch { 0 => p.Z0, 1 => p.X1, 2 => p.Z1, _ => p.X0 };
        var free = new List<(double Lo, double Hi)> { alongX ? (p.X0, p.X1) : (p.Z0, p.Z1) };
        foreach (var c in contacts)
        {
            if ((c.A != piece && c.B != piece) || c.AlongX != alongX || Math.Abs(c.At - at) > 1e-4) continue;
            var cut = new List<(double, double)>();
            foreach (var (lo, hi) in free)
            {
                if (c.Hi <= lo || c.Lo >= hi) { cut.Add((lo, hi)); continue; }
                if (c.Lo > lo) cut.Add((lo, c.Lo));
                if (c.Hi < hi) cut.Add((c.Hi, hi));
            }
            free = cut;
        }
        return free.Where(f => f.Hi - f.Lo > 1e-3).ToList();
    }

    /// <summary>The room's spans across the line z = <paramref name="z"/> (even-odd), left to right.</summary>
    private static List<(double X0, double X1)> Spans((double X, double Z)[] p, double z)
    {
        var xs = new List<double>();
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            var a = p[i];
            var b = p[j];
            if ((a.Z > z) != (b.Z > z)) xs.Add(a.X + (z - a.Z) * (b.X - a.X) / (b.Z - a.Z));
        }
        xs.Sort();
        var spans = new List<(double, double)>();
        for (int i = 0; i + 1 < xs.Count; i += 2) spans.Add((xs[i], xs[i + 1]));
        return spans;
    }

    /// <summary>Whether a wall that is not square to the strips crosses the band between z = a and z = b.</summary>
    private static bool Angled(IReadOnlyList<(double X, double Z)[]> rooms, double a, double b)
    {
        foreach (var p in rooms)
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                double z0 = Math.Min(p[i].Z, p[j].Z), z1 = Math.Max(p[i].Z, p[j].Z);
                if (z1 <= a + 1e-6 || z0 >= b - 1e-6) continue;
                if (Math.Abs(p[i].X - p[j].X) > 0.02) return true;
            }
        return false;
    }
}
