using Godot;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// A seat on a stand (#885), found from where someone stands: snapped to a seat's place along the
/// row (every <see cref="StadiumSpec.SeatWidth"/>), the hip on the front of the row stood on, the feet
/// on the row below, facing the pitch. No seat is modelled, so any row of a seat block, a corner or
/// a stair's segment is one; the aisles, gates and vomitories are not, nor a tier's first row (its
/// feet would hang over the front wall).
/// </summary>
/// <param name="E">The hip, LV95.</param>
/// <param name="Yaw">Facing the pitch, radians about +Y, 0 = −Z (north), as a body's rotation.</param>
/// <param name="Hip">The hip's height over whatever the stand's collision is under the asker's feet.</param>
/// <param name="Drop">How far under the hip the feet rest.</param>
public readonly record struct StadiumSeat(string Stadium, int Tier, int Row, int Place, double E, double N, float Yaw, float Hip, float Drop);

public static class StadiumSeats
{
    /// <summary>How far from the seat's place along the row the asker may stand, m: the next seat over.</summary>
    public const float Reach = 0.8f;

    /// <summary>The seat at an LV95 point, if it stands on a stand's row; null anywhere else.</summary>
    public static StadiumSeat? At(double e, double n)
    {
        foreach (var s in StadiumCatalog.All)
        {
            float de = (float)(e - s.E), dn = (float)(n - s.N);
            if (de * de + dn * dn > 200f * 200f) continue;
            float a = Mathf.DegToRad(s.AzimuthDeg);
            // LV95 offsets as tile axes (x east, z south), then into the stadium frame
            float x = de, z = -dn;
            var uv = new Vector2(x * MathF.Cos(a) + z * MathF.Sin(a), -x * MathF.Sin(a) + z * MathF.Cos(a));
            if (StadiumBuilder.SeatAt(s, uv) is not { } hit) continue;
            // and the seat's place back to LV95
            Vector2 Out(Vector2 p) => new(p.X * MathF.Cos(a) - p.Y * MathF.Sin(a), p.X * MathF.Sin(a) + p.Y * MathF.Cos(a));
            var hip = Out(hit.Hip);
            var face = Out(hit.Facing);
            return new StadiumSeat(s.Name, hit.Tier, hit.Row, hit.Place, s.E + hip.X, s.N - hip.Y,
                Mathf.Atan2(-face.X, -face.Y), hit.HipOverRamp, hit.Drop);
        }
        return null;
    }
}
