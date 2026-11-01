namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The joint ground solve in towns (#1419): the ground between the roads and the buildings as one smooth
/// membrane. Plain C# so a unit test and the stage (<see cref="TownGroundStage"/>) share it.
///
/// <para>
/// <b>Constraints.</b> A road cell (under a ribbon, its sidewalks, a junction cap: where the runtime blend
/// holds the ground at one height) is fixed at that height, the collision floor the blend leaves there; a
/// footprint and <see cref="FootprintShoulder"/> past it keep their (pad-graded) height; cells that must not
/// move (beside a retaining wall, under a bridge, along a stream) keep theirs too.
/// </para>
///
/// <para>
/// <b>Free cells</b> lie within <see cref="Reach"/> of a road or a footprint and in town (the
/// <c>UrbanField</c> weight above zero). They get the harmonic fill of the constraints and of the untouched
/// ground round them (Laplace, red-black SOR from the original heights), then the change fades to nothing
/// with the urban weight and over the outer <see cref="FadeM"/> of the reach, and is capped at <see cref="Cap"/>
/// (the footings and plinths cover the rest). A road cell keeps its stored height: the runtime blend sets the
/// ground there anyway (the collision at the road, the mesh the clearance under it), and a door beside a
/// sidewalk reads the stored heights (writing the visual height there buried and floated doors).
/// </para>
///
/// <para>
/// <b>Locality.</b> A red-black sweep carries a value two cells, so after <see cref="Iterations"/> sweeps a
/// cell depends on nothing farther than <c>2 * Iterations</c> cells plus <see cref="Reach"/> (which cells are
/// free). A window <see cref="Margin"/> wider than that on every side, built from the same inputs, gives a
/// tile's border cells bit-identical heights in both tiles (the window origins are 1000 cells apart, so the
/// red-black parity agrees).
/// </para>
/// </summary>
public static class TownGround
{
    /// <summary>Metres: free cells lie within this of a road cell or a footprint.</summary>
    public const double Reach = 15;

    /// <summary>Metres: over the outer part of the reach the change fades to the original ground.</summary>
    public const double FadeM = 5;

    /// <summary>Metres: the most a free cell moves.</summary>
    public const double Cap = 2;

    /// <summary>Metres past a footprint's outline the (pad) ground is kept: the door reads it 0.8 m out.</summary>
    public const double FootprintShoulder = 2.0;

    /// <summary>Red-black SOR sweeps; the free regions are at most ~2 Reach across, which this resolves to millimetres.</summary>
    public const int Iterations = 60;

    /// <summary>Over-relaxation, near the optimum for a 30-cell-wide region.</summary>
    public const float Omega = 1.8f;

    /// <summary>Cells of window round a tile: past what the sweeps and the free-cell test can see.</summary>
    public const int Margin = 2 * Iterations + (int)Reach + 17;

    public enum Kind : byte
    {
        /// <summary>No data (no tile there): neither moved nor read.</summary>
        Outside,
        /// <summary>A candidate: free if it is near a road or a footprint and in town.</summary>
        Free,
        /// <summary>A road cell: fixed at <see cref="Window.Fixed"/> in the solve, its stored height not changed.</summary>
        Road,
        /// <summary>Kept at its height (a wall, a bridge, a stream).</summary>
        Keep,
        /// <summary>A footprint and its shoulder: kept at its height, and a site the reach is measured from.</summary>
        Footprint,
    }

    /// <summary>A square window of the lattice, row-major, <see cref="Size"/> on a side.</summary>
    public sealed class Window(int size)
    {
        public int Size { get; } = size;
        public float[] H0 { get; } = new float[size * size];
        public Kind[] Kinds { get; } = new Kind[size * size];
        /// <summary>A road cell's height in the solve (the collision floor the runtime blend leaves there).</summary>
        public float[] Fixed { get; } = new float[size * size];
    }

    public sealed record Result(float[] Heights, int Free, float Residual);

    private static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The solved heights of the window (the original where nothing changes). <paramref name="urban"/> gives the
    /// urban weight (0 rural .. 1 town) at a window index; it is called for the candidates in reach only.
    /// </summary>
    public static Result Solve(Window win, Func<int, double> urban)
    {
        int w = win.Size, count = w * w;
        var kinds = win.Kinds;
        var h0 = win.H0;
        var result = (float[])h0.Clone();

        var sites = new bool[count];
        bool any = false;
        for (int i = 0; i < count; i++)
            if (kinds[i] is Kind.Road or Kind.Footprint) { sites[i] = true; any = true; }
        if (!any) return new Result(result, 0, 0);
        var dist = new float[count];
        DistanceTransform.Run(sites, w, w, dist);

        // the free cells and their weights, the values every sweep reads
        var v = new float[count];
        var weight = new float[count];
        var red = new List<int>();
        var black = new List<int>();
        for (int i = 0; i < count; i++)
        {
            switch (kinds[i])
            {
                case Kind.Outside: v[i] = float.NaN; continue;
                case Kind.Road: v[i] = win.Fixed[i]; break;
                default: v[i] = h0[i]; break;
            }
            int r = i / w, c = i - r * w;
            if (kinds[i] != Kind.Free || dist[i] >= Reach) continue;
            // a cell on the window's rim or next to no data stays put
            if (r == 0 || c == 0 || r == w - 1 || c == w - 1
                || kinds[i - 1] == Kind.Outside || kinds[i + 1] == Kind.Outside
                || kinds[i - w] == Kind.Outside || kinds[i + w] == Kind.Outside) continue;
            double uw = urban(i);
            if (uw <= 1e-3) continue;
            weight[i] = (float)(uw * Smoothstep((Reach - dist[i]) / FadeM));
            ((r + c & 1) == 0 ? red : black).Add(i);
        }

        // red-black SOR on the free cells; everything else is a fixed value
        float residual = 0;
        var redA = red.ToArray();
        var blackA = black.ToArray();
        for (int it = 0; it < Iterations; it++)
        {
            float maxStep = 0;
            Sweep(redA, ref maxStep);
            Sweep(blackA, ref maxStep);
            residual = maxStep;
        }

        void Sweep(int[] cells, ref float maxStep)
        {
            foreach (int i in cells)
            {
                float a = v[i - 1], b = v[i + 1], cc = v[i - w], d = v[i + w];
                float avg = (a + b + cc + d) * 0.25f;
                float step = Omega * (avg - v[i]);
                v[i] += step;
                float m = step < 0 ? -step : step;
                if (m > maxStep) maxStep = m;
            }
        }

        foreach (var cells in new[] { redA, blackA })
            foreach (int i in cells)
            {
                double d = Math.Clamp(weight[i] * (v[i] - h0[i]), -Cap, Cap);
                result[i] = (float)(h0[i] + d);
            }
        return new Result(result, redA.Length + blackA.Length, residual);
    }
}
