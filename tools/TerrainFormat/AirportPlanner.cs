namespace UnitSport.Terrain.Format;

/// <summary>
/// Plans the parked aircraft of an airport (#422) from its data: OpenStreetMap stands
/// (<c>aeroway=parking_position</c>, a node or the lead-in line ending at the nose's stop), aprons
/// (<c>aeroway=apron</c>, named) and taxiways, swissTLM3D's paved movement area
/// (<c>tlm_bauten_verkehrsbaute_ply</c> <c>Rollfeld Hartbelag</c> and <c>Hartbelagpiste</c>), and the
/// built tiles' ground. Pure: the preprocessor runs it over the real airports (<c>--airports</c>),
/// the fixture airport over its own shapes, the unit tests over made-up ones.
///
/// <para>
/// <b>A stand's heading</b>: a lead-in line runs from the taxilane to where the nose stops, so the
/// nose is its end farther from the nearest taxiway; a stand mapped as a node faces away from the
/// nearest taxiway. The aircraft's nose gear stands on the stop point.
/// </para>
/// <para>
/// <b>The choice</b>: a stand fits a type when its footprint (nose to tail, wingtip to wingtip, plus
/// <see cref="Margin"/>) is at least <see cref="PavedShare"/> on paved ground (buildings, grass and
/// jet bridges are not) and clear of the footprints already chosen. The cargo apron (an apron or stand
/// named cargo / fret / fracht, else every stand) gets the AN-124 then the military freighter, the
/// roomiest stands first (farthest from the next stand); the passenger apron with the most stands that
/// fit an A320 gets up to <see cref="Airliners"/> A320s, in the stands' ref order. Airliners only park
/// where a runway is <see cref="MinRunway"/> long (GVA and ZRH; Bern, Lugano, Sion are shorter).
/// </para>
/// </summary>
public static class AirportPlanner
{
    /// <summary>A parked type's footprint: from its node origin, metres forward to the nose and back to the tail, its span, and the nose gear's station.</summary>
    public readonly record struct Size(double Nose, double Tail, double Span, double NoseGear);

    // the game's own numbers (src/Avatar/*Layout.cs NoseZ, TailZ, NoseGearZ; AirlinerCatalog Span)
    public static readonly Size A320 = new(17.6, 19.97, 35.8, 12.64);
    public static readonly Size An124 = new(31.0, 38.1, 73.3, 22.0);
    public static readonly Size Freighter = new(12.6, 17.2, 40.4, 9.8);

    public static Size SizeOf(StandUse use) => use switch
    {
        StandUse.Heavy => An124,
        StandUse.Military => Freighter,
        _ => A320,
    };

    /// <summary>Clearance kept round a footprint, m.</summary>
    public const double Margin = 3;

    /// <summary>Share of a footprint's sample points that must be paved.</summary>
    public const double PavedShare = 0.95;

    /// <summary>A320s per airport.</summary>
    public const int Airliners = 6;

    /// <summary>The longest runway an airport needs for airliners to be parked there, m.</summary>
    public const double MinRunway = 2500;

    /// <summary>How much farther from the taxiways a lead-in line's nose end must be than its other end, m.</summary>
    public const double MinLeadIn = 15;

    /// <summary>A stand as mapped: its ref, the nose's stop point, the nose heading (null: unknown) and the apron it lies on ("" none).</summary>
    public sealed record Candidate(string Ref, double E, double N, double? Heading, string Apron, bool Cargo);

