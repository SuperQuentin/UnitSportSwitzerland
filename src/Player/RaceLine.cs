using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// A racing line over a road and the speed a given car can carry along it.
///
/// <para>
/// The line is an elastic band: start on the centreline, repeatedly pull every point toward the
/// midpoint of its neighbours (which shortens and straightens the band — the minimum-curvature
/// line), and push it back inside the drivable width, keeping the car's own half width off each
/// edge. That is outside–apex–outside through every bend without ever being told what a bend is.
/// Driving the centreline instead is what made the scripted cars look drunk: every kink of the
/// surveyed polyline became a steering correction.
/// </para>
///
/// <para>
/// The drivable width is the tarmac plus, where <see cref="Widen"/> has surveyed it, a verge that
/// is safe to put two wheels on: flat ground level with the road, no trunk, wall, water or
/// building in the car's path, and no drop within a car width. Any doubt is no verge at all.
/// </para>
///
/// <para>
/// The speed profile is the classic quasi-steady lap simulation, per vehicle: the corner limit
/// <c>√(a/κ)</c> from the lateral acceleration it can hold (less on the verge), a crest limit
/// <c>√(g·R)</c> (faster than that and the road drops away beneath it), then a forward pass that
/// only lets it accelerate as its power, drag and traction allow, and a backward pass that makes it
/// brake in time for what is ahead with its own brakes. No top speed is imposed: on a straight the
/// profile is whatever the forward pass reaches, and the car itself tops out where its own engine,
/// gearing and drag put it.
/// </para>
/// </summary>
public sealed class RaceLine
{
    public readonly List<Vector3> Points = new();
    public readonly List<float> Arc = new();
    /// <summary>Signed curvature (1/m, + turns left) at each point.</summary>
    public readonly List<float> Curvature = new();
    /// <summary>Signed offset of the line from the centreline at each point, m, + to the left of travel.</summary>
    public readonly List<float> Offset = new();
    /// <summary>How far the car's centre may go from the centreline to each side, m (tarmac plus safe verge).</summary>
    public readonly List<float> RoomLeft = new(), RoomRight = new();
    /// <summary>Surveyed safe verge beyond each tarmac edge, m: 0 where anything is in doubt.</summary>
    public readonly List<float> MarginLeft = new(), MarginRight = new();
    /// <summary>Why a side got less verge than it could have (the last limit that applied).</summary>
    public readonly List<Block> WhyLeft = new(), WhyRight = new();
    /// <summary>Metres of the car's width past the tarmac edge where the line runs, 0 on the road.</summary>
    public readonly List<float> Beyond = new();
    /// <summary>True once the verge has been surveyed (<see cref="Widen"/>); before that the line keeps to the tarmac.</summary>
    public bool Surveyed { get; private set; }

    /// <summary>The frame <see cref="Points"/> are in (#185); null: taken to be the first one <see cref="Follow"/> is given.</summary>
    public OriginFrame? Frame { get; set; }

    /// <summary>Moves the points into <paramref name="now"/> (the origin moved, #185); a no-op once they are there.</summary>
    public void Follow(OriginFrame now)
    {
        if (Frame is { } was && !(was.E == now.E && was.N == now.N))
        {
            var shift = now.Since(was);
            for (int i = 0; i < Points.Count; i++) Points[i] = shift.Point(Points[i]);
        }
        Frame = now;
    }

    /// <summary>What stopped the verge on one side of one point.</summary>
    public enum Block : byte { Clear, NoData, Drop, Bank, Tree, Wall, Water, Building }

    public float Length => Arc.Count == 0 ? 0 : Arc[^1];

