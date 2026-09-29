using Godot;

namespace UnitSport.Player;

/// <summary>
/// A racing line over a road and the speed a given car can carry along it.
///
/// <para>
/// The line is an elastic band: start on the centreline, repeatedly pull every point toward the
/// midpoint of its neighbours (which shortens and straightens the band — the minimum-curvature
/// line), and push it back inside the tarmac, keeping the car's own half width off each edge.
/// That is outside–apex–outside through every bend without ever being told what a bend is.
/// Driving the centreline instead is what made the scripted cars look drunk: every kink of the
/// surveyed polyline became a steering correction.
/// </para>
///
/// <para>
/// The speed profile is the classic quasi-steady lap simulation, per car: the corner limit
/// <c>√(μg/κ)</c> from the car's own grip, a crest limit <c>√(g·R)</c> (faster than that and the
/// road drops away beneath it), then a forward pass that only lets the car accelerate as its power,
/// mass and driven-axle grip allow, and a backward pass that makes it brake in time for what is
/// ahead. An AE86 and a GT-R therefore reach the same corner at different speeds, for the reasons
/// they would in reality.
/// </para>
/// </summary>
public sealed class RaceLine
{
    public readonly List<Vector3> Points = new();
    public readonly List<float> Arc = new();
    /// <summary>Signed curvature (1/m, + turns left) at each point.</summary>
    public readonly List<float> Curvature = new();
    /// <summary>Half width of tarmac available to the line at each point, m.</summary>
    public readonly List<float> Room = new();

    public float Length => Arc.Count == 0 ? 0 : Arc[^1];

    /// <param name="centre">The road's centreline, points ~2 m apart.</param>
    /// <param name="width">Road width at each centreline point, m.</param>
    /// <param name="carHalfWidth">Kept clear of each edge, m.</param>
    public static RaceLine Build(IReadOnlyList<Vector3> centre, IReadOnlyList<float> width, float carHalfWidth)
    {
        int n = centre.Count;
        var line = new RaceLine();
        if (n < 3) { foreach (var c in centre) line.Points.Add(c); line.Finish(); return line; }

        // left normal and the room on each side of the centreline
        var normal = new Vector2[n];
        var room = new float[n];
        for (int i = 0; i < n; i++)
        {
            var a = centre[Mathf.Max(0, i - 1)];
            var b = centre[Mathf.Min(n - 1, i + 1)];
            var t = new Vector2(b.X - a.X, b.Z - a.Z);
            t = t.LengthSquared() > 1e-6f ? t.Normalized() : Vector2.Right;
            normal[i] = new Vector2(t.Y, -t.X);   // to the left of travel, in (X, Z)
            room[i] = Mathf.Max(0f, width[i] * 0.5f - carHalfWidth - 0.3f);
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
                offset[i] = Mathf.Clamp((want - c).Dot(normal[i]), -room[i], room[i]);
                pos[i] = c + normal[i] * offset[i];
            }
        }

        for (int i = 0; i < n; i++)
        {
            line.Points.Add(new Vector3(pos[i].X, centre[i].Y, pos[i].Y));
            line.Room.Add(room[i]);
        }
        line.Finish();
        return line;
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
            // over ±8 m, like the drivers read it
            var a = PointAt(Arc[i] - 8f); var b = Points[i]; var c = PointAt(Arc[i] + 8f);
            var u = Flat(b - a); var v = Flat(c - b);
            float k = u.LengthSquared() < 1f || v.LengthSquared() < 1f ? 0f
                : Mathf.Atan2(u.Z * v.X - u.X * v.Z, u.X * v.X + u.Z * v.Z) / 16f;
            Curvature.Add(k);
        }
        while (Room.Count < Points.Count) Room.Add(0f);
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
    /// corner and crest after it, given how hard it can accelerate and brake.
    /// </summary>
    /// <param name="courage">Share of the tyre limit the driver uses in corners (0.8 careful, 0.95 on it).</param>
    public float[] SpeedProfile(CarSpec car, bool arcade, float courage, float topSpeed)
    {
        int n = Points.Count;
        var v = new float[n];
        float mu = car.Grip * (arcade ? 1.12f : 1f);
        float g = Rideable.Gravity;
        float power = car.PeakKw * 1000f * (arcade ? 1.35f : 1f) * 0.85f;   // at the wheels
        // share of the weight on the driven wheels: what the tyres can put down under power
        float rearShare = car.FrontAxle / car.Wheelbase;
        float driven = car.Drive switch { Drivetrain.All => 1f, Drivetrain.Front => 1f - rearShare, _ => rearShare };
        float brake = 0.85f * mu * g;

        for (int i = 0; i < n; i++)
        {
            float k = Mathf.Abs(Curvature[i]);
            float corner = Mathf.Sqrt(courage * mu * g / Mathf.Max(k, 1e-4f));
            float crest = Mathf.Sqrt(0.9f * g / Mathf.Max(Crest(i), 1e-4f));
            v[i] = Mathf.Min(topSpeed, Mathf.Min(corner, crest));
        }
        // forward: from a standing start, as fast as power and traction allow
        v[0] = 0f;
        for (int i = 1; i < n; i++)
        {
            float ds = Arc[i] - Arc[i - 1];
            float u = Mathf.Max(v[i - 1], 1f);
            float drag = 0.5f * 1.2f * car.DragArea * u * u / car.Mass;
            float grade = (Points[i].Y - Points[i - 1].Y) / Mathf.Max(ds, 0.1f);
            float accel = Mathf.Min(power / (car.Mass * u), driven * mu * g) - drag - g * grade;
            v[i] = Mathf.Min(v[i], Mathf.Sqrt(Mathf.Max(0f, v[i - 1] * v[i - 1] + 2f * accel * ds)));
        }
        // backward: brake in time for everything ahead
        for (int i = n - 2; i >= 0; i--)
        {
            float ds = Arc[i + 1] - Arc[i];
            float grade = (Points[i + 1].Y - Points[i].Y) / Mathf.Max(ds, 0.1f);
            float decel = Mathf.Max(1f, brake + g * grade);
            v[i] = Mathf.Min(v[i], Mathf.Sqrt(v[i + 1] * v[i + 1] + 2f * decel * ds));
        }
        return v;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
}
