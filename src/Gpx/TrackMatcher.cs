using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>What a matching run produced, including why it produced nothing.</summary>
/// <param name="MatchedShare">Fraction of thinned fixes that had a road within reach.</param>
/// <param name="MeanOffset">Mean metres each fix moved. The check that matching did anything.</param>
/// <param name="P95Offset">95th percentile of the same — where a wrong road would show up.</param>
/// <param name="Jumps">
/// Consecutive fixes the snap pushed more than 10 m further apart than they were recorded.
/// Each one is a sideways hop between roads — the exact artefact the HMM exists to prevent —
/// so this is the number that says whether it worked, not the average offset.
/// </param>
public readonly record struct MatchResult(GpxTrack? Track, string Status, double MatchedShare,
    double MeanOffset = 0, double P95Offset = 0, int Jumps = 0)
{
    public bool Ok => Track != null;
}

/// <summary>
/// Snaps a recorded GPX track onto the road network it was actually run or ridden on.
///
/// <para>
/// This is map matching, and the reason it is not just "move each point to the nearest road" is
/// that the nearest road is very often the wrong one. A consumer GPS is out by 5-10 m and much
/// worse in a valley or under trees, which is exactly where the two carriageways of a road are
/// 8 m apart, where a cycle path runs beside the carriageway, and where a footbridge crosses a
/// road it never joins. Snapping pointwise makes the runner flicker between them several times
/// a second.
/// </para>
///
/// <para>
/// So the choice is made over the whole track at once, as a hidden Markov model in the style of
/// Newson and Krumm (2009). Each recorded fix has candidate projections onto nearby roads; the
/// <b>emission</b> probability says a fix is unlikely to be far from the road it was recorded on,
/// and the <b>transition</b> probability says that between two consecutive fixes the distance
/// travelled <i>along the roads</i> should resemble the distance the GPS moved. That second term
/// is what does the real work: hopping to a parallel road and back costs a large detour on the
/// network while costing nothing in GPS distance, so Viterbi never chooses it.
/// </para>
/// </summary>
public static class TrackMatcher
{
    /// <summary>
    /// Fixes closer together than this are dropped before matching.
    ///
    /// <para>
    /// A 1 Hz recording puts fixes 1-2 m apart, which is well inside the noise: consecutive
    /// points carry almost no information about direction of travel, and feeding them all in
    /// makes the transition term compare two numbers that are both mostly error. Thinning to a
    /// spacing comfortably above the GPS error is what makes the model's assumptions true.
    /// </para>
    /// </summary>
    private const double SampleSpacingM = 12.0;

    /// <summary>How far off a road a fix may be and still be a candidate.</summary>
    private const double SearchRadiusM = 30.0;

    /// <summary>Assumed GPS standard deviation. Newson and Krumm measured 4.07 m; this is Alpine.</summary>
    private const double GpsSigmaM = 8.0;

    /// <summary>
    /// Scale of the route-vs-GPS distance disagreement, metres. Newson and Krumm derive 4.07 m
    /// from taxi data on a dense street grid; a looser value suits a network where the nearest
    /// legal connection between two paths can genuinely be a detour.
    /// </summary>
    private const double TransitionBetaM = 7.0;

    /// <summary>Cap on the route search between consecutive fixes.</summary>
    private const double RouteLimitM = 400.0;

    /// <summary>
    /// Below this share of fixes matched, the result is rejected outright.
    ///
    /// <para>
    /// A track that mostly runs where there is no mapped road — a mountainside, a beach, a
    /// region whose tiles were never built — should come back as "these are not roads", not as a
    /// track dragged onto whatever happened to be within thirty metres.
    /// </para>
    /// </summary>
    private const double MinimumMatchedShare = 0.55;

    public static async Task<MatchResult> MatchAsync(GpxTrack track, IChunkSource source,
        CancellationToken ct = default)
    {
        if (track.Points.Count < 4) return new MatchResult(null, "track too short to match", 0);

        double minE = double.MaxValue, minN = double.MaxValue;
        double maxE = double.MinValue, maxN = double.MinValue;
        foreach (var p in track.Points)
        {
            minE = Math.Min(minE, p.E); maxE = Math.Max(maxE, p.E);
            minN = Math.Min(minN, p.N); maxN = Math.Max(maxN, p.N);
        }

        var network = await RoadNetwork.LoadAsync(source, minE, minN, maxE, maxN, ct: ct)
            .ConfigureAwait(false);

        if (network.Edges.Count == 0)
            return new MatchResult(null, "no road data covers this track", 0);

        return Match(track, network, ct);
    }