    /// <param name="centre">The road's centreline, points ~2 m apart.</param>
    /// <param name="width">Road width at each centreline point, m.</param>
    /// <param name="carHalfWidth">Kept clear of each edge, m.</param>
    /// <param name="marginLeft">Safe verge beyond the left edge per point, m (null: none; negative: keep that much further in).</param>
    /// <param name="marginRight">Same on the right.</param>
    public static RaceLine Build(IReadOnlyList<Vector3> centre, IReadOnlyList<float> width, float carHalfWidth,
        IReadOnlyList<float>? marginLeft = null, IReadOnlyList<float>? marginRight = null)
    {
        int n = centre.Count;
        var line = new RaceLine();
        if (n < 3)
        {
            for (int i = 0; i < n; i++) { line.Points.Add(centre[i]); line.Offset.Add(0f); line.Beyond.Add(0f); }
            line.Finish(); return line;
        }

        // left normal and the room on each side of the centreline: the tarmac, and at most half the
        // car (two wheels, its centre on the edge) onto a verge that was surveyed safe
        var normal = new Vector2[n];
        var roomL = new float[n];
        var roomR = new float[n];
        for (int i = 0; i < n; i++)
        {
            normal[i] = Normal(centre, i);
            float tarmac = Mathf.Max(0f, width[i] * 0.5f - carHalfWidth - 0.3f);
            float ml = marginLeft?[i] ?? 0f, mr = marginRight?[i] ?? 0f;
            roomL[i] = Mathf.Max(0f, tarmac + Mathf.Min(ml, carHalfWidth + 0.3f));
            roomR[i] = Mathf.Max(0f, tarmac + Mathf.Min(mr, carHalfWidth + 0.3f));
        }

        var offset = new float[n];
        var pos = new Vector2[n];
        for (int i = 0; i < n; i++) pos[i] = new Vector2(centre[i].X, centre[i].Z);
        const int Iterations = 400;
        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 1; i < n - 1; i++)
            {
                var mid = (pos[i - 1] + pos[i + 1]) * 0.5f;
                var want = pos[i] + (mid - pos[i]) * 0.6f;
                var c = new Vector2(centre[i].X, centre[i].Z);
                offset[i] = Mathf.Clamp((want - c).Dot(normal[i]), -roomR[i], roomL[i]);
                pos[i] = c + normal[i] * offset[i];
            }
        }

        for (int i = 0; i < n; i++)
        {
            line.Points.Add(new Vector3(pos[i].X, centre[i].Y, pos[i].Y));
            line.Offset.Add(offset[i]);
            line.RoomLeft.Add(roomL[i]);
            line.RoomRight.Add(roomR[i]);
            line.MarginLeft.Add(Mathf.Max(0f, marginLeft?[i] ?? 0f));
            line.MarginRight.Add(Mathf.Max(0f, marginRight?[i] ?? 0f));
            line.Beyond.Add(Mathf.Max(0f, Mathf.Abs(offset[i]) + carHalfWidth - width[i] * 0.5f));
        }
        line.Surveyed = marginLeft != null;
        line.Finish();
        return line;
    }

    /// <summary>Unit normal to the left of travel at centreline point i, in (X, Z).</summary>
    public static Vector2 Normal(IReadOnlyList<Vector3> centre, int i)
    {
        int n = centre.Count;
        var a = centre[Mathf.Max(0, i - 1)];
        var b = centre[Mathf.Min(n - 1, i + 1)];
        var t = new Vector2(b.X - a.X, b.Z - a.Z);
        t = t.LengthSquared() > 1e-6f ? t.Normalized() : Vector2.Right;
        return new Vector2(t.Y, -t.X);
    }

    private void Finish()
    {
        float s = 0;
        for (int i = 0; i < Points.Count; i++)
        {
            if (i > 0) s += Flat(Points[i] - Points[i - 1]).Length();
            Arc.Add(s);
        }
        for (int i = 0; i < Points.Count; i++)
        {
            // over ±8 m, like the drivers read it — and over ±4 m, keeping whichever is sharper:
            // RoadGen's junction gaps leave kinks of 15-20° in a few metres that the wider window
            // smooths into a gentle bend, and the speed profile then arrives at them far too fast
            float k8 = Bend(i, 8f), k4 = Bend(i, 4f);
            Curvature.Add(Mathf.Abs(k4) > Mathf.Abs(k8) ? k4 : k8);
        }
        foreach (var list in new[] { RoomLeft, RoomRight, MarginLeft, MarginRight })
            while (list.Count < Points.Count) list.Add(0f);
        while (WhyLeft.Count < Points.Count) WhyLeft.Add(Block.NoData);
        while (WhyRight.Count < Points.Count) WhyRight.Add(Block.NoData);
    }

    private float Bend(int i, float half)
    {
        var a = PointAt(Arc[i] - half); var b = Points[i]; var c = PointAt(Arc[i] + half);
        var u = Flat(b - a); var v = Flat(c - b);
        if (u.LengthSquared() < half * half * 0.25f || v.LengthSquared() < half * half * 0.25f) return 0f;
        return Mathf.Atan2(u.Z * v.X - u.X * v.Z, u.X * v.X + u.Z * v.Z) / (2f * half);
    }

    public Vector3 PointAt(float s)
    {
        if (Points.Count == 0) return Vector3.Zero;
        if (s <= 0) return Points[0];
        if (s >= Arc[^1]) return Points[^1];
        int i = Arc.BinarySearch(s);
        if (i < 0) i = ~i - 1;
        float span = Arc[i + 1] - Arc[i];
        return Points[i].Lerp(Points[i + 1], span > 1e-4f ? (s - Arc[i]) / span : 0f);
    }

    public int IndexAt(float s)
    {
        if (s <= 0) return 0;
        if (s >= Arc[^1]) return Points.Count - 1;
        int i = Arc.BinarySearch(s);
        return i < 0 ? ~i - 1 : i;
    }

    /// <summary>Vertical curvature of a crest at point i (1/m, 0 in a dip), over ±10 m.</summary>
    public float Crest(int i)
    {
        float s = Arc[i];
        float y0 = PointAt(s - 10f).Y, y1 = Points[i].Y, y2 = PointAt(s + 10f).Y;
        return Mathf.Max(0f, (2f * y1 - y0 - y2) / 100f);
    }

    /// <summary>
    /// The fastest speed (m/s) this car can be doing at each point of the line and still make every
    /// corner and crest after it, from its own grip, power, drag and published braking.
    /// </summary>
    /// <param name="courage">Share of the tyre limit the driver uses in corners (0.8 careful, 0.95 on it).</param>
    /// <param name="brakeShare">Share of the car's braking limit it plans with (0.85: a margin for the road).</param>
    public float[] SpeedProfile(CarSpec car, bool arcade, float courage, float brakeShare = 0.85f)
    {
        float mu = car.Grip * (arcade ? 1.12f : 1f);
        float g = Rideable.Gravity;
        float power = car.PeakKw * 1000f * (arcade ? 1.35f : 1f) * 0.85f;   // at the wheels
        // share of the weight on the driven wheels: what the tyres can put down under power
        float rearShare = car.FrontAxle / car.Wheelbase;
        float driven = car.Drive switch { Drivetrain.All => 1f, Drivetrain.Front => 1f - rearShare, _ => rearShare };
        // the car's own brakes (as Car.Step applies them), never more than the tyres, with a margin
        float brakes = car.BrakeDecel > 0 ? car.BrakeDecel * (arcade ? 1.1f : 1f) : 99f;
        // Car.Step splits the brakes 65/35 front/rear, and braking takes load off the rear: past
        // this decel the rear circle is all brake and the car swaps ends (it did, straight-line
        // braking from 225 km/h). All of it at 110 km/h and below, 80% from 215 km/h, where a line
        // correction is enough to start the rear going, as a driver without ABS or ESC would.
        float rearSat = mu * g * car.FrontAxle / car.Wheelbase / (0.35f + mu * car.CgHeight / car.Wheelbase);
        float Brake(float u) => brakeShare * Mathf.Min(Mathf.Min(brakes, 0.95f * mu * g),
            rearSat * Mathf.Lerp(1f, 0.8f, Mathf.Clamp((u - 30f) / 30f, 0f, 1f)));
        // air and rolling resistance: they cap the straights and help every brake from high speed
        float Resist(float u) => 0.5f * 1.2f * car.DragArea * u * u / car.Mass + 0.013f * g;
        return SpeedProfile(courage * mu * g, Brake,
            u => Mathf.Min(power / (car.Mass * Mathf.Max(u, 1f)), driven * mu * g) - Resist(u), Resist, mu * g);
    }

    /// <summary>
    /// The quasi-steady profile for any vehicle: <paramref name="lateral"/> is the sideways
    /// acceleration it holds in a corner on tarmac (m/s²), <paramref name="brake"/> its braking
    /// at a speed (m/s²); <paramref name="drive"/> its net forward acceleration on the flat at a speed (null:
    /// no forward pass, it simply gets there when it gets there); <paramref name="resist"/> what
    /// slows it off the throttle at a speed (added to the braking); <paramref name="grip"/> its whole
    /// tyre limit (m/s², 0: braking never shares it with the turn).
    /// </summary>
    public float[] SpeedProfile(float lateral, System.Func<float, float> brake, System.Func<float, float>? drive = null,
        System.Func<float, float>? resist = null, float grip = 0f)
    {
        int n = Points.Count;
        var v = new float[n];
        float g = Rideable.Gravity;
        for (int i = 0; i < n; i++)
        {
            float k = Mathf.Abs(Curvature[i]);
            // two wheels on grass and dirt: roughly half the grip under half the car
            float verge = i < Beyond.Count ? Mathf.Clamp(Beyond[i] / 0.9f, 0f, 1f) : 0f;
            float corner = Mathf.Sqrt(lateral * (1f - 0.25f * verge) / Mathf.Max(k, 1e-5f));
            float crest = Mathf.Sqrt(0.9f * g / Mathf.Max(Crest(i), 1e-5f));
            v[i] = Mathf.Min(corner, crest);
        }
        // forward: from a standing start, as fast as power and traction allow
        v[0] = 0f;
        if (drive != null)
            for (int i = 1; i < n; i++)
            {
                float ds = Arc[i] - Arc[i - 1];
                float u = Mathf.Max(v[i - 1], 1f);
                float grade = (Points[i].Y - Points[i - 1].Y) / Mathf.Max(ds, 0.1f);
                float accel = drive(u) - g * grade;
                v[i] = Mathf.Min(v[i], Mathf.Sqrt(Mathf.Max(0f, v[i - 1] * v[i - 1] + 2f * accel * ds)));
            }
        // backward: brake in time for everything ahead
        for (int i = n - 2; i >= 0; i--)
        {
            float ds = Arc[i + 1] - Arc[i];
            float grade = (Points[i + 1].Y - Points[i].Y) / Mathf.Max(ds, 0.1f);
            // braking in a bend shares the tyres with the turn (friction circle): what is left of the
            // grip after the corner takes its share — full brakes into a fast bend spun the car
            float turning = grip > 0f ? Mathf.Min(1f, v[i + 1] * v[i + 1] * Mathf.Abs(Curvature[i]) / grip) : 0f;
            float decel = Mathf.Max(1f, brake(v[i + 1]) * Mathf.Sqrt(1f - turning * turning) + (resist?.Invoke(v[i + 1]) ?? 0f) + g * grade);
            v[i] = Mathf.Min(v[i], Mathf.Sqrt(v[i + 1] * v[i + 1] + 2f * decel * ds));
        }
        return v;
    }

    // ------------------------------------------------------------------------------------
    // the verge survey
    // ------------------------------------------------------------------------------------

    /// <summary>Widest verge counted, m: more than half a car is never used anyway.</summary>
    private const float MaxMargin = 1.5f;
    /// <summary>How far past the edge the ground must not fall away, m: a car width.</summary>
    private const float DropReach = 1.8f;
    /// <summary>Extra clearance the line keeps from an edge with a drop beyond it, m.</summary>
    private const float DropClearance = 0.9f;

    private readonly record struct Obstacle(Vector2 At, float Radius, Block Why);

    /// <summary>
    /// The same route's line, rebuilt with a surveyed safe verge on each side of every point — from
    /// the data every client has: the height grid, the <c>.trees</c> trunks, walls and watercourses
    /// in the <c>.road</c> tiles, the cover raster (water) and building footprints. Runs off the
    /// main thread; the line keeps the same point count, so indices into the old one stay valid.
    /// It works on a copy of the route taken in one frame, the route's (the origin may move while
    /// it runs, #185), and the line it returns is in that frame: <see cref="Follow"/> it before use.
    /// </summary>
    public static async Task<RaceLine> Widen(RaceRoute route, IChunkSource source, WorldOrigin live,
        float carHalfWidth = 0.9f, CancellationToken ct = default)
    {
        var origin = route.Frame ?? live.Frame;
        var centre = route.Centre.ToArray();
        var widths = route.Width.ToArray();
        var tiles = new HashSet<TileId>();
        foreach (var p in centre)
            for (int dx = -12; dx <= 12; dx += 12)
                for (int dz = -12; dz <= 12; dz += 12)
                    tiles.Add(origin.TileAt(p + new Vector3(dx, 0, dz)));

        var grids = new Dictionary<TileId, ChunkGrid>();
        var covers = new Dictionary<TileId, byte[]>();
        var cells = new Dictionary<long, List<Obstacle>>();
        var boxes = new List<Rect2>();
        void Add(Vector2 at, float r, Block why)
        {
            long key = Cell(at);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new();
            list.Add(new Obstacle(at, r, why));
        }
        Vector2 Xz(TileId t, float x, float y, float z)
        {
            var w = origin.ToWorld(t.MinE + x, t.MaxN - z, y);
            return new Vector2(w.X, w.Z);
        }

        foreach (var t in tiles)
        {
            try { if (await source.LoadChunkAsync(t, ct) is { } g) grids[t] = g; } catch (System.Exception) { }
            try { if (await source.LoadCoverAsync(t, ct) is { } c) covers[t] = c; } catch (System.Exception) { }
            try
            {
                if (await source.LoadTreesAsync(t, ct) is { } trees)
                    foreach (var tr in trees)
                    {
                        if (tr.Kind == 1) continue;   // shrubs: walk-through, as in TreeColliders
                        float slender = tr.Kind switch { 2 => 0.34f, 3 => 0.40f, _ => 0.26f };
                        Add(Xz(t, tr.X, tr.Y, tr.Z), Mathf.Clamp(tr.Height * slender * 0.10f, 0.12f, 0.5f), Block.Tree);
                    }
            }
            catch (System.Exception) { }
            try
            {
                if (await source.LoadRoadsAsync(t, ct) is { } roads)
                {
                    foreach (var seg in roads.Segments)
                    {
                        var (why, r) = seg.Class switch
                        {
                            RoadClass.Wall or RoadClass.DryStoneWall or RoadClass.AvalancheBarrier
                                or RoadClass.TorrentWorks or RoadClass.Railway => (Block.Wall, 0.4f),
                            RoadClass.Watercourse or RoadClass.DryChannel or RoadClass.Bisse => (Block.Water, 0.5f + seg.Width * 0.5f),
                            _ => (Block.Clear, 0f),
                        };
                        if (why == Block.Clear) continue;
                        var pts = seg.Points;
                        for (int k = 0; k + 5 < pts.Length; k += 3)
                        {
                            var a = Xz(t, pts[k], pts[k + 1], pts[k + 2]);
                            var b = Xz(t, pts[k + 3], pts[k + 4], pts[k + 5]);
                            int steps = Mathf.Max(1, Mathf.CeilToInt(a.DistanceTo(b) / 0.5f));
                            for (int s = 0; s <= steps; s++) Add(a.Lerp(b, s / (float)steps), r, why);
                        }
                    }
                    // retaining walls and railings (#125, #126): solid along their line
                    foreach (var prop in roads.LinearProps)
                    {
                        var pts = prop.Points;
                        for (int k = 0; k + 7 < pts.Length; k += 4)
                        {
                            var a = Xz(t, pts[k], pts[k + 1], pts[k + 2]);
                            var b = Xz(t, pts[k + 4], pts[k + 5], pts[k + 6]);
                            int steps = Mathf.Max(1, Mathf.CeilToInt(a.DistanceTo(b) / 0.5f));
                            for (int s = 0; s <= steps; s++) Add(a.Lerp(b, s / (float)steps), 0.4f, Block.Wall);
                        }
                    }
                }
            }
            catch (System.Exception) { }
            try
            {
                if (await source.LoadBuildingsAsync(t, ct) is { } bt)
                    foreach (var b in bt.Buildings)
                    {
                        var tri = b.Triangles;
                        if (tri.Length < 3) continue;
                        var box = new Rect2(Xz(t, tri[0], tri[1], tri[2]), Vector2.Zero);
                        for (int k = 3; k + 2 < tri.Length; k += 3) box = box.Expand(Xz(t, tri[k], tri[k + 1], tri[k + 2]));
                        boxes.Add(box.Grow(0.5f));
                    }
            }
            catch (System.Exception) { }
        }

        return await Task.Run(() =>
        {
            int n = centre.Length;
            var ml = new float[n]; var mr = new float[n];
            var wl = new Block[n]; var wr = new Block[n];
            for (int i = 0; i < n; i++)
            {
                ml[i] = Margin(i, +1, out wl[i]);
                mr[i] = Margin(i, -1, out wr[i]);
            }
            // a car is ~4.3 m long and the points 2 m apart: a side is only as good as its worst
            // neighbour within a car length
            var sl = Worst(ml, wl, out var swl);
            var sr = Worst(mr, wr, out var swr);
            // beside a drop the line keeps a further 0.5 m in: tracking error (S-bends, a pack, a drift)
            // put a wheel up to 0.25 m over the edge with 0.3
            for (int i = 0; i < n; i++)
            {
                if (sl[i] == 0f && swl[i] == Block.Drop) sl[i] = -DropClearance;
                if (sr[i] == 0f && swr[i] == Block.Drop) sr[i] = -DropClearance;
            }
            var line = Build(centre, widths, carHalfWidth, sl, sr);
            line.Frame = origin;
            line.WhyLeft.Clear(); line.WhyLeft.AddRange(swl);
            line.WhyRight.Clear(); line.WhyRight.AddRange(swr);
            return line;
        }, ct);

        bool Height(Vector3 world, out float h)
        {
            h = 0f;
            var (e, nn) = origin.ToLv95(world);
            if (!grids.TryGetValue(TileId.FromLv95(e, nn), out var grid)) return false;
            h = (float)grid.SampleHeight(e, nn);
            return true;
        }

        bool Wet(Vector3 world)
        {
            var (e, nn) = origin.ToLv95(world);
            var id = TileId.FromLv95(e, nn);
            if (!covers.TryGetValue(id, out var raster)) return false;
            int col = (int)System.Math.Round(e - id.MinE), row = (int)System.Math.Round(id.MaxN - nn);
            int k = row * ChunkFormat.GridSize + col;
            if ((uint)col >= ChunkFormat.GridSize || (uint)row >= ChunkFormat.GridSize || k >= raster.Length) return false;
            return (CoverClass)raster[k] is CoverClass.Water or CoverClass.Wetland or CoverClass.Glacier;
        }

        // the safe verge beyond one edge of one centreline point, m; why says what limited it
        float Margin(int i, int side, out Block why)
        {
            var nrm2 = Normal(centre, i) * side;
            var nrm = new Vector3(nrm2.X, 0, nrm2.Y);
            var tan = new Vector2(-nrm2.Y, nrm2.X) * side;   // along the road
            var edge = centre[i] + nrm * widths[i] * 0.5f;
            why = Block.NoData;
            if (!Height(edge, out float h0)) return 0f;
            // the road is not on the ground here (bridge, embankment, cutting): nothing beside it to use
            why = Block.Drop;
            if (Mathf.Abs(centre[i].Y - h0) > 0.6f) return 0f;
            why = Block.Clear;
            float m = MaxMargin;
            for (float d = 0.25f; d <= DropReach + 0.01f; d += 0.25f)
            {
                var p = edge + nrm * d;
                if (!Height(p, out float h)) { why = Block.NoData; return 0f; }
                // falls away within a car width: a wheel over that edge takes the car with it
                if (h0 - h > 0.5f) { why = Block.Drop; return 0f; }
                if (d > m) continue;
                // a bank rising faster than ~30%: it tips the car, stop short of it
                if (h - h0 > 0.1f + 0.3f * d) { m = d - 0.25f; why = Block.Bank; }
                else if (Wet(p)) { m = d - 0.5f; why = Block.Water; }
                else foreach (var box in boxes)
                    if (box.HasPoint(new Vector2(p.X, p.Z))) { m = d - 0.5f; why = Block.Building; break; }
            }
            // trunks, walls, watercourses: anything within a car length along the road and not
            // well clear to the side takes its width plus half a metre off the verge
            var e2 = new Vector2(edge.X, edge.Z);
            int cx = Mathf.FloorToInt(e2.X / CellSize), cz = Mathf.FloorToInt(e2.Y / CellSize);
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (!cells.TryGetValue(Key(cx + dx, cz + dz), out var list)) continue;
                    foreach (var o in list)
                    {
                        var rel = o.At - e2;
                        if (Mathf.Abs(rel.Dot(tan)) > 3f) continue;
                        float lat = rel.Dot(nrm2);
                        if (lat < -1f) continue;   // on the road side of the edge: not the verge's business
                        float lim = lat - o.Radius - 0.5f;
                        if (lim < m) { m = lim; why = o.Why; }
                    }
                }
            return m < 0.3f ? 0f : m;
        }
    }

    private const float CellSize = 4f;
    private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    private static long Cell(Vector2 at) => Key(Mathf.FloorToInt(at.X / CellSize), Mathf.FloorToInt(at.Y / CellSize));

    private static float[] Worst(float[] m, Block[] why, out Block[] whyOut)
    {
        int n = m.Length;
        var o = new float[n];
        whyOut = new Block[n];
        for (int i = 0; i < n; i++)
        {
            o[i] = m[i]; whyOut[i] = why[i];
            for (int k = Mathf.Max(0, i - 2); k <= Mathf.Min(n - 1, i + 2); k++)
                if (m[k] < o[i] || (m[k] == o[i] && m[k] == 0f && whyOut[i] == Block.Clear)) { o[i] = m[k]; whyOut[i] = why[k]; }
        }
        return o;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
}
