using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The player's own figure seen from its own eye (#833): first person on foot, the head in a
/// cockpit, the whole driver in a bare cockpit. It is still the third-person figure, built and
/// posed, only drawn on <see cref="Layer"/>: the eye's cameras leave that layer out, the mirrors
/// keep it. Godot casts no shadow from a layer the camera culls, so a shadow-only twin of the
/// figure on the world's layer casts it, which no camera draws either.
/// </summary>
public static class OwnBody
{
    /// <summary>The layer of a figure seen from its own eye: the one the VR headset already leaves out.</summary>
    public const uint Layer = XR.XrSession.SpectatorOnlyLayer;

    /// <summary>Godot's default visual layer, where the world and every other figure are.</summary>
    public const uint World = 1u;

    /// <summary>
    /// Puts <paramref name="figure"/>, and what hangs on it, on <see cref="Layer"/> while the eye
    /// is in its head, else back on <see cref="World"/>; only a change walks the children. Its
    /// shadow twin follows the figure's mesh, so call it after the mesh is set for the frame.
    /// </summary>
    public static void See(MeshInstance3D figure, bool fromEye, ref MeshInstance3D? shadow)
    {
        uint layers = fromEye ? Layer : World;
        if (figure.Layers != layers) SetLayers(figure, layers);
        bool had = shadow != null && GodotObject.IsInstanceValid(shadow) && shadow.GetParent() == figure;
        if (!had && !fromEye) return;
        if (!had)
        {
            shadow = new MeshInstance3D
            {
                Name = "Shadow",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                MaterialOverride = figure.MaterialOverride,
            };
            figure.AddChild(shadow);
        }
        var twin = shadow!;
        if (twin.Layers != World) twin.Layers = World;
        if (twin.Visible != fromEye) twin.Visible = fromEye;
        if (fromEye && twin.Mesh != figure.Mesh) twin.Mesh = figure.Mesh;
    }

    /// <summary>Sets the visual layers of a node and everything under it.</summary>
    public static void SetLayers(Node node, uint layers)
    {
        if (node is VisualInstance3D v) v.Layers = layers;
        for (int i = 0; i < node.GetChildCount(); i++) SetLayers(node.GetChild(i), layers);
    }
}
