using Godot;

namespace UnitSport.Interiors.Games;

/// <summary>
/// What each playable piece is (#1324, #1326): its furniture type, the game it holds, how near and from where a
/// player uses it, the box outlined, and on a client the rig that shows it and the controls that play it. One
/// row per game; a game's own PR adds its row.
/// </summary>
public sealed record GameSpec(
    FurnitureType Type,
    GameKind Kind,
    string Title,
    Func<FurniturePlan, TableGame> Make,
    // the piece's frame (back −Z, front +Z): where a body may use it from, the box outlined round it
    Func<Vector3, FurniturePlan, bool> UseFrom,
    Func<FurniturePlan, (Vector3 Centre, Vector3 Size)> Box,
    Func<FurniturePlan, TableRig>? Rig = null,
    Func<TableGamePlay.Mode>? Play = null,
    // how far from the piece E finds it [m]: a dartboard is played from its throw line
    float Reach = 1.6f);

public static class GameCatalog
{
    private static readonly List<GameSpec> Specs = new();

    static GameCatalog()
    {
        // each game adds its row here
        Add(Pool.PoolPlay.Spec);
        Add(Darts.DartsPlay.Spec);
        Add(Foosball.FoosballPlay.Spec);
    }

    public static void Add(GameSpec spec) => Specs.Add(spec);

    public static GameSpec? Of(FurnitureType t)
    {
        foreach (var s in Specs)
            if (s.Type == t) return s;
        return null;
    }

    public static GameSpec? Of(GameKind k)
    {
        foreach (var s in Specs)
            if (s.Kind == k) return s;
        return null;
    }

    public static bool Playable(FurnitureType t) => Of(t) != null;

    public static IEnumerable<GameSpec> All => Specs;

    /// <summary>The farthest any game is used from (E's search; each game's own <see cref="GameSpec.UseFrom"/> then decides).</summary>
    public static float MaxReach => Specs.Count == 0 ? 0 : Specs.Max(s => s.Reach);

    /// <summary>Within <paramref name="side"/> of the piece's footprint on every side (a table walked round).</summary>
    public static bool Round(Vector3 local, FurniturePlan f, float side) =>
        Mathf.Abs(local.X) < f.W / 2 + side && Mathf.Abs(local.Z) < f.D / 2 + side && Mathf.Abs(local.Y) < 2.5f;

    /// <summary>In front of the piece (+Z), within <paramref name="depth"/> of its face and its width plus a little.</summary>
    public static bool Front(Vector3 local, FurniturePlan f, float depth) =>
        Mathf.Abs(local.X) < f.W / 2 + 0.3f && local.Z > f.D / 2 - 0.1f && local.Z < f.D / 2 + depth && Mathf.Abs(local.Y) < 2.5f;
}