    /// <summary>The matching itself, separated so it can be run against a network built any way.</summary>
    public static MatchResult Match(GpxTrack track, RoadNetwork network, CancellationToken ct = default)
    {
        var samples = Thin(track);
        if (samples.Count < 3) return new MatchResult(null, "track too short to match", 0);

        // ---- candidates ------------------------------------------------------------------
        var candidates = new List<RoadHit>[samples.Count];
        int withCandidates = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var p = track.Points[samples[i]];
            candidates[i] = network.Near(p.E, p.N, SearchRadiusM);
            if (candidates[i].Count > 0) withCandidates++;
        }

        double share = withCandidates / (double)samples.Count;
        if (share < MinimumMatchedShare)
            return new MatchResult(null, $"only {share:P0} of the track is near a road", share);

        // ---- Viterbi ---------------------------------------------------------------------
        // Log probabilities throughout: a track has thousands of steps and the product of
        // thousands of probabilities underflows a double long before the end of a run.
        var score = new double[samples.Count][];
        var back = new int[samples.Count][];

        int first = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var here = candidates[i];
            score[i] = new double[here.Count];
            back[i] = new int[here.Count];

            if (here.Count == 0) continue;

            if (first < 0)
            {
                for (int c = 0; c < here.Count; c++) score[i][c] = Emission(here[c].Distance);
                first = i;
                continue;
            }

            int previous = PreviousWithCandidates(candidates, i);
            if (previous < 0)
            {
                for (int c = 0; c < here.Count; c++) score[i][c] = Emission(here[c].Distance);
                continue;
            }

            var before = candidates[previous];
            var a = track.Points[samples[previous]];
            var b = track.Points[samples[i]];
            double straight = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            double limit = Math.Min(RouteLimitM, straight * 3 + 60);

