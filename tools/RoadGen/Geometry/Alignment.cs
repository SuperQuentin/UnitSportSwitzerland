namespace UnitSport.Tools.RoadGen.Geometry;

/// <summary>One sampled station along an alignment.</summary>
public readonly record struct AlignmentSample(Vec2 Position, double Heading, double Curvature, double Distance);

/// <summary>
/// A chain of <see cref="GeometryPiece"/>s that is C1 by construction (each piece starts
/// where the last ended, with its heading) and G2 wherever spirals were used (curvature
/// matches across the join too).
/// </summary>
public sealed class Alignment
{
    private readonly List<GeometryPiece> _pieces;
    private readonly double[] _starts;   // cumulative arc length at each piece's start

    public Alignment(List<GeometryPiece> pieces)
    {
        _pieces = pieces;
        _starts = new double[pieces.Count + 1];
        for (int i = 0; i < pieces.Count; i++) _starts[i + 1] = _starts[i] + pieces[i].Length;
    }

    public IReadOnlyList<GeometryPiece> Pieces => _pieces;
    public double Length => _starts[^1];
    public bool IsEmpty => _pieces.Count == 0 || Length < 1e-9;

    private int IndexContaining(double s)
    {
        int i = Array.BinarySearch(_starts, s);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _pieces.Count - 1);
    }

    private int PieceAt(double s, out double local)
    {
        if (_pieces.Count == 0) { local = 0; return -1; }

        int i = IndexContaining(s);
        local = Math.Clamp(s - _starts[i], 0, _pieces[i].Length);
        return i;
    }

    public Vec2 PointAt(double s)
    {
        int i = PieceAt(s, out double local);
        return i < 0 ? Vec2.Zero : _pieces[i].PointAt(local);
    }

    public double HeadingAt(double s)
    {
        int i = PieceAt(s, out double local);
        return i < 0 ? 0 : _pieces[i].HeadingAt(local);
    }

    public double CurvatureAt(double s)
    {
        int i = PieceAt(s, out double local);
        return i < 0 ? 0 : _pieces[i].CurvatureAt(local);
    }

    /// <summary>
    /// Walks the alignment emitting samples whose chord never departs from the true curve by
    /// more than <paramref name="maxDeviation"/>.
    ///
    /// <para>
    /// The step comes from the sagitta of a circular chord, <c>d ≈ κL²/8</c>, solved for L.
    /// This is what makes the output cheap *and* smooth at once: a motorway straight emits a
    /// vertex every <paramref name="maxStep"/> metres, a 20 m hairpin emits one every 1.5 m,
    /// and neither is chosen by hand. The existing pipeline's flat 4 m densification does the
    /// opposite — it over-samples straights and still cuts corners on tight bends.
    /// </para>
    /// </summary>
    public List<AlignmentSample> Sample(double maxDeviation = 0.05, double minStep = 0.5, double maxStep = 25.0)
        => Sample(0, Length, maxDeviation, minStep, maxStep);

    /// <summary>
    /// Samples a sub-range. Callers that want part of an alignment — a ribbon between its
    /// junction trims, a dash between its ends — must use this rather than sampling the whole
    /// thing and filtering.
    ///
    /// <para>
    /// Filtering looks equivalent and is not: a short link trimmed at both ends can have every
    /// adaptive sample fall outside what survives, leaving the two trim points as the only
    /// stations and one straight chord thrown across a curve. Measured just under 6 m of error
    /// that way on real swissTLM3D links between close junctions.
    /// </para>
    /// </summary>
    public List<AlignmentSample> Sample(double from, double to,
        double maxDeviation = 0.05, double minStep = 0.5, double maxStep = 25.0)
    {
        var result = new List<AlignmentSample>();
        if (IsEmpty) return result;

        from = Math.Clamp(from, 0, Length);
        to = Math.Clamp(to, from, Length);

        // A cursor, rather than a binary search per step. Not merely faster: landing exactly on
        // a piece boundary made the search resolve to the piece *behind* the cursor, so the
        // distance to the next boundary read as zero, the ceiling fell back to maxStep, and the
        // curvature was read off the piece already left behind. On an alpine track — 29 pieces
        // in 79 m — that threw a single 25 m chord across a 2.3 m radius bend, 6 m wide of the
        // real curve.
        int i = IndexContaining(from);
        double s = from;

        while (true)
        {
            double local = Math.Clamp(s - _starts[i], 0, _pieces[i].Length);
            result.Add(new AlignmentSample(
                _pieces[i].PointAt(local), _pieces[i].HeadingAt(local), _pieces[i].CurvatureAt(local), s));

            if (s >= to - 1e-9) break;

            // a step never leaves its piece, so curvature stays linear across it
            double ceiling = Math.Min(maxStep, Math.Max(_pieces[i].Length - local, 1e-9));
            // a short piece can be smaller than minStep, so the floor yields to the ceiling
            double floor = Math.Min(minStep, ceiling);

            // The step must be judged on the curvature it will REACH, not the one it starts
            // from. A clothoid begins at zero curvature where it leaves the straight, so reading
            // κ only at the start says "this is flat, take the longest step available" and then
            // crosses the whole transition in one chord — measured 320 mm against a 50 mm
            // budget. Because curvature is linear within a piece, the extreme is at one end or
            // the other, and iterating on the pair settles in two or three rounds.
            double step = ceiling;
            for (int iter = 0; iter < 5; iter++)
            {
                double k = Math.Max(
                    Math.Abs(_pieces[i].CurvatureAt(local)),
                    Math.Abs(_pieces[i].CurvatureAt(Math.Min(local + step, _pieces[i].Length))));

                double next = k < 1e-9 ? maxStep : Math.Sqrt(8.0 * maxDeviation / k);
                next = Math.Clamp(next, floor, ceiling);
                if (Math.Abs(next - step) < 1e-4) { step = next; break; }
                step = next;
            }

            s = Math.Min(s + step, to);
            while (i + 1 < _pieces.Count && s >= _starts[i + 1] - 1e-9) i++;
        }

        return result;
    }

    /// <summary>Convenience: just the positions.</summary>
    public List<Vec2> SamplePoints(double maxDeviation = 0.05, double minStep = 0.5, double maxStep = 25.0)
        => Sample(maxDeviation, minStep, maxStep).Select(x => x.Position).ToList();

    /// <summary>
    /// Largest curvature step between consecutive pieces. Zero means the whole alignment is
    /// G2 — the number the smoothing is judged by.
    /// </summary>
    public double WorstCurvatureJump()
    {
        double worst = 0;
        for (int i = 1; i < _pieces.Count; i++)
            worst = Math.Max(worst, Math.Abs(_pieces[i].Curvature - _pieces[i - 1].EndCurvature));
        return worst;
    }
}
