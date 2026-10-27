using Godot;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema seat to sit on (#1279): the seat in front of a player standing in a row's lane, read from the seating bank's
/// own rows (<see cref="InteriorGenerator.RowsOf"/>, as its mesh draws them). E (pad Y; VR: Y, or reach out and grip)
/// sits there facing the screen, as on a stadium's stand (<see cref="FootPlayer.TrySit"/>); nothing new on the wire.
/// </summary>
public static class CinemaSeats
{
    /// <summary>How far along the row from a seat's middle a player may stand to sit on it, m.</summary>
    public const float Reach = 0.45f;
    /// <summary>The seated hip over the row's tread, and its depth from the row's back edge (the cushion let down), m.</summary>
    private const float HipOverTread = 0.45f, HipFromBack = 0.42f;

    /// <summary>The seat <paramref name="p"/> stands in front of in the interior they are in, or null.</summary>
    public static FootPlayer.SitSpot? At(FootPlayer p)
    {
        if (InteriorManager.Instance is not { Current: { Type: BuildingType.Cinema } l, CurrentNode: { } node }) return null;
        var here = node.ToLocal(p.GlobalPosition);
        foreach (var bank in l.Furniture)
        {
            if (bank.Type != FurnitureType.SeatingBank) continue;
            var basis = new Basis(Vector3.Up, bank.Turns * Mathf.Pi / 2);
            var origin = new Vector3(bank.X, l.FloorY(bank.Floor) + bank.Lift, bank.Z);
            var local = basis.Inverse() * (here - origin);
            if (Math.Abs(local.X) > bank.W / 2 || Math.Abs(local.Z) > bank.D / 2) continue;
            var rows = InteriorGenerator.RowsOf(bank);
            int k = Math.Clamp((int)((rows.D - local.Z) / rows.Pitch), 0, rows.Rows - 1);
            var (z0, _, y) = rows.Row(k);
            // standing on that row's tread, in its lane
            if (Math.Abs(local.Y - y) > 0.3f || local.Z < z0 + InteriorGenerator.SeatDepth) continue;
            int n = rows.SeatsIn(k), best = -1;
            float bestD = Reach;
            for (int i = 0; i < n; i++)
            {
                float dx = Math.Abs(rows.SeatX(k, i) - local.X);
                if (dx < bestD) (best, bestD) = (i, dx);
            }
            if (best < 0) continue;
            var hip = node.ToGlobal(origin + basis * new Vector3(rows.SeatX(k, best), y + HipOverTread, z0 + HipFromBack));
            // facing the screen: the bank's +Z, a body facing its own -Z
            var ahead = (node.GlobalBasis * basis * Vector3.Back) with { Y = 0 };
            float yaw = Mathf.Atan2(-ahead.X, -ahead.Z);
            return new FootPlayer.SitSpot(hip, HipOverTread, yaw, p.GlobalPosition + Vector3.Up * 0.05f);
        }
        return null;
    }
}
