namespace UnitSport.Terrain.Format;

/// <summary>The ground and the still water under a point, for planning piers. LV95 in, metres (LN02) out.</summary>
public interface IShoreSampler
{
    /// <summary>The ground, the lake bed under water; NaN where nothing is known (outside the region).</summary>
    double Ground(double e, double n);

    /// <summary>The still water level; NaN where it is dry or unknown.</summary>
    double Level(double e, double n);
}

/// <summary>
/// Plans the piers of the boat landings and the harbour jetties (#377) from the stops and lines of
/// swissTLM3D over the region's ground and water. Pure: the preprocessor runs it over the tiles
/// (<c>--landings</c>), the fixture lake over its own shapes, and the unit tests over a made-up shore.
///
/// <para>
/// <b>A landing</b> (a <c>Haltestelle Schiff</c>): swissTLM3D only has the stop's point, which lies
/// on the pier's head out in the water (Nyon: 45 m off the quay, 3.5 m deep). The plan, from it:
/// the way offshore (<c>n</c>, away from the dry ground round it); the steamer lying along the shore,
/// its port side to the pier, its centre of mass <see cref="Options.FaceOffset"/> out from the head's
/// face; the head (a slab <see cref="Options.HeadLength"/> along the ship, from just aft of the
/// paddle box, <see cref="Options.HeadDepth"/> across), its deck at the steamer's gangway plank so
/// the plank lands flush on it; a neck from the head's back to the shore, rising or falling to the
/// quay as a ramp. The head is moved out (the neck longer) until the hull floats with its clearance
/// all along (<see cref="Options.MaxExtend"/>); if it never does, it stays where the real boats
/// lie and the berth says it does not fit.
/// </para>
///
/// <para>
/// <b>A surveyed pier</b>: where swissTLM3D maps the landing's pier itself (a road with
/// <c>kunstbaute = Steg</c> ending at the stop: Nyon's 4 m one from the quay), the roads draw and
/// collide it as a bridge deck; the plan then has no neck of its own: a ramp from the road's end
/// down (or up) to the head, the head across the pier's end, the ship along it.
/// </para>
///
/// <para>
/// <b>A jetty</b> (a <c>Hafensteg</c> line): its TLM heights, but at least
/// <see cref="Options.JettyOverWater"/> over the water, meeting the ground where it runs ashore,
/// no ramp steeper than <see cref="Options.MaxRamp"/>.
/// </para>
/// </summary>
public static class LandingPlanner
{
    public sealed record Options
    {
        /// <summary>
        /// The steamer's main deck over the still water: its keel floats 1.64 m under the surface, the
        /// deck is 3.0 m over the keel (<c>SteamerMeshBuilder.DeckY</c>). Its gangway plank (#383) tilts
        /// from the deck's edge to the head's face, so a head may stand up to <see cref="PlankRise"/>
        /// over or under this.
        /// </summary>
        public double ShipDeckOverWater { get; init; } = 1.36;

        /// <summary>The head's deck over the water where nothing else sets it (a neck of its own): the plank 0.3 m down.</summary>
        public double DeckOverWater { get; init; } = 1.06;

        /// <summary>How far the plank tilts up or down over its 1.3 m: 0.52 m, 1 in 2.5.</summary>
        public double PlankRise { get; init; } = 0.52;

        /// <summary>
        /// The ship's centreline to the head's face: the plank's hinge on the deck's edge (4.35 m) plus
        /// its reach (1.3 m). The plank runs from the hinge to the face's top edge, whatever the head's
        /// height, so its foot is flush with the head's deck there (#383).
        /// </summary>
        public double FaceOffset { get; init; } = 5.65;

        /// <summary>The head starts this far aft of the ship's centre of mass (its paddle box ends 3.25 m aft of it) and runs <see cref="HeadLength"/> aft.</summary>
        public double HeadAft { get; init; } = 3.6;

        public double HeadLength { get; init; } = 10;
        public double HeadDepth { get; init; } = 5;
        public double NeckWidth { get; init; } = 3;

        /// <summary>The underwater hull: half its length, half its beam, and the water it needs (the design draught 1.68 + 0.6 clearance, as the steamer's berth search).</summary>
        public double HullHalfLength { get; init; } = 35;
        public double HullHalfBeam { get; init; } = 4.25;
        public double HullNeeds { get; init; } = 2.28;

