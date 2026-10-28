using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The moving parts of a lane pair (#1323), each its own small mesh built once and moved as a node
/// (<see cref="LaneRig"/>): a turned pin, a house ball, the pinsetter's table and its sweep.
/// </summary>
public static partial class InteriorMeshBuilder
{
    /// <summary>A regulation pin's outline [S]: height above its base, radius there.</summary>
    private static readonly (float Y, float R)[] PinProfile =
    {
        (0f, 0.0258f), (0.019f, 0.036f), (0.057f, 0.0478f), (0.102f, 0.0567f), (0.143f, 0.0605f), (0.19f, 0.0548f),
        (0.2286f, 0.0416f), (0.235f, 0.039f), (0.248f, 0.033f), (0.258f, 0.029f), (0.27f, 0.0245f), (0.2731f, 0.0234f),
        (0.3016f, 0.0275f), (0.3302f, 0.0315f), (0.3524f, 0.029f), (0.3683f, 0.022f), (0.381f, 0f),
    };

    /// <summary>
    /// A pin turned on a lathe: white, its two red neck stripes. Its origin at the middle of its base,
    /// so a pin stands where its node is and falls round its foot.
    /// </summary>
    public static MeshData Pin()
    {
        const int sides = 10;
        var s = new Scratch();
        for (int k = 0; k + 1 < PinProfile.Length; k++)
        {
            var (y0, r0) = PinProfile[k];
            var (y1, r1) = PinProfile[k + 1];
            float mid = (y0 + y1) / 2;
            var col = mid is > 0.235f and < 0.248f || mid is > 0.258f and < 0.27f ? PinRed : PinWhite;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.Tau / sides, a1 = (i + 1) * Mathf.Tau / sides;
                var p00 = new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0);
                var p01 = new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0);
                var p10 = new Vector3(Mathf.Cos(a0) * r1, y1, Mathf.Sin(a0) * r1);
                var p11 = new Vector3(Mathf.Cos(a1) * r1, y1, Mathf.Sin(a1) * r1);
                if (r1 <= 0) s.Tri(p00, p10, p01, col, false);
                else s.Quad(p00, p10, p11, p01, col, false);
            }
        }
        // the base: a flat disc to stand on
        float rb = PinProfile[0].R;
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.Tau / sides, a1 = (i + 1) * Mathf.Tau / sides;
            s.Tri(Vector3.Zero, new Vector3(Mathf.Cos(a0) * rb, 0, Mathf.Sin(a0) * rb), new Vector3(Mathf.Cos(a1) * rb, 0, Mathf.Sin(a1) * rb), PinWhite, false);
        }
        return Data(s);
    }

    /// <summary>How many house ball colours there are (<see cref="Ball"/>'s index).</summary>
    public static int BallColorCount => BallColors.Length;

    /// <summary>
    /// A house ball of colour <paramref name="color"/>: a sphere centred on its origin, its three
    /// holes (thumb and two fingers) dark on its +Y side, so a rolling ball is seen to roll.
    /// </summary>
    public static MeshData Ball(int color)
    {
        const int rings = 8, sides = 12;
        float r = LaneGeometry.BallRadius;
        var col = BallColors[Mathf.PosMod(color, BallColors.Length)];
        var hole = C(0.02f, 0.02f, 0.03f);
        var s = new Scratch();
        Vector3 At(int ring, int i)
        {
            float lat = Mathf.Pi * ring / rings, lon = Mathf.Tau * i / sides;
            return new Vector3(Mathf.Sin(lat) * Mathf.Cos(lon), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(lon)) * r;
        }
        for (int ring = 0; ring < rings; ring++)
            for (int i = 0; i < sides; i++)
            {
                var a = At(ring, i); var b = At(ring, i + 1); var c = At(ring + 1, i + 1); var d = At(ring + 1, i);
                // the grip: two finger holes side by side near the top, the thumb hole further down
                bool isHole = ring == 1 && (i == 0 || i == 1) || ring == 3 && i == 0;
                if (ring == 0) s.Tri(a, c, d, isHole ? hole : col, false);
                else if (ring == rings - 1) s.Tri(a, b, d, col, false);
                else s.Quad(a, b, c, d, isHole ? hole : col, false);
            }
        return Data(s);
    }

    /// <summary>
    /// The pinsetter's table for one lane: a grey deck the width of the pin triangle, its underside at
    /// the origin, the head pin's spot under (0, 0, 0) and the back row toward +Z.
    /// </summary>
    public static MeshData PinTable(float scale)
    {
        var s = new Scratch();
        float back = 3 * LaneGeometry.PinRow * scale;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            s.Box(new Vector3(xa, ya, za), new Vector3(xb, yb, zb), col, false);
        B(-0.56f, 0.02f, -0.16f, 0.56f, 0.09f, back + 0.16f, MachineGrey);
        B(-0.5f, 0.09f, -0.1f, 0.5f, 0.42f, back + 0.1f, C(0.32f, 0.34f, 0.34f));
        // a grip cup over each spot: what the pin's head goes into
        for (int k = 0; k < 4; k++)
            for (int i = 0; i <= k; i++)
            {
                float x = (i - k / 2f) * LaneGeometry.PinSpacing * scale, z = k * LaneGeometry.PinRow * scale;
                // up into the plate, its top a centimetre past the plate's underside: no face shared with it
                B(x - 0.045f, 0, z - 0.045f, x + 0.045f, 0.035f, z + 0.045f, C(0.12f, 0.12f, 0.13f));
            }
        return Data(s);
    }

    /// <summary>The sweep: a black bar across the lane and both gutters, yellow chevrons on its face, its bottom edge at the origin.</summary>
    public static MeshData Sweep()
    {
        var s = new Scratch();
        float half = LaneGeometry.HalfLane + LaneGeometry.Gutter;
        s.Box(new Vector3(-half, 0, -0.03f), new Vector3(half, 0.24f, 0.01f), C(0.08f, 0.08f, 0.09f), false);
        for (int i = 0; i < 6; i++)
        {
            float x = -half + 0.12f + i * (2 * half - 0.24f) / 5;
            s.Box(new Vector3(x - 0.06f, 0.06f, -0.04f), new Vector3(x + 0.06f, 0.16f, -0.028f), C(0.96f, 0.78f, 0.08f), false);
        }
        // the arms it hangs from, up into the machine
        foreach (float x in new[] { -half + 0.03f, half - 0.03f })
            s.Box(new Vector3(x - 0.02f, 0.2f, -0.02f), new Vector3(x + 0.02f, 1.2f, -0.005f), C(0.3f, 0.3f, 0.32f), false);
        return Data(s);
    }

    private static MeshData Data(Scratch s) => new(s.V.ToArray(), s.C.ToArray(), Array.Empty<Vector3>());
}
