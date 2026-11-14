using Godot;

namespace UnitSport.Avatar;

public static partial class HumanMeshBuilder
{
    private static readonly Color HelmetShell = new(0.93f, 0.94f, 0.95f), HelmetVisor = new(0.78f, 0.58f, 0.16f),
        HelmetRing = new(0.55f, 0.58f, 0.62f), HelmetLamp = new(0.98f, 0.96f, 0.85f);

    /// <summary>
    /// The suit's helmet (#1941, docs/plans/moon-surface.md section 6): a white shell round the whole head (the figure
    /// draws no face or hair under it), the gold visor over the front, the neck ring, two lamps at the temples. Worn
    /// outside a colony's pressurised modules on the Moon; off inside.
    /// </summary>
    private static void AppendSpaceHelmet(MeshScratch s, Vector3 centre, Vector3 side, Vector3 up, Vector3 fwd, float k, Basis frame)
    {
        var c = centre + up * 0.015f * k;
        s.Tube(c - up * 0.16f * k, c - up * 0.06f * k, 0.13f * k, 0.165f * k, HelmetShell, 12);
        s.Tube(c - up * 0.06f * k, c + up * 0.08f * k, 0.165f * k, 0.15f * k, HelmetShell, 12);
        s.Tube(c + up * 0.08f * k, c + up * 0.16f * k, 0.15f * k, 0.07f * k, HelmetShell, 12);
        // the visor: a gold plate curving over the face
        s.Box(c + (fwd * 0.148f + up * 0.01f) * k, new Vector3(0.2f, 0.15f, 0.03f) * k, HelmetVisor, frame);
        s.Box(c + (fwd * 0.12f + up * 0.01f + side * 0.1f) * k, new Vector3(0.06f, 0.13f, 0.06f) * k, HelmetVisor, frame * new Basis(Vector3.Up, 0.6f));
        s.Box(c + (fwd * 0.12f + up * 0.01f - side * 0.1f) * k, new Vector3(0.06f, 0.13f, 0.06f) * k, HelmetVisor, frame * new Basis(Vector3.Up, -0.6f));
        s.Tube(c - up * 0.19f * k, c - up * 0.15f * k, 0.14f * k, 0.14f * k, HelmetRing, 12);
        foreach (float sd in new[] { -1f, 1f })
            s.Box(c + (side * sd * 0.15f + up * 0.07f + fwd * 0.05f) * k, new Vector3(0.03f, 0.04f, 0.05f) * k, HelmetLamp, frame);
    }
}
