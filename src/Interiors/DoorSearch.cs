using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// "Wait for the nearest door of this kind, then give up loudly" (#507). Shared by
/// <see cref="InteriorProbe"/> and <see cref="Player.GarageProbe"/>: both wait for a building the
/// generated world may not have put near the spawn, and a probe that waits in silence costs
/// minutes and then says nothing about why. Until the fixture world carries buildings (#508),
/// a door check is at the generator's mercy, so it must at least report what it saw.
/// </summary>
public static class DoorSearch
{
    /// <summary>
    /// How far "a door in reach" looks: the player is expected to be standing among the houses.
    /// </summary>
    public const float NearReach = 400f;

    /// <summary>
    /// How far a search for one <see cref="BuildingKind"/> looks. A barn or a garage is rarely in
    /// the village you spawn in, so this is the 1.5 km <see cref="Player.GarageProbe"/> has always
    /// used rather than <see cref="NearReach"/>, which asking for a kind would otherwise inherit.
    /// </summary>
    public const float KindReach = 1500f;

    /// <summary>
    /// Seconds a probe looks before giving up. Doors appear as their tile's buildings commit, so
    /// the search cannot be a single frame's answer; but once the tiles around are drawn, more
    /// waiting changes nothing.
    /// </summary>
    public const double GiveUp = 25;

    /// <summary>How far a search for <paramref name="kind"/> reaches (null: any door).</summary>
    public static float Reach(BuildingKind? kind) => kind == null ? NearReach : KindReach;

    /// <summary>The nearest door a probe asked for, or null while none is drawn in reach.</summary>
    public static DoorIndex.Entry? Nearest(Vector3 at, BuildingKind? kind) =>
        kind is { } k ? DoorIndex.Nearest(at, KindReach, k) : DoorIndex.Nearest(at, NearReach);

    /// <summary>
    /// Why the search came up empty, in the log: the doors of that kind that <i>are</i> drawn,
    /// nearest first, each with the rule that ruled it out. That tells "no barn anywhere" apart
    /// from "a barn, but 12 m below you" and "a barn, but you are behind it" — three failures that
    /// otherwise look identical. With none drawn at all, the nearest villages to try instead.
    /// </summary>
    public static void Explain(string tag, Vector3 at, BuildingKind? kind, WorldOrigin? origin)
    {
        string what = kind?.ToString() ?? "building";
        var seen = DoorIndex.Candidates(at, kind).Take(6).ToList();
        if (seen.Count == 0)
        {
            GD.Print($"[{tag}] no {what} door is drawn at all ({DoorIndex.All().Count()} door(s) of every kind).");
            foreach (string village in Villages(at, origin)) GD.Print($"[{tag}]   {village}");
            return;
        }
        GD.Print($"[{tag}] no {what} door within {Reach(kind):F0} m. The nearest that are drawn:");
        foreach (var (door, distance, rejected) in seen)
            GD.Print($"[{tag}]   {door.Key} {door.Kind} {door.Width:F1} m wide, {distance:F0} m away"
                + $"{Lv95(origin, door.World)}{(rejected == null ? "" : $" — {rejected}")}");
    }

    /// <summary>Where a door is in LV95, so a run that failed at it can be walked into with <c>--at</c>.</summary>
    public static string Lv95(WorldOrigin? origin, Vector3 world)
    {
        if (origin == null) return "";
        var (e, n) = origin.ToLv95(world);
        return $" (--at {e:F0},{n:F0})";
    }

    /// <summary>
    /// Somewhere to look instead when nothing of the kind is drawn here: the generated villages
    /// have barns at their ends and garages beside their side streets.
    /// </summary>
    private static IEnumerable<string> Villages(Vector3 at, WorldOrigin? origin)
    {
        if (origin == null) yield break;
        var (e, n) = origin.ToLv95(at);
        foreach (var town in Occasions.OccasionTowns.All
                     .OrderBy(t => Math.Pow(t.E - e, 2) + Math.Pow(t.N - n, 2)).Take(3))
            yield return $"village {town.Name}: --at {town.E:F0},{town.N:F0}";
    }
}
