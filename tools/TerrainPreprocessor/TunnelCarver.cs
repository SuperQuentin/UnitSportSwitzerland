using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Punches the hole a tunnel needs in the terrain: at each portal, and nowhere else (#119).
///
/// <para>
/// A tunnel does not shape the ground. Over a bore the road blend keeps the ground above its crown
/// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>, <see cref="RoadTunnels"/>), and the trench an
/// underpass ramp runs down is the at-grade approach road's own cut, slopes and walls (#125). The
/// only thing in the way is the heightfield's quad that spans from the approach (road height) to
/// the ground over the bore (crown height) at the mouth: that strip, from the portal (quads whose
/// centre is within half a quad in front of it) to <see cref="Inside"/> behind it and as wide as
/// the bore, is the hole. The
/// portal block (<c>TerrainMeshBuilder.AppendPortalWall</c>) roofs it, and the tunnel's floor
/// collision (<c>RoadMeshBuilder.BuildBridgeCollisionFaces</c>) carries whoever drives through.
/// </para>
///
/// <para>
/// It used to carve wherever the ground lay inside the bore's vertical span, which in a mountain
/// is only the mouth, but under a town is the whole of a cut-and-cover tunnel: the street over it
/// lost its ground and the tube showed.
/// </para>
/// </summary>
public static class TunnelCarver
{
    /// <summary>
    /// Quads whose centre lies up to this far in front of the portal are punched (so no quad edge
    /// reaches past the headwall, which stands <c>RoadMeshBuilder.PortalExtension</c> = 1 m out):
    /// the hole stays behind the face and no sky shows beside the approach road.
    /// </summary>
    public const double Outside = 0.0;

    /// <summary>... and runs this far in: past the lattice quad that rises to the ground over the bore.</summary>
    public const double Inside = 1.5;

    /// <summary>Beyond the bore's half width on each side.</summary>
    private const double SideMargin = 0.3;

    /// <summary>The mouth's direction is taken over this much of the line, as the runtime does.</summary>
    private const double Aim = 3.0;

    /// <summary>
    /// Adds hole quads for one tunnel centreline (map coordinates) at each of its
    /// <paramref name="portals"/> (its ends that meet a road or a rail, or nothing).
    /// </summary>
    public static void Carve(List<(double E, double N, double Z)> centre,
        double width, double bodyHeight,
        Func<double, double, double?> heightOf,
        Dictionary<TileId, HashSet<int>> holes,
        IReadOnlyList<(double E, double N)> portals)
    {
        if (centre.Count < 2) return;
        double half = width * 0.5 + SideMargin;
        foreach (bool atStart in new[] { true, false })
        {
            var end = atStart ? centre[0] : centre[^1];
            if (!portals.Any(p => Math.Abs(p.E - end.E) < 0.05 && Math.Abs(p.N - end.N) < 0.05)) continue;
            // inward direction, over the first Aim metres
            var aim = end;
            for (int k = 1; k < centre.Count; k++)
            {
                aim = atStart ? centre[k] : centre[centre.Count - 1 - k];
                if ((aim.E - end.E) * (aim.E - end.E) + (aim.N - end.N) * (aim.N - end.N) >= Aim * Aim) break;
            }
            var (fx, fy) = Normalize(aim.E - end.E, aim.N - end.N);
            if (fx == 0 && fy == 0) continue;
            Punch(end.E, end.N, fx, fy, half, holes);
        }
    }

    /// <summary>Every lattice quad whose centre lies in the strip at the portal.</summary>
    private static void Punch(double e, double n, double fx, double fy, double half,
        Dictionary<TileId, HashSet<int>> holes)
    {
        double spacing = ChunkFormat.SpacingM;
        double reach = Math.Max(half, Inside) + spacing;
        for (double qe = Math.Floor((e - reach) / spacing) * spacing; qe <= e + reach; qe += spacing)
            for (double qn = Math.Floor((n - reach) / spacing) * spacing; qn <= n + reach; qn += spacing)
            {
                // the quad's centre
                double ce = qe + spacing * 0.5, cn = qn + spacing * 0.5;
                double along = (ce - e) * fx + (cn - n) * fy, across = -(ce - e) * fy + (cn - n) * fx;
                if (along < -Outside - spacing * 0.5 || along > Inside || Math.Abs(across) > half) continue;
                var tile = TileId.FromLv95(ce, cn);
                int col = (int)Math.Floor((ce - tile.MinE) / spacing);
                int row = (int)Math.Floor((tile.MaxN - cn) / spacing);
                if ((uint)col >= HoleFormat.QuadsPerSide || (uint)row >= HoleFormat.QuadsPerSide) continue;
                if (!holes.TryGetValue(tile, out var set)) holes[tile] = set = new HashSet<int>();
                set.Add(HoleFormat.CellIndex(col, row));
            }
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        double len = Math.Sqrt(x * x + y * y);
        return len < 1e-9 ? (0, 0) : (x / len, y / len);
    }
}
