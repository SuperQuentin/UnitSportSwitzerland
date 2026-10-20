using Godot;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// A stadium's pitch on a concert day (#956): flat protection boards laid over the grass wherever the
/// crowd stands, inside the seating front's rounded corners, and the grass left as it is under the
/// stage (<see cref="StagePlan.Grass"/>). The terrain under a pitch is not quite flat, so the floor
/// stands at <c>top</c>, just over its highest bump (the director measures it): the crowd stands on
/// it, and one flat collision quad holds whoever walks there. Built with the stage, struck with it.
/// Arenas have their own floor and open airs keep their field: neither gets one.
/// </summary>
public static class ConcertFloor
{
    /// <summary>A board's side: a few metres of the panels a crew lays, a lighter and a darker in turn.</summary>
    public const float Board = 2f;
    /// <summary>The boards' thickness over the grass.</summary>
    public const float Thickness = 0.04f;
    /// <summary>The floor kept off the seating front: its wall stands there.</summary>
    private const float Margin = 0.3f;

    // sRGB
    private static readonly Color BoardA = new(0.6f, 0.61f, 0.6f), BoardB = new(0.55f, 0.56f, 0.55f),
        Turf = new(0.12f, 0.27f, 0.08f), TurfStripe = new(0.14f, 0.31f, 0.1f);

    /// <summary>The floor's boards and the stage's grass into <paramref name="g"/>, their top at <paramref name="top"/> over the centre spot.</summary>
    internal static void Build(StagePlan plan, float top, StadiumBuilder.Geo g)
    {
        var s = plan.Event.Stadium;
        if (s.Kind != VenueKind.Stadium) return;
        float hu = s.FrontHalfU - Margin, hv = s.FrontHalfV - Margin, corner = Math.Max(0f, s.FrontCorner - Margin);
        // whether a point is inside the seating front's rounded rectangle
        bool Inside(float u, float v)
        {
            float du = MathF.Abs(u) - (hu - corner), dv = MathF.Abs(v) - (hv - corner);
            if (MathF.Abs(u) > hu || MathF.Abs(v) > hv) return false;
            return du <= 0 || dv <= 0 || du * du + dv * dv <= corner * corner;
        }
        int nu = (int)MathF.Ceiling(2 * hu / Board), nv = (int)MathF.Ceiling(2 * hv / Board);
        float grass = top - Thickness;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                float u0 = -hu + i * Board, u1 = Math.Min(hu, u0 + Board), v0 = -hv + j * Board, v1 = Math.Min(hv, v0 + Board);
                var mid = new Vector2((u0 + u1) / 2, (v0 + v1) / 2);
                if (!(Inside(u0, v0) && Inside(u1, v0) && Inside(u1, v1) && Inside(u0, v1))) continue;
                // the stage's grass: mown in stripes across the pitch, as a football ground is
                if (plan.OnGrass(mid))
                    Flat(g, u0, u1, v0, v1, grass, ((int)MathF.Floor((mid.Y + hv) / 5f) & 1) == 0 ? Turf : TurfStripe);
                else
                    Flat(g, u0, u1, v0, v1, top, ((i + j) & 1) == 0 ? BoardA : BoardB);
            }
        // one flat floor to walk on, the grass's bit of it a hair high
        g.Solid(new Vector3(-hu, top, -hv), new Vector3(hu, top, -hv), new Vector3(hu, top, hv), new Vector3(-hu, top, hv));
    }

    private static void Flat(StadiumBuilder.Geo g, float u0, float u1, float v0, float v1, float y, Color c) =>
        g.Quad(new Vector3(u0, y, v0), new Vector3(u0, y, v1), new Vector3(u1, y, v1), new Vector3(u1, y, v0), c.SrgbToLinear() with { A = 0f }, solid: false);
}
