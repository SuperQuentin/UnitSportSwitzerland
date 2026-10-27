using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The navigation plan's models (#1226), each built once (<c>general/no-mesh-rebuild-per-frame</c>), vertex-coloured
/// boxes with the origin at the foot: the four travel signs (the SBB pole with its clock and blue board, the yellow
/// PostAuto stop sign with the horn, a lake landing's board, a ropeway station's board), each lit (unlocked) and dark
/// (locked), drawn as <see cref="MultiMesh"/> instances by <see cref="TravelSigns"/>; and the guide's ground chevron
/// (<see cref="GuideArrow"/>), amber with a dark outline so it reads on snow, grass and asphalt.
/// </summary>
public static class NavMeshBuilder
{
    private static readonly ArrayMesh?[] Signs = new ArrayMesh?[10];
    private static ArrayMesh? _arrow;

    private static readonly Color Grey = new(0.45f, 0.47f, 0.5f), White = new(0.96f, 0.96f, 0.95f), Black = new(0.06f, 0.06f, 0.07f),
        SbbBlue = new(0.07f, 0.22f, 0.55f), SbbRed = new(0.85f, 0.05f, 0.1f), PostYellow = new(0.99f, 0.8f, 0.0f),
        LakeBlue = new(0.08f, 0.32f, 0.6f), RopeRed = new(0.75f, 0.1f, 0.12f), Dark = new(0.16f, 0.17f, 0.19f),
        Amber = new(0.98f, 0.72f, 0.1f), Outline = new(0.08f, 0.06f, 0.05f);

    /// <summary>A travel sign of a kind, lit (an unlocked point) or dark.</summary>
    public static ArrayMesh Sign(TravelKind kind, bool lit) => Signs[(int)kind * 2 + (lit ? 1 : 0)] ??= kind switch
    {
        TravelKind.Bus => PostAuto(lit),
        TravelKind.Ship => Landing(lit),
        TravelKind.CableCar => Ropeway(lit),
        _ => Sbb(lit),
    };