        /// <summary>Water either side of the hull (its paddle boxes), m out from the centreline and deep.</summary>
        public double MarginHalf { get; init; } = 8;
        public double MarginDepth { get; init; } = 1.2;

        /// <summary>How far the head may be moved out from the surveyed stop to find water for the hull.</summary>
        public double MaxExtend { get; init; } = 80;

        /// <summary>How far the stop may be from the nearest dry ground, and a neck at most.</summary>
        public double MaxShore { get; init; } = 250;

        public double JettyWidth { get; init; } = 2.2;
        public double JettyOverWater { get; init; } = 0.35;

        /// <summary>The steepest ramp a deck has, rise over run.</summary>
        public double MaxRamp { get; init; } = 0.2;
    }

    public static readonly Options Default = new();

    /// <summary>
    /// The end of a surveyed pier the roads already draw (a <c>Steg</c>): where it ends (LV95), its
    /// deck's top there as the game draws it, the way it runs out (level, unit), its width.
    /// </summary>
    public readonly record struct RoadEnd(double E, double N, double Deck, double DirE, double DirN, double Width);

    /// <summary>How near the stop a surveyed pier must end to be the landing's, m.</summary>
    public const double RoadEndReach = 12;

    /// <summary>A bridge deck's top over its road points as the game draws and collides it (<c>RoadMeshBuilder.BridgeLift</c>).</summary>
    public const double RoadBridgeLift = 0.15;

    /// <summary>
    /// The surveyed pier ending at a stop: the nearest end within <see cref="RoadEndReach"/> of a
    /// bridge-flagged road at most 6 m wide, over water; null when there is none.
    /// </summary>
    public static RoadEnd? FindRoadEnd(double e, double n, IEnumerable<(TileId Tile, RoadSegment Segment)> roads, IShoreSampler s)
    {
        RoadEnd? best = null;
        double bestD = RoadEndReach;
        foreach (var (tile, seg) in roads)
        {
            if ((seg.Flags & RoadFlags.Bridge) == 0 || seg.Width > 6 || seg.PointCount < 2) continue;
            foreach (int i in new[] { 0, seg.PointCount - 1 })
            {
                var (pe, pn) = seg.Lv95(tile, i);
                double d = Dist(pe, pn, e, n);
                if (d > bestD || Depth(s, pe, pn) < 0.5) continue;
                var (qe, qn) = seg.Lv95(tile, i == 0 ? 1 : seg.PointCount - 2);
                double len = Dist(qe, qn, pe, pn);
                if (len < 1e-3) continue;
                bestD = d;
                best = new RoadEnd(pe, pn, seg.Points[i * 3 + 1] + RoadBridgeLift, (pe - qe) / len, (pn - qn) / len, seg.Width);
            }
        }
        return best;
    }

    // ---- landings ------------------------------------------------------------------------------

    /// <summary>Water depth at a point: 0 where dry or unknown.</summary>
    public static double Depth(IShoreSampler s, double e, double n)
    {
        double level = s.Level(e, n), ground = s.Ground(e, n);
        return double.IsNaN(level) || double.IsNaN(ground) || ground >= level ? 0 : level - ground;
    }

    private static bool Dry(IShoreSampler s, double e, double n) => Depth(s, e, n) <= 0;

