using Godot;

namespace UnitSport.Terrain.Leaves;

// Plain C# with Godot maths only: linked into the unit tests.

/// <summary>
/// A building site's graded ground on a leaf (#1731), in the leaf's anchor frame: its whole area at one height, the mean of
/// the ground in it, and <see cref="SitePads.Skirt"/> metres round it blending back. The flat generator's own relief is
/// gentle round a village; a fill leaf follows the real relief (#1678), and a site's yard stood on slopes its machines
/// could not climb. Made by <c>LeafStand.PadsOf</c>.
/// </summary>
public readonly record struct SitePad(Vector2 Center, Vector2 AxisU, float Width, float Depth, float Height);

public static class SitePads
{
    /// <summary>Metres round a site's area where its graded ground blends back to the leaf's.</summary>
    public const float Skirt = 8f;

    /// <summary>Metres of level working ground round a site's area, inside the pad: room for a telehandler or a loader beside the yard.</summary>
    public const float Apron = 12f;

    /// <summary>
    /// Grades the pads into a leaf's vertices (anchor frame, x y z): level inside each, blended back over <see cref="Skirt"/>.
    /// With <paramref name="bare"/> (the same lattice before the road blend), a vertex the roads moved is left as it is: a
    /// road's surface stays where its blend put it.
    /// </summary>
    public static void Apply(float[] vertices, IReadOnlyList<SitePad> pads, float[]? bare = null)
    {
        foreach (var pad in pads)
        {
            var v = new Vector2(-pad.AxisU.Y, pad.AxisU.X);
            float hu = pad.Width / 2, hv = pad.Depth / 2, reach = Math.Max(hu, hv) * 1.5f + Skirt;
            for (int k = 0; k + 2 < vertices.Length; k += 3)
            {
                float y = vertices[k + 1];
                if (float.IsNaN(y) || bare != null && Math.Abs(bare[k + 1] - y) > 0.01f) continue;
                var d = new Vector2(vertices[k], vertices[k + 2]) - pad.Center;
                if (Math.Abs(d.X) > reach || Math.Abs(d.Y) > reach) continue;
                float du = Math.Max(0, Math.Abs(d.Dot(pad.AxisU)) - hu), dv = Math.Max(0, Math.Abs(d.Dot(v)) - hv);
                float out_ = MathF.Sqrt(du * du + dv * dv);
                if (out_ >= Skirt) continue;
                float t = out_ / Skirt;   // 0 on the pad, 1 at the skirt's edge
                vertices[k + 1] = pad.Height + (y - pad.Height) * t * t * (3 - 2 * t);
            }
        }
    }
}
