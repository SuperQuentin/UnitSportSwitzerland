using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Build;

/// <summary>
/// The gadgets' rules (#275), shared by the server's check (<see cref="PlacedObjects"/> calls
/// <see cref="Check"/>) and the client's ghost: which item sets down which placed kind, and what each
/// kind's payload says. Zipline: placed at its low end, the payload names the high end; between 8 and
/// 150 m, at least 2 m downhill, no steeper than 45°. Rope ladder: placed at its top, the payload is
/// its length (1.5 to 8 m). Docs: <c>docs/notes/build/gadgets.md</c>.
/// </summary>
public static class Gadgets
{
    public const float ZipMin = 8f, ZipMax = 150f, ZipDrop = 2f, PostHeight = 3f;
    public const float LadderMin = 1.5f, LadderMax = 8f;

    public static readonly Dictionary<ItemId, PlacedKind> KindOf = new()
    {
        [ItemId.Zipline] = PlacedKind.Zipline,
        [ItemId.RopeLadder] = PlacedKind.RopeLadder,
        [ItemId.Trampoline] = PlacedKind.Trampoline,
        [ItemId.LaunchPad] = PlacedKind.LaunchPad,
        [ItemId.CamoNet] = PlacedKind.CamoNet,
        [ItemId.HayHideout] = PlacedKind.HayHideout,
    };

    public static readonly Dictionary<PlacedKind, ItemId> ItemOf = KindOf.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsGadget(PlacedKind k) => ItemOf.ContainsKey(k);

    // ---- payloads -------------------------------------------------------------------------------

    public static string ZipPayload(double e, double n, double alt) =>
        string.Create(CultureInfo.InvariantCulture, $"{e:F2};{n:F2};{alt:F2}");

    public static (double E, double N, double Alt)? ZipStart(string payload)
    {
        var p = payload.Split(';');
        return p.Length == 3
               && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double e)
               && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
               && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
               && double.IsFinite(e) && double.IsFinite(n) && double.IsFinite(a)
            ? (e, n, a) : null;
    }

    public static string LadderPayload(float length) => length.ToString("F2", CultureInfo.InvariantCulture);

    public static float? LadderLength(string payload) =>
        float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out float l) && float.IsFinite(l) ? l : null;

    /// <summary>Why a zipline from <paramref name="high"/> down to <paramref name="low"/> (foot of each post) cannot be strung, or null.</summary>
    public static string? ZipProblem(Vector3 high, Vector3 low)
    {
        float drop = high.Y - low.Y;
        float flat = new Vector2(high.X - low.X, high.Z - low.Z).Length();
        float len = high.DistanceTo(low);
        return len < ZipMin ? $"Too short: at least {ZipMin:F0} m."
            : len > ZipMax ? $"Too long: at most {ZipMax:F0} m."
            : drop < ZipDrop ? "It has to run downhill (at least 2 m)."
            : drop > flat ? "Too steep: 45° at most."
            : null;
    }

    /// <summary>
    /// A body's feet at <paramref name="feet"/> (world, this peer's frame) are hidden (#359): under a camo
    /// net (its 3.8 m square, below the net) or inside a hay hideout. Worked out by each viewer from the
    /// positions it already has: nothing is sent.
    /// </summary>
    public static bool Hidden(Vector3 feet)
    {
        if (PlacedObjects.Instance is not { } placed) return false;
        foreach (var o in placed.All.Values)
        {
            if (o.Kind is not (PlacedKind.CamoNet or PlacedKind.HayHideout)) continue;
            var local = o.WorldTransform(placed.Origin).AffineInverse() * feet;
            float half = o.Kind == PlacedKind.CamoNet ? 1.9f : 1.0f, top = o.Kind == PlacedKind.CamoNet ? 2.0f : 1.5f;
            if (Mathf.Abs(local.X) <= half && Mathf.Abs(local.Z) <= half && local.Y > -0.5f && local.Y < top) return true;
        }
        return false;
    }

    /// <summary>The server's check of a gadget's payload (other kinds: null, nothing to add).</summary>
    public static string? Check(PlacedObject o, WorldOrigin origin)
    {
        switch (o.Kind)
        {
            case PlacedKind.Zipline:
            {
                if (ZipStart(o.Payload) is not { } s) return "Where does it start?";
                return ZipProblem(origin.ToWorld(s.E, s.N, s.Alt), origin.ToWorld(o.E, o.N, o.Altitude));
            }
            case PlacedKind.RopeLadder:
                return LadderLength(o.Payload) is { } l && l >= LadderMin - 0.01f && l <= LadderMax + 0.01f ? null : "A ladder is 1.5 to 8 m long.";
            default:
                return null;
        }
    }
}