    /// <summary>Plans a landing's pier and berth from its stop; null when there is no water by it or no shore within reach.</summary>
    /// <param name="road">The surveyed pier ending at the stop, if any (<see cref="RoadEnd"/>).</param>
    public static Landing? PlanLanding(string name, double e, double n, IShoreSampler s, Options? o = null,
        string source = "Haltestelle Schiff", RoadEnd? road = null)
    {
        o ??= Default;
        // the stop on the quay's edge: the nearest water a metre deep
        var (se, sn) = (e, n);
        double ne, nn;
        double ramp = 0, headDeck = double.NaN;
        if (road is { } r)
        {
            // the head beyond the surveyed pier's end, across it, at the pier's own height when the
            // plank can tilt to it (#383); a ramp between them only when it cannot
            (ne, nn) = (r.DirE, r.DirN);
            double ship = s.Level(r.E, r.N) + o.ShipDeckOverWater;
            headDeck = Math.Clamp(r.Deck, ship - o.PlankRise, ship + o.PlankRise);
            ramp = Math.Abs(r.Deck - headDeck) < 0.005 ? 0 : Math.Abs(r.Deck - headDeck) / o.MaxRamp;
            (se, sn) = (r.E, r.N);
        }
        else
        {
            if (Depth(s, se, sn) < 1.0)
            {
                if (NearestWhere(s, e, n, 80, (pe, pn) => Depth(s, pe, pn) >= 1.0) is not { } wet) return null;
                (se, sn) = wet;
            }
            // offshore: away from the dry ground round the stop
            if (Offshore(s, se, sn, o.MaxShore) is not { } off) return null;
            (ne, nn) = off;
        }
        // the bow along the shore, the port side (left of the bow) to the pier: left(b) = -n
        double be = -nn, bn = ne;
        double level = s.Level(se, sn);
        double deck = double.IsNaN(headDeck) ? level + o.DeckOverWater : headDeck;

        // moved out until the hull floats
        double extend = 0, depth = 0;
        bool fits = false;
        // the head's face with the head where the boats lie: through the stop, a metre beyond it; or
        // past a surveyed pier's end by the ramp and the head's depth
        double reach = road != null ? ramp + o.HeadDepth : 1;
        for (double x = 0; x <= o.MaxExtend; x += 2)
        {
            double fe = se + ne * (reach + x), fn = sn + nn * (reach + x);
            var (ce, cn) = Centre(fe, fn, ne, nn, be, bn, o);
            var (d, ok) = HullWater(s, ce, cn, be, bn, o);
            if (x == 0) depth = d;
            if (ok) { extend = x; depth = d; fits = true; break; }
        }
        double faceE = se + ne * (reach + extend), faceN = sn + nn * (reach + extend);
        var (keelE, keelN) = Centre(faceE, faceN, ne, nn, be, bn, o);

        var landing = new Landing { Name = name, E = e, N = n, Source = source };
        landing.Berth = new LandingBerth
        {
            E = Round(keelE), N = Round(keelN),
            Heading = Round((Math.Atan2(be, bn) * 180 / Math.PI + 360) % 360),
            Side = 0,
            Level = Round(level),
            Depth = Round(depth),
            Deck = Round(deck),
            Fits = fits,
        };

        // the head: along the ship at the face, HeadDepth back toward the shore
        double hd = o.HeadDepth * 0.5;
        double h0e = faceE - ne * hd + be * o.HeadLength * 0.5, h0n = faceN - nn * hd + bn * o.HeadLength * 0.5;
        double h1e = faceE - ne * hd - be * o.HeadLength * 0.5, h1n = faceN - nn * hd - bn * o.HeadLength * 0.5;
        landing.Ribbons.Add(new PierRibbon
        {
            Kind = PierKind.Pier,
            Width = o.HeadDepth,
            Points = { P(h0e, h0n, deck), P(h1e, h1n, deck) },
        });
        // bollards at the face's ends, clear of the gangway's landing
        foreach (double t in new[] { 0.42, -0.42 })
            landing.Bollards.Add(P(faceE - ne * 0.35 + be * o.HeadLength * t, faceN - nn * 0.35 + bn * o.HeadLength * t, deck));

        double pe0 = faceE - ne * o.HeadDepth, pn0 = faceN - nn * o.HeadDepth;
        if (road is { } rd)
        {
            // the ramp from the surveyed pier's end: its deck there, down to the head's at the slope,
            // then level out to the head (when the head was moved out for water)
            // (none when the head meets the pier's end at its own height)
            double run = Dist(rd.E, rd.N, pe0, pn0);
            if (run > 0.05)
            {
                var pts = new List<double[]> { P(rd.E, rd.N, rd.Deck) };
                double slope = Math.Min(run, Math.Abs(rd.Deck - deck) / o.MaxRamp);
                if (slope > 0.05 && slope < run - 0.05) pts.Add(P(rd.E + ne * slope, rd.N + nn * slope, deck));
                pts.Add(P(pe0, pn0, deck));
                landing.Ribbons.Add(new PierRibbon { Kind = PierKind.Pier, Width = rd.Width, Rails = true, Points = pts });
            }
            return landing;
        }
        // the neck: from the head's back toward the shore, onto the quay
        if (Neck(s, pe0, pn0, -ne, -nn, deck, o.NeckWidth, o) is not { } neck) return null;
        landing.Ribbons.Add(new PierRibbon { Kind = PierKind.Pier, Width = o.NeckWidth, Rails = true, Points = neck });
        return landing;
    }

