namespace UnitSport.Tools.RoadGen.Import;

/// <summary>Movements one lane allows, from an OSM <c>turn:lanes</c> value. A lane may allow several.</summary>
[Flags]
public enum TurnMove : ushort
{
    /// <summary>An empty lane (<c>||</c>) or <c>none</c>: nothing marked on the road.</summary>
    None = 0,
    Left = 1 << 0,
    SlightLeft = 1 << 1,
    SharpLeft = 1 << 2,
    Through = 1 << 3,
    Right = 1 << 4,
    SlightRight = 1 << 5,
    SharpRight = 1 << 6,
    Reverse = 1 << 7,
    MergeToLeft = 1 << 8,
    MergeToRight = 1 << 9,
    /// <summary>A value outside the OSM list (typos, local tags): the lane exists, its movement is unknown.</summary>
    Unknown = 1 << 15,
}

/// <summary>
/// OSM <c>turn:lanes</c> (https://wiki.openstreetmap.org/wiki/Key:turn): lanes are separated by
/// <c>|</c>, listed from left to right in the direction of travel; a lane's movements are separated
/// by <c>;</c>. The overlay columns <c>turn_lanes_fwd</c>/<c>turn_lanes_bwd</c> are already per TLM
/// drawing direction, but each value keeps OSM's lane order: left to right as seen by a driver on it.
/// </summary>
public static class TurnLanes
{
    /// <summary>One entry per lane, left to right; empty for an empty or missing value.</summary>
    public static TurnMove[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var lanes = value.Split('|');
        var result = new TurnMove[lanes.Length];
        for (int i = 0; i < lanes.Length; i++)
            foreach (var part in lanes[i].Split(';'))
                result[i] |= Move(part.Trim());
        return result;
    }

    private static TurnMove Move(string v) => v switch
    {
        "" or "none" => TurnMove.None,
        "left" => TurnMove.Left,
        "slight_left" => TurnMove.SlightLeft,
        "sharp_left" => TurnMove.SharpLeft,
        "through" => TurnMove.Through,
        "right" => TurnMove.Right,
        "slight_right" => TurnMove.SlightRight,
        "sharp_right" => TurnMove.SharpRight,
        "reverse" => TurnMove.Reverse,
        "merge_to_left" => TurnMove.MergeToLeft,
        "merge_to_right" => TurnMove.MergeToRight,
        _ => TurnMove.Unknown,
    };

    /// <summary>Any left movement (left, slight, sharp): what a left-turn pocket serves.</summary>
    public const TurnMove AnyLeft = TurnMove.Left | TurnMove.SlightLeft | TurnMove.SharpLeft;
    /// <summary>Any right movement (right, slight, sharp).</summary>
    public const TurnMove AnyRight = TurnMove.Right | TurnMove.SlightRight | TurnMove.SharpRight;
}