    [Showcase("World", "Travel signs")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseSigns()
    {
        foreach (var kind in new[] { TravelKind.Rail, TravelKind.Bus, TravelKind.Ship, TravelKind.CableCar })
            foreach (bool lit in new[] { true, false })
                yield return ($"{TravelNetwork.LineName(kind)} sign {(lit ? "unlocked" : "locked")}", () => new MeshInstance3D
                {
                    Mesh = Sign(kind, lit),
                    MaterialOverride = PoiMarkerMeshBuilder.Material(),
                });
    }

    /// <summary>The SBB station pole: the clock on top, the blue name board, a red stripe when lit.</summary>
    private static ArrayMesh Sbb(bool lit) => PoiMarkerMeshBuilder.Build(st =>
    {
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 1.5f, 0), new Vector3(0.1f, 3.0f, 0.1f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 3.2f, 0), new Vector3(0.56f, 0.56f, 0.08f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 3.2f, 0.045f), new Vector3(0.48f, 0.48f, 0.01f), White);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 3.27f, 0.055f), new Vector3(0.03f, 0.16f, 0.008f), Black);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0.06f, 3.2f, 0.055f), new Vector3(0.14f, 0.025f, 0.008f), Black);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 3.33f, 0.062f), new Vector3(0.016f, 0.012f, 0.006f), SbbRed);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.45f, 0), new Vector3(1.2f, 0.36f, 0.06f), lit ? SbbBlue : Dark);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.45f, 0.035f), new Vector3(0.9f, 0.08f, 0.01f), lit ? White : Grey);
        if (lit) PoiMarkerMeshBuilder.Box(st, new Vector3(-0.52f, 2.45f, 0.035f), new Vector3(0.1f, 0.26f, 0.01f), SbbRed);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 0.03f, 0), new Vector3(0.4f, 0.06f, 0.4f), Grey);
    });

    /// <summary>The PostAuto stop: a yellow plate with the black horn, a "H" timetable box under it.</summary>
    private static ArrayMesh PostAuto(bool lit) => PoiMarkerMeshBuilder.Build(st =>
    {
        var plate = lit ? PostYellow : new Color(0.45f, 0.38f, 0.12f);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 1.3f, 0), new Vector3(0.08f, 2.6f, 0.08f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.4f, 0), new Vector3(0.6f, 0.45f, 0.05f), plate);
        // the horn: a bell and a looped tube, in black on both faces
        foreach (float z in new[] { 0.03f, -0.03f })
        {
            PoiMarkerMeshBuilder.Box(st, new Vector3(-0.12f, 2.4f, z), new Vector3(0.22f, 0.05f, 0.01f), Black);
            PoiMarkerMeshBuilder.Box(st, new Vector3(0.06f, 2.4f, z), new Vector3(0.06f, 0.16f, 0.01f), Black);
            PoiMarkerMeshBuilder.Box(st, new Vector3(0.14f, 2.4f, z), new Vector3(0.1f, 0.24f, 0.01f), Black);
        }
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 1.75f, 0.06f), new Vector3(0.36f, 0.5f, 0.04f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 0.03f, 0), new Vector3(0.3f, 0.06f, 0.3f), Grey);
    });

    /// <summary>A lake landing: two posts and a wide blue board with a white wave.</summary>
    private static ArrayMesh Landing(bool lit) => PoiMarkerMeshBuilder.Build(st =>
    {
        PoiMarkerMeshBuilder.Box(st, new Vector3(-0.7f, 1.1f, 0), new Vector3(0.1f, 2.2f, 0.1f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0.7f, 1.1f, 0), new Vector3(0.1f, 2.2f, 0.1f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.0f, 0), new Vector3(1.6f, 0.45f, 0.06f), lit ? LakeBlue : Dark);
        PoiMarkerMeshBuilder.Box(st, new Vector3(-0.2f, 1.93f, 0.035f), new Vector3(0.5f, 0.05f, 0.01f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0.3f, 1.98f, 0.035f), new Vector3(0.5f, 0.05f, 0.01f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 0.05f, 0), new Vector3(1.8f, 0.1f, 0.5f), new Color(0.42f, 0.3f, 0.18f));
    });

    /// <summary>A ropeway station: a post with a red board and a white cabin hanging from its rope.</summary>
    private static ArrayMesh Ropeway(bool lit) => PoiMarkerMeshBuilder.Build(st =>
    {
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 1.3f, 0), new Vector3(0.1f, 2.6f, 0.1f), Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.4f, 0), new Vector3(0.9f, 0.55f, 0.06f), lit ? RopeRed : Dark);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.6f, 0.035f), new Vector3(0.8f, 0.02f, 0.01f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.55f, 0.035f), new Vector3(0.02f, 0.08f, 0.01f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 2.38f, 0.035f), new Vector3(0.22f, 0.26f, 0.01f), lit ? White : Grey);
        PoiMarkerMeshBuilder.Box(st, new Vector3(0, 0.03f, 0), new Vector3(0.3f, 0.06f, 0.3f), Grey);
    });

    /// <summary>
    /// The guide's ground chevron: three arrows one behind the other pointing -Z (forward), 1.6 m long in all, a dark
    /// outline drawn 2 cm under a slightly smaller amber fill (no shared plane: no z-fight).
    /// </summary>
    [Showcase("Ui", "Guide arrow")]
    public static ArrayMesh GuideArrow() => _arrow ??= BuildArrow();

    private static ArrayMesh BuildArrow()
    {
        ShowcaseTrace.Mark();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 3; i++)
        {
            float z = -i * 0.55f;
            Chevron(st, z, 0.02f, 0.5f, 0.42f, 0.16f, Outline);
            Chevron(st, z - 0.02f, 0.04f, 0.44f, 0.36f, 0.1f, Amber);
        }
        return st.Commit();
    }

    /// <summary>A flat chevron at <paramref name="z"/> (its tip toward -Z), half-width <paramref name="w"/>, depth <paramref name="d"/>, arm thickness <paramref name="t"/>.</summary>
    private static void Chevron(SurfaceTool st, float z, float y, float w, float d, float t, Color c)
    {
        // two arms: tip (0, z - d) to the outer corners (±w, z); each a quad of thickness t along z
        void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 dd)
        {
            foreach (var p in new[] { a, b, cc, a, cc, dd })
            {
                st.SetColor(c);
                st.SetNormal(Vector3.Up);
                st.AddVertex(p);
            }
        }
        var tip = new Vector3(0, y, z - d);
        var tipIn = new Vector3(0, y, z - d + t * 1.6f);
        Quad(new Vector3(-w, y, z), tip, tipIn, new Vector3(-w, y, z + t));
        Quad(tip, new Vector3(w, y, z), new Vector3(w, y, z + t), tipIn);
    }
}