            for (int c = 0; c < here.Count; c++)
            {
                double best = double.NegativeInfinity;
                int bestFrom = -1;

                for (int p = 0; p < before.Count; p++)
                {
                    if (double.IsNegativeInfinity(score[previous][p])) continue;

                    double route = network.RouteDistance(before[p], here[c], limit);
                    double value = score[previous][p] + Transition(route, straight);
                    if (value > best) { best = value; bestFrom = p; }
                }

                score[i][c] = best + Emission(here[c].Distance);
                back[i][c] = Math.Max(0, bestFrom);
            }
        }

        // ---- backtrack -------------------------------------------------------------------
        int last = -1;
        for (int i = samples.Count - 1; i >= 0; i--)
            if (candidates[i].Count > 0) { last = i; break; }
        if (last < 0 || first < 0) return new MatchResult(null, "no candidate roads found", share);

        var chosen = new int[samples.Count];
        for (int i = 0; i < chosen.Length; i++) chosen[i] = -1;

        int pick = 0;
        for (int c = 1; c < score[last].Length; c++)
            if (score[last][c] > score[last][pick]) pick = c;
        chosen[last] = candidates[last][pick].Edge;

        int cursor = last;
        while (true)
        {
            int prev = PreviousWithCandidates(candidates, cursor);
            if (prev < 0) break;
            pick = back[cursor][pick];
            if (pick >= candidates[prev].Count) pick = 0;
            chosen[prev] = candidates[prev][pick].Edge;
            cursor = prev;
        }

        // roughly 60 m of track: long enough to cross a gap under a bridge, short enough that a
        // genuine excursion off the network is still left as recorded
        BridgeGaps(chosen, 5);

        var snapped = Rebuild(track, samples, chosen, network);
        var (mean, p95, jumps) = Quality(track, snapped);
        return new MatchResult(snapped, "on", share, mean, p95, jumps);
    }

    /// <summary>
    /// How far the fixes actually moved.
    ///
    /// <para>
    /// The one statistic that says whether matching did anything. A track can report a high
    /// matched share and an unchanged length while having barely moved — which would mean the
    /// snap is a no-op — and it can move a long way, which means it picked a road the recording
    /// was never on. The mean should sit near the GPS error and the tail well inside the search
    /// radius; anything else is worth looking at before trusting the result.
    /// </para>
    /// </summary>
    /// <summary>How far the snap displacement may change between consecutive fixes, metres.</summary>
    private const double MaxOffsetStepM = 1.2;

    /// <summary>
    /// Limits how fast the snap displacement may change, so a road-to-road transition ramps
    /// instead of stepping.
    ///
    /// <para>
    /// Where the model changes road, the point the fixes project onto changes discontinuously —
    /// the two roads meet at a junction, but the switch happens wherever the fixes stop being
    /// nearer one than the other, which is not the same place. Measured on a 16 km ride: 29
    /// steps of more than 10 m in a single fix, about one every 550 m, each a visible sideways
    /// twitch under the runner.
    /// </para>
    ///
    /// <para>
    /// Limiting the <i>displacement</i> rather than the position is what makes this safe: away
    /// from a transition the displacement barely changes, so nothing moves. Only at a
    /// discontinuity does the limiter engage, spreading it over about ten fixes — and there,
    /// briefly, the track is between the two roads rather than exactly on one. That is the
    /// trade, and it is the right way round: a runner cutting a corner across a junction looks
    /// like a runner, and one teleporting sideways does not.
    /// </para>
    ///
    /// <para>
    /// Run forwards then backwards so the ramp is symmetric about the transition. A single
    /// forward pass would put the whole ramp after it, which is just a lag.
    /// </para>
    /// </summary>
    private static void SmoothOffsets(double[] e, double[] n)
    {
        for (int i = 1; i < e.Length; i++) Limit(e, n, i, i - 1);
        for (int i = e.Length - 2; i >= 0; i--) Limit(e, n, i, i + 1);

        static void Limit(double[] e, double[] n, int i, int from)
        {
            double dx = e[i] - e[from], dy = n[i] - n[from];
            double step = Math.Sqrt(dx * dx + dy * dy);
            if (step <= MaxOffsetStepM) return;

            double scale = MaxOffsetStepM / step;
            e[i] = e[from] + dx * scale;
            n[i] = n[from] + dy * scale;
        }
    }

    private static IEnumerable<int> Bracket(int[] chosen, int sample)
    {
        yield return chosen[sample];
        if (sample + 1 < chosen.Length) yield return chosen[sample + 1];
    }

    /// <summary>
    /// Carries a chosen road across short stretches where no candidate was found.
    ///
    /// <para>
    /// Unmatched samples fall back to the raw coordinates, and every boundary between a snapped
    /// stretch and a raw one is a step of however far the snap was moving the track — which is
    /// the largest single source of sideways hops in the output. Most of these gaps are a few
    /// fixes under a bridge or beside a building, where the road plainly continues; bridging
    /// them removes both boundaries at once.
    /// </para>
    ///
    /// <para>
    /// Only short gaps. A long one means the track really has left the network — across a field,
    /// up a mountainside — and dragging a road across it would be inventing the route.
    /// </para>
    /// </summary>
    private static void BridgeGaps(int[] chosen, int maxSamples)
    {
        int i = 0;
        while (i < chosen.Length)
        {
            if (chosen[i] >= 0) { i++; continue; }

            int end = i;
            while (end < chosen.Length && chosen[end] < 0) end++;

            int before = i > 0 ? chosen[i - 1] : -1;
            int after = end < chosen.Length ? chosen[end] : -1;

            // Only when both sides agree on the same road. Different roads either side means
            // the gap is where the track left one and joined another, and the honest answer is
            // that we do not know which one it was on in between.
            if (end - i <= maxSamples && before >= 0 && before == after)
                for (int k = i; k < end; k++) chosen[k] = before;

            i = end;
        }
    }

    private static (double Mean, double P95, int Jumps) Quality(GpxTrack raw, GpxTrack snapped)
    {
        int n = Math.Min(raw.Points.Count, snapped.Points.Count);
        if (n == 0) return (0, 0, 0);

        var offsets = new double[n];
        double total = 0;
        int jumps = 0;

        for (int i = 0; i < n; i++)
        {
            double dx = snapped.Points[i].E - raw.Points[i].E;
            double dy = snapped.Points[i].N - raw.Points[i].N;
            offsets[i] = Math.Sqrt(dx * dx + dy * dy);
            total += offsets[i];

            if (i == 0) continue;

            // A pair of fixes that the snap pushed much further apart than they were recorded
            // did not both land on the same road. That is the sideways hop between a
            // carriageway and the cycle path beside it, and one is one too many.
            double rawStep = Distance(raw, i);
            double snapStep = Distance(snapped, i);
            if (snapStep - rawStep > 10.0) jumps++;
        }

        Array.Sort(offsets);
        return (total / n, offsets[(int)(n * 0.95)], jumps);

        static double Distance(GpxTrack t, int i)
        {
            double dx = t.Points[i].E - t.Points[i - 1].E;
            double dy = t.Points[i].N - t.Points[i - 1].N;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static int PreviousWithCandidates(List<RoadHit>[] candidates, int index)
    {
        for (int i = index - 1; i >= 0; i--)
            if (candidates[i].Count > 0) return i;
        return -1;
    }

    /// <summary>Log emission: a fix is Gaussian-distributed about the road it was recorded on.</summary>
    private static double Emission(double distance) =>
        -0.5 * (distance / GpsSigmaM) * (distance / GpsSigmaM);

    /// <summary>
    /// Log transition: how surprising the road distance is, given how far the GPS moved.
    ///
    /// <para>
    /// An unreachable pair is not merely unlikely, it is impossible — but scoring it as negative
    /// infinity lets one gap in the network erase every path and the backtrack has nothing to
    /// follow. A large finite penalty keeps the model able to jump a missing link and carry on.
    /// </para>
    /// </summary>
    private static double Transition(double route, double straight)
    {
        if (double.IsInfinity(route)) return -12.0;
        return -Math.Abs(route - straight) / TransitionBetaM;
    }

    /// <summary>Indices of the fixes kept for matching, spaced by <see cref="SampleSpacingM"/>.</summary>
    private static List<int> Thin(GpxTrack track)
    {
        var kept = new List<int> { 0 };
        double last = track.Points[0].Distance;

        for (int i = 1; i < track.Points.Count; i++)
            if (track.Points[i].Distance - last >= SampleSpacingM)
            {
                kept.Add(i);
                last = track.Points[i].Distance;
            }

        if (kept[^1] != track.Points.Count - 1) kept.Add(track.Points.Count - 1);
        return kept;
    }

    /// <summary>
    /// Rebuilds a full-resolution track on the chosen roads.
    ///
    /// <para>
    /// Every original fix is kept, with its own timestamp and recorded elevation, and only its
    /// position moves — onto the road the model chose for that part of the track. Projecting each
    /// fix individually (rather than walking the road at a fixed rate) is what preserves the
    /// pacing: where the recording slowed down, the snapped track slows down in the same place.
    /// </para>
    ///
    /// <para>
    /// Distances are recomputed, so they shrink a little. That is not a loss — a large part of
    /// the difference is the GPS jitter that was inflating the recorded length in the first place.
    /// </para>
    /// </summary>
    private static GpxTrack Rebuild(GpxTrack track, List<int> samples, int[] chosen,
        RoadNetwork network)
    {
        int total = track.Points.Count;
        var offsetE = new double[total];
        var offsetN = new double[total];

        int sample = 0;
        for (int i = 0; i < total; i++)
        {
            // advance to the sample bracketing this fix
            while (sample + 1 < samples.Count && samples[sample + 1] <= i) sample++;

            var p = track.Points[i];
            double e = p.E, n = p.N;

            // Consider the edge chosen here and at the next sample, and take whichever the fix
            // is actually nearer. This looks like the pointwise snapping the HMM exists to
            // replace, and it is not: both candidates were already chosen by the model, so the
            // choice is only *where along the track* to change between two roads it committed
            // to. Switching at the sample boundary instead was tried and measured worse —
            // 51 hops against 29 — because the boundary falls wherever the 12 m thinning put
            // it rather than at the junction.
            double best = double.MaxValue;
            foreach (int edge in Bracket(chosen, sample))
            {
                if (edge < 0) continue;
                var hit = Closest(network.Edges[edge], p.E, p.N);
                if (hit.Distance < best) { best = hit.Distance; e = hit.E; n = hit.N; }
            }

            offsetE[i] = e - p.E;
            offsetN[i] = n - p.N;
        }

        SmoothOffsets(offsetE, offsetN);

        var points = new List<TrackPoint>(total);
        double cumulative = 0;
        double previousE = 0, previousN = 0;

        for (int i = 0; i < total; i++)
        {
            var p = track.Points[i];
            double e = p.E + offsetE[i], n = p.N + offsetN[i];

            if (i > 0)
            {
                double dx = e - previousE, dy = n - previousN;
                cumulative += Math.Sqrt(dx * dx + dy * dy);
            }
            previousE = e; previousN = n;

            points.Add(new TrackPoint(e, n, p.Elevation, p.Seconds, cumulative));
        }

        return new GpxTrack
        {
            Name = track.Name,
            Points = points,
            HasTiming = track.HasTiming,
            MinElevation = track.MinElevation,
            MaxElevation = track.MaxElevation,
            Ascent = track.Ascent,
        };
    }

    /// <summary>Nearest point on a whole edge, scanning its segments.</summary>
    private static (double Distance, double E, double N) Closest(RoadEdge edge, double e, double n)
    {
        double best = double.MaxValue, bestE = edge.E[0], bestN = edge.N[0];

        for (int i = 0; i < edge.E.Length - 1; i++)
        {
            double ax = edge.E[i], ay = edge.N[i];
            double dx = edge.E[i + 1] - ax, dy = edge.N[i + 1] - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 1e-12 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / len2, 0, 1) : 0;

            double px = ax + dx * t, py = ay + dy * t;
            double d = (e - px) * (e - px) + (n - py) * (n - py);
            if (d < best) { best = d; bestE = px; bestN = py; }
        }

        return (Math.Sqrt(best), bestE, bestN);
    }
}
