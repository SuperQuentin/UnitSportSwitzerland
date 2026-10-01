using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

// The road blend: plain C# over TerrainFormat, no Godot types, so tools/BlendCheck compiles it.
public static partial class TerrainMeshBuilder
{

    /// <summary>
    /// The visual mesh's blend target sits this far below each road's own drawn surface —
    /// matching <c>RoadExtractor.DrapeOffset</c> (tools/TerrainPreprocessor, a separate
    /// project so the constant can't be shared directly) so the terrain rises to just under
    /// the ribbon instead of erasing the deliberate lift that keeps the two from z-fighting.
    /// Collision uses 0 instead — see <see cref="ComputeRoadBlend"/>.
    /// </summary>
    public const double VisualBlendClearance = 0.35;

    /// <summary>
    /// Every lattice cell under an at-grade road corridor, with how strongly it is pulled toward
    /// the road (<c>Weight</c>, 0..1) and the road height it is pulled to (<c>Target</c>,
    /// clearance 0) — so a blended height is <c>ground + (Target - clearance - ground)·Weight</c>. Sparse: a tile's corridors cover a few percent of it.
    /// Computed once per tile and applied twice — to the collision floor at clearance 0 and to
    /// the visual mesh at <see cref="VisualBlendClearance"/> — which is why the clearance is not
    /// baked in.
    /// </summary>
    public sealed record RoadBlend(int[] Cells, float[] Weight, float[] Target);

    private static float BlendedHeight(float ground, RoadBlend blend, int k, double clearance) =>
        (float)(ground + (blend.Target[k] - clearance - ground) * blend.Weight[k]);

    /// <summary>Applies a blend to a full-resolution height map in place.</summary>
    public static void ApplyRoadBlend(float[] map, RoadBlend blend, double clearance)
    {
        for (int k = 0; k < blend.Cells.Length; k++)
        {
            int cell = blend.Cells[k];
            map[cell] = BlendedHeight(map[cell], blend, k, clearance);
        }
    }


    /// <summary>How far past a road's own half-width the collision blend fades back to bare
    /// terrain — the same "ramp the approach" idea RoadExtractor already applies to the visual
    /// drape (see CLAUDE.md), applied here to the physics floor instead.</summary>
    private const double CorridorFalloffM = 3.0;

