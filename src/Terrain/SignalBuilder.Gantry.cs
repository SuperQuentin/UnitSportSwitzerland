using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Gantries of lane signs with heads in them (#1110, <c>docs/notes/tools/direction-signs.md</c>; Geneva, the user's photos):
/// two square posts and a beam over the approach, a sign over each run of lanes (green to a motorway, blue on main roads, white on
/// others, SSV Art. 51-53), under each sign a black band with each lane's white arrow and, on a lit gantry, a horizontal head
/// beside it (SSV Art. 70: red on the left, green on the right). The destinations are Label3Ds
/// (<see cref="GantryTexts"/>); the lenses are the tile's (<see cref="GantryLenses"/>).
/// </summary>
public static partial class SignalBuilder
{
    /// <summary>The black band under a sign: its height, and a lane's arrow and head in it, side by side about the lane's middle (m).</summary>
    private const float BandHeight = 0.55f, BandArrowAt = -0.42f, BandHeadAt = 0.3f, BandArrow = 0.2f;
    /// <summary>The band reaches this far either side of a lane's middle (m): past its arrow and its head.</summary>
    private const float BandLaneHalf = 0.95f;
    /// <summary>A sign's height for its lines: a margin and a line each (m); its depth, its white border.</summary>
    private const float SignMargin = 0.4f, SignLine = 0.42f, SignDepth = 0.08f, SignBorder = 0.06f;
    /// <summary>The gantry's beam over the signs (a square box) and its posts (m).</summary>
    private const float GantryBeam = 0.35f, GantryPostHalf = 0.17f;
    /// <summary>A horizontal head's housing: three 200 mm lenses side by side (the band is too low for 300 mm ones).</summary>
    private const float FlatHalfWidth = 1.5f * Pitch + 0.03f, FlatHalfHeight = 0.5f * Pitch + 0.02f;

    private static readonly Color SignGreen = new(0.0f, 0.42f, 0.24f);   // RAL 6024 traffic green
    private static readonly Color SignBlue = new(0.0f, 0.33f, 0.63f);    // RAL 5017 traffic blue
    private static readonly Color SignBack = new(0.62f, 0.64f, 0.66f);   // the aluminium back

    private static Color FaceOf(SignColour c) => c switch { SignColour.Green => SignGreen, SignColour.White => WhitePlate, _ => SignBlue };

    private static float SignHeight(GantrySign s) => SignMargin + SignLine * Mathf.Max(1, s.Lines.Count);

    /// <summary>The gantry's frame: foot A, the unit vector along it to B, its length, the signs' facing and the viewer's right.</summary>
    private static (Vector3 A, Vector3 Dir, float Length, Vector3 Front, Vector3 Right) GantryFrame(SignGantry g)
    {
        var a = new Vector3(g.X, g.Y, g.Z);
        var dir = new Vector3(g.EndX - g.X, 0f, g.EndZ - g.Z);
        float length = dir.Length();
        var (front, right) = Frame(g.Heading);
        return (a, length > 1e-3f ? dir / length : -right, length, front, right);
    }

    /// <summary>A sign's plates stand this far in front of the beam's line (m): the beam's half depth and a hanger.</summary>
    private const float SignOut = GantryBeam * 0.5f + 0.06f;

    /// <summary>The middle of a lane's band, its face (tile-local).</summary>
    private static Vector3 BandFace(SignGantry g, float along)
    {
        var (a, dir, _, front, _) = GantryFrame(g);
        var p = a + dir * along;
        p.Y = g.Bottom + BandHeight * 0.5f;
        return p + front * (SignOut + SignDepth);
    }

    /// <summary>Appends a gantry: posts, beam, hangers, signs with their borders, bands, arrows and heads (#1110).</summary>
    private static void Gantry(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalPlan plan, SignGantry g, Color colour)
    {
        var (a, dir, length, front, right) = GantryFrame(g);
        if (length < 1f || g.Signs.Count == 0) return;
        float top = g.Signs.Max(s => g.Bottom + BandHeight + SignHeight(s));
        float beamY = top + 0.1f + GantryBeam * 0.5f;
        var across = dir.Cross(Vector3.Up).Normalized();
        // two posts and the beam on them
        var b = a + dir * length;
        b.Y = g.EndY;
        Post(vertices, colors, uvs, uv2s, indices, a, beamY + GantryBeam * 0.5f, dir, across, GantryPostHalf, colour);
        Post(vertices, colors, uvs, uv2s, indices, b, beamY + GantryBeam * 0.5f, dir, across, GantryPostHalf, colour);
        var start = a; start.Y = beamY;
        var end = a + dir * length; end.Y = beamY;
        Beam(vertices, colors, uvs, uv2s, indices, start, end, across, GantryBeam * 0.5f, GantryBeam * 0.5f, colour);
        var black = Housing.SrgbToLinear();
        var back = SignBack.SrgbToLinear();
        foreach (var s in g.Signs)
        {
            float h = SignHeight(s), w = s.Width * 0.5f;
            var mid = a + dir * s.Along;
            // the sign's body (its back aluminium), the band under it black, both hung in front of the beam
            var body = mid + front * (SignOut + SignDepth * 0.5f);
            body.Y = g.Bottom + BandHeight + h * 0.5f;
            Beam(vertices, colors, uvs, uv2s, indices, body - right * w, body + right * w, front, SignDepth * 0.5f, h * 0.5f, back);
            // the band runs under the sign and on over every lane of it (a sign is at most GantryLayout.MaxWidth wide; its lanes may
            // spread wider): each lane's arrow and head stay in it
            float from = s.Along - w, to = s.Along + w;
            foreach (var lane in s.Lanes) { from = Mathf.Min(from, lane.Along - BandLaneHalf); to = Mathf.Max(to, lane.Along + BandLaneHalf); }
            var band = a + dir * ((from + to) * 0.5f) + front * (SignOut + SignDepth * 0.5f);
            band.Y = g.Bottom + BandHeight * 0.5f;
            Beam(vertices, colors, uvs, uv2s, indices, band - dir * ((to - from) * 0.5f), band + dir * ((to - from) * 0.5f), front,
                SignDepth * 0.5f, BandHeight * 0.5f, black);
            // hangers from the beam down the sign's back
            foreach (float side in (ReadOnlySpan<float>)[-0.7f, 0.7f])
            {
                var hang = mid + right * (w * side) + front * (SignOut * 0.5f);
                hang.Y = g.Bottom;
                Post(vertices, colors, uvs, uv2s, indices, hang, beamY, dir, across, 0.04f, colour, sink: false);
            }
            // the face: a border (white, black round a white sign) and the field inside it, each layer 1 cm out from the one
            // under it (4 mm fought over 2-3 m² a gantry: zfight's gap is 8 mm)
            var face = body + front * (SignDepth * 0.5f + 0.01f);
            var rim = s.Colour == SignColour.White ? Housing : WhitePlate;
            Plate(vertices, colors, uvs, uv2s, indices, rim.SrgbToLinear(), face, front, right, w, h * 0.5f);
            Plate(vertices, colors, uvs, uv2s, indices, FaceOf(s.Colour).SrgbToLinear(), face + front * 0.01f, front, right, w - SignBorder, h * 0.5f - SignBorder);
            // the motorway symbol on a white square before the first line (SSV 4.01)
            if (s.Lines.Count > 0 && s.Lines[0].StartsWith(SignGantry.MotorwayMark))
            {
                var at = face + front * 0.02f + Vector3.Up * (h * 0.5f - SignMargin * 0.5f - SignLine * 0.5f) - right * (w - SignBorder - 0.3f);
                MotorwaySymbol(vertices, colors, uvs, uv2s, indices, at, front, right, 0.17f);
            }
            // each lane: its white arrow and its head in the band
            foreach (var lane in s.Lanes)
            {
                var c = BandFace(g, lane.Along);
                WhiteArrow(vertices, colors, uvs, uv2s, indices, lane.Moves, c + front * 0.01f + right * BandArrowAt, right, BandArrow);
                if (!g.Lit) continue;
                var head = c + right * BandHeadAt;
                RoundedHousing(vertices, colors, uvs, uv2s, indices, head + front * (HeadDepth * 0.5f), right, front,
                    FlatHalfWidth, HeadDepth * 0.5f, FlatHalfHeight, CornerRadius);
                for (int k = -1; k <= 1; k++)
                    LensFittings(vertices, colors, uvs, uv2s, indices, head + front * HeadDepth + right * (Pitch * k), right, front, 1f, square: false);
            }
        }
    }

    /// <summary>The motorway symbol (SSV 4.01) simplified: a white square, on it the bridge over the road in green.</summary>
    private static void MotorwaySymbol(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 at, Vector3 front, Vector3 right, float half)
    {
        Plate(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), at, front, right, half, half);
        var green = SignGreen.SrgbToLinear();
        var on = at + front * 0.01f;
        Plate(vertices, colors, uvs, uv2s, indices, green, on + Vector3.Up * (half * 0.45f), front, right, half * 0.8f, half * 0.12f);
        foreach (float x in (ReadOnlySpan<float>)[-0.5f, 0.5f])
            Plate(vertices, colors, uvs, uv2s, indices, green, on + right * (half * x) - Vector3.Up * (half * 0.15f), front, right, half * 0.1f, half * 0.6f);
    }

    /// <summary>A lane arrow in white (<see cref="SignalGlyphs"/>), <paramref name="size"/> metres to a lens radius.</summary>
    private static void WhiteArrow(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalMoves moves, Vector3 centre, Vector3 right, float size)
    {
        var colour = WhitePlate.SrgbToLinear();
        foreach (var polygon in SignalGlyphs.Arrow(moves))
        {
            var ring = new Vector3[polygon.Length];
            for (int i = 0; i < polygon.Length; i++) ring[i] = centre + right * (polygon[i].X * size) + Vector3.Up * (polygon[i].Y * size);
            Polygon(vertices, colors, uvs, uv2s, indices, colour, ring);
        }
    }

    /// <summary>A lit gantry's lenses (#1110): three per lane side by side, red on the left as the driver sees them, the group its lanes' moves give.</summary>
    private static void GantryLenses(RoadSignal signal, int junction, Lamps lamps)
    {
        foreach (var g in signal.Gantries)
        {
            if (!g.Lit) continue;
            var (_, _, _, front, right) = GantryFrame(g);
            var basis = new Basis(right, Vector3.Up, front);
            foreach (var s in g.Signs)
                foreach (var lane in s.Lanes)
                {
                    var (group, shape) = OverheadGroup(signal.Plan, g.Arm, lane.Moves);
                    if (group < 0) continue;
                    var arrow = shape == Shape.Circle ? LaneArrow(signal.Plan, g.Arm, lane.Moves) : (SignalMoves?)null;
                    var head = BandFace(g, lane.Along) + right * BandHeadAt + front * (HeadDepth + 0.006f);
                    for (int k = 0; k < 3; k++)
                        lamps.Lenses.Add(new Lens(shape, new Transform3D(basis, head + right * (Pitch * (k - 1))), junction, group, (Role)k, -1, arrow));
                }
        }
    }

    /// <summary>A line of a gantry sign's destinations (#1110), tile-local: its middle, the way the sign faces, the text, the width it fits in, dark letters (on a white sign).</summary>
    public readonly record struct GantryText(Vector3 Position, Vector3 Front, string Text, float Width, bool Dark);

    /// <summary>The destinations on the tile's gantry signs (#1110), a line each (drawn as <c>Label3D</c>s by <c>RoadSignBuilder.GantryLabels</c>); null when none.</summary>
    public static List<GantryText>? GantryTexts(RoadTile tile)
    {
        List<GantryText>? texts = null;
        foreach (var signal in tile.Signals)
            foreach (var g in signal.Gantries)
            {
                var (a, dir, _, front, right) = GantryFrame(g);
                foreach (var s in g.Signs)
                {
                    float h = SignHeight(s);
                    // in front of the face by more than the road material's depth pull
                    var face = a + dir * s.Along + front * (SignOut + SignDepth + 0.05f);
                    float topY = g.Bottom + BandHeight + h - SignMargin * 0.5f;
                    for (int i = 0; i < s.Lines.Count; i++)
                    {
                        string text = s.Lines[i].TrimStart(SignGantry.MotorwayMark);
                        if (text.Length == 0) continue;
                        // the first line of a motorway sign stands right of its symbol
                        float shift = i == 0 && s.Lines[i].StartsWith(SignGantry.MotorwayMark) ? 0.25f : 0f;
                        var at = face + right * shift;
                        at.Y = topY - SignLine * (i + 0.5f);
                        (texts ??= new()).Add(new GantryText(at, front, text, s.Width - 2 * SignBorder - 0.1f - shift * 2, s.Colour == SignColour.White));
                    }
                }
            }
        return texts;
    }

    /// <summary>A gantry's two posts for the collision (#1110): everything above them is over the 4 m vehicle limit.</summary>
    private static IEnumerable<Vector3> GantryFeet(RoadTile tile)
    {
        foreach (var signal in tile.Signals)
            foreach (var g in signal.Gantries)
            {
                yield return new Vector3(g.X, g.Y, g.Z);
                yield return new Vector3(g.EndX, g.EndY, g.EndZ);
            }
    }
}
