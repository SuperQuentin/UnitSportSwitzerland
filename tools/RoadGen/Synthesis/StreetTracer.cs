namespace UnitSport.Tools.RoadGen.Synthesis;

using UnitSport.Tools.RoadGen.Geometry;

public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Vec2 Centre => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
}

public sealed record TraceOptions(
    double StepLength = 4.0,
    /// <summary>How close two streets of the same generation may run before one is stopped.</summary>
    double Separation = 90.0,
    double MaxLength = 4000.0,
    /// <summary>A trace ending this close to an existing street snaps onto it, forming a junction.</summary>
    double SnapFactor = 0.55);

/// <summary>
/// Traces hyperstreamlines through a tensor field — the Chen et al. street-modelling method.
///
/// <para>
/// A streamline follows one eigenvector of the field continuously, so major-eigenvector traces
/// become the avenues and minor-eigenvector traces become the cross streets, automatically
/// perpendicular to them because the two eigenvectors of a symmetric tensor always are.
/// </para>
///
/// <para>
/// The detail that makes the output usable rather than decorative is the snap: a trace that
/// wanders within reach of an existing street is terminated <i>on</i> that street, at its exact
/// point. That single rule is what produces a connected graph instead of a pile of near-misses
/// — the shared point becomes a node, and the road it landed on gets split into a T junction by
/// the same code that handles surveyed data.
/// </para>
/// </summary>
public sealed class StreetTracer
{
    private readonly TraceOptions _options;
    private readonly Bounds _bounds;
    private readonly Dictionary<(long, long), List<Vec2>> _occupancy = new();
    private readonly double _cell;

    public List<List<Vec2>> Streets { get; } = new();

    public StreetTracer(Bounds bounds, TraceOptions? options = null)
    {
        _bounds = bounds;
        _options = options ?? new TraceOptions();
        _cell = Math.Max(_options.Separation, 1.0);
    }

    /// <summary>
    /// Grows one generation of streets. Call it twice — once for the major eigenvector, once
    /// for the minor — and then again with a smaller separation for the next level down.
    /// </summary>
    public int Grow(TensorField field, bool useMajor, double separation, int maxStreets, int seed)
    {
        var random = new Random(seed);
        int made = 0;

        // seeds on a jittered lattice: pure random seeding clumps, a pure lattice reads as one
        var candidates = new List<Vec2>();
        double spacing = separation * 0.8;
        for (double x = _bounds.MinX; x <= _bounds.MaxX; x += spacing)
        for (double y = _bounds.MinY; y <= _bounds.MaxY; y += spacing)
            candidates.Add(new Vec2(
                x + (random.NextDouble() - 0.5) * spacing * 0.6,
                y + (random.NextDouble() - 0.5) * spacing * 0.6));

        // nearest-the-middle first, so the important streets are laid before the fringe ones
        var centre = _bounds.Centre;
        candidates.Sort((a, b) => a.DistanceSquaredTo(centre).CompareTo(b.DistanceSquaredTo(centre)));

        foreach (var seedPoint in candidates)
        {
            if (made >= maxStreets) break;
            if (!_bounds.Contains(seedPoint)) continue;
            if (NearestExisting(seedPoint, separation * 0.9) is not null) continue;

            var street = TraceBoth(field, seedPoint, useMajor, separation);
            if (street.Count < 3) continue;
            if (Polyline.Length(street) < separation * 0.5) continue;

            Streets.Add(street);
            Occupy(street);
            made++;
        }

        return made;
    }

    private List<Vec2> TraceBoth(TensorField field, Vec2 seed, bool useMajor, double separation)
    {
        var forward = TraceOne(field, seed, useMajor, +1, separation);
        var backward = TraceOne(field, seed, useMajor, -1, separation);

        var street = new List<Vec2>();
        for (int i = backward.Count - 1; i >= 1; i--) street.Add(backward[i]);
        street.AddRange(forward);
        return Polyline.Dedupe(street, 0.05);
    }

    private List<Vec2> TraceOne(TensorField field, Vec2 seed, bool useMajor, int sense, double separation)
    {
        var points = new List<Vec2> { seed };
        var previous = Vec2.Zero;
        double travelled = 0;
        double snapDistance = separation * _options.SnapFactor;

        var p = seed;
        for (int step = 0; step < 4000; step++)
        {
            var direction = Direction(field, p, useMajor, previous, sense, step == 0);
            if (direction is null) break;

            // midpoint step: a plain Euler walk visibly spirals out of a radial field
            var mid = p + direction.Value * (_options.StepLength * 0.5);
            var corrected = Direction(field, mid, useMajor, direction.Value, +1, false) ?? direction.Value;
            var next = p + corrected * _options.StepLength;

            if (!_bounds.Contains(next)) break;

            travelled += _options.StepLength;
            if (travelled > _options.MaxLength) break;

            // snap onto whatever it ran into, so the network stays connected
            var hit = NearestExisting(next, snapDistance);
            if (hit is not null)
            {
                points.Add(hit.Value);
                break;
            }

            // and stop before doubling back over its own tail
            if (TouchesOwnTail(points, next, snapDistance)) break;

            points.Add(next);
            previous = corrected;
            p = next;
        }

        return points;
    }

    /// <summary>
    /// Picks the eigenvector, resolving its 180° ambiguity against the previous step. Without
    /// this the trace flips direction the moment the field's major angle crosses ±π/2 and the
    /// street folds back on itself.
    /// </summary>
    private static Vec2? Direction(TensorField field, Vec2 p, bool useMajor, Vec2 previous, int sense, bool first)
    {
        var tensor = field.At(p);
        if (tensor.Magnitude < 1e-6) return null;   // degenerate point: the field has no opinion here

        var direction = useMajor ? tensor.Major : tensor.Minor;
        if (first) return direction * sense;
        return direction.Dot(previous) < 0 ? -direction : direction;
    }

    private static bool TouchesOwnTail(List<Vec2> points, Vec2 candidate, double distance)
    {
        int ignore = Math.Max(4, (int)(distance / 2));
        for (int i = 0; i < points.Count - ignore; i++)
            if (points[i].DistanceTo(candidate) < distance * 0.5) return true;
        return false;
    }

    private Vec2? NearestExisting(Vec2 p, double radius)
    {
        Vec2? best = null;
        double bestDistance = radius;

        long cx = (long)Math.Floor(p.X / _cell), cy = (long)Math.Floor(p.Y / _cell);
        int reach = (int)Math.Ceiling(radius / _cell);

        for (long dx = -reach; dx <= reach; dx++)
        for (long dy = -reach; dy <= reach; dy++)
        {
            if (!_occupancy.TryGetValue((cx + dx, cy + dy), out var list)) continue;
            foreach (var q in list)
            {
                double d = p.DistanceTo(q);
                if (d < bestDistance) { bestDistance = d; best = q; }
            }
        }

        return best;
    }

    /// <summary>
    /// Registers a street so later traces can see and snap to it. Public because a later
    /// generation has to be told about the one above it: the occupancy grid, not the street
    /// list, is what the snap test reads, and a generation that cannot see its parent traces
    /// straight across main roads without ever joining them.
    /// </summary>
    public void Register(List<Vec2> street)
    {
        Streets.Add(street);
        Occupy(street);
    }

    private void Occupy(List<Vec2> street)
    {
        foreach (var p in Polyline.Densify(street, _cell * 0.4))
        {
            var key = ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell));
            if (!_occupancy.TryGetValue(key, out var list)) _occupancy[key] = list = new List<Vec2>();
            list.Add(p);
        }
    }
}