    /// <summary>
    /// Where the terrain is pulled toward each at-grade road/path/rail's own surveyed height, so
    /// the ground a player stands on (and sees) matches the road — swissALTI3D does not model the
    /// embankment climbing to a grade change, so without this the physics floor and the visible
    /// tread silently disagree by however much grading did.
    ///
    /// <para>
    /// Bridges and tunnels are excluded outright (<c>RoadFlags.Bridge/Tunnel</c>): a heightfield
    /// has one height per (x, z) column, so it cannot represent a deck floating above a valley
    /// floor at the same position — blending toward a bridge's deck height would fill in the
    /// gorge it crosses. Those get dedicated collision geometry instead (bridge decks/piers);
    /// tunnels keep working via the existing hole-carving at the portal. Aerial ropeways,
    /// watercourses and walls are excluded too (<c>RoadFormat.IsAerial/IsWatercourse/IsWall</c>)
    /// — none of them is a surface at ground level.
    /// </para>
    ///
    /// <para>
    /// The shape is the original one: walk each polyline piece in 1 m steps and at every step
    /// lerp a disc of cells toward the road by <c>w = 1 - smoothstep(half, half + 3 m, dist)</c>.
    /// Because each stamp lerps the result of the previous one, the pull compounds — shoulder
    /// cells end up much closer to the road than one smoothstep suggests. That shape is what is
    /// in the game, so it is kept exactly; only the arithmetic changed. The recursion
    /// <c>h = h + (y - h)·w</c> is linear in the ground height, so after every stamp a cell is
    /// <c>ground·P + S</c> with <c>P = Π(1 - w)</c> and <c>S</c> the accumulated road term. Tracking
    /// those two numbers instead of heights means one pass serves every clearance
    /// (<c>S - clearance·(1 - P)</c>) and needs no height map at all; and the weights depend only
    /// on the cell's offset from the stamp centre, so they come from a per-road table instead of
    /// a sqrt and a smoothstep per cell per stamp.
    /// </para>
    /// </summary>
    public static RoadBlend ComputeRoadBlend(RoadTile roadTile)
    {
        int n = ChunkFormat.GridSize;
        double spacing = ChunkFormat.SpacingM;
        var pool = System.Buffers.ArrayPool<double>.Shared;
        // dense scratch, pooled: 8 MB each, which would otherwise land on the large object heap
        // once per tile build. keep starts at 1 and road at 0 for a cell never touched, so only
        // the 1 MB mask needs clearing; the two value arrays are initialised on first touch.
        double[] keep = pool.Rent(n * n);     // P: the fraction of the ground height left
        double[] road = pool.Rent(n * n);     // S: the accumulated road term
        byte[] seen = System.Buffers.ArrayPool<byte>.Shared.Rent(n * n);
        // Distance from a cell to the nearest centreline stamp that covered it with a road's full
        // width, or +inf. A cell under a road takes THAT stamp's height and nothing else: letting
        // the last stamp win, or a neighbouring road's fade-out, drag it was measured leaving the
        // floor 0.1-0.4 m off the drawn ribbon, so feet sank into one path and hovered over the
        // next. Initialised on first touch, like the two arrays above.
        float[] coreDist = System.Buffers.ArrayPool<float>.Shared.Rent(n * n);
        Array.Clear(seen, 0, n * n);
        var touched = new List<int>(8192);
        // the stamp for the current road, as linear index offsets plus (1 - w) and w
        var offDc = new List<int>(); var offDr = new List<int>();
        var offLinear = new List<int>(); var offKeep = new List<double>(); var offW = new List<double>();

        try
        {
            foreach (var seg in roadTile.Segments)
            {
                if ((seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
                if (RoadFormat.IsAerial(seg.Class) || RoadFormat.IsWatercourse(seg.Class)
                    || RoadFormat.IsWall(seg.Class)) continue;

                // At least one lattice spacing: a footpath 1.2 m wide is narrower than a cell's
                // diagonal, so the four corners of the quad its centreline crosses could all fall
                // outside its core and only be partly pulled — measured 0.1 m of sinking on paths
                // alone. One spacing puts every corner of that quad under the road.
                double half = Math.Max(seg.Width * 0.5, spacing);
                double radius = half + CorridorFalloffM;
                int cells = (int)Math.Ceiling(radius / spacing);

                // the stamp, once per road: every offset inside the radius and its weight
                offDc.Clear(); offDr.Clear(); offLinear.Clear(); offKeep.Clear(); offW.Clear();
                for (int dr = -cells; dr <= cells; dr++)
                    for (int dc = -cells; dc <= cells; dc++)
                    {
                        double dist = Math.Sqrt((double)(dc * dc + dr * dr)) * spacing;
                        if (dist > radius) continue;
                        double w = 1.0 - Smoothstep(half, radius, dist);
                        if (w <= 0) continue;
                        offDc.Add(dc); offDr.Add(dr); offLinear.Add(dr * n + dc);
                        offKeep.Add(1.0 - w); offW.Add(w);
                    }
                var lin = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(offLinear);
                var kp = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(offKeep);
                var ww = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(offW);

                for (int i = 0; i < seg.PointCount - 1; i++)
                {
                    double ax = seg.Points[i * 3], ay = seg.Points[i * 3 + 1], az = seg.Points[i * 3 + 2];
                    double bx = seg.Points[(i + 1) * 3], by = seg.Points[(i + 1) * 3 + 1],
                        bz = seg.Points[(i + 1) * 3 + 2];
                    double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                    int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));
                    double segLen2 = len * len;

                    for (int st = 0; st <= steps; st++)
                    {
                        double t = (double)st / steps;
                        double x = ax + (bx - ax) * t, z = az + (bz - az) * t;
                        // the segment's own stored Y already carries the approach-ramp blend
                        // RoadExtractor baked in at preprocess time - no re-derivation needed
                        double roadY = ay + (by - ay) * t;
                        int c0 = (int)Math.Round(x / spacing), r0 = (int)Math.Round(z / spacing);

                        // almost every stamp lies wholly inside the tile, and then no offset
                        // needs its own bounds test
                        bool inside = c0 - cells >= 0 && c0 + cells < n && r0 - cells >= 0 && r0 + cells < n;
                        int centre = r0 * n + c0;
                        for (int o = 0; o < lin.Length; o++)
                        {
                            int idx;
                            if (inside) idx = centre + lin[o];
                            else
                            {
                                int c = c0 + offDc[o], r = r0 + offDr[o];
                                if ((uint)c >= (uint)n || (uint)r >= (uint)n) continue;
                                idx = r * n + c;
                            }
                            bool core = ww[o] >= 0.999;
                            if (core)
                            {
                                // Under the road: the height of the centreline at this cell's own
                                // perpendicular foot on the segment, not at the stamp that happened
                                // to reach it - stamps are a metre apart, which on a 30% alpine
                                // path is 15 cm of error. Nearest segment wins outright.
                                double cx = (idx % n) * spacing, cz = (idx / n) * spacing;
                                double foot = segLen2 > 1e-9
                                    ? Math.Clamp(((cx - ax) * (bx - ax) + (cz - az) * (bz - az)) / segLen2, 0, 1)
                                    : 0;
                                double px = ax + (bx - ax) * foot - cx, pz = az + (bz - az) * foot - cz;
                                float perp = (float)Math.Sqrt(px * px + pz * pz);
                                if (seen[idx] == 0)
                                {
                                    seen[idx] = 1;
                                    touched.Add(idx);
                                }
                                else if (perp >= coreDist[idx]) continue;
                                coreDist[idx] = perp;
                                keep[idx] = 0;
                                road[idx] = ay + (by - ay) * foot;
                                continue;
                            }
                            if (seen[idx] == 0)
                            {
                                seen[idx] = 1;
                                touched.Add(idx);
                                coreDist[idx] = float.PositiveInfinity;
                                keep[idx] = kp[o];
                                road[idx] = roadY * ww[o];
                                continue;
                            }
                            // a shoulder never reaches under another road's surface
                            if (coreDist[idx] < float.PositiveInfinity) continue;
                            keep[idx] *= kp[o];
                            road[idx] = road[idx] * kp[o] + roadY * ww[o];
                        }
                    }
                }
            }

            var result = new List<int>(touched.Count);
            var weights = new List<float>(touched.Count);
            var targets = new List<float>(touched.Count);
            foreach (int idx in touched)
            {
                double weight = 1.0 - keep[idx];
                if (weight <= 1e-9) continue;
                result.Add(idx);
                weights.Add((float)weight);
                targets.Add((float)(road[idx] / weight));
            }
            return new RoadBlend(result.ToArray(), weights.ToArray(), targets.ToArray());
        }
        finally
        {
            pool.Return(keep);
            pool.Return(road);
            System.Buffers.ArrayPool<byte>.Shared.Return(seen);
            System.Buffers.ArrayPool<float>.Shared.Return(coreDist);
        }
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 1e-6), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