    /// <summary>The ship's centre of mass for a face point: FaceOffset out, the head's middle HeadAft + HeadLength/2 aft of it.</summary>
    private static (double E, double N) Centre(double fe, double fn, double ne, double nn, double be, double bn, Options o)
    {
        double aft = o.HeadAft + o.HeadLength * 0.5;
        return (fe + ne * o.FaceOffset + be * aft, fn + nn * o.FaceOffset + bn * aft);
    }

    /// <summary>The least water under the hull lying at (ce, cn) along b, and whether it floats with its margins.</summary>
    public static (double Least, bool Floats) HullWater(IShoreSampler s, double ce, double cn, double be, double bn, Options? o = null)
    {
        o ??= Default;
        double le = -bn, ln = be;   // the beam, toward port
        double least = double.MaxValue;
        bool floats = true;
        for (double z = -o.HullHalfLength; z <= o.HullHalfLength + 1e-6; z += 2.5)
        {
            for (int k = -2; k <= 2; k++)
            {
                double x = o.HullHalfBeam * k / 2.0;
                double d = Depth(s, ce + be * z + le * x, cn + bn * z + ln * x);
                least = Math.Min(least, d);
                if (d < o.HullNeeds) floats = false;
            }
            foreach (double x in new[] { -o.MarginHalf, o.MarginHalf })
                if (Depth(s, ce + be * z + le * x, cn + bn * z + ln * x) < o.MarginDepth) floats = false;
        }
        return (least, floats);
    }

    /// <summary>
    /// A neck from (e, n) along (de, dn) to the shore, its deck from <paramref name="deck"/> ramping
    /// to the ground: over the water toward the height it meets ashore, then a few metres inland to a
    /// point 2 cm under the ground (the slab ends buried, not as a lip). Null when no shore is found.
    /// </summary>
    private static List<double[]>? Neck(IShoreSampler s, double e, double n, double de, double dn, double deck, double width, Options o)
    {
        // the first dry point along the way, then inland until the ground reaches the deck (a quay) or 6 m
        double reach = -1;
        for (double k = 0; k <= o.MaxShore; k += 1)
            if (Dry(s, e + de * k, n + dn * k)) { reach = k; break; }
        if (reach < 0) return null;
        double we = -dn * width * 0.5, wn = de * width * 0.5;
        double GroundAcross(double k)
        {
            double pe = e + de * k, pn = n + dn * k, g = double.MinValue;
            foreach (double t in new[] { -1.0, 0, 1.0 })
            {
                double gg = s.Ground(pe + we * t, pn + wn * t);
                if (!double.IsNaN(gg)) g = Math.Max(g, gg);
            }
            return g == double.MinValue ? deck : g;
        }
        // where the ground stands at the deck's height or over it: the quay's top, a beach's crest
        double end = reach + 3;
        for (double k = reach; k <= reach + 6; k += 0.5)
            if (GroundAcross(k) >= deck - 0.15) { end = k + 1.5; break; }
        end = Math.Min(end, reach + 6);
        double landE = e + de * end, landN = n + dn * end;
        double landH = s.Ground(landE, landN);
        if (double.IsNaN(landH)) landH = GroundAcross(end);

        var pts = new List<double[]>();
        int steps = Math.Max(1, (int)Math.Ceiling(reach / 4.0));
        // over the water: from the head's deck to the height ashore (never under the ground it crosses)
        double ashore = Math.Max(landH, GroundAcross(reach) + 0.03);
        for (int i = 0; i <= steps; i++)
        {
            double k = reach * i / steps;
            double h = deck + (ashore - deck) * (k / Math.Max(reach, 1e-6));
            pts.Add(P(e + de * k, n + dn * k, Math.Max(h, GroundAcross(k) + 0.03)));
        }
        // ashore: across the bank, the last point buried 2 cm
        for (double k = reach + 1.5; k < end - 0.75; k += 1.5)
            pts.Add(P(e + de * k, n + dn * k, Math.Max(ashore, GroundAcross(k) + 0.03)));
        pts.Add(P(landE, landN, landH - 0.02));
        LimitRamps(pts, o.MaxRamp, keepFirst: true);
        return pts;
    }

    // ---- jetties -------------------------------------------------------------------------------

