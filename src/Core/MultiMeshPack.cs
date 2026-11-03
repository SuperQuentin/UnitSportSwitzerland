using Godot;

namespace UnitSport.Core;

/// <summary>
/// A MultiMesh's instances written into one float buffer and sent in a single call
/// (<see cref="MultiMesh.Buffer"/>), instead of an engine call per instance and per value: across the
/// engine boundary each call costs more than the pose it carries (#1112, a Quest's CPU).
/// The layout is Godot's: per instance the transform (12 floats), then the colour (4) if the mesh
/// uses colours, then the custom data (4) if it uses custom data.
/// </summary>
public static class MultiMeshPack
{
    /// <summary>Floats per instance in <paramref name="multi"/>'s buffer (3D transforms).</summary>
    public static int Stride(MultiMesh multi) => 12 + (multi.UseColors ? 4 : 0) + (multi.UseCustomData ? 4 : 0);

    /// <summary>A buffer for every instance of <paramref name="multi"/>, reused while its size still fits.</summary>
    public static float[] Buffer(MultiMesh multi, float[]? reuse)
    {
        int length = multi.InstanceCount * Stride(multi);
        return reuse != null && reuse.Length == length ? reuse : new float[length];
    }

    /// <summary>The transform at <paramref name="offset"/>: the basis' rows, each followed by the origin's coordinate.</summary>
    public static void Transform(float[] buffer, int offset, in Transform3D t)
    {
        buffer[offset] = t.Basis.X.X; buffer[offset + 1] = t.Basis.Y.X; buffer[offset + 2] = t.Basis.Z.X; buffer[offset + 3] = t.Origin.X;
        buffer[offset + 4] = t.Basis.X.Y; buffer[offset + 5] = t.Basis.Y.Y; buffer[offset + 6] = t.Basis.Z.Y; buffer[offset + 7] = t.Origin.Y;
        buffer[offset + 8] = t.Basis.X.Z; buffer[offset + 9] = t.Basis.Y.Z; buffer[offset + 10] = t.Basis.Z.Z; buffer[offset + 11] = t.Origin.Z;
    }

    /// <summary>Four floats (a colour or custom data) at <paramref name="offset"/>.</summary>
    public static void Color(float[] buffer, int offset, Color c)
    {
        buffer[offset] = c.R; buffer[offset + 1] = c.G; buffer[offset + 2] = c.B; buffer[offset + 3] = c.A;
    }
}
