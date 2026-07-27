using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Works out which terrain quads must be dropped so a tunnel mouth is open.
///
/// The rule is purely geometric: a quad is removed when it lies within the bore's
/// horizontal footprint AND the terrain surface there falls inside the bore's vertical
/// span. Deep inside the mountain the surface is far above the roof, so nothing is
/// removed; out in the open the surface is below the floor, so again nothing is removed.
/// The only place both tests pass is where the bore breaks through — the portal.
/// </summary>
public static class TunnelCarver
{
    /// <summary>
    /// Extra horizontal margin around the bore. Must clear the headwall rim, otherwise
    /// the portal facing is buried in the hillside and the mouth reads as a bare notch.
    /// </summary>
    private const double SideMargin = 1.6;

    /// <summary>
    /// The bore is extruded past each end of the centreline so it breaks the surface;
    /// the carve has to follow it out, or the extension stays entombed.
    /// </summary>
    private const double EndExtension = 5.0;

    /// <summary>
    /// How far above the roof still counts as portal mouth. Kept tight: every extra metre
    /// widens the opening along the hillside.
    /// </summary>
    private const double RoofMargin = 1.2;

    /// <summary>
    /// Ground must stand at least this far above the carriageway before it is carved.
    ///
    /// Without it an underpass wrecks its surroundings: beside a road dipping under a rail
    /// embankment the ground sits at carriageway level, falls inside the bore's vertical
    /// span, and gets removed — leaving holes in flat ground around the portal. Only
    /// ground that actually stands over the road should ever be cut.
    /// </summary>
    private const double MinCoverAboveRoad = 1.2;

    /// <summary>Step along the centreline when stamping the footprint.</summary>
    private const double StepM = 1.0;

    /// <summary>
    /// Adds hole quads for one tunnel centreline (map coordinates, surveyed Z at the
    /// road surface) into <paramref name="holes"/>, keyed by tile.
    /// </summary>
    public static void Carve(List<(double E, double N, double Z)> centre,
        double width, double bodyHeight,
        Func<double, double, double?> heightOf,
        Dictionary<TileId, HashSet<int>> holes)
    {
        double halfWidth = width * 0.5 + SideMargin;

        // walk the centreline extended past both ends, matching the rendered bore
        var path = new List<(double E, double N, double Z)>(centre.Count + 2);
        if (centre.Count >= 2)
        {
            var a0 = centre[0];
            var a1 = centre[1];
            var d0 = Normalize(a0.E - a1.E, a0.N - a1.N);
            path.Add((a0.E + d0.X * EndExtension, a0.N + d0.Y * EndExtension, a0.Z));
        }
        path.AddRange(centre);
        if (centre.Count >= 2)
        {
            var b0 = centre[^1];
            var b1 = centre[^2];
            var d1 = Normalize(b0.E - b1.E, b0.N - b1.N);
            path.Add((b0.E + d1.X * EndExtension, b0.N + d1.Y * EndExtension, b0.Z));
        }
        centre = path;

        for (int i = 0; i < centre.Count - 1; i++)
        {
            var a = centre[i];
            var b = centre[i + 1];
            double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int steps = Math.Max(1, (int)Math.Ceiling(len / StepM));

            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                double e = a.E + (b.E - a.E) * t;
                double n = a.N + (b.N - a.N) * t;
                double z = a.Z + (b.Z - a.Z) * t;
                StampDisc(e, n, z, halfWidth, bodyHeight, heightOf, holes);
            }
        }
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        double len = Math.Sqrt(x * x + y * y);
        return len < 1e-9 ? (0, 0) : (x / len, y / len);
    }

    private static void StampDisc(double e, double n, double roadZ, double radius,
        double bodyHeight, Func<double, double, double?> heightOf,
        Dictionary<TileId, HashSet<int>> holes)
    {
        double floor = roadZ + MinCoverAboveRoad;
        double roof = roadZ + bodyHeight + RoofMargin;
        double spacing = ChunkFormat.SpacingM;

        int cells = (int)Math.Ceiling(radius / spacing);
        for (int dy = -cells; dy <= cells; dy++)
            for (int dx = -cells; dx <= cells; dx++)
            {
                double qe = e + dx * spacing;
                double qn = n + dy * spacing;
                if ((qe - e) * (qe - e) + (qn - n) * (qn - n) > radius * radius) continue;

                double? h = heightOf(qe, qn);
                // only carve where the ground actually intersects the bore
                if (h == null || h < floor || h > roof) continue;

                var tile = TileId.FromLv95(qe, qn);
                int col = (int)Math.Floor((qe - tile.MinE) / spacing);
                int row = (int)Math.Floor((tile.MaxN - qn) / spacing);
                if ((uint)col >= HoleFormat.QuadsPerSide || (uint)row >= HoleFormat.QuadsPerSide)
                    continue;

                if (!holes.TryGetValue(tile, out var set))
                    holes[tile] = set = new HashSet<int>();
                set.Add(HoleFormat.CellIndex(col, row));
            }
    }
}
