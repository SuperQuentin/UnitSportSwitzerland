using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Turns building triangle soups into a single mesh per tile, colouring each triangle by
/// building kind and whether the face is roof or wall. Worker-thread safe: produces plain
/// arrays only.
///
/// <para>
/// Detected <see cref="BuildingTypes"/> override the per-solid kind: every solid of a church is
/// dressed as one, its nave with a single row of tall windows and its tower bare stone under a
/// slate spire, whatever the cadastre match made each of them.
/// </para>
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>
    /// <see cref="Frames"/> is CUSTOM0, four floats a vertex: the wall's horizontal tangent (x, z),
    /// the storey height in metres, and 0. The facade shader needs the wall's own axes to cast
    /// the fake rooms behind the windows, and the screen-space normal is too jittery for that.
    /// </summary>
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, Vector2[] Uvs, Vector2[] Uv2s, float[] Frames);

    /// <summary>
    /// UV2.y flags read by <c>ps1_building.gdshader</c>: 0 plain, <see cref="SignFlag"/> a garage
    /// sign's light face (lit at night), <see cref="GarageWallFlag"/> a garage wall, whose UV is
    /// (metres along, metres above the base) and UV2.x the eave height the painted stripe runs under,
    /// <see cref="BlankWallFlag"/> a wall run too short for a window (bands and plinth only, #742).
    /// </summary>
    public const float SignFlag = 1f, GarageWallFlag = 2f, BlankWallFlag = 3f;

    /// <summary>
    /// The building mesh plus a front door on each building. The doors are a few boxes appended
    /// after the facades, with no windows (uv.y &lt; 0), so a tile's buildings and all their doors
    /// stay one surface and one draw call.
    /// </summary>
    /// <param name="detail">The visual style's mesh detail; only <see cref="Styles.MeshDetail.Low"/> exists so far.</param>
    public static MeshData? Build(BuildingTile tile, Interiors.DoorSpot[]? doors,
        Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
        Core.ShowcaseTrace.Mark();
        var data = Build(tile);
        if (data == null || doors == null || doors.Length == 0) return data;

        var v = new List<Vector3>(doors.Length * 120);
        var c = new List<Color>(doors.Length * 120);
        var f = new List<float>(doors.Length * 120); // UV2.y flag per vertex
        var types = BuildingTypes.For(tile);
        foreach (var d in doors)
            if (d.Width > 0) AppendDoor(v, c, f, d, KindOf(tile.Buildings[d.Index], types.TypeOf(d.Index)));

        int n = data.Vertices.Length;
        var vertices = new Vector3[n + v.Count];
        var colors = new Color[n + v.Count];
        var uvs = new Vector2[n + v.Count];
        var uv2s = new Vector2[n + v.Count];
        var frames = new float[(n + v.Count) * 4];
        Array.Copy(data.Vertices, vertices, n);
        Array.Copy(data.Colors, colors, n);
        Array.Copy(data.Uvs, uvs, n);
        Array.Copy(data.Uv2s, uv2s, n);
        Array.Copy(data.Frames, frames, n * 4);
        for (int i = 0; i < v.Count; i++)
        {
            vertices[n + i] = v[i];
            colors[n + i] = c[i];
            uvs[n + i] = new Vector2(0f, -1f);
            uv2s[n + i] = new Vector2(0f, f[i]);
        }
        return new MeshData(vertices, colors, uvs, uv2s, frames);
    }

    /// <summary>
    /// Frame, leaf and a doorstep, in the door's own frame (along the wall, out, up). A garage's
    /// leaf is its shut roll-up door (the live one is <see cref="Interiors.DoorLeaf.CreateRollUp"/>),
    /// its step is flush with the ground a car drives over, and a sign hangs over the opening: a
    /// coloured board with a light face.
    /// </summary>
    private static void AppendDoor(List<Vector3> v, List<Color> c, List<float> f, Interiors.DoorSpot d, BuildingKind kind)
    {
        // the leaf follows the door's own hang (#498): a plain leaf even on a barn or a garage
        var hang = d.Hang;
        var o = d.Outward;
        var t = new Vector3(-o.Z, 0, o.X);
        var at = d.Position;
        float hw = d.Width / 2, h = d.Height;

        Vector3 P(float along, float up, float out_) => at + t * along + Vector3.Up * up + o * out_;
        void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 dd, Color col)
        {
            // both windings: the building shader culls back faces and a door is seen from outside
            v.Add(a); v.Add(b); v.Add(cc); v.Add(a); v.Add(cc); v.Add(dd);
            v.Add(a); v.Add(cc); v.Add(b); v.Add(a); v.Add(dd); v.Add(cc);
            for (int i = 0; i < 12; i++) c.Add(col);
            for (int i = 0; i < 12; i++) f.Add(0f);
        }
        void Box(float a0, float a1, float u0, float u1, float o0, float o1, Color col)
        {
            var side = col * 0.85f;
            Quad(P(a0, u0, o1), P(a1, u0, o1), P(a1, u1, o1), P(a0, u1, o1), col);   // face
            Quad(P(a0, u1, o0), P(a1, u1, o0), P(a1, u1, o1), P(a0, u1, o1), col);   // top
            Quad(P(a0, u0, o0), P(a0, u1, o0), P(a0, u1, o1), P(a0, u0, o1), side);  // ends
            Quad(P(a1, u0, o0), P(a1, u0, o1), P(a1, u1, o1), P(a1, u1, o0), side);
            Quad(P(a0, u0, o0), P(a1, u0, o0), P(a1, u0, o1), P(a0, u0, o1), side * 0.8f);
        }

        var frame = BuildingFootprint.DoorFrameColor;
        var leaf = BuildingFootprint.DoorLeafColorFor(kind);
        var step = new Color(0.62f, 0.61f, 0.58f).SrgbToLinear();

        Box(-hw - 0.12f, -hw, 0, h + 0.12f, 0, 0.08f, frame);
        Box(hw, hw + 0.12f, 0, h + 0.12f, 0, 0.08f, frame);
        Box(-hw - 0.12f, hw + 0.12f, h, h + 0.12f, 0, 0.08f, frame);
        if (Interiors.DoorLeaf.RollsUp(hang))
        {
            // The shut roll-up door: slats in the leaf's own plane (6 cm out of the facade), the
            // one the building shader drops at every height while the door's portal shows.
            var metal = new Color(0.80f, 0.82f, 0.85f).SrgbToLinear();
            int slats = Mathf.Max(4, Mathf.RoundToInt(h / 0.22f));
            for (int i = 0; i < slats; i++)
            {
                float y1 = h - i * h / slats, y0 = y1 - h / slats, mid = y1 - h / slats * 0.35f;
                Quad(P(-hw, mid, 0.03f), P(hw, mid, 0.03f), P(hw, y1, 0.03f), P(-hw, y1, 0.03f), metal);
                Quad(P(-hw, y0, 0.03f), P(hw, y0, 0.03f), P(hw, mid, 0.03f), P(-hw, mid, 0.03f), metal * 0.8f);
            }
            // The sign: a workshop-blue board with a light face the shader lights at night. Only
            // over the MAIN door — a works has one name over its entrance, not one over every
            // loading bay (#528). A row of four lit shop signs along a warehouse wall read as a
            // parade of garages.
            if (d.Slot == 0)
            {
                Box(-hw - 0.35f, hw + 0.35f, h + 0.2f, h + 0.85f, 0, 0.12f, new Color(0.16f, 0.30f, 0.58f).SrgbToLinear());
                int start = f.Count;
                Quad(P(-hw - 0.22f, h + 0.3f, 0.13f), P(hw + 0.22f, h + 0.3f, 0.13f),
                    P(hw + 0.22f, h + 0.75f, 0.13f), P(-hw - 0.22f, h + 0.75f, 0.13f), Colors.White);
                for (int i = start; i < f.Count; i++) f[i] = SignFlag;
            }
            else
            {
                // a bay gets a painted lintel band instead, which is what numbers them in a real yard
                Box(-hw - 0.2f, hw + 0.2f, h + 0.12f, h + 0.42f, 0, 0.1f,
                    new Color(0.80f, 0.68f, 0.16f).SrgbToLinear());
            }
            // flush with the ground, or a car would hit a kerb
            Box(-hw - 0.2f, hw + 0.2f, -0.3f, 0.01f, 0, 0.45f, step);
            // a garage door of a block of flats reaches the road in front of it (#558)
            if (d.Link.Any) AppendLink(v, c, f, d);
            return;
        }
        Quad(P(-hw, 0, 0.03f), P(hw, 0, 0.03f), P(hw, h, 0.03f), P(-hw, h, 0.03f), leaf);
        if (Interiors.DoorLeaf.SwingsOut(hang))
        {
            // a pair (DoorLeaf.CreateOutward): the seam where they meet, a handle each beside it
            Quad(P(-0.015f, 0, 0.035f), P(0.015f, 0, 0.035f), P(0.015f, h, 0.035f), P(-0.015f, h, 0.035f), leaf * 0.6f);
            foreach (float px in new[] { -hw * 0.15f, hw * 0.15f })
                Box(px - 0.04f, px + 0.04f, 1.0f, 1.08f, 0.03f, 0.08f, BuildingFootprint.DoorHandleColor);
        }
        else
        {
            // handle, on the free edge: the doorway frame's X is -t, and DoorLeaf hinges on its -X jamb
            float hx = -hw * 0.7f;
            Box(hx - 0.04f, hx + 0.04f, 1.0f, 1.08f, 0.03f, 0.08f, BuildingFootprint.DoorHandleColor);
        }
        // a doorstep: the cue that says "this is a way in" from across the street
        Box(-hw - 0.2f, hw + 0.2f, -0.3f, 0.12f, 0, 0.45f, step);
    }

    /// <summary>
    /// Whether a solid is drawn and collided as itself. A building site's (#608) is not: its
    /// surveyed volume is replaced by the half-built shell <c>Construction.SiteShellBuilder</c> draws.
    /// </summary>
    public static bool Drawn(Building b) => b.Kind != BuildingKind.UnderConstruction;

    public static MeshData? Build(BuildingTile tile)
    {
        int triangles = 0;
        foreach (var b in tile.Buildings) if (Drawn(b)) triangles += b.TriangleCount;
        if (triangles == 0) return null;

        var types = BuildingTypes.For(tile);
        var vertices = new Vector3[triangles * 3];
        var colors = new Color[triangles * 3];
        // uv.x = metres along the facade, uv.y = storey coordinate (<0 disables windows)
        var uvs = new Vector2[triangles * 3];
        // uv2.x = number of whole storeys in the wall, so the shader can stop the window
        // grid at the wall plate instead of letting the roof slice the top row
        var uv2s = new Vector2[triangles * 3];
        var frames = new float[triangles * 3 * 4];
        // each pitched roof plane's lowest and highest point up its slope (eave and ridge, metres)
        var roofSpans = new Dictionary<(int, int, int, int), (float Min, float Max)>();
        var runs = new List<WallRun>();
        int v = 0;

        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            if (!Drawn(b)) continue;
            var part = types.PartOf(bi);
            var type = types.TypeOf(bi);
            var kind = KindOf(b, type);
            var wall = WallColor(kind, b.YearBuilt, type).SrgbToLinear();
            var roof = RoofColor(kind, type).SrgbToLinear();
            var (storey, storeyCount) = part switch
            {
                // one tall storey: the shader's window row becomes a church window
                BuildingPart.Nave => (Math.Max(3f, (types.Boxes[bi]?.Eave ?? b.MaxY) - b.MinY), 1),
                // a tower's few openings are not a grid of flats
                BuildingPart.Tower => (0f, 0),
                // a big-box store has no windows at all, whatever kind the cadastre calls it: a
                // grid of flats painted across 200 m of blue sheet is the one thing that would
                // stop it reading as an IKEA (#501)
                _ when type == BuildingType.Ikea => (0f, 0),
                _ => Storeys(b),
            };
            // a spire's faces are steep enough to count as wall; above the eave they are roof
            float spireFrom = part == BuildingPart.Tower ? (types.Boxes[bi]?.Eave ?? b.MaxY) + 0.3f : float.MaxValue;
            var uv2 = new Vector2(storeyCount, 0f);
            if (b.Kind == BuildingKind.Garage)
            {
                // the painted stripe runs under the eave; a flat-roofed garage has its eave near
                // the top, a pitched one lower down (same 0.78 split Storeys uses)
                float wallHeight = b.MaxY - b.MinY;
                uv2 = new Vector2(Mathf.Max(wallHeight * 0.78f, Mathf.Min(wallHeight - 0.1f, 4f)), GarageWallFlag);
                storey = 1f; // facade v in metres
            }
            // the window grid's frame on each wall triangle: its wall run's outward axis, and
            // the column grid fitted to the run's ends
            if (storey > 0f) WallRuns(b, spireFrom, runs);

            // first pass: where each pitched roof plane starts and ends up its slope, so every
            // triangle of it measures from the same eave
            roofSpans.Clear();
            // and the building's long axis (its longest level wall edge) and how much flat roof
            // it has: a large one carries solar panels in rows along that axis
            var axis = Vector3.Right;
            float axisLen = 0f, flatArea = 0f;
            for (int t = 0; t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);
                if (!IsRoof(a, c, d, spireFrom))
                {
                    for (int k = 0; k < 3; k++)
                    {
                        var e = (k == 0 ? c - a : k == 1 ? d - c : a - d);
                        float l = new Vector2(e.X, e.Z).Length();
                        if (l > axisLen && Mathf.Abs(e.Y) < 0.2f * l)
                        {
                            axisLen = l;
                            axis = new Vector3(e.X, 0f, e.Z) / l;
                        }
                    }
                    continue;
                }
                if (FlatRoof(a, c, d, b.MinY)) flatArea += 0.5f * (c - a).Cross(d - a).Length();
                if (!PitchedRoof(a, c, d, out var up, out var key)) continue;
                float s0 = Mathf.Min(a.Dot(up), Mathf.Min(c.Dot(up), d.Dot(up)));
                float s1 = Mathf.Max(a.Dot(up), Mathf.Max(c.Dot(up), d.Dot(up)));
                roofSpans[key] = roofSpans.TryGetValue(key, out var span)
                    ? (Mathf.Min(span.Min, s0), Mathf.Max(span.Max, s1)) : (s0, s1);
            }
            // the flat roof's extent in the axis frame (along, across); most large flat roofs
            // have panels, a church never
            var across = new Vector3(-axis.Z, 0f, axis.X);
            bool solar = flatArea >= SolarRoofArea && part == BuildingPart.None
                && (uint)(bi * 2654435761u + tile.Buildings.Count) % 10 < 7;
            Vector2 flatMin = new(float.MaxValue, float.MaxValue), flatMax = new(float.MinValue, float.MinValue);
            for (int t = 0; solar && t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);
                if (!IsRoof(a, c, d, spireFrom) || !FlatRoof(a, c, d, b.MinY)) continue;
                for (int k = 0; k < 3; k++)
                {
                    var p = k == 0 ? a : k == 1 ? c : d;
                    var q = new Vector2(p.Dot(axis), p.Dot(across));
                    flatMin = flatMin.Min(q);
                    flatMax = flatMax.Max(q);
                }
            }

            for (int t = 0; t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);

                var normal = (c - a).Cross(d - a);
                bool isRoof = IsRoof(a, c, d, spireFrom);
                var color = isRoof ? roof : wall;

                // A pitched roof's own frame (#683): CUSTOM0 = the unit up-slope vector and 1,
                // UV = (metres along the eave, -1: still no windows), UV2 = (metres up the slope
                // from the eave, the eave-to-ridge length), for the shader's tile courses.
                if (isRoof && PitchedRoof(a, c, d, out var upSlope, out var plane))
                {
                    var (eave, ridge) = roofSpans[plane];
                    var alongEave = new Vector3(-upSlope.Z, 0f, upSlope.X).Normalized();
                    for (int k = 0; k < 3; k++)
                    {
                        var p = k == 0 ? a : k == 1 ? c : d;
                        int f = v * 4;
                        frames[f] = upSlope.X;
                        frames[f + 1] = upSlope.Y;
                        frames[f + 2] = upSlope.Z;
                        frames[f + 3] = 1f;
                        vertices[v] = p; colors[v] = color;
                        uvs[v] = new Vector2(p.Dot(alongEave), -1f);
                        uv2s[v++] = new Vector2(p.Dot(upSlope) - eave, ridge - eave);
                    }
                    continue;
                }

                // A large flat roof's solar field: CUSTOM0 = (its extent along the axis, across
                // it, the across direction's angle atan2(z, x), 2), UV = (metres along from its edge,
                // -1), UV2 = (metres across from its edge, 0). The panels are the shader's.
                if (solar && isRoof && FlatRoof(a, c, d, b.MinY))
                {
                    var extent = flatMax - flatMin;
                    for (int k = 0; k < 3; k++)
                    {
                        var p = k == 0 ? a : k == 1 ? c : d;
                        int f = v * 4;
                        frames[f] = extent.X;
                        frames[f + 1] = extent.Y;
                        frames[f + 2] = Mathf.Atan2(across.Z, across.X);
                        frames[f + 3] = 2f;
                        vertices[v] = p; colors[v] = color;
                        uvs[v] = new Vector2(p.Dot(axis) - flatMin.X, -1f);
                        uv2s[v++] = new Vector2(p.Dot(across) - flatMin.Y, 0f);
                    }
                    continue;
                }

                // Facade coordinates are baked here rather than derived in the shader:
                // the fragment normal comes from screen-space derivatives and jitters,
                // which turned the window grid into speckle. Every triangle of a wall run
                // shares the run's axis and grid, so u stays continuous across the wall and
                // whatever way the TIN wound each triangle (#742).
                Vector2 uvA, uvB, uvC;
                var tangent = Vector3.Zero;
                var triUv2 = uv2;
                if (isRoof || storey <= 0f)
                {
                    uvA = uvB = uvC = new Vector2(0f, -1f);
                }
                else
                {
                    var run = runs[t];
                    tangent = run.Tangent;
                    uvA = FacadeUv(a, run, b.MinY, storey);
                    uvB = FacadeUv(c, run, b.MinY, storey);
                    uvC = FacadeUv(d, run, b.MinY, storey);
                    // too short for one window: the wall's bands and plinth, no glass
                    if (run.Scale == 0f && uv2.Y == 0f) triUv2 = new Vector2(uv2.X, BlankWallFlag);
                }
                for (int k = 0; k < 3; k++)
                {
                    int f = (v + k) * 4;
                    frames[f] = tangent.X;
                    frames[f + 1] = tangent.Z;
                    frames[f + 2] = storey;
                }

                vertices[v] = a; colors[v] = color; uvs[v] = uvA; uv2s[v++] = triUv2;
                vertices[v] = c; colors[v] = color; uvs[v] = uvB; uv2s[v++] = triUv2;
                vertices[v] = d; colors[v] = color; uvs[v] = uvC; uv2s[v++] = triUv2;
            }
        }

        return new MeshData(vertices, colors, uvs, uv2s, frames);
    }

    // roof or wall by the triangle's slope; a spire's faces above the eave are roof however steep
    private static bool IsRoof(Vector3 a, Vector3 c, Vector3 d, float spireFrom)
    {
        var normal = (c - a).Cross(d - a);
        float len = normal.Length();
        return len > 1e-6f && Mathf.Abs(normal.Y / len) >= BuildingTriangles.RoofNormalY
            || (a.Y + c.Y + d.Y) / 3f > spireFrom;
    }

    /// <summary>
    /// A roof triangle that slopes (flat roofs, |normal.y| above 0.97, have no eave to measure
    /// from): its unit up-slope vector and its plane's key, the upward normal and the plane's
    /// offset rounded, so the triangles of one roof face group together. The source TINs wind
    /// faces either way, so the normal is turned to face up.
    /// </summary>
    private static bool PitchedRoof(Vector3 a, Vector3 c, Vector3 d, out Vector3 upSlope, out (int, int, int, int) key)
    {
        var n = (c - a).Cross(d - a);
        upSlope = Vector3.Zero;
        key = default;
        float len = n.Length();
        if (len < 1e-6f) return false;
        n /= len;
        if (n.Y < 0f) n = -n;
        if (n.Y > 0.97f) return false;
        upSlope = (Vector3.Up - n * n.Y).Normalized();
        key = ((int)MathF.Round(n.X * 50f), (int)MathF.Round(n.Y * 50f), (int)MathF.Round(n.Z * 50f),
            (int)MathF.Round(n.Dot(a) * 10f));
        return true;
    }

    /// <summary>Flat roof area (square metres) from which a building carries solar panels.</summary>
    public const float SolarRoofArea = 250f;

    // a level roof face; the solid's own floor (also level, and wound either way) is not one
    private static bool FlatRoof(Vector3 a, Vector3 c, Vector3 d, float baseY)
    {
        var n = (c - a).Cross(d - a);
        float len = n.Length();
        return len > 1e-6f && Mathf.Abs(n.Y / len) > 0.97f && (a.Y + c.Y + d.Y) / 3f > baseY + 2f;
    }

    // u: the run's grid, whole columns from end to end; v: the storey coordinate
    private static Vector2 FacadeUv(Vector3 p, WallRun run, float baseY, float storey) =>
        new((p.X * run.Tangent.X + p.Z * run.Tangent.Z - run.Start) * (run.Scale > 0f ? run.Scale : 1f),
            (p.Y - baseY) / storey);

    /// <summary>
    /// The window grid's column pitch in metres: <c>window_spacing</c> in the building shader.
    /// A wall run stretches it a little so whole columns fit it end to end.
    /// </summary>
    public const float WindowSpacing = 3.2f;

    /// <summary>
    /// A wall triangle's window frame: its run's outward-facing horizontal axis (u grows that
    /// way), where the run starts along it, and how much u is stretched so whole columns fit
    /// the run (0: too short for a window, u in plain metres).
    /// </summary>
    private readonly record struct WallRun(Vector3 Tangent, float Start, float Scale);

    /// <summary>
    /// Groups a building's wall triangles into wall runs and gives each triangle its run's frame
    /// in <paramref name="runs"/> (indexed by triangle). A run is the triangles that touch and
    /// face the same way within 3 degrees: the surveyed walls are not quite flat, so their
    /// facets differ by up to a degree or two, and comparing plane offsets would split a wall
    /// hundreds of metres from the tile's origin.
    ///
    /// The window grid used to be <c>dot(xz, tangent)</c> with the tangent from each triangle's
    /// own winding (#742): the columns were not anchored to the wall, so a window straddled its
    /// end or corner and was cut in half; each facet of a curved wall had its own axis and
    /// sliced the windows at every seam; and the two triangles of one wall wound either way
    /// mirrored u, so a window was lit or furnished differently on each side of the diagonal.
    /// </summary>
    private static void WallRuns(Building b, float spireFrom, List<WallRun> runs)
    {
        runs.Clear();
        int count = b.TriangleCount;
        var flats = new Vector2[count]; // horizontal unit normal, either way; zero off the walls
        var parent = new int[count];
        var touching = new Dictionary<(int, int, int), List<int>>();
        int Find(int t)
        {
            while (parent[t] != t) t = parent[t] = parent[parent[t]];
            return t;
        }

        float cx = 0f, cz = 0f; // the plan's centre
        for (int t = 0; t < count; t++)
        {
            runs.Add(new WallRun(Vector3.Right, 0f, 0f));
            parent[t] = t;
            var (a, c, d) = b.Tri(t);
            cx += a.X + c.X + d.X;
            cz += a.Z + c.Z + d.Z;
            if (IsRoof(a, c, d, spireFrom)) continue;
            var n = (c - a).Cross(d - a);
            var flat = new Vector2(n.X, n.Z);
            if (flat.LengthSquared() < 1e-10f) continue;
            flats[t] = flat = flat.Normalized();
            // the same wall: a corner it shares with a triangle facing the same way (a vertex,
            // not an edge, so a T-junction in the TIN still joins)
            for (int k = 0; k < 3; k++)
            {
                var p = k == 0 ? a : k == 1 ? c : d;
                var key = ((int)MathF.Round(p.X * 100f), (int)MathF.Round(p.Y * 100f), (int)MathF.Round(p.Z * 100f));
                if (!touching.TryGetValue(key, out var others)) touching[key] = others = new List<int>(4);
                foreach (int s in others)
                    if (Mathf.Abs(flats[s].Dot(flat)) > 0.9986f) parent[Find(s)] = Find(t);
                others.Add(t);
            }
        }
        cx /= count * 3;
        cz /= count * 3;

        // each run's facing (area-weighted, every facet turned the root's way) and centre
        var facing = new Dictionary<int, (Vector2 Normal, Vector2 Centre, float Area)>();
        for (int t = 0; t < count; t++)
        {
            if (flats[t] == Vector2.Zero) continue;
            var (a, c, d) = b.Tri(t);
            int r = Find(t);
            float area = (c - a).Cross(d - a).Length() + 1e-6f;
            var normal = flats[t].Dot(flats[r]) < 0f ? -flats[t] : flats[t];
            var centre = new Vector2(a.X + c.X + d.X, a.Z + c.Z + d.Z) / 3f;
            facing.TryGetValue(r, out var sum);
            facing[r] = (sum.Normal + normal * area, sum.Centre + centre * area, sum.Area + area);
        }
        // turned away from the plan's centre, so u runs the same way whatever the winding
        var axes = new Dictionary<int, (Vector3 Tangent, float U0, float U1)>(facing.Count);
        foreach (var (r, (sum, weighted, area)) in facing)
        {
            var o = sum.LengthSquared() > 1e-12f ? sum.Normalized() : flats[r];
            var centre = weighted / area;
            float side = o.X * (centre.X - cx) + o.Y * (centre.Y - cz);
            if (side < -0.05f || (Mathf.Abs(side) <= 0.05f && (o.X < 0f || (o.X == 0f && o.Y < 0f)))) o = -o;
            axes[r] = (new Vector3(-o.Y, 0f, o.X), float.MaxValue, float.MinValue);
        }
        // each run's span along its axis
        for (int t = 0; t < count; t++)
        {
            if (flats[t] == Vector2.Zero) continue;
            var (a, c, d) = b.Tri(t);
            int r = Find(t);
            var (tangent, u0, u1) = axes[r];
            for (int k = 0; k < 3; k++)
            {
                var p = k == 0 ? a : k == 1 ? c : d;
                float u = p.X * tangent.X + p.Z * tangent.Z;
                u0 = Mathf.Min(u0, u);
                u1 = Mathf.Max(u1, u);
            }
            axes[r] = (tangent, u0, u1);
        }
        // whole columns from end to end: the pitch stretched to fit, or none on a sliver
        for (int t = 0; t < count; t++)
        {
            if (flats[t] == Vector2.Zero) continue;
            var (tangent, u0, u1) = axes[Find(t)];
            float len = u1 - u0;
            int columns = Mathf.RoundToInt(len / WindowSpacing);
            runs[t] = new WallRun(tangent, u0, columns > 0 ? columns * WindowSpacing / len : 0f);
        }
    }

    /// <summary>Flat triangle list for ConcavePolygonShape3D.</summary>
    public static Vector3[] BuildCollisionFaces(BuildingTile tile)
    {
        int triangles = 0;
        foreach (var b in tile.Buildings) if (Drawn(b)) triangles += b.TriangleCount;
        var faces = new Vector3[triangles * 3];
        int v = 0;
        foreach (var b in tile.Buildings)
            for (int t = 0; Drawn(b) && t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);
                faces[v++] = a; faces[v++] = c; faces[v++] = d;
            }
        return faces;
    }

    /// <summary>
    /// Storey height and whole-storey count for the window grid.
    ///
    /// The height is chosen so the storeys divide the usable wall *exactly*: with an
    /// arbitrary height the top row lands part-way into the eave and the roof slices it,
    /// which is what made windows look cropped. GWR supplies the floor count for about
    /// 69% of buildings; otherwise it is inferred from a typical 2.9 m storey.
    /// Kinds that genuinely have few windows (barns, garages, tanks) opt out with 0.
    /// </summary>
    public static (float Height, int Count) Storeys(Building b)
    {
        if (b.Kind is BuildingKind.Annex or BuildingKind.Garage or BuildingKind.Agricultural
            or BuildingKind.Industrial or BuildingKind.UnderConstruction)
            return (0f, 0);

        float wallHeight = b.MaxY - b.MinY;
        if (wallHeight < 3f) return (0f, 0); // too small to read as a facade

        // the pitched roof occupies the upper part of the solid
        float usable = wallHeight * 0.78f;

        int count = b.Floors > 0
            ? b.Floors
            : Mathf.Max(1, Mathf.RoundToInt(usable / 2.9f));

        float height = usable / count;
        // an implausible floor count (GWR counts basements on some records) would give
        // absurd bands, so fall back to a sane storey and recompute the count
        if (height < 2.2f || height > 4.5f)
        {
            count = Mathf.Max(1, Mathf.RoundToInt(usable / 2.9f));
            height = usable / count;
        }
        return (height, count);
    }

    /// <summary>The kind a building is dressed as: a church's every solid as a church.</summary>
    private static BuildingKind KindOf(Building b, BuildingType type) =>
        type == BuildingType.Church ? BuildingKind.Sacral : b.Kind;

    private static Color WallColor(BuildingKind kind, ushort year, BuildingType type = BuildingType.None)
    {
        // a brand paints its own box, and does not weather: ApplyAge on IKEA blue would make a
        // 1973 store a different colour from a 2006 one, and they are the same blue (#501)
        if (type == BuildingType.Ikea) return IkeaBlue;

        var baseColor = kind switch
        {
            BuildingKind.House => new Color(0.82f, 0.76f, 0.65f),        // rendered cream
            BuildingKind.Apartment => new Color(0.75f, 0.72f, 0.67f),
            BuildingKind.Commercial => new Color(0.78f, 0.78f, 0.76f),
            BuildingKind.Industrial => new Color(0.66f, 0.67f, 0.68f),   // sheet metal
            BuildingKind.Agricultural => new Color(0.52f, 0.42f, 0.31f), // dark timber
            BuildingKind.Sacral => new Color(0.88f, 0.86f, 0.80f),       // pale stone
            BuildingKind.Civic => new Color(0.80f, 0.79f, 0.75f),
            BuildingKind.Annex => new Color(0.62f, 0.59f, 0.54f),
            BuildingKind.Garage => new Color(0.66f, 0.66f, 0.64f),        // sheet-metal workshop
            BuildingKind.UnderConstruction => new Color(0.70f, 0.69f, 0.66f),
            _ => new Color(0.72f, 0.70f, 0.66f),
        };
        return ApplyAge(baseColor, year);
    }

    /// <summary>IKEA blue, Pantone 294 C (#0051BA): what makes the box recognisable (#501).</summary>
    public static readonly Color IkeaBlue = new(0.00f, 0.32f, 0.73f);

    private static Color RoofColor(BuildingKind kind, BuildingType type = BuildingType.None) => type == BuildingType.Ikea
        // plant and ducts on a grey membrane, the way it looks from the motorway bridge
        ? new Color(0.44f, 0.45f, 0.46f)
        : kind switch
    {
        BuildingKind.Agricultural => new Color(0.42f, 0.36f, 0.30f),
        BuildingKind.Industrial => new Color(0.46f, 0.48f, 0.49f),
        BuildingKind.Sacral or BuildingKind.Civic => new Color(0.35f, 0.33f, 0.34f), // slate
        BuildingKind.Annex => new Color(0.44f, 0.40f, 0.36f),
        BuildingKind.Garage => new Color(0.46f, 0.47f, 0.48f),
        BuildingKind.UnderConstruction => new Color(0.60f, 0.59f, 0.57f),
        _ => new Color(0.50f, 0.30f, 0.23f), // the usual Swiss reddish-brown tile
    };

    /// <summary>
    /// Nudges older buildings warmer and darker so a village reads as varied rather than
    /// uniform. Year is only known for about a third of buildings, so an unknown year
    /// must leave the colour untouched rather than defaulting to "old".
    /// </summary>
    private static Color ApplyAge(Color c, ushort year)
    {
        if (year == 0) return c;
        // 1900 and earlier = fully weathered, 2000+ = as-built
        float t = Mathf.Clamp((year - 1900) / 100f, 0f, 1f);
        float darken = Mathf.Lerp(0.82f, 1.0f, t);
        float warm = Mathf.Lerp(1.06f, 1.0f, t);
        return new Color(
            Mathf.Min(c.R * darken * warm, 1f),
            c.G * darken,
            Mathf.Min(c.B * darken / warm, 1f));
    }
}