    /// <summary>Plans a harbour jetty from its TLM line; null when it does not touch the water.</summary>
    public static Jetty? PlanJetty(string id, IReadOnlyList<(double E, double N, double Z)> line, IShoreSampler s, Options? o = null)
    {
        o ??= Default;
        if (line.Count < 2) return null;
        var pts = new List<double[]>();
        bool wet = false;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 3.0));
            for (int k = i == 0 ? 0 : 1; k <= steps; k++)
            {
                double t = (double)k / steps;
                double e = a.E + (b.E - a.E) * t, n = a.N + (b.N - a.N) * t, z = a.Z + (b.Z - a.Z) * t;
                double level = s.Level(e, n), ground = s.Ground(e, n);
                double h;
                if (!double.IsNaN(level) && !double.IsNaN(ground) && ground < level)
                {
                    wet = true;
                    h = Math.Max(z, level + o.JettyOverWater);
                }
                else h = double.IsNaN(ground) ? z : ground - 0.02;
                pts.Add(P(e, n, h));
            }
        }
        if (!wet) return null;
        // a ramp from a pontoon up to a quay: raise the water end, never dig into the ground
        LimitRamps(pts, o.MaxRamp, keepFirst: false);
        return new Jetty { Id = id, Ribbon = new PierRibbon { Kind = PierKind.Jetty, Width = o.JettyWidth, Points = pts } };
    }

    /// <summary>
    /// Raises points so no stretch climbs or falls faster than <paramref name="max"/> (only ever up,
    /// so nothing digs into the ground). <paramref name="keepFirst"/>: the first point (a head's deck,
    /// which the steamer's plank lands on) stays where it is.
    /// </summary>
    public static void LimitRamps(List<double[]> pts, double max, bool keepFirst)
    {
        double Dist(int i, int j) => Math.Sqrt(Sq(pts[i][0] - pts[j][0]) + Sq(pts[i][1] - pts[j][1]));
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < pts.Count; i++)
                pts[i][2] = Math.Max(pts[i][2], pts[i - 1][2] - max * Dist(i, i - 1));
            for (int i = pts.Count - 2; i >= (keepFirst ? 1 : 0); i--)
                pts[i][2] = Math.Max(pts[i][2], pts[i + 1][2] - max * Dist(i, i + 1));
        }
        foreach (var p in pts) p[2] = Round(p[2]);
    }

    // ---- shore -------------------------------------------------------------------------------

    /// <summary>The way out from the shore at a wet point: the mean direction away from the dry samples nearest it. Null: no dry ground within reach.</summary>
    public static (double E, double N)? Offshore(IShoreSampler s, double e, double n, double reach)
    {
        double first = -1, se = 0, sn = 0;
        for (double r = 2; r <= reach; r += 2)
        {
            if (first > 0 && r > first + 20) break;
            int around = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r / 2));
            for (int k = 0; k < around; k++)
            {
                double a = 2 * Math.PI * k / around;
                double pe = e + Math.Cos(a) * r, pn = n + Math.Sin(a) * r;
                if (!Dry(s, pe, pn)) continue;
                if (first < 0) first = r;
                // nearer dry ground counts more
                double w = 1 / r;
                se += (e - pe) / r * w;
                sn += (n - pn) / r * w;
            }
        }
        double len = Math.Sqrt(se * se + sn * sn);
        return first < 0 || len < 1e-9 ? null : (se / len, sn / len);
    }

    private static (double E, double N)? NearestWhere(IShoreSampler s, double e, double n, double reach, Func<double, double, bool> ok)
    {
        if (ok(e, n)) return (e, n);
        for (double r = 2; r <= reach; r += 2)
        {
            int around = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r / 2));
            for (int k = 0; k < around; k++)
            {
                double a = 2 * Math.PI * k / around;
                double pe = e + Math.Cos(a) * r, pn = n + Math.Sin(a) * r;
                if (ok(pe, pn)) return (pe, pn);
            }
        }
        return null;
    }

    private static double Dist(double e0, double n0, double e1, double n1) => Math.Sqrt(Sq(e1 - e0) + Sq(n1 - n0));

    private static double[] P(double e, double n, double h) => new[] { Round(e), Round(n), Round(h) };
    private static double Round(double v) => Math.Round(v, 3);
    private static double Sq(double v) => v * v;
}
