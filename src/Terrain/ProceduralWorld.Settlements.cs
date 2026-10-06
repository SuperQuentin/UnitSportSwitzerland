using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    // ---- layout ------------------------------------------------------------------------------
    //
    // Villages stand on the valley roads, in the slots the network placed (Network.Villages).
    // Each is a row of houses either side of the road plus one or two side streets climbing away
    // from the river. Farms are scattered on their own over the floors and the lower slopes, with
    // alpine huts higher up. Everything is planned in LV95 and handed to whichever tile holds a
    // building's centre, as the real extractor does.

    private const double RoadStep = 3;   // drawn polyline spacing; under the 4 m the drape expects

    /// <summary>
    /// A village or town street. A bridge has <see cref="RoadFlags.Bridge"/>; the streets running up
    /// to either end of one name it in <paramref name="Bridge"/> (and which of their ends it meets),
    /// so their heights can ramp up to its deck instead of the deck sinking to them (#559).
    /// </summary>
    private sealed record Street(List<(double E, double N)> Points, RoadClass Class,
        RoadFlags Flags = 0, Street? Bridge = null, bool BridgeAtEnd = false);

    private readonly record struct Footprint(double E, double N, double UE, double UN,
        double HalfLength, double HalfWidth);

    /// <param name="Outline">A shaped building's outline (#598, <see cref="Shape"/>): its walls and flat roof
    /// follow it, and <paramref name="Rect"/> is only its bounding box. Null for a plain rectangle.</param>
    private sealed record Plan(Footprint Rect, BuildingKind Kind, double WallHeight, double Pitch,
        byte Floors, ushort Year, bool Tower = false, Shape? Outline = null);

    private sealed record Village(VillageSlot Slot, List<Street> Streets, List<Plan> Buildings, bool IsTown = false);

    private readonly Dictionary<int, Village> _villages = new();

    private Village VillageAt(int id)
    {
        lock (_villages)
            if (_villages.TryGetValue(id, out var cached)) return cached;
        var village = PlanVillage(Network.Instance.Villages[id]);
        lock (_villages) _villages[id] = village;
        return village;
    }

    private IEnumerable<Village> VillagesNear(double minE, double minN, double maxE, double maxN)
    {
        foreach (int id in Network.Instance.VillageIndex.In(minE, minN, maxE, maxN))
            yield return VillageAt(id);
    }

    /// <summary>
    /// The generated villages, as the stand-in for <c>places.json</c> (which a generated world does
    /// not have): each village's centre on its road, in LV95, with its building count.
    /// </summary>
    public IEnumerable<(double E, double N, int Buildings, string Name)> VillageCentres()
    {
        double half = RadiusTiles * 1000.0;
        foreach (var v in VillagesNear(CenterE - half, CenterN - half, CenterE + half, CenterN + half))
        {
            var s = v.Slot;
            if (Math.Abs(s.E - CenterE) > half || Math.Abs(s.N - CenterN) > half) continue;
            yield return (s.E, s.N, v.Buildings.Count, $"Village {s.Id}");
        }
    }

    /// <summary>
    /// A point on a line at an arc length, its unit direction, and the normal pointing away from
    /// the river it follows, in LV95.
    /// </summary>
    private static ((double E, double N) P, (double E, double N) T, (double E, double N) Nrm) RoadFrame(
        Line line, double s)
    {
        int i = Array.BinarySearch(line.S, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, line.Count - 2);
        double dx = line.E[i + 1] - line.E[i], dy = line.N[i + 1] - line.N[i];
        double len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        var t = (dx / len, dy / len);
        return (Network.PointAt(line, s), t, (-t.Item2 * line.Side, t.Item1 * line.Side));
    }

    private static Village PlanVillage(VillageSlot slot)
    {
        var line = Network.Instance.Lines[slot.Line];
        var rng = new Random(unchecked(slot.Id * 7919 + 104729));
        double x = slot.S;
        double halfLength = slot.HalfLength;
        var streets = new List<Street>();
        var plans = new List<Plan>();
        // the biggest slots, if they can host it, are towns: their cross road takes the first side street's place
        var town = slot.IsTown ? PlanTown(slot, line) : null;

        // side streets, away from the river
        var streetXs = new List<double> { town is null ? x + 25 : x };
        if (halfLength > 260) streetXs.Add(x + (rng.NextDouble() < 0.5 ? -1 : 1) * halfLength * 0.6);
        foreach (double sx in streetXs)
        {
            if (town is not null && sx == x) continue;
            var (p, t, nrm) = RoadFrame(line, sx);
            double length = 170 + rng.NextDouble() * 150;
            double bend = (rng.NextDouble() - 0.5) * 0.5;   // radians over the street's length
            var points = new List<(double E, double N)>();
            double e = p.E, n = p.N, heading = Math.Atan2(nrm.N, nrm.E);
            for (double s = 0; s <= length; s += RoadStep)
            {
                points.Add((e, n));
                heading += bend * RoadStep / length;
                e += Math.Cos(heading) * RoadStep;
                n += Math.Sin(heading) * RoadStep;
            }
            streets.Add(new Street(points, RoadClass.Minor));

            // houses along both sides of it, starting clear of the main road's frontage
            for (int side = -1; side <= 1; side += 2)
                for (double s = 34 + rng.NextDouble() * 8; s < length - 8;)
                {
                    int i = Math.Min((int)(s / RoadStep), points.Count - 2);
                    var a = points[i];
                    var b = points[i + 1];
                    double dl = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
                    var dir = ((b.E - a.E) / dl, (b.N - a.N) / dl);
                    var left = (-dir.Item2, dir.Item1);
                    double width = 9 + rng.NextDouble() * 4, depth = 10 + rng.NextDouble() * 3;
                    double setback = 7 + rng.NextDouble() * 3 + depth / 2;
                    var c = (E: a.E + left.Item1 * side * setback, N: a.N + left.Item2 * side * setback);
                    bool house = rng.NextDouble() < 0.85;
                    if (house)
                        plans.Add(House(rng, c, dir, width, depth, s < 80 ? 0.2 : 0.0));
                    double gap = 5 + rng.NextDouble() * 10;
                    // a garage beside some of the houses, in the gap before the next one (which is
                    // at most 13 m wide), its front in line with the house's and its door to the street
                    double gs = s + width / 2 + 2.5;
                    if (house && gap >= 11.5 - width / 2 && gs < length - 4
                        && Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 211) < 0.5)
                        plans.Add(Garage(points, gs, side, setback - depth / 2 + GarageHalfDepth));
                    s += width + gap;
                }
        }

        // the main street: a row each side, thinning toward the village's ends
        double mainStreet = streetXs[0];
        for (int side = -1; side <= 1; side += 2)
        {
            double along = -halfLength;
            while (along < halfLength)
            {
                double width = 9 + rng.NextDouble() * 5, depth = 10 + rng.NextDouble() * 4;
                double cx = x + along + width / 2;
                // a town's core, round the crossroads, is built tight
                bool core = town is not null && Math.Abs(cx - x) < TownCoreM;
                along += width + (core ? 2 + rng.NextDouble() * 5 : 5 + rng.NextDouble() * 12);

                // leave the mouths of the side streets and the church plot clear
                if (streetXs.Any(sx => Math.Abs(cx - sx) < width / 2 + 9)) continue;
                if (side > 0 && Math.Abs(cx - (mainStreet + 32)) < 22) continue;
                double edge = Math.Abs(cx - x) / halfLength;
                if (!core && rng.NextDouble() > 0.92 - 0.5 * edge * edge) continue;

                var (p, t, nrm) = RoadFrame(line, cx);
                double setback = core ? 11 + rng.NextDouble() * 2 + depth / 2 : 9 + rng.NextDouble() * 5 + depth / 2;
                var c = (E: p.E + nrm.E * side * setback, N: p.N + nrm.N * side * setback);
                // barns at the ends of the village, a few apartment blocks in the middle
                if (edge > 0.75 && rng.NextDouble() < 0.4)
                    plans.Add(new Plan(new Footprint(c.E, c.N, t.E, t.N, 9 + rng.NextDouble() * 3, 6.5),
                        BuildingKind.Agricultural, 5, 24, 0, (ushort)(1880 + rng.Next(80))));
                else
                {
                    var plan = House(rng, c, t, width, depth, core ? 0.5 : edge < 0.3 ? 0.3 : 0.0);
                    // the village shops (#273): a third of the houses in its middle keep a shop on
                    // the ground floor, by a hash of where they stand, so no other building moves
                    if (plan.Kind == BuildingKind.House && edge < 0.35
                        && Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 227) < (core ? 0.5 : 0.35))
                        plan = plan with { Kind = BuildingKind.Commercial };
                    // a block is wider than the slot its neighbours leave it: push it on
                    if (core && plan.Kind == BuildingKind.Apartment)
                    {
                        plan = plan with { Rect = plan.Rect with { E = plan.Rect.E + t.E * 3, N = plan.Rect.N + t.N * 3 } };
                        along += 6;
                    }
                    plans.Add(plan);
                }
            }
        }

        // the church, beside the first side street, turned to face along the valley
        {
            var (p, t, nrm) = RoadFrame(line, mainStreet + 32);
            var c = (E: p.E + nrm.E * 26, N: p.N + nrm.N * 26);
            plans.Add(new Plan(new Footprint(c.E, c.N, nrm.E, nrm.N, 12, 5.5), BuildingKind.Sacral,
                9, 40, 0, 1650));
            var tower = (E: c.E - nrm.E * 14.5, N: c.N - nrm.N * 14.5);
            plans.Add(new Plan(new Footprint(tower.E, tower.N, nrm.E, nrm.N, 2.6, 2.6), BuildingKind.Sacral,
                24, 0, 0, 1650, Tower: true));
        }

        // The works, beyond one end of the village (#531). A village is houses, a church and a few
        // barns, and nothing in the generated world was ever `BuildingKind.Industrial` — so the
        // warehouses, yards and loading bays of #496 could be built but never driven to. It stands
        // off the valley road past the last house, on the side away from the river, with its long
        // wall to the road: that is the wall `BuildingFootprint` puts the door on, and so the wall
        // the loading bays and the yard go on (#528, #516).
        if (halfLength > WorksMinVillage)
        {
            double at = x + (rng.NextDouble() < 0.5 ? -1 : 1) * (halfLength + WorksBeyondEnd);
            var (p, t, nrm) = RoadFrame(line, at);
            var c = (E: p.E + nrm.E * WorksSetback, N: p.N + nrm.N * WorksSetback);
            // two sizes, by where it stands rather than by the village's own rng, so the works
            // never moves a house: a big shed is a warehouse or a works, a medium one can also be
            // a haulier's depot (BuildingTypes.SiteFor decides from the footprint)
            bool big = Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 229) < 0.55;
            double halfLong = big ? 17 : 11, halfShort = big ? 11 : 8;
            var year = (ushort)(1968 + (int)(Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 233) * 50));
            plans.Add(new Plan(new Footprint(c.E, c.N, t.E, t.N, halfLong, halfShort),
                BuildingKind.Industrial, big ? 8.5 : 6.5, 0, 0, year));
        }

        if (town is not null) AddTown(slot, town, streets, plans);
        AddShaped(slot, line, streets, plans);
        return new Village(slot, streets, plans, town is not null);
    }

    /// <summary>A village shorter than this is a hamlet, and a hamlet has no works.</summary>
    private const double WorksMinVillage = 200;

    /// <summary>How far past the last house the works stands, metres along the valley road.</summary>
    private const double WorksBeyondEnd = 70;

    /// <summary>
    /// Its middle, off the valley road's centre line. Far enough that the yard in front of it
    /// (<c>SiteYards</c>, 7 m apron plus up to 34 m of standing room) does not reach the road —
    /// a dormant lorry on the carriageway would be dropped, and the yard would look half-used.
    /// </summary>
    private const double WorksSetback = 52;

    private const double GarageHalfWidth = 1.7, GarageHalfDepth = 3.1;

    /// <summary>
    /// A flat-roofed single garage <paramref name="along"/> metres up a street, on its
    /// <paramref name="side"/>, its middle <paramref name="setback"/> metres off the centre line:
    /// one car wide and deep, tall enough for its door and the sign over it.
    /// </summary>
    private static Plan Garage(List<(double E, double N)> points, double along, int side, double setback)
    {
        int i = Math.Min((int)(along / RoadStep), points.Count - 2);
        var a = points[i];
        var b = points[i + 1];
        double dl = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
        var dir = ((b.E - a.E) / dl, (b.N - a.N) / dl);
        var left = (-dir.Item2, dir.Item1);
        double off = along - i * RoadStep;
        var c = (E: a.E + dir.Item1 * off + left.Item1 * side * setback, N: a.N + dir.Item2 * off + left.Item2 * side * setback);
        var year = (ushort)(1955 + (int)(Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 223) * 60));
        return new Plan(new Footprint(c.E, c.N, dir.Item1, dir.Item2, GarageHalfWidth, GarageHalfDepth),
            BuildingKind.Garage, 3.3, 0, 1, year);
    }

    private static Plan House(Random rng, (double E, double N) c, (double E, double N) along,
        double width, double depth, double apartmentChance)
    {
        if (rng.NextDouble() < apartmentChance)
            return new Plan(new Footprint(c.E, c.N, along.E, along.N, width / 2 + 3, depth / 2 + 1),
                BuildingKind.Apartment, 9 + rng.Next(2) * 3, 0, (byte)(3 + rng.Next(2)),
                (ushort)(1960 + rng.Next(55)));

        // ridge along the street, or across it for the older, narrower houses
        bool gableToStreet = rng.NextDouble() < 0.35;
        var u = gableToStreet ? (-along.N, along.E) : along;
        double halfLength = (gableToStreet ? depth : width) / 2;
        double halfWidth = (gableToStreet ? width : depth) / 2;
        return new Plan(new Footprint(c.E, c.N, u.Item1, u.Item2, halfLength, halfWidth), BuildingKind.House,
            5.2 + rng.NextDouble() * 1.4, 28 + rng.NextDouble() * 12, 2, (ushort)(1850 + rng.Next(170)));
    }

    /// <summary>Whether a point is in or beside a village: its stretch of road and the streets off it.</summary>
    private static bool NearVillage(double e, double n)
    {
        var net = Network.Instance;
        foreach (int id in net.VillageIndex.At(e, n))
        {
            var v = net.Villages[id];
            if (Sq(e - v.E) + Sq(n - v.N) < Sq(v.HalfLength + 420)) return true;
        }
        return false;
    }

    /// <summary>Farms and alpine huts on a jittered 350 m grid, wherever the ground allows.</summary>
    private IEnumerable<Plan> FarmsNear(Site site, double minE, double minN, double maxE, double maxN)
    {
        const double cell = 350;
        for (int gi = (int)Math.Floor((minE - 60) / cell); gi <= (int)Math.Floor((maxE + 60) / cell); gi++)
            for (int gj = (int)Math.Floor((minN - 60) / cell); gj <= (int)Math.Floor((maxN + 60) / cell); gj++)
            {
                if (Noise.Hash01(gi, gj, 101) > 0.3) continue;
                double e = (gi + 0.15 + 0.7 * Noise.Hash01(gi, gj, 103)) * cell;
                double n = (gj + 0.15 + 0.7 * Noise.Hash01(gi, gj, 107)) * cell;

                // clear of the roads, the railways, the rivers, the lakes and the villages
                if (NearestLine(e, n).Distance < 45) continue;
                if (FineNear(e, n).Dr < 50) continue;
                if (SampleCoarse(site.Lattice, e, n).Lake > 0.1) continue;
                if (NearVillage(e, n)) continue;

                double angle = Noise.Hash01(gi, gj, 109) * Math.PI;
                var u = (Math.Cos(angle), Math.Sin(angle));
                double alt = Ground(site, e, n);
                if (alt > 2250) continue;
                if (CoverAt(site, e, n) is not CoverClass.Open) continue;

                var rng = new Random(unchecked(gi * 92821 ^ gj * 68917));
                if (alt > 1350)
                {
                    // an alpine hut: small, low, dark timber
                    yield return new Plan(new Footprint(e, n, u.Item1, u.Item2, 4.5, 3.5),
                        BuildingKind.Agricultural, 3.2, 26, 1, (ushort)(1800 + rng.Next(120)));
                    continue;
                }
                yield return new Plan(new Footprint(e, n, u.Item1, u.Item2, 6, 5), BuildingKind.House,
                    5.5, 32, 2, (ushort)(1850 + rng.Next(150)));
                var barn = (E: e + u.Item2 * 17, N: n - u.Item1 * 17);
                yield return new Plan(new Footprint(barn.E, barn.N, u.Item1, u.Item2, 11, 7.5),
                    BuildingKind.Agricultural, 5, 24, 0, (ushort)(1870 + rng.Next(100)));
            }
    }

    /// <summary>Cover at one point, classified the way the raster is, for siting farms.</summary>
    private CoverClass CoverAt(Site site, double e, double n)
    {
        const double d = CoverStep;
        double h = Ground(site, e, n);
        double gx = (Ground(site, e + d, n) - Ground(site, e - d, n)) / (2 * d);
        double gy = (Ground(site, e, n + d) - Ground(site, e, n - d)) / (2 * d);
        bool waterOk = site.Blend == null || site.Blend.WaterAllowed(e, n);
        return Classify(Inputs(e, n, h, gx, gy, SampleCoarse(site.Lattice, e, n), FineNear(e, n), waterOk));
    }

    private IEnumerable<Plan> PlansNear(Site site, double minE, double minN, double maxE, double maxN)
    {
        bool In(Plan p) => p.Rect.E >= minE && p.Rect.E < maxE && p.Rect.N >= minN && p.Rect.N < maxN;
        var villages = VillagesNear(minE, minN, maxE, maxN).ToList();
        foreach (var v in villages)
            foreach (var p in v.Buildings)
                if (In(p) && p.Kind is not (BuildingKind.Garage or BuildingKind.Industrial) && p.Outline == null) yield return p;
        foreach (var p in FarmsNear(site, minE, minN, maxE, maxN))
            if (In(p)) yield return p;
        // Garages, then the works, each after everything that came before it. A building's index in
        // its tile is its name — the interior's plan key, its loot records, a check's hard-coded
        // `2585_1114_52` — so a kind added later has to be yielded last or it renames everything
        // after it. Garages learnt this in #139; the works keeps the rule in #531.
        foreach (var v in villages)
            foreach (var p in v.Buildings)
                if (In(p) && p.Kind == BuildingKind.Garage) yield return p;
        foreach (var v in villages)
            foreach (var p in v.Buildings)
                if (In(p) && p.Kind == BuildingKind.Industrial) yield return p;
        // shaped buildings (#598) after the works, for the same reason
        foreach (var v in villages)
            foreach (var p in v.Buildings)
                if (In(p) && p.Outline != null) yield return p;
    }

    // ---- roads -------------------------------------------------------------------------------

    /// <summary>
    /// The nearest valley road or railway to a point, within <see cref="Network.LineReach"/>: its
    /// distance and class, or the reach and null. Village streets are not in it.
    /// </summary>
    private static (double Distance, RoadClass? Class) NearestLine(double e, double n)
    {
        var net = Network.Instance;
        double best = Network.LineReach;
        RoadClass? cls = null;
        foreach (int item in net.LineSegs.At(e, n))
        {
            var line = net.Lines[item >> 16];
            int i = item & 0xFFFF;
            var (d, _) = SegmentDistance(e, n, line.E[i], line.N[i], line.E[i + 1], line.N[i + 1]);
            if (d < best)
            {
                best = d;
                cls = line.Class;
            }
        }
        return (best, cls);
    }

    /// <summary>
    /// World polylines passing near a box: the valley roads and railways, drawn as a Catmull-Rom
    /// through their points every <see cref="RoadStep"/>, and the village streets. A segment is
    /// cut into the same steps whichever tile asks, so pieces meet at a seam.
    /// </summary>
    private readonly record struct Drawn(List<(double E, double N)> Points, RoadClass Class, string Key, double FromM,
        Street? Street = null, bool Town = false);

    private IEnumerable<Drawn> LinesNear(
        double minE, double minN, double maxE, double maxN)
    {
        var net = Network.Instance;
        var items = net.LineSegs.In(minE, minN, maxE, maxN);
        for (int k = 0; k < items.Count;)
        {
            int l = items[k] >> 16;
            var line = net.Lines[l];
            // a run of consecutive segments of one line
            int first = items[k] & 0xFFFF, last = first;
            k++;
            while (k < items.Count && items[k] >> 16 == l && (items[k] & 0xFFFF) == last + 1)
            {
                last++;
                k++;
            }
            var points = new List<(double E, double N)>();
            for (int i = first; i <= last; i++)
            {
                int i0 = Math.Max(0, i - 1), i3 = Math.Min(line.Count - 1, i + 2);
                double len = Math.Sqrt(Sq(line.E[i + 1] - line.E[i]) + Sq(line.N[i + 1] - line.N[i]));
                int steps = Math.Max(1, (int)Math.Ceiling(len / RoadStep));
                for (int s = 0; s < steps; s++)
                {
                    double t = s / (double)steps;
                    points.Add((CatmullRom(line.E[i0], line.E[i], line.E[i + 1], line.E[i3], t),
                        CatmullRom(line.N[i0], line.N[i], line.N[i + 1], line.N[i3], t)));
                }
            }
            points.Add((line.E[last + 1], line.N[last + 1]));
            yield return new Drawn(points, line.Class, $"gen-line-{l}", DrawnStation(l, first));
        }

        foreach (var v in VillagesNear(minE, minN, maxE, maxN))
            for (int k = 0; k < v.Streets.Count; k++)
                yield return new Drawn(v.Streets[k].Points, v.Streets[k].Class, $"gen-village-{v.Slot.Id}-{k}", 0,
                    v.Streets[k], v.IsTown);
    }

    public RoadTile? BuildRoads(TileId id, Blend? blend = null) => BuildRoadsKeyed(id, blend).Tile;

    /// <summary>
    /// Which generated line a road segment is a piece of, and how far along it the piece starts:
    /// the stand-in for the TLM uuid and along-line metre RoadGen keys its per-street choices on.
    /// </summary>
    public readonly record struct RoadKey(string Line, double FromM);

    /// <summary><see cref="BuildRoads"/>, with a <see cref="RoadKey"/> per segment.</summary>
    public (RoadTile? Tile, List<RoadKey> Keys) BuildRoadsKeyed(TileId id, Blend? blend = null)
    {
        CheckBlend(id, blend);
        blend?.PrepareLattice();
        var site = new Site(LatticeFor(id, fine: false), blend);
        double maxE = id.MinE + ChunkFormat.TileSizeM;
        var segments = new List<RoadSegment>();
        var keys = new List<RoadKey>();
        foreach (var drawn in LinesNear(id.MinE, id.MinN, maxE, id.MaxN))
        {
            var (points, cls, key, fromM) = (drawn.Points, drawn.Class, drawn.Key, drawn.FromM);
            var bridge = drawn.Street is { } st ? (st.Flags & RoadFlags.Bridge) != 0 ? st : st.Bridge : null;
            // a bridge's deck is level, over the higher of its two banks; the street running up to
            // it ramps from the ground to the deck over ApproachM, never the deck down to the ground
            double deck = 0, delta = 0;
            (double E, double N) meet = default;
            if (bridge is not null)
            {
                deck = Math.Max(Ground(site, bridge.Points[0].E, bridge.Points[0].N),
                    Ground(site, bridge.Points[^1].E, bridge.Points[^1].N)) + DeckClearance;
                if (bridge != drawn.Street)
                {
                    meet = drawn.Street!.BridgeAtEnd ? drawn.Street.Points[^1] : drawn.Street.Points[0];
                    delta = deck - Ground(site, meet.E, meet.N);
                }
            }
            foreach (var (piece, at) in Clip(points, id.MinE, id.MinN, maxE, id.MaxN))
            {
                var xyz = new float[piece.Count * 3];
                for (int i = 0; i < piece.Count; i++)
                {
                    var (e, n) = piece[i];
                    double y = Ground(site, e, n);
                    if (bridge is not null)
                        y = bridge == drawn.Street ? deck
                            : y + delta * (1 - SmoothStep(0, ApproachM, Math.Sqrt(Sq(e - meet.E) + Sq(n - meet.N))));
                    xyz[i * 3] = (float)(e - id.MinE);
                    xyz[i * 3 + 1] = (float)y;
                    xyz[i * 3 + 2] = (float)(id.MaxN - n);
                }
                segments.Add(new RoadSegment
                {
                    Class = cls,
                    Surface = cls == RoadClass.Railway ? RoadSurface.Unknown : RoadSurface.Paved,
                    Flags = drawn.Street?.Flags ?? 0,
                    Width = RoadFormat.DefaultWidth(cls),
                    Points = xyz,
                });
                keys.Add(new RoadKey(key, fromM + at));
            }
        }
        return (segments.Count == 0 ? null : new RoadTile { Id = id, Segments = segments }, keys);
    }

    private readonly Dictionary<int, double[]> _drawnStations = new();

    /// <summary>
    /// Metres along a valley line's drawn polyline (as <see cref="LinesNear"/> draws it) to its
    /// node <paramref name="node"/>, the same whichever tile asks.
    /// </summary>
    private double DrawnStation(int l, int node)
    {
        double[]? at;
        lock (_drawnStations) _drawnStations.TryGetValue(l, out at);
        if (at is null)
        {
            var line = Network.Instance.Lines[l];
            at = new double[line.Count];
            for (int i = 0; i + 1 < line.Count; i++)
            {
                int i0 = Math.Max(0, i - 1), i3 = Math.Min(line.Count - 1, i + 2);
                double len = Math.Sqrt(Sq(line.E[i + 1] - line.E[i]) + Sq(line.N[i + 1] - line.N[i]));
                int steps = Math.Max(1, (int)Math.Ceiling(len / RoadStep));
                double pe = line.E[i], pn = line.N[i], sum = 0;
                for (int s = 1; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    double e = s == steps ? line.E[i + 1] : CatmullRom(line.E[i0], line.E[i], line.E[i + 1], line.E[i3], t);
                    double n = s == steps ? line.N[i + 1] : CatmullRom(line.N[i0], line.N[i], line.N[i + 1], line.N[i3], t);
                    sum += Math.Sqrt(Sq(e - pe) + Sq(n - pn));
                    (pe, pn) = (e, n);
                }
                at[i + 1] = at[i] + sum;
            }
            lock (_drawnStations) _drawnStations[l] = at;
        }
        return at[node];
    }

    /// <summary>
    /// Cuts a polyline to a box, inserting the crossing points (Liang-Barsky per segment). Two
    /// tiles sharing an edge compute the same crossing from the same segment, so pieces meet.
    /// </summary>
    private static List<(List<(double E, double N)> Points, double At)> Clip(List<(double E, double N)> line,
        double minE, double minN, double maxE, double maxN)
    {
        var pieces = new List<(List<(double E, double N)> Points, double At)>();
        List<(double E, double N)>? current = null;
        double along = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.E - a.E, dy = b.N - a.N;
            double from = along;
            along += Math.Sqrt(dx * dx + dy * dy);
            double t0 = 0, t1 = 1;
            if (!Edge(-dx, a.E - minE) || !Edge(dx, maxE - a.E)
                || !Edge(-dy, a.N - minN) || !Edge(dy, maxN - a.N)
                // only touching the box at a vertex that lies on its edge
                || t1 - t0 < 1e-9)
            {
                current = null;
                continue;
            }

            var start = (a.E + dx * t0, a.N + dy * t0);
            var end = (a.E + dx * t1, a.N + dy * t1);
            if (current == null || t0 > 0)
            {
                current = new List<(double E, double N)> { start };
                pieces.Add((current, from + (along - from) * t0));
            }
            current.Add(end);
            if (t1 < 1) current = null;

            bool Edge(double p, double q)
            {
                if (p == 0) return q >= 0;
                double r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }
        }
        pieces.RemoveAll(p => p.Points.Count < 2);
        return pieces;
    }

    // ---- buildings ---------------------------------------------------------------------------

    public BuildingTile? BuildBuildings(TileId id, Blend? blend = null)
    {
        CheckBlend(id, blend);
        blend?.PrepareLattice();
        var site = new Site(LatticeFor(id, fine: false), blend);
        var buildings = new List<Building>();
        foreach (var plan in PlansNear(site, id.MinE, id.MinN, id.MinE + ChunkFormat.TileSizeM, id.MaxN))
            if (Solid(plan, id, site) is { } b) buildings.Add(b);
        return buildings.Count == 0 ? null : new BuildingTile { Id = id, Buildings = buildings };
    }

    /// <summary>
    /// A closed solid in the tile's frame: walls from 0.8 m below the lowest footprint corner, a
    /// gable roof with a short overhang (or a flat one, or a spire on a tower). Each triangle is
    /// wound by checking its normal against the side it must be seen from, not by bookkeeping
    /// the vertex order — the lesson from FranceBuildings' roofs.
    /// </summary>
    private Building? Solid(Plan plan, TileId tile, Site site)
    {
        var f = plan.Rect;
        var u = (E: f.UE, N: f.UN);
        var v = (E: -f.UN, N: f.UE);
        (double E, double N) At(double a, double b) =>
            (f.E + u.E * a + v.E * b, f.N + u.N * a + v.N * b);

        // counter-clockwise in LV95; a shaped building's outline, courtyard clockwise (#598)
        var rings = Rings(plan);
        var ring = rings[0];
        double low = double.MaxValue, high = double.MinValue;
        foreach (var p in ring.Append((f.E, f.N)))
        {
            double g = Ground(site, p.Item1, p.Item2);
            low = Math.Min(low, g);
            high = Math.Max(high, g);
        }
        // a house does not stand on a cliff, in a river or a lake, or on a road
        if (high - low > 4.5) return null;
        var channel = FineNear(f.E, f.N);
        if (channel.Dr < channel.Bank + Math.Max(f.HalfLength, f.HalfWidth) + 4) return null;
        if (SampleCoarse(site.Lattice, f.E, f.N).Lake > 0.1) return null;
        foreach (var p in ring.Append((f.E, f.N)))
            if (NearestLine(p.Item1, p.Item2) is (var d, { } cls) && d < RoadFormat.DefaultWidth(cls) / 2 + 1.5)
                return null;

        double baseY = low - 0.8, eave = high + plan.WallHeight;
        var tris = new List<float>(64 * 9);

        void Tri((double E, double N) a, double ay, (double E, double N) b, double by,
            (double E, double N) c, double cy, double faceE, double faceY, double faceN)
        {
            // tile frame: X east, Y up, Z south
            double ax = a.E - tile.MinE, az = tile.MaxN - a.N;
            double bx = b.E - tile.MinE, bz = tile.MaxN - b.N;
            double cx = c.E - tile.MinE, cz = tile.MaxN - c.N;
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double wx = cx - ax, wy = cy - ay, wz = cz - az;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            // Godot's front face is clockwise as seen, so the right-hand normal of a visible
            // triangle points AWAY from whoever is looking at it
            bool flip = nx * faceE + ny * faceY + nz * -faceN > 0;
            void Add(double x, double y, double z) { tris.Add((float)x); tris.Add((float)y); tris.Add((float)z); }
            Add(ax, ay, az);
            if (flip) { Add(cx, cy, cz); Add(bx, by, bz); }
            else { Add(bx, by, bz); Add(cx, cy, cz); }
        }
        void Quad((double E, double N) a, double ay, (double E, double N) b, double by,
            (double E, double N) c, double cy, (double E, double N) d, double dy,
            double faceE, double faceY, double faceN)
        {
            Tri(a, ay, b, by, c, cy, faceE, faceY, faceN);
            Tri(a, ay, c, cy, d, dy, faceE, faceY, faceN);
        }

        // walls
        foreach (var r in rings)
            for (int i = 0; i < r.Length; i++)
            {
                var a = r[i];
                var b = r[(i + 1) % r.Length];
                // outward: the outer ring is counter-clockwise and a courtyard's clockwise, so the
                // outside is to the right of each edge
                double oe = b.N - a.N, on = -(b.E - a.E);
                Quad(a, baseY, a, eave, b, eave, b, baseY, oe, 0, on);
            }

        double top = eave;
        if (RoofParts(plan) is { } parts)
        {
            // a shaped building's flat roof, part by part (each convex: a fan)
            foreach (var q in parts)
                for (int i = 1; i + 1 < q.Length; i++)
                    Tri(q[0], eave, q[i], eave, q[i + 1], eave, 0, 1, 0);
        }
        else if (plan.Tower)
        {
            // a pyramid spire, four times the tower's width
            var apex = (f.E, f.N);
            top = eave + f.HalfWidth * 4.5;
            for (int i = 0; i < 4; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % 4];
                Tri(a, eave, b, eave, apex, top, b.N - a.N, 0.5, -(b.E - a.E));
            }
        }
        else if (plan.Pitch <= 0)
        {
            Quad(ring[0], eave, ring[1], eave, ring[2], eave, ring[3], eave, 0, 1, 0);
        }
        else
        {
            // ridge along u; overhang 0.5 m past every wall
            const double over = 0.5;
            double tan = Math.Tan(plan.Pitch * Math.PI / 180);
            double ridge = eave + f.HalfWidth * tan;
            double lip = eave - over * tan;
            double l = f.HalfLength + over, w = f.HalfWidth + over;
            var r0 = At(-l, 0);
            var r1 = At(l, 0);
            // gable ends, flush with the walls
            Tri(ring[1], eave, ring[2], eave, At(f.HalfLength, 0), ridge, u.E, 0, u.N);
            Tri(ring[3], eave, ring[0], eave, At(-f.HalfLength, 0), ridge, -u.E, 0, -u.N);
            // the two slopes
            Quad(At(-l, -w), lip, At(l, -w), lip, r1, ridge, r0, ridge, -v.E, 1, -v.N);
            Quad(At(l, w), lip, At(-l, w), lip, r0, ridge, r1, ridge, v.E, 1, v.N);
            top = ridge;
        }

        return new Building
        {
            Kind = plan.Kind,
            YearBuilt = plan.Year,
            Floors = plan.Floors,
            MinY = (float)baseY,
            MaxY = (float)top,
            Triangles = tris.ToArray(),
        };
    }

    // ---- where trees may not grow ------------------------------------------------------------

    /// <summary>Cells under a road (plus a verge) or a building (plus a garden strip).</summary>
    private bool[] BuildTreeMask(TileId id, Site site)
    {
        const int size = CoverFormat.Size;
        var blocked = new bool[size * size];
        double maxE = id.MinE + ChunkFormat.TileSizeM;
        const double margin = 20;

        void Stamp(double e, double n, double radius)
        {
            int c0 = (int)Math.Floor(e - id.MinE - radius), c1 = (int)Math.Ceiling(e - id.MinE + radius);
            int r0 = (int)Math.Floor(id.MaxN - n - radius), r1 = (int)Math.Ceiling(id.MaxN - n + radius);
            for (int r = Math.Max(r0, 0); r <= Math.Min(r1, size - 1); r++)
                for (int c = Math.Max(c0, 0); c <= Math.Min(c1, size - 1); c++)
                {
                    double de = id.MinE + c - e, dn = id.MaxN - r - n;
                    if (de * de + dn * dn <= radius * radius) blocked[r * size + c] = true;
                }
        }

        foreach (var drawn in LinesNear(id.MinE - margin, id.MinN - margin, maxE + margin, id.MaxN + margin))
        {
            var (points, cls) = (drawn.Points, drawn.Class);
            // a town's streets have sidewalks and bike paths, reaching ~6 m past the carriageway edge
            double radius = RoadFormat.DefaultWidth(cls) / 2 + (drawn.Town ? 7 : 3);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (Math.Max(a.E, b.E) < id.MinE - radius || Math.Min(a.E, b.E) > maxE + radius
                    || Math.Max(a.N, b.N) < id.MinN - radius || Math.Min(a.N, b.N) > id.MaxN + radius) continue;
                double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
                int steps = Math.Max(1, (int)Math.Ceiling(len));
                for (int s = 0; s <= steps; s++)
                    Stamp(a.E + (b.E - a.E) * s / steps, a.N + (b.N - a.N) * s / steps, radius);
            }
        }

        foreach (var p in PlansNear(site, id.MinE - margin, id.MinN - margin, maxE + margin, id.MaxN + margin))
        {
            var f = p.Rect;
            double reach = Math.Sqrt(f.HalfLength * f.HalfLength + f.HalfWidth * f.HalfWidth) + 3;
            int c0 = (int)Math.Floor(f.E - id.MinE - reach), c1 = (int)Math.Ceiling(f.E - id.MinE + reach);
            int r0 = (int)Math.Floor(id.MaxN - f.N - reach), r1 = (int)Math.Ceiling(id.MaxN - f.N + reach);
            for (int r = Math.Max(r0, 0); r <= Math.Min(r1, size - 1); r++)
                for (int c = Math.Max(c0, 0); c <= Math.Min(c1, size - 1); c++)
                {
                    double de = id.MinE + c - f.E, dn = id.MaxN - r - f.N;
                    double a = de * f.UE + dn * f.UN, b = -de * f.UN + dn * f.UE;
                    if (Math.Abs(a) <= f.HalfLength + 3 && Math.Abs(b) <= f.HalfWidth + 3)
                        blocked[r * size + c] = true;
                }
        }
        return blocked;
    }
}