    /// <summary>
    /// A stand mapped as a lead-in line (OSM way): its end farther from the nearest taxiway is the
    /// nose's stop, the heading the line's last 15 m toward it. Null for a degenerate line.
    /// </summary>
    public static (double E, double N, double Heading)? FromLine(IReadOnlyList<(double E, double N)> line,
        IReadOnlyList<IReadOnlyList<(double E, double N)>> taxiways)
    {
        if (line.Count < 2) return null;
        var a = line[0];
        var b = line[^1];
        double da = DistanceTo(a.E, a.N, taxiways), db = DistanceTo(b.E, b.N, taxiways);
        // a line between two taxilanes (a drive-through stand) says nothing of where the nose is
        if (Math.Abs(da - db) < MinLeadIn) return null;
        bool aIsNose = da > db;
        var pts = aIsNose ? line.Reverse().ToList() : line.ToList();
        var nose = pts[^1];
        // back along the line 15 m for the direction
        double walked = 0;
        var from = pts[^1];
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            walked += Dist(pts[i], pts[i + 1]);
            from = pts[i];
            if (walked >= 15) break;
        }
        if (Dist(from, nose) < 1) return null;
        return (nose.E, nose.N, HeadingOf(nose.E - from.E, nose.N - from.N));
    }

    /// <summary>A stand mapped as a node: it faces away from the nearest taxiway; null when there is none.</summary>
    public static double? FromPoint(double e, double n, IReadOnlyList<IReadOnlyList<(double E, double N)>> taxiways)
    {
        var near = Nearest(e, n, taxiways);
        if (near is not { } p || Math.Abs(p.E - e) + Math.Abs(p.N - n) < 0.5) return null;
        return HeadingOf(e - p.E, n - p.N);
    }

    /// <summary>
    /// The stands chosen at one airport: the AN-124 and the freighter on the cargo apron, A320s on
    /// one passenger apron. <paramref name="paved"/> answers whether a point is paved movement area;
    /// <paramref name="ground"/> gives the stand's height (NaN unknown).
    /// </summary>
    public static List<AirportStand> Choose(string code, IReadOnlyList<Candidate> candidates, Func<double, double, bool> paved,
        Func<double, double, double>? ground = null, bool airliners = true)
    {
        var chosen = new List<AirportStand>();
        var placed = new List<(double E, double N, double Heading, Size Size)>();
        var usable = candidates.Where(c => c.Heading != null).OrderBy(c => c.Ref, RefOrder.Instance).ThenBy(c => c.E).ThenBy(c => c.N).ToList();
        if (usable.Count == 0) return chosen;
        // the room round each stand: how far the next stand is
        double Room(Candidate c) => usable.Where(o => o != c).Select(o => Math.Sqrt((o.E - c.E) * (o.E - c.E) + (o.N - c.N) * (o.N - c.N))).DefaultIfEmpty(1e9).Min();
        var cargo = usable.Where(c => c.Cargo).ToList();
        if (cargo.Count == 0) cargo = usable;
        var roomiest = cargo.OrderByDescending(Room).ToList();

        bool TryPlace(Candidate c, StandUse use)
        {
            var size = SizeOf(use);
            var (oe, on) = Origin(c.E, c.N, c.Heading!.Value, size);
            if (PavedFraction(oe, on, c.Heading!.Value, size, paved) < PavedShare) return false;
            foreach (var p in placed)
                if (Overlap(oe, on, c.Heading!.Value, size, p.E, p.N, p.Heading, p.Size)) return false;
            placed.Add((oe, on, c.Heading!.Value, size));
            string id = $"{code}-{(c.Ref.Length > 0 ? c.Ref : $"{c.E:F0}_{c.N:F0}")}";
            chosen.Add(new AirportStand
            {
                Id = id, Ref = c.Ref, Use = use, E = Math.Round(oe, 2), N = Math.Round(on, 2), Heading = Math.Round(c.Heading!.Value, 1),
                Ground = ground == null ? double.NaN : Math.Round(ground(oe, on), 2),
            });
            return true;
        }

        if (airliners)
        {
            foreach (var c in roomiest) if (TryPlace(c, StandUse.Heavy)) break;
            foreach (var c in roomiest) if (!chosen.Any(s => s.Ref == c.Ref && c.Ref.Length > 0) && TryPlace(c, StandUse.Military)) break;
            // stands spaced for an airliner (business jets' stands are packed 15-20 m apart) that fit one on their own
            var fits = usable.Where(c => !c.Cargo && Room(c) >= A320.Span * 0.8 && Fits(c, A320, paved)).ToList();
            // a row: from each stand in turn, its nearest neighbours on the same apron facing the same
            // way, as long as they fit beside one another; the seed giving the most, then the tightest row
            List<Candidate>? best = null;
            double bestSpread = double.MaxValue;
            foreach (var seed in fits)
            {
                var row = new List<Candidate>();
                var taken = new List<(double E, double N, double Heading, Size Size)>(placed);
                foreach (var c in fits.Where(c => c.Apron == seed.Apron && AngleBetween(c.Heading!.Value, seed.Heading!.Value) <= 30)
                             .OrderBy(c => Dist2(c, seed)))
                {
                    if (row.Count >= Airliners) break;
                    var (oe, on) = Origin(c.E, c.N, c.Heading!.Value, A320);
                    if (taken.Any(p => Overlap(oe, on, c.Heading!.Value, A320, p.E, p.N, p.Heading, p.Size))) continue;
                    taken.Add((oe, on, c.Heading!.Value, A320));
                    row.Add(c);
                }
                double spread = row.Sum(c => Math.Sqrt(Dist2(c, seed)));
                if (best == null || row.Count > best.Count || (row.Count == best.Count && spread < bestSpread - 1e-6))
                {
                    best = row;
                    bestSpread = spread;
                }
            }
            foreach (var c in best ?? new List<Candidate>()) TryPlace(c, StandUse.Airliner);
        }
        return chosen;
    }

    /// <summary>Stand refs in natural order ("2" before "10", "E2" before "E10"), unnamed last.</summary>
    public sealed class RefOrder : IComparer<string>
    {
        public static readonly RefOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            if (x.Length == 0 || y.Length == 0) return (x.Length == 0).CompareTo(y.Length == 0);
            var (px, nx, sx) = Split(x);
            var (py, ny, sy) = Split(y);
            int c = string.CompareOrdinal(px, py);
            if (c != 0) return c;
            c = nx.CompareTo(ny);
            return c != 0 ? c : string.CompareOrdinal(sx, sy);
        }

        private static (string Prefix, long Number, string Suffix) Split(string s)
        {
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            long n = j > i && long.TryParse(s.AsSpan(i, Math.Min(j - i, 18)), out var v) ? v : -1;
            return (s[..i], n, s[j..]);
        }
    }

    private static double Dist2(Candidate a, Candidate b) => (a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N);

    private static bool Fits(Candidate c, Size size, Func<double, double, bool> paved)
    {
        var (oe, on) = Origin(c.E, c.N, c.Heading!.Value, size);
        return PavedFraction(oe, on, c.Heading!.Value, size, paved) >= PavedShare;
    }

    /// <summary>The aircraft's origin with its nose gear on the stop point.</summary>
    public static (double E, double N) Origin(double e, double n, double heading, Size size)
    {
        var (fe, fn) = Dir(heading);
        return (e - fe * size.NoseGear, n - fn * size.NoseGear);
    }

    /// <summary>Share of the footprint's sample points (every 4 m, margin included) that are paved.</summary>
    public static double PavedFraction(double e, double n, double heading, Size size, Func<double, double, bool> paved)
    {
        var (fe, fn) = Dir(heading);
        double re = fn, rn = -fe;   // the right wing
        int all = 0, ok = 0;
        double half = size.Span / 2 + Margin;
        for (double along = -size.Tail - Margin; along <= size.Nose + Margin + 0.01; along += 4)
            for (double side = -half; side <= half + 0.01; side += 4)
            {
                // the wings are only so deep: far out, only the stretch round the wing's root counts
                if (Math.Abs(side) > 5 && (along < -size.Tail * 0.6 || along > size.Nose * 0.5)) continue;
                all++;
                if (paved(e + fe * along + re * side, n + fn * along + rn * side)) ok++;
            }
        return all == 0 ? 0 : (double)ok / all;
    }

    /// <summary>Whether two footprints (boxes nose to tail by wingtip to wingtip, margins included) overlap.</summary>
    public static bool Overlap(double e1, double n1, double h1, Size s1, double e2, double n2, double h2, Size s2)
    {
        var a = Corners(e1, n1, h1, s1);
        var b = Corners(e2, n2, h2, s2);
        foreach (var poly in new[] { a, b })
            for (int i = 0; i < 4; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % 4];
                double ax = -(q.N - p.N), ay = q.E - p.E;
                var (amin, amax) = Project(a, ax, ay);
                var (bmin, bmax) = Project(b, ax, ay);
                if (amax <= bmin || bmax <= amin) return false;
            }
        return true;
    }

    private static (double Min, double Max) Project((double E, double N)[] poly, double ax, double ay)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in poly)
        {
            double d = p.E * ax + p.N * ay;
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
        return (min, max);
    }

    private static (double E, double N)[] Corners(double e, double n, double heading, Size s)
    {
        var (fe, fn) = Dir(heading);
        double re = fn, rn = -fe;
        double f = s.Nose + Margin / 2, b = -(s.Tail + Margin / 2), w = s.Span / 2 + Margin / 2;
        return new[]
        {
            (e + fe * f + re * w, n + fn * f + rn * w), (e + fe * f - re * w, n + fn * f - rn * w),
            (e + fe * b - re * w, n + fn * b - rn * w), (e + fe * b + re * w, n + fn * b + rn * w),
        };
    }

    // ---- runways --------------------------------------------------------------------------------

    /// <summary>The steepest 100 m of a runway's centreline that an airliner still takes, %.</summary>
    public const double MaxGrade = 2.0;

    /// <summary>The largest bump (the ground's distance from a 200 m chord) an airliner still rolls over, m.</summary>
    public const double MaxBump = 0.6;

    /// <summary>The shortest runway counted usable, m (the freighter needs ~1 km, an A320 ~1.8 km).</summary>
    public const double MinUsable = 1500;

    /// <summary>
    /// A runway's profile on the ground: every 10 m along its centreline and both edges, the
    /// steepest 100 m of the centreline and the worst departure from a straight 200 m chord.
    /// </summary>
    public static AirportRunway Profile(string reference, double e1, double n1, double e2, double n2, double width, Func<double, double, double> ground)
    {
        double length = Math.Sqrt((e2 - e1) * (e2 - e1) + (n2 - n1) * (n2 - n1));
        var rw = new AirportRunway
        {
            Ref = reference, E1 = Math.Round(e1, 1), N1 = Math.Round(n1, 1), E2 = Math.Round(e2, 1), N2 = Math.Round(n2, 1),
            Width = width, Length = Math.Round(length), Grade = double.NaN, Bump = double.NaN,
        };
        if (length < 200) return rw;
        double ue = (e2 - e1) / length, un = (n2 - n1) / length;
        double re = un, rn = -ue;
        int steps = (int)(length / 10);
        double grade = 0, bump = 0;
        foreach (double side in new[] { 0, -width / 2 + 1, width / 2 - 1 })
        {
            var h = new double[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double t = i * length / steps;
                h[i] = ground(e1 + ue * t + re * side, n1 + un * t + rn * side);
                if (double.IsNaN(h[i])) return rw;
            }
            double dt = length / steps;
            int w100 = Math.Max(1, (int)Math.Round(100 / dt)), w200 = Math.Max(2, (int)Math.Round(200 / dt));
            if (side == 0)
                for (int i = 0; i + w100 <= steps; i++) grade = Math.Max(grade, Math.Abs(h[i + w100] - h[i]) / (w100 * dt) * 100);
            for (int i = 0; i + w200 <= steps; i++)
                for (int k = 1; k < w200; k++)
                    bump = Math.Max(bump, Math.Abs(h[i + k] - (h[i] + (h[i + w200] - h[i]) * k / w200)));
        }
        rw.Grade = Math.Round(grade, 2);
        rw.Bump = Math.Round(bump, 2);
        rw.Usable = length >= MinUsable && grade <= MaxGrade && bump <= MaxBump;
        return rw;
    }

    // ---- geometry -------------------------------------------------------------------------------

    /// <summary>The angle between two headings, degrees 0-180.</summary>
    public static double AngleBetween(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Degrees clockwise from grid north of a direction (east, north).</summary>
    public static double HeadingOf(double de, double dn)
    {
        double h = Math.Atan2(de, dn) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    public static (double E, double N) Dir(double heading)
    {
        double r = heading * Math.PI / 180;
        return (Math.Sin(r), Math.Cos(r));
    }

    private static double Dist((double E, double N) a, (double E, double N) b) =>
        Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));

    private static double DistanceTo(double e, double n, IReadOnlyList<IReadOnlyList<(double E, double N)>> lines) =>
        Nearest(e, n, lines) is { } p ? Math.Sqrt((p.E - e) * (p.E - e) + (p.N - n) * (p.N - n)) : double.MaxValue;

    /// <summary>The nearest point on any of the lines; null when there are none.</summary>
    public static (double E, double N)? Nearest(double e, double n, IReadOnlyList<IReadOnlyList<(double E, double N)>> lines)
    {
        (double E, double N)? best = null;
        double bestD = double.MaxValue;
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                double dx = b.E - a.E, dy = b.N - a.N, len2 = dx * dx + dy * dy;
                double t = len2 < 1e-9 ? 0 : Math.Clamp(((e - a.E) * dx + (n - a.N) * dy) / len2, 0, 1);
                double pe = a.E + dx * t, pn = a.N + dy * t;
                double d = (pe - e) * (pe - e) + (pn - n) * (pn - n);
                if (d < bestD) { bestD = d; best = (pe, pn); }
            }
        return best;
    }

    /// <summary>Whether a point lies inside a closed ring (even-odd), flat E,N pairs.</summary>
    public static bool Inside(IReadOnlyList<(double E, double N)> ring, double e, double n)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.N > n) != (b.N > n) && e < (b.E - a.E) * (n - a.N) / (b.N - a.N) + a.E) inside = !inside;
        }
        return inside;
    }
}
