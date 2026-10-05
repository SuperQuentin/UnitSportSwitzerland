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
public static class BuildingMeshBuilder
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
    /// (metres along, metres above the base) and UV2.x the eave height the painted stripe runs under.
    /// </summary>
    public const float SignFlag = 1f, GarageWallFlag = 2f;

    /// <summary>
    /// The building mesh plus a front door on each building. The doors are a few boxes appended
    /// after the facades, with no windows (uv.y &lt; 0), so a tile's buildings and all their doors
    /// stay one surface and one draw call.
    /// </summary>
    /// <param name="detail">The visual style's mesh detail; only <see cref="Styles.MeshDetail.Low"/> exists so far.</param>
    public static MeshData? Build(BuildingTile tile, Interiors.DoorSpot[]? doors,
        Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
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

    public static MeshData? Build(BuildingTile tile)
    {
        int triangles = 0;
        foreach (var b in tile.Buildings) triangles += b.TriangleCount;
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
        int v = 0;

        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            var part = types.PartOf(bi);
            var kind = KindOf(b, types.TypeOf(bi));
            var wall = WallColor(kind, b.YearBuilt).SrgbToLinear();
            var roof = RoofColor(kind).SrgbToLinear();
            var (storey, storeyCount) = part switch
            {
                // one tall storey: the shader's window row becomes a church window
                BuildingPart.Nave => (Math.Max(3f, (types.Boxes[bi]?.Eave ?? b.MaxY) - b.MinY), 1),
                // a tower's few openings are not a grid of flats
                BuildingPart.Tower => (0f, 0),
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

            for (int t = 0; t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);

                var normal = (c - a).Cross(d - a);
                float len = normal.Length();
                bool isRoof = len > 1e-6f && Mathf.Abs(normal.Y / len) >= BuildingTriangles.RoofNormalY
                    || (a.Y + c.Y + d.Y) / 3f > spireFrom;
                var color = isRoof ? roof : wall;

                // Facade coordinates are baked here rather than derived in the shader:
                // the fragment normal comes from screen-space derivatives and jitters,
                // which turned the window grid into speckle. The triangle normal is exact
                // and shared by coplanar faces, so u stays continuous across a wall.
                Vector2 uvA, uvB, uvC;
                var tangent = Vector3.Zero;
                if (isRoof || storey <= 0f)
                {
                    uvA = uvB = uvC = new Vector2(0f, -1f);
                }
                else
                {
                    var flat = new Vector3(normal.X, 0f, normal.Z);
                    tangent = flat.LengthSquared() > 1e-8f
                        ? new Vector3(-flat.Z, 0f, flat.X).Normalized()
                        : Vector3.Right;
                    uvA = FacadeUv(a, tangent, b.MinY, storey);
                    uvB = FacadeUv(c, tangent, b.MinY, storey);
                    uvC = FacadeUv(d, tangent, b.MinY, storey);
                }
                for (int k = 0; k < 3; k++)
                {
                    int f = (v + k) * 4;
                    frames[f] = tangent.X;
                    frames[f + 1] = tangent.Z;
                    frames[f + 2] = storey;
                }

                vertices[v] = a; colors[v] = color; uvs[v] = uvA; uv2s[v++] = uv2;
                vertices[v] = c; colors[v] = color; uvs[v] = uvB; uv2s[v++] = uv2;
                vertices[v] = d; colors[v] = color; uvs[v] = uvC; uv2s[v++] = uv2;
            }
        }

        return new MeshData(vertices, colors, uvs, uv2s, frames);
    }

    private static Vector2 FacadeUv(Vector3 p, Vector3 tangent, float baseY, float storey) =>
        new(p.X * tangent.X + p.Z * tangent.Z, (p.Y - baseY) / storey);

    /// <summary>Flat triangle list for ConcavePolygonShape3D.</summary>
    public static Vector3[] BuildCollisionFaces(BuildingTile tile)
    {
        int triangles = 0;
        foreach (var b in tile.Buildings) triangles += b.TriangleCount;
        var faces = new Vector3[triangles * 3];
        int v = 0;
        foreach (var b in tile.Buildings)
            for (int t = 0; t < b.TriangleCount; t++)
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

    private static Color WallColor(BuildingKind kind, ushort year)
    {
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

    private static Color RoofColor(BuildingKind kind) => kind switch
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
