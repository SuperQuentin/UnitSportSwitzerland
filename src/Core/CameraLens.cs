using Godot;

namespace UnitSport.Core;

/// <summary>
/// A camera's field of view at any angle (#1440). Godot's <see cref="Camera3D.Fov"/> stops at 1°, and
/// the telescope goes to a sixth of one: below 1° the camera takes a frustum projection of the same
/// angle (its <see cref="Camera3D.Size"/> at the near plane). Whatever reads a camera's angle (the
/// zoom-aware detail, the terrain's view cone) reads it through <see cref="FovOf"/>.
/// </summary>
public static class CameraLens
{
    /// <summary>The narrowest angle Godot's perspective camera takes, degrees.</summary>
    public const float PerspectiveMin = 1f;

    /// <summary>Sets <paramref name="cam"/>'s angle along its kept axis (vertical by default), degrees.</summary>
    public static void SetFov(Camera3D cam, float fovDeg)
    {
        if (fovDeg >= PerspectiveMin)
        {
            cam.Projection = Camera3D.ProjectionType.Perspective;
            cam.Fov = fovDeg;
            return;
        }
        cam.SetFrustum(2f * cam.Near * Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f), Vector2.Zero, cam.Near, cam.Far);
    }

    /// <summary>The camera's angle along its kept axis, degrees, perspective or frustum.</summary>
    public static float FovOf(Camera3D cam) => cam.Projection == Camera3D.ProjectionType.Frustum
        ? Mathf.RadToDeg(2f * Mathf.Atan(cam.Size / (2f * cam.Near)))
        : cam.Fov;
}
