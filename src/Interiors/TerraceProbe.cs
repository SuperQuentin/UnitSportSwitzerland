using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --terracecheck</c> (#1243): <see cref="VenueTerraces.Plan"/> in front of
/// a hand-made door: a street far enough out, one too close, a slope on one side, OSM's yes and no, a
/// beer garden, a club; every set keeps the passage to the carriageway, and the pieces build.
/// </summary>
public partial class TerraceProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--terracecheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[terrace] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    // a door at the origin facing +Z (the street), its carriageway's edge a straight line `road` m out
    private static readonly DoorSpot Door = new() { Position = Vector3.Zero, Outward = Vector3.Back, Width = 1.2f, Height = 2.2f, Venue = 1 };

    private static VenueRecord Venue(VenueType t, VenueFlags flags = VenueFlags.Outdoor) =>
        new("n42", t, 2600000, 1200000, VenueFix.Inside, flags, 0, "", "Test");

    private static List<TerraceItem> Plan(VenueRecord v, float road, Func<Vector3, float?>? ground = null) =>
        VenueTerraces.Plan(Door, v, p => road - p.Z, ground ?? (_ => 0f));

    public override void _Ready()
    {
        int Count(List<TerraceItem> items, TerraceKind k) => items.Count(i => i.Kind == k);

        var wide = Plan(Venue(VenueType.Restaurant), 6f);
        Check(Count(wide, TerraceKind.BistroSet) == 4, $"a pavement 6 m deep: four bistro sets ({Count(wide, TerraceKind.BistroSet)})");
        Check(wide.Any(i => i.Kind is TerraceKind.Parasol or TerraceKind.Awning), "shade: parasols or an awning");
        Check(wide.Where(i => i.Kind == TerraceKind.BistroSet).All(i => 6f - (i.At.Z + 0.4f) >= VenueTerraces.Passage),
            "every set keeps the passage to the carriageway");
        Check(wide.Where(i => i.Kind == TerraceKind.BistroSet).All(i => Math.Abs(i.At.X) >= Door.Width / 2 + 0.9f),
            "the way in stays clear");
        Check(Count(wide, TerraceKind.MenuBoard) == 1, "a menu board by the door");

        Check(Plan(Venue(VenueType.Cafe), 2.5f).Count == 0, "a pavement 2.5 m deep: no room, no terrace");
        Check(Plan(Venue(VenueType.Bar, VenueFlags.NoOutdoor), 6f).Count == 0, "outdoor_seating=no: none");
        Check(Plan(Venue(VenueType.Nightclub), 6f).Count == 0, "a club: none");

        var level = Plan(Venue(VenueType.Cafe), 6f, p => p.X > 0 ? 1.0f : 0f);
        Check(Count(level, TerraceKind.BistroSet) == 2 && level.Where(i => i.Kind == TerraceKind.BistroSet).All(i => i.At.X < 0),
            $"ground 1 m up on one side: sets on the other only ({Count(level, TerraceKind.BistroSet)})");
        var slope = Plan(Venue(VenueType.Cafe), 6f, p => p.X > 0 ? p.X * 0.25f : 0f);
        Check(slope.Where(i => i.Kind == TerraceKind.BistroSet).All(i => i.At.X < 0), "a slope of 1 in 4 on one side: no table on it");
        var step = Plan(Venue(VenueType.Cafe), 6f, p => p.Z > 0.2f ? -0.3f : 0f);
        Check(Count(step, TerraceKind.BistroSet) == 4 && step.Where(i => i.Kind == TerraceKind.BistroSet).All(i => Math.Abs(i.At.Y + 0.3f) < 0.01f),
            "a door 0.3 m up a step: the sets on the pavement below it");

        var garden = Plan(Venue(VenueType.Biergarten), 10f);
        Check(Count(garden, TerraceKind.BeerBench) >= 6 && Count(garden, TerraceKind.BistroSet) == 0,
            $"a beer garden: beer benches in two rows ({Count(garden, TerraceKind.BeerBench)})");

        // no carriageway known (no roads loaded): the odds and the ground decide
        Check(VenueTerraces.Carriageway(null, Vector3.Zero) == float.MaxValue, "no roads: nothing to keep off");

        foreach (var kind in Enum.GetValues<TerraceKind>())
            Check(TerraceProps.Mesh(kind, 1).Mesh.GetSurfaceCount() > 0, $"the {kind} builds");

        GD.Print($"[terrace] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
