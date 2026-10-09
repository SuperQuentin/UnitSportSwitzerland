using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Turns road polylines into flat ribbon meshes. Runs on worker threads, so it only
/// produces arrays. Colour is baked per vertex so every road in a tile shares one
/// material and one draw call.
/// </summary>
public static class RoadMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, Vector2[] Uvs, Vector2[] Uv2s, int[] Indices);


    /// <param name="detail">The visual style's mesh detail: Cartoon's rounds the signal plates' corners (#759).</param>
    public static MeshData? Build(RoadTile tile, ChunkGrid? grid = null, Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
        // Bridge piers, cableway pylons and wall footings are grown from the ground up, so a
        // decimated grid would stand them on a 20 m approximation of it.
        grid?.RequireFull(nameof(RoadMeshBuilder));
        int quadCount = 0;
        foreach (var s in tile.Segments)
            quadCount += Math.Max(0, s.PointCount - 1);
        if (quadCount == 0 && tile.Junctions.Count == 0) return null;

        var vertices = new List<Vector3>(quadCount * 4);
        var colors = new List<Color>(quadCount * 4);
        // uv  = (metres along the line, lateral position across it in [-1,1])
        // uv2 = (surface style id, unused) — see MarkingStyle
        var uvs = new List<Vector2>(quadCount * 4);
        var uv2s = new List<Vector2>(quadCount * 4);
        var indices = new List<int>(quadCount * 6);

        foreach (var junction in tile.Junctions)
            AppendJunction(junction, vertices, colors, uvs, uv2s, indices);

        var joins = FindTypeJoins(tile);
        // the network stage's tiles carry their markings as paint (RoadPaintBuilder), not stripes
        bool painted = (tile.Flags & RoadTileFlags.Network) != 0;

        for (int i = 0; i < tile.Segments.Count; i++)
        {
            var seg = tile.Segments[i];

            // Aerial ropeways are not ribbons on the ground, and watercourses are meshed by
            // WaterMeshBuilder so they share the water material rather than the asphalt one.
            if (RoadFormat.IsWatercourse(seg.Class)) continue;
            if (RoadFormat.IsAerial(seg.Class))
            {
                AppendCableway(seg, tile.Id, grid, vertices, colors, uvs, uv2s, indices);
                continue;
            }
            if (RoadFormat.IsWall(seg.Class))
            {
                AppendWall(seg, tile.Id, grid, vertices, colors, uvs, uv2s, indices);
                continue;
            }

            // a rail embedded in a carriageway (#124) is the road's RailGroove paint: no ballast, no raised rails
            if (seg.Class == RoadClass.Railway && seg.Attributes.Has(RoadAttrFlags.Embedded)) continue;
            AppendSegment(seg, joins[i], painted, vertices, colors, uvs, uv2s, indices);
            // a town tram's track lies in paving (#119): its grooves are paint, no raised rails
            if (seg.Class == RoadClass.Railway && !seg.Attributes.Has(RoadAttrFlags.PavedBed))
                AppendRails(seg, vertices, colors, uvs, uv2s, indices);
            if ((seg.Flags & RoadFlags.Tunnel) != 0)
                AppendTunnelBore(seg, tile, vertices, colors, uvs, uv2s, indices);
            if ((seg.Flags & RoadFlags.Bridge) != 0)
                AppendBridgeStructure(seg, tile.Id, grid, vertices, colors, uvs, uv2s, indices);
        }
        RoadWallBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);   // retaining walls (#125)
        RailingBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);    // guardrails and fences (#126)
        IslandBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);     // roundabout islands (#122)
        PavementBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);   // turn lane widenings (#123)
        RoadSignBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);   // junction signs (#121)
        ParkingBuilder.Append(tile, vertices, colors, uvs, uv2s, indices);    // car park barrier, kiosk, shelter, P (#499)
        SignalBuilder.Append(tile, vertices, colors, uvs, uv2s, indices, rounded: detail == Styles.MeshDetail.High);   // traffic-light poles and heads (#350)
        RoadStreetBuilder.Append(tile, vertices, colors, uvs, uv2s, indices); // sidewalks (#119)

        return vertices.Count == 0
            ? null
            : new MeshData(vertices.ToArray(), colors.ToArray(), uvs.ToArray(), uv2s.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// Flat triangle soup for the walkable TOP of every bridge deck in the tile — the one piece
    /// of collision a heightfield genuinely cannot provide, since it has one height per (x, z)
    /// column and a deck floats above whatever the terrain is doing underneath (a gorge, a
    /// river). <c>TerrainMeshBuilder.ComputeRoadBlend</c> explicitly excludes bridges for the
    /// same reason in reverse — blending terrain toward a deck's height would fill in the gorge
    /// it crosses.
    ///
    /// <para>
    /// Deck only: piers and parapets stay visual-only. A player clipping through a pier or
    /// resting an elbow on a parapet was never the reported problem — falling clean through the
    /// deck was — and piers already stand inside terrain collision for their full height below
    /// the deck, so they are self-supporting without needing their own shape.
    /// </para>
    /// </summary>
    public static Vector3[] BuildBridgeCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();

        foreach (var seg in tile.Segments)
        {
            // and every tunnel's floor (#119): the ground over a bore is its roof, not its floor
            bool tunnel = RoadTunnels.IsBore(seg);
            if ((seg.Flags & RoadFlags.Bridge) == 0 && !tunnel) continue;
            int n = seg.PointCount;
            if (n < 2) continue;

            // a tunnel's floor reaches past the bore's sides over the hole its mouth punched (0.3 m)
            float half = tunnel ? RoadTunnels.HalfWidth(seg) + RoadTunnels.FloorMargin : seg.Width * 0.5f;
            // a tunnel's floor is its road, run out through the mouth's hole like the bore
            var line = tunnel ? ExtendedTunnelPath(seg) : Enumerable.Range(0, n).Select(i => Point(seg, i)).ToList();
            n = line.Count;
            var lift = tunnel ? Vector3.Zero : new Vector3(0, BridgeLift, 0);
            var left = new Vector3[n];
            var right = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                // same deck line AppendBridgeStructure draws from, so the collision sits exactly
                // under the visible tread rather than needing its own separate height source
                var p = line[i] + lift;
                Vector3 forward = i == 0 ? line[1] - line[0]
                    : i == n - 1 ? line[n - 1] - line[n - 2]
                    : line[i + 1] - line[i - 1];
                forward.Y = 0;
                if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
                forward = forward.Normalized();
                var side = new Vector3(-forward.Z, 0, forward.X) * half;
                left[i] = p - side;
                right[i] = p + side;
            }

            for (int i = 0; i < n - 1; i++)
            {
                faces.Add(left[i]); faces.Add(right[i]); faces.Add(left[i + 1]);
                faces.Add(right[i]); faces.Add(right[i + 1]); faces.Add(left[i + 1]);
            }
            if (!tunnel) continue;
            // the bore's walls, floor to crown: a body drifting off the road meets the tunnel's
            // side as it would the drawn arch, rather than driving out of it into the hillside
            var up = new Vector3(0, RoadTunnels.ClearHeight(seg, tile), 0);
            for (int i = 0; i < n - 1; i++)
                foreach (var (a, b) in new[] { (left[i], left[i + 1]), (right[i], right[i + 1]) })
                {
                    faces.Add(a); faces.Add(b); faces.Add(b + up);
                    faces.Add(a); faces.Add(b + up); faces.Add(a + up);
                }
        }

        return faces.ToArray();
    }

    /// <summary>
    /// Draws the paved area where roads meet, pre-triangulated by <c>tools/RoadGen</c>.
    ///
    /// <para>
    /// The carriageways arriving here have been trimmed back to this polygon's edge, so nothing
    /// overlaps and the cap is what fills the middle. It carries no lane markings on purpose:
    /// the whole reason lane lines used to cross each other in the middle of an intersection is
    /// that the ribbons ran straight through it, and painting the cap would put them back.
    /// </para>
    ///
    /// <para>
    /// Present only in format v2 tiles. A region built before the rewrite simply has none, and
    /// renders exactly as it did.
    /// </para>
    /// </summary>
    /// <summary>
    /// The cap borrows the dominant arm's tint via a stand-in segment, so a junction between
    /// farm tracks stays dirt-coloured instead of turning into a slab of asphalt (sRGB). The
    /// flush pavement beside it (turn-lane widenings, corner fill) takes the same (#682).
    /// </summary>
    public static Color CapColour(RoadJunction junction) => ColorFor(new RoadSegment
    {
        Class = junction.Class,
        Surface = junction.Class is RoadClass.Track or RoadClass.Path ? RoadSurface.Natural : RoadSurface.Paved,
        Flags = RoadFlags.None,
        Width = 0,
        Points = Array.Empty<float>(),
    });

    private static void AppendJunction(RoadJunction junction, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = junction.VertexCount;
        if (n < 3 || junction.Indices.Length < 3) return;

        // a bridge deck's junction has to ride at the same lift as the deck it sits on, or the
        // cap sinks into the soffit
        float lift = junction.Layer > 0 ? BridgeLift : 0f;

        var color = CapColour(junction).SrgbToLinear();

        int baseIndex = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(
                junction.Vertices[i * 3],
                junction.Vertices[i * 3 + 1] + lift,
                junction.Vertices[i * 3 + 2]));
            colors.Add(color);
            uvs.Add(new Vector2(0f, 0f));
            uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
        }

        for (int i = 0; i + 2 < junction.Indices.Length; i += 3)
        {
            indices.Add(baseIndex + junction.Indices[i]);
            indices.Add(baseIndex + junction.Indices[i + 1]);
            indices.Add(baseIndex + junction.Indices[i + 2]);
        }
    }

    /// <summary>
    /// An aerial ropeway: the cable at its surveyed height, plus a tower under every vertex.
    ///
    /// <para>
    /// The heights are not invented. swissTLM3D digitises these lines along the <i>cable</i>,
    /// which was verified against this project's own heightfield around Riddes: chairlifts run
    /// a median 11.9 m above ground, gondolas 14.6 m, and an aerial tramway reaches 73 m where
    /// it crosses a gorge. So the line is used verbatim and the towers are grown up from the
    /// terrain to meet it, which also means a pylon lands wherever the surveyors put a vertex —
    /// exactly where the real ones are, because that is where a cable changes direction.
    /// </para>
    ///
    /// <para>
    /// No catenary sag is modelled. The vertices already sit at the sheave heights, so curving
    /// between them would dip the cable below its own towers on every span.
    /// </para>
    /// </summary>
    private static void AppendCableway(RoadSegment seg, TileId id, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        var cableColour = new Color(0.18f, 0.18f, 0.20f).SrgbToLinear();
        var pylonColour = new Color(0.42f, 0.41f, 0.39f).SrgbToLinear();
        float half = Math.Max(seg.Width, 0.04f) * 0.5f;

        // Two ribbons crossed in a plus, not one. A single flat ribbon disappears completely
        // when viewed edge-on — which for a cableway strung across a valley is most of the time,
        // since the player is usually beside it rather than under it.
        for (int pass = 0; pass < 2; pass++)
        {
            int baseIndex = vertices.Count;
            for (int i = 0; i < n; i++)
            {
                var p = Point(seg, i);
                Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                    : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                    : Point(seg, i + 1) - Point(seg, i - 1);
                forward.Y = 0;
                if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
                forward = forward.Normalized();

                var lateral = pass == 0
                    ? new Vector3(-forward.Z, 0, forward.X) * half   // horizontal
                    : new Vector3(0, half, 0);                       // vertical

                vertices.Add(p - lateral);
                vertices.Add(p + lateral);
                colors.Add(cableColour); colors.Add(cableColour);
                uvs.Add(new Vector2(0f, -1f));
                uvs.Add(new Vector2(0f, 1f));
                uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
                uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
            }

            for (int i = 0; i < n - 1; i++)
            {
                int a = baseIndex + i * 2;
                indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
                indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
            }
        }

        if (grid == null) return;

        float radius = RoadFormat.PylonRadius(seg.Class);
        float headroom = RoadFormat.PylonHeadroom(seg.Class);

        for (int i = 0; i < n; i++)
        {
            var top = Point(seg, i);
            double e = id.MinE + top.X;
            double north = id.MaxN - top.Z;
            double ground = grid.SampleHeight(e, north);
            if (double.IsNaN(ground)) continue;

            // A terminal station sits on the ground and needs no tower; so does any vertex the
            // cable happens to pass close over. Below this the mast would be a stub in the mud.
            float height = top.Y - (float)ground;
            if (height < 2.0f) continue;

            AppendPylon(top, height + headroom, radius, pylonColour,
                vertices, colors, uvs, uv2s, indices);
        }
    }

    /// <summary>
    /// A structure standing along a line: avalanche barriers, torrent works, dry-stone walls.
    ///
    /// <para>
    /// The height is real where the data has it. TLM digitises a <c>Schutzverbauung</c> along
    /// the <i>top</i> of the barrier — measured against this project's heightfield around
    /// Riddes, a median 2.80 m above ground with a 90th percentile of 5.81 m, which is the
    /// actual height range of snow bridges. So each post is grown from the terrain up to the
    /// surveyed Z, and only clamped where that would be absurd: walls and torrent works are
    /// digitised much closer to the ground (+0.15 to +0.95 m) and without a floor would render
    /// as kerbstones.
    /// </para>
    /// </summary>
    private static void AppendWall(RoadSegment seg, TileId id, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2 || grid == null) return;

        var (minHeight, maxHeight) = RoadFormat.WallHeight(seg.Class);
        float halfThickness = RoadFormat.WallThickness(seg.Class) * 0.5f;
        var colour = WallColour(seg.Class).SrgbToLinear();

        // 4 vertices per station: outer/inner at base and top
        int baseIndex = vertices.Count;
        int emitted = 0;

        for (int i = 0; i < n; i++)
        {
            var top = Point(seg, i);
            double ground = grid.SampleHeight(id.MinE + top.X, id.MaxN - top.Z);
            if (double.IsNaN(ground)) return;

            float height = Math.Clamp(top.Y - (float)ground, minHeight, maxHeight);
            float baseY = (float)ground;

            Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                : Point(seg, i + 1) - Point(seg, i - 1);
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var lateral = new Vector3(-forward.Z, 0, forward.X) * halfThickness;

            var footA = new Vector3(top.X, baseY, top.Z) - lateral;
            var footB = new Vector3(top.X, baseY, top.Z) + lateral;
            var capA = footA + new Vector3(0, height, 0);
            var capB = footB + new Vector3(0, height, 0);

            foreach (var v in stackalloc[] { footA, capA, footB, capB })
            {
                vertices.Add(v);
                colors.Add(colour);
                uvs.Add(new Vector2(0f, 0f));
                uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
            }
            emitted++;
        }

        for (int i = 0; i < emitted - 1; i++)
        {
            int a = baseIndex + i * 4;
            int b = a + 4;

            // outward face, inward face, and the cap between them
            Quad(indices, a + 0, a + 1, b + 1, b + 0);
            Quad(indices, b + 2, b + 3, a + 3, a + 2);
            Quad(indices, a + 1, a + 3, b + 3, b + 1);
        }
    }

    private static void Quad(List<int> indices, int a, int b, int c, int d)
    {
        indices.Add(a); indices.Add(b); indices.Add(c);
        indices.Add(a); indices.Add(c); indices.Add(d);
    }

    private static Color WallColour(RoadClass cls) => cls switch
    {
        // galvanised steel snow bridges, weathered
        RoadClass.AvalancheBarrier => new Color(0.44f, 0.44f, 0.42f),
        // poured concrete check dams
        RoadClass.TorrentWorks => new Color(0.54f, 0.53f, 0.50f),
        // warm local stone, the Valais terracing
        RoadClass.DryStoneWall => new Color(0.53f, 0.47f, 0.38f),
        _ => new Color(0.56f, 0.55f, 0.52f),
    };

    /// <summary>A four-sided tapering mast. Square section: at PS1 resolution a round one costs
    /// triangles nobody can see.</summary>
    private static void AppendPylon(Vector3 top, float height, float radius, Color colour,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        var baseCentre = top - new Vector3(0, height, 0);
        float baseRadius = radius * 1.6f;   // splayed feet read as a lattice tower

        int start = vertices.Count;
        for (int corner = 0; corner < 4; corner++)
        {
            double angle = corner * Math.PI / 2 + Math.PI / 4;
            var dir = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
            vertices.Add(baseCentre + dir * baseRadius);
            vertices.Add(top + dir * radius);
            colors.Add(colour); colors.Add(colour);
            uvs.Add(new Vector2(0f, -1f));
            uvs.Add(new Vector2(0f, 1f));
            uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
            uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
        }

        for (int corner = 0; corner < 4; corner++)
        {
            int a = start + corner * 2;
            int b = start + ((corner + 1) % 4) * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(b + 1);
            indices.Add(a); indices.Add(b + 1); indices.Add(b);
        }
    }

    /// <summary>
    /// What a segment's ribbon should be at each end so a differently-classed neighbour meets it
    /// exactly. A blend length of zero means the end is left alone.
    /// </summary>
    private readonly record struct RoadJoin(
        float StartWidth, Color StartColour, float StartBlend,
        float EndWidth, Color EndColour, float EndBlend);

    /// <summary>Shortest taper, so even a small change gets a few metres to happen over.</summary>
    private const float MinTypeBlend = 5f;
    private const float MaxTypeBlend = 22f;

    /// <summary>
    /// Finds where two roads of different type meet end to end, and works out the width and
    /// colour they must share at that point.
    ///
    /// <para>
    /// swissTLM3D splits a road wherever any attribute changes, so a lane widening from 3 m to
    /// 4 m or a road turning from asphalt to gravel is two separate features that happen to
    /// share an endpoint. Ribboned independently they meet in a step — a visible shoulder
    /// sticking out of the carriageway, and a hard colour seam across it.
    /// </para>
    ///
    /// <para>
    /// Both sides are pulled to the <i>mean</i> width and colour at the shared vertex and then
    /// eased back to their own over the following stretch. Taking the mean is what makes the
    /// join exact: if each side merely tapered toward the other's value it would still arrive at
    /// a different number, and the step would shrink rather than close.
    /// </para>
    ///
    /// <para>
    /// Only ends shared by exactly two segments are considered. Three or more is a junction, and
    /// the junction polygon already covers that ground.
    /// </para>
    /// </summary>
    private static RoadJoin[] FindTypeJoins(RoadTile tile)
    {
        var joins = new RoadJoin[tile.Segments.Count];
        var ends = new Dictionary<(int, int), (int Seg, bool AtStart, int Count)>();

        for (int i = 0; i < tile.Segments.Count; i++)
        {
            var seg = tile.Segments[i];
            if (seg.PointCount < 2) continue;
            if (RoadFormat.IsWatercourse(seg.Class) || RoadFormat.IsAerial(seg.Class)
                || RoadFormat.IsWall(seg.Class)) continue;

            Record(ends, Key(seg, 0), i, true);
            Record(ends, Key(seg, seg.PointCount - 1), i, false);
        }

        for (int i = 0; i < tile.Segments.Count; i++)
        {
            var seg = tile.Segments[i];
            if (seg.PointCount < 2) continue;
            if (RoadFormat.IsWatercourse(seg.Class) || RoadFormat.IsAerial(seg.Class)
                || RoadFormat.IsWall(seg.Class)) continue;

            var mine = ColorFor(seg).SrgbToLinear();

            foreach (bool atStart in stackalloc[] { true, false })
            {
                var key = Key(seg, atStart ? 0 : seg.PointCount - 1);
                if (!ends.TryGetValue(key, out var slot) || slot.Count != 2) continue;

                // the slot holds the *other* member if this one is the second to arrive
                int otherIndex = slot.Seg == i ? -1 : slot.Seg;
                if (otherIndex < 0) continue;

                var other = tile.Segments[otherIndex];
                var theirs = ColorFor(other).SrgbToLinear();

                float widthDelta = Math.Abs(seg.Width - other.Width);
                float colourDelta = Math.Abs(mine.R - theirs.R) + Math.Abs(mine.G - theirs.G)
                    + Math.Abs(mine.B - theirs.B);
                if (widthDelta < 0.05f && colourDelta < 0.02f) continue;   // same type, nothing to do

                float meetWidth = (seg.Width + other.Width) * 0.5f;
                var meetColour = mine.Lerp(theirs, 0.5f);
                float blend = Math.Clamp(widthDelta * 6f, MinTypeBlend, MaxTypeBlend);

                joins[i] = atStart
                    ? joins[i] with { StartWidth = meetWidth, StartColour = meetColour, StartBlend = blend }
                    : joins[i] with { EndWidth = meetWidth, EndColour = meetColour, EndBlend = blend };
                joins[otherIndex] = ApplyToOther(joins[otherIndex], other, key, meetWidth, meetColour, blend);
            }
        }

        return joins;
    }

    private static RoadJoin ApplyToOther(RoadJoin join, RoadSegment other, (int, int) key,
        float width, Color colour, float blend)
    {
        bool atStart = Key(other, 0) == key;
        return atStart
            ? join with { StartWidth = width, StartColour = colour, StartBlend = blend }
            : join with { EndWidth = width, EndColour = colour, EndBlend = blend };
    }

    /// <summary>Endpoint identity, quantised to a centimetre.</summary>
    private static (int, int) Key(RoadSegment seg, int i) =>
        ((int)MathF.Round(seg.Points[i * 3] * 100f), (int)MathF.Round(seg.Points[i * 3 + 2] * 100f));

    private static void Record(Dictionary<(int, int), (int Seg, bool AtStart, int Count)> ends,
        (int, int) key, int segment, bool atStart)
    {
        if (ends.TryGetValue(key, out var existing))
            ends[key] = (existing.Seg, existing.AtStart, existing.Count + 1);
        else
            ends[key] = (segment, atStart, 1);
    }

    private static void AppendSegment(RoadSegment seg, in RoadJoin join, bool painted, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        // Each end runs on EndTuck past its point, EndTuckDrop lower: under the junction cap or the
        // next piece it stays hidden, and it closes the hairline crack between the cap's edge and
        // the ribbon's end (different vertices, the same line) through which the ground showed.
        var pts = new Vector3[n + 2];
        for (int i = 0; i < n; i++)
            pts[i + 1] = new Vector3(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
        pts[0] = Tuck(pts[1], pts[2]);
        pts[n + 1] = Tuck(pts[n], pts[n - 1]);
        n += 2;

        float lift = (seg.Flags & RoadFlags.Bridge) != 0 ? BridgeLift : 0f;
        float half = seg.Width * 0.5f;
        // Colours below are authored in sRGB (what they should look like on screen).
        // Shader uniforms marked ": source_color" get this conversion automatically, but
        // raw vertex colours do not — without it dark asphalt renders washed-out grey.
        var color = ColorFor(seg).SrgbToLinear();

        var along = new float[n];
        for (int i = 1; i < n; i++) along[i] = along[i - 1] + pts[i].DistanceTo(pts[i - 1]);
        float total = along[^1];

        // neither taper may reach past the middle, or a short segment would blend both ends
        // into each other and never reach its own width at all
        float startBlend = Math.Min(join.StartBlend, total * 0.5f);
        float endBlend = Math.Min(join.EndBlend, total * 0.5f);

        // Per-vertex offset direction = bisector of adjacent segment directions, so the
        // ribbon stays continuous through corners instead of tearing at each joint.
        int baseIndex = vertices.Count;
        float style = (float)MarkingStyleFor(seg, painted);

        for (int i = 0; i < n; i++)
        {
            Vector3 forward;
            if (i == 0) forward = pts[1] - pts[0];
            else if (i == n - 1) forward = pts[n - 1] - pts[n - 2];
            else forward = (pts[i + 1] - pts[i - 1]);

            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();

            float vertexHalf = half;
            var vertexColor = color;

            // Ease rather than lerp: a straight ramp still leaves a visible crease where the
            // taper meets the constant width, because the *rate* of change jumps there.
            if (startBlend > 0 && along[i] < startBlend)
            {
                float t = Mathf.SmoothStep(0f, 1f, along[i] / startBlend);
                vertexHalf = Mathf.Lerp(join.StartWidth * 0.5f, half, t);
                vertexColor = join.StartColour.Lerp(color, t);
            }
            else if (endBlend > 0 && total - along[i] < endBlend)
            {
                float t = Mathf.SmoothStep(0f, 1f, (total - along[i]) / endBlend);
                vertexHalf = Mathf.Lerp(join.EndWidth * 0.5f, half, t);
                vertexColor = join.EndColour.Lerp(color, t);
            }

            var side = new Vector3(-forward.Z, 0, forward.X) * vertexHalf;
            var p = pts[i] + new Vector3(0, lift, 0);
            vertices.Add(p - side);
            vertices.Add(p + side);
            colors.Add(vertexColor);
            colors.Add(vertexColor);
            uvs.Add(new Vector2(along[i], -1f));
            uvs.Add(new Vector2(along[i], 1f));
            uv2s.Add(new Vector2(style, 0f));
            uv2s.Add(new Vector2(style, 0f));
        }

        for (int i = 0; i < n - 1; i++)
        {
            int a = baseIndex + i * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }
    }

    private const float EndTuck = 0.15f, EndTuckDrop = 0.01f;

    /// <summary>The point EndTuck on past <paramref name="end"/>, away from <paramref name="inner"/>, EndTuckDrop lower.</summary>
    private static Vector3 Tuck(Vector3 end, Vector3 inner)
    {
        var d = end - inner;
        d.Y = 0;
        if (d.LengthSquared() < 1e-8f) return end + Vector3.Down * EndTuckDrop;
        return end + d.Normalized() * EndTuck + Vector3.Down * EndTuckDrop;
    }

    /// <summary>
    /// Surface pattern id baked into uv.y and drawn by the shader.
    ///
    /// Switzerland publishes no lane-marking dataset, so these are inferred from what
    /// swissTLM3D does record: width class, surface, and whether the carriageway is
    /// direction-separated. A divided carriageway carries no centre line because both
    /// sides run the same way.
    /// </summary>
    private enum MarkingStyle
    {
        None = 0,
        CentreDashed = 1,  // ordinary two-way road
        EdgeOnly = 2,      // divided carriageway: edge lines, no centre
        Motorway = 3,      // edge lines plus a dashed lane divider
        RailBallast = 4,   // sleeper stripes
        RailSteel = 5,     // the rails themselves
        // 6: v3 paint, a separate surface (RoadPaintBuilder.Style)
    }

    private static MarkingStyle MarkingStyleFor(RoadSegment seg, bool painted)
    {
        if (seg.Class == RoadClass.Railway)
            return seg.Attributes.Has(RoadAttrFlags.PavedBed) ? MarkingStyle.None : MarkingStyle.RailBallast;
        if (painted) return MarkingStyle.None;
        // unpaved surfaces and anything narrower than a lane are never marked
        if (seg.Surface != RoadSurface.Paved) return MarkingStyle.None;
        if (seg.Class >= RoadClass.Track) return MarkingStyle.None;

        if (seg.Class is RoadClass.Motorway or RoadClass.Expressway) return MarkingStyle.Motorway;
        if ((seg.Flags & RoadFlags.Divided) != 0) return MarkingStyle.EdgeOnly;
        // below ~4 m Swiss roads are generally unmarked
        return seg.Class <= RoadClass.Minor ? MarkingStyle.CentreDashed : MarkingStyle.None;
    }

    /// <summary>
    /// Lays the running rails on top of the ballast ribbon. Sleepers are drawn by the
    /// shader as stripes rather than modelled — at real 0.65 m spacing they would add
    /// tens of thousands of triangles per kilometre for detail a few pixels wide.
    /// </summary>
    private static void AppendRails(RoadSegment seg, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2 || (seg.Flags & RoadFlags.Funicular) != 0) return;

        float gauge = RoadFormat.RailGauge(seg.Flags);
        float trackOffset = RoadFormat.TrackOffset(seg.Flags);
        var centres = trackOffset > 0f
            ? new[] { -trackOffset, trackOffset }
            : new[] { 0f };

        var railColour = (seg.Flags & RoadFlags.Disused) != 0
            ? new Color(0.38f, 0.30f, 0.24f)   // rusted
            : new Color(0.55f, 0.55f, 0.58f);  // polished steel
        const float RailHeight = 0.18f;
        const float RailHalfWidth = 0.075f;

        foreach (float centre in centres)
            foreach (int side in new[] { -1, 1 })
            {
                float offset = centre + side * gauge * 0.5f;
                int baseIndex = vertices.Count;

                for (int i = 0; i < n; i++)
                {
                    var p = Point(seg, i);
                    Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                        : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                        : Point(seg, i + 1) - Point(seg, i - 1);
                    forward.Y = 0;
                    if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
                    forward = forward.Normalized();
                    var lateral = new Vector3(-forward.Z, 0, forward.X);

                    var mid = p + lateral * offset + new Vector3(0, RailHeight, 0);
                    vertices.Add(mid - lateral * RailHalfWidth);
                    vertices.Add(mid + lateral * RailHalfWidth);
                    var linear = railColour.SrgbToLinear();
                    colors.Add(linear); colors.Add(linear);
                    uvs.Add(new Vector2(0f, -1f));
                    uvs.Add(new Vector2(0f, 1f));
                    uv2s.Add(new Vector2((float)MarkingStyle.RailSteel, 0f));
                    uv2s.Add(new Vector2((float)MarkingStyle.RailSteel, 0f));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    int a = baseIndex + i * 2;
                    indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
                    indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
                }
            }
    }

    private static readonly Color DeckColor = new Color(0.44f, 0.43f, 0.41f);   // concrete fascia
    private static readonly Color SoffitColor = new Color(0.30f, 0.29f, 0.28f); // shaded underside
    private static readonly Color ParapetColor = new Color(0.52f, 0.51f, 0.49f);
    private static readonly Color PierColor = new Color(0.38f, 0.37f, 0.36f);

    /// <summary>
    /// Gives a bridge deck real thickness, edge parapets, and piers down to the ground.
    /// Without this a bridge is a ribbon hanging in mid-air with nothing underneath.
    /// <paramref name="grid"/> supplies the terrain height for pier footings; when it is
    /// unavailable the piers are simply skipped.
    /// </summary>
    private static void AppendBridgeStructure(RoadSegment seg, TileId tile, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        float half = seg.Width * 0.5f;
        float thickness = seg.Class <= RoadClass.Major ? 1.2f : 0.7f;
        const float ParapetHeight = 1.0f;

        // deck edge rails, computed the same way as the road ribbon so they line up
        var left = new Vector3[n];
        var right = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var p = Point(seg, i) + new Vector3(0, BridgeLift, 0);
            Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                : Point(seg, i + 1) - Point(seg, i - 1);
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X) * half;
            left[i] = p - side;
            right[i] = p + side;
        }

        var down = new Vector3(0, -thickness, 0);
        var up = new Vector3(0, ParapetHeight, 0);

        for (int i = 0; i < n - 1; i++)
        {
            // soffit (underside), seen from below
            AddQuad(vertices, colors, uvs, uv2s, indices, SoffitColor,
                left[i] + down, right[i] + down, left[i + 1] + down, right[i + 1] + down);
            // fascia beams down each side
            AddQuad(vertices, colors, uvs, uv2s, indices, DeckColor,
                left[i], left[i] + down, left[i + 1], left[i + 1] + down);
            AddQuad(vertices, colors, uvs, uv2s, indices, DeckColor,
                right[i], right[i] + down, right[i + 1], right[i + 1] + down);
            // parapets
            AddQuad(vertices, colors, uvs, uv2s, indices, ParapetColor,
                left[i], left[i] + up, left[i + 1], left[i + 1] + up);
            AddQuad(vertices, colors, uvs, uv2s, indices, ParapetColor,
                right[i], right[i] + up, right[i + 1], right[i + 1] + up);
        }

        if (grid == null) return;
        AppendPiers(seg, tile, grid, left, right, thickness, vertices, colors, uvs, uv2s, indices);
    }

    /// <summary>
    /// Small lift off the surveyed deck height, purely to stop the carriageway z-fighting
    /// with the terrain at abutments where the gap goes to zero.
    /// </summary>
    private const float BridgeLift = 0.15f;

    private const float PierSpacing = 25f;
    private const float MinPierGap = 4f;

    /// <summary>
    /// Above this the crossing is almost certainly a single span (suspension or arch) —
    /// TLM3D does not record bridge type, and stamping columns under a footbridge over a
    /// 130 m gorge looks far worse than leaving the deck unsupported.
    /// </summary>
    private const float MaxPierHeight = 35f;

    private static void AppendPiers(RoadSegment seg, TileId tile, ChunkGrid grid,
        Vector3[] left, Vector3[] right, float thickness,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        float sinceLast = PierSpacing; // place one at the first eligible point
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sinceLast += left[i].DistanceTo(left[i - 1]);
            if (sinceLast < PierSpacing) continue;

            var centre = (left[i] + right[i]) * 0.5f;
            double e = tile.MinE + centre.X;
            double nn = tile.MaxN - centre.Z;
            float ground = (float)grid.SampleHeight(e, nn);

            float deckBottom = centre.Y - thickness;
            float gap = deckBottom - ground;
            if (gap < MinPierGap || gap > MaxPierHeight) continue;

            sinceLast = 0;
            float w = Math.Min(2.5f, seg.Width * 0.35f);
            var axis = (right[i] - left[i]).Normalized() * w * 0.5f;
            var perp = new Vector3(-axis.Z, 0, axis.X);

            // four sides of a simple column, sunk slightly into the ground
            var top = new Vector3(centre.X, deckBottom, centre.Z);
            var bot = new Vector3(centre.X, ground - 0.5f, centre.Z);
            for (int k = 0; k < 4; k++)
            {
                var o1 = k switch { 0 => axis + perp, 1 => axis - perp, 2 => -axis - perp, _ => -axis + perp };
                var o2 = k switch { 0 => axis - perp, 1 => -axis - perp, 2 => -axis + perp, _ => axis + perp };
                AddQuad(vertices, colors, uvs, uv2s, indices, PierColor, top + o1, bot + o1, top + o2, bot + o2);
            }
        }
    }

    /// <summary>Adds a quad from four corners (a,b / c,d form the two edges).</summary>
    private static void AddQuad(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int i0 = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        var linear = color.SrgbToLinear();
        // structural geometry carries no surface markings
        for (int k = 0; k < 4; k++) { colors.Add(linear); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        // cull_disabled, so winding only needs to be consistent
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
        indices.Add(i0 + 1); indices.Add(i0 + 3); indices.Add(i0 + 2);
    }

    /// <summary>
    /// One tunnel mouth's placement, shared between the bore extrusion here and
    /// <c>TerrainMeshBuilder</c>'s portal wall so both draw the identical arch at the
    /// identical position instead of two independently-computed shapes.
    /// </summary>
    public readonly record struct TunnelPortal(Vector3 Mouth, Vector3 Inward, float HalfWidth, float Height);

    /// <summary>
    /// Arch cross-section of a tunnel bore as (lateral, up) fractions of half-width and
    /// clear height. Deliberately few segments — a faceted bore is the right look here.
    /// Internal (not private) so <c>TerrainMeshBuilder</c>'s portal wall extrudes the exact
    /// same shape at each mouth instead of guessing its own.
    /// </summary>
    internal static readonly (float X, float Y)[] BoreProfile =
    {
        (-1.00f, 0.00f),
        (-1.00f, 0.35f),
        (-0.92f, 0.62f),
        (-0.55f, 0.90f),
        (0.00f, 1.00f),
        (0.55f, 0.90f),
        (0.92f, 0.62f),
        (1.00f, 0.35f),
        (1.00f, 0.00f),
    };

    /// <summary>
    /// Distance the bore is pushed out past each end of the tunnel centreline. Without
    /// it the arch begins exactly where the rock begins, so from outside the road simply
    /// stops at a notch in the hillside with nothing to drive into. Also how far
    /// <c>TunnelCarver</c> (tools/TerrainPreprocessor) extends its own carve, so the hole
    /// breaks the surface at exactly the same point the bore does.
    /// </summary>
    private const float PortalExtension = 1.0f;

    /// <summary>The mouth's direction is taken over this much of the centreline, not its first chord (5 cm).</summary>
    private const float PortalAim = 3f;

    /// <summary>
    /// A tunnel segment's centreline, extended <see cref="PortalExtension"/> past each end
    /// so the bore/portal break the surface instead of stopping at a bare notch. Shared by
    /// the bore extrusion and <see cref="ComputeTunnelPortals"/> so both agree on exactly
    /// where a mouth sits.
    /// </summary>
    private static List<Vector3> ExtendedTunnelPath(RoadSegment seg)
    {
        int n = seg.PointCount;
        var path = new List<Vector3>(n + 2);
        if (n < 2)
        {
            for (int i = 0; i < n; i++) path.Add(Point(seg, i));
            return path;
        }

        var firstDir = (Point(seg, Aim(seg, fromStart: true)) - Point(seg, 0)) with { Y = 0 };
        var lastDir = (Point(seg, n - 1) - Point(seg, Aim(seg, fromStart: false))) with { Y = 0 };
        if (firstDir.LengthSquared() > 1e-8f)
            path.Add(Point(seg, 0) - firstDir.Normalized() * PortalExtension);
        for (int i = 0; i < n; i++) path.Add(Point(seg, i));
        if (lastDir.LengthSquared() > 1e-8f)
            path.Add(Point(seg, n - 1) + lastDir.Normalized() * PortalExtension);
        return path;
    }

    /// <summary>The first point at least <see cref="PortalAim"/> (plan) from the given end, or the far end.</summary>
    private static int Aim(RoadSegment seg, bool fromStart)
    {
        int n = seg.PointCount;
        var end = Point(seg, fromStart ? 0 : n - 1);
        for (int k = 1; k < n; k++)
        {
            int i = fromStart ? k : n - 1 - k;
            var d = Point(seg, i) - end;
            if (d.X * d.X + d.Z * d.Z >= PortalAim * PortalAim) return i;
        }
        return fromStart ? n - 1 : 0;
    }

    /// <summary>
    /// A tunnel segment's half-width and clear height (<see cref="RoadTunnels.ClearHeight"/>: the
    /// class's, lowered only under a road crossing over it). Shared by the bore extrusion,
    /// <see cref="ComputeTunnelPortals"/> and the road blend, which keeps ground over the crown.
    /// </summary>
    private static (float HalfWidth, float Height) TunnelDims(RoadSegment seg, RoadTile tile) =>
        (RoadTunnels.HalfWidth(seg), RoadTunnels.ClearHeight(seg, tile));

    /// <summary>
    /// Every tunnel mouth in a tile, for <c>TerrainMeshBuilder</c>'s portal wall — computed
    /// from the same extended path and dimensions <see cref="AppendTunnelBore"/> itself
    /// extrudes, so the terrain-side wall and the bore agree exactly instead of two
    /// independently-derived shapes hoping to coincide.
    /// </summary>
    public static List<TunnelPortal> ComputeTunnelPortals(RoadTile tile, ChunkGrid? grid)
    {
        var portals = new List<TunnelPortal>();
        // A tunnel line is cut into pieces (tile seams, junction trims): only an end that does not
        // carry on into another piece is a mouth (#119). A face at every piece end stood across
        // the bore inside the tunnel, its outline sized to the ground above.
        var ends = new Dictionary<(int, int), int>();
        foreach (var seg in tile.Segments)
            if ((seg.Flags & RoadFlags.Tunnel) != 0 && seg.PointCount >= 2)
                foreach (int i in new[] { 0, seg.PointCount - 1 })
                {
                    var key = Key(seg, i);
                    ends[key] = ends.GetValueOrDefault(key) + 1;
                }
        foreach (var seg in tile.Segments)
        {
            if ((seg.Flags & RoadFlags.Tunnel) == 0) continue;
            var path = ExtendedTunnelPath(seg);
            if (path.Count < 2) continue;

            var (halfWidth, height) = TunnelDims(seg, tile);
            // a mouth: no other piece carries on there, not a tile seam (the neighbour's piece
            // does), and the bore meets the surface there (not a gap in an underground line)
            bool Mouth(int i)
            {
                var p = Point(seg, i);
                if (ends.GetValueOrDefault(Key(seg, i)) != 1) return false;
                if (p.X < 0.05f || p.Z < 0.05f || p.X > 999.95f || p.Z > 999.95f) return false;
                double ground = grid?.SampleHeight(tile.Id.MinE + p.X, tile.Id.MaxN - p.Z) ?? double.NaN;
                return RoadTunnels.AtSurface(ground, p.Y, height);
            }
            if (Mouth(0)) portals.Add(new TunnelPortal(path[0], path[1], halfWidth, height));
            if (Mouth(seg.PointCount - 1)) portals.Add(new TunnelPortal(path[^1], path[^2], halfWidth, height));
        }
        return portals;
    }

    /// <summary>
    /// Extrudes the arch profile along a tunnel centreline, extended past both ends so it
    /// breaks the surface. The road ribbon already provides the carriageway, so this adds
    /// only walls and crown — the mouth itself is closed by <c>TerrainMeshBuilder</c>'s
    /// portal wall (see <see cref="ComputeTunnelPortals"/>), not by anything built here, so
    /// it's derived from the same hole mask the carved opening actually used rather than an
    /// independent guess. Rendered with cull_disabled, so the faces read correctly from
    /// inside the bore.
    /// </summary>
    private static void AppendTunnelBore(RoadSegment seg, RoadTile tile,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        var path = ExtendedTunnelPath(seg);
        int m = path.Count;
        if (m < 2) return;

        var (halfWidth, height) = TunnelDims(seg, tile);
        int ring = BoreProfile.Length;
        int baseIndex = vertices.Count;

        for (int i = 0; i < m; i++)
        {
            var p = path[i];
            Vector3 forward = i == 0 ? path[1] - path[0]
                : i == m - 1 ? path[m - 1] - path[m - 2]
                : path[i + 1] - path[i - 1];
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X);

            for (int k = 0; k < ring; k++)
            {
                var (px, py) = BoreProfile[k];
                vertices.Add(p + side * (px * halfWidth) + new Vector3(0, py * height, 0));
                // crown darker than the walls so the bore reads as depth, not a flat band
                float shade = Mathf.Lerp(0.34f, 0.16f, py);
                colors.Add(new Color(shade, shade * 0.97f, shade * 0.92f).SrgbToLinear());
                uvs.Add(Vector2.Zero);
                uv2s.Add(Vector2.Zero);
            }
        }

        for (int i = 0; i < m - 1; i++)
            for (int k = 0; k < ring - 1; k++)
            {
                int a = baseIndex + i * ring + k;
                int b = a + 1;
                int c = a + ring;
                int d = c + 1;
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }

        // the floor from wall to wall, just under the road ribbon: the ground is the bore's roof
        // (#119), so nothing else is under it, and the sky showed beside the road
        var floorColor = new Color(0.30f, 0.29f, 0.28f).SrgbToLinear();
        int floorBase = vertices.Count;
        for (int i = 0; i < m; i++)
        {
            int a = baseIndex + i * ring;
            // 10 cm on under each wall, or a hairline of sky shows along its foot
            var across = (vertices[a + ring - 1] - vertices[a]) with { Y = 0 };
            across = across.LengthSquared() > 1e-8f ? across.Normalized() * 0.1f : Vector3.Zero;
            vertices.Add(vertices[a] - across + new Vector3(0, -0.03f, 0));
            vertices.Add(vertices[a + ring - 1] + across + new Vector3(0, -0.03f, 0));
            for (int k = 0; k < 2; k++) { colors.Add(floorColor); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        }
        for (int i = 0; i < m - 1; i++)
        {
            int a = floorBase + i * 2;
            indices.Add(a); indices.Add(a + 2); indices.Add(a + 1);
            indices.Add(a + 1); indices.Add(a + 2); indices.Add(a + 3);
        }
    }

    private static Vector3 Point(RoadSegment s, int i) =>
        new(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);

    /// <summary>
    /// PS1-palette colour per road kind. Surface (paved vs natural) dominates, because
    /// that is what actually reads at a distance; class then shifts the tone.
    /// </summary>
    public static Color ColorFor(RoadSegment seg)
    {
        if (seg.Class == RoadClass.Railway && seg.Attributes.Has(RoadAttrFlags.PavedBed))
            return new Color(0.40f, 0.40f, 0.38f);  // a tram track laid in paving (#119)
        if (seg.Class == RoadClass.Railway)
            return (seg.Flags & RoadFlags.Funicular) != 0
                ? new Color(0.34f, 0.32f, 0.30f)   // concrete funicular bed
                : new Color(0.36f, 0.32f, 0.29f);  // crushed-stone ballast

        if ((seg.Flags & RoadFlags.Stairs) != 0)
            return new Color(0.55f, 0.52f, 0.48f);

        bool natural = seg.Surface == RoadSurface.Natural;

        // hiking trails read as trodden earth whatever their nominal surface
        if ((seg.Flags & (RoadFlags.Hiking | RoadFlags.MountainHiking)) != 0 && seg.Class >= RoadClass.Track)
            return natural ? new Color(0.60f, 0.50f, 0.36f) : new Color(0.55f, 0.50f, 0.43f);

        if (natural)
            return seg.Class switch
            {
                RoadClass.Path => new Color(0.62f, 0.53f, 0.39f),   // pale trodden dirt
                RoadClass.Track => new Color(0.52f, 0.44f, 0.32f),  // gravel/farm track
                _ => new Color(0.46f, 0.40f, 0.31f),                // graded dirt road
            };

        // neutral-to-warm greys; a blue bias here reads as purple once dithered
        return seg.Class switch
        {
            RoadClass.Motorway or RoadClass.Expressway => new Color(0.26f, 0.26f, 0.25f),
            RoadClass.Ramp or RoadClass.Major => new Color(0.30f, 0.30f, 0.29f),
            RoadClass.Road or RoadClass.Minor => new Color(0.34f, 0.34f, 0.33f),
            RoadClass.Path => new Color(0.46f, 0.45f, 0.43f),
            _ => new Color(0.37f, 0.37f, 0.36f),
        };
    }
}
