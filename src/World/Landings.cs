using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The region's boat landings and harbour jetties (#377): the piers the tiles build (walkable, with
/// collision, <see cref="Terrain.PierMeshBuilder"/>) and the berths the steamer lies at.
/// <c>landings.json</c> from the chunk source (the preprocessor's <c>--landings</c> pass; a fixture
/// course plans its own; a streaming client gets the server's). Same data on every peer, so every
/// peer builds the same piers. <b>The API for #379</b> (steamer AI): <see cref="All"/>,
/// <see cref="Find"/>, <see cref="Nearest"/>, <see cref="Berths"/>, and per berth its keel
/// position and yaw in this peer's frame (<see cref="KeelWorld"/>, <see cref="Yaw"/>); see
/// <c>docs/notes/world/landings.md</c>.
/// </summary>
public static class Landings
{
    private static volatile LandingIndex _current = new();

    /// <summary>What the tiles build and the berths are read from; empty (never null) before anything is loaded. Any thread.</summary>
    public static LandingIndex Current => _current;

    /// <summary>Main thread: a new index was adopted (loaded at boot, or streamed from a server).</summary>
    public static event Action? Changed;

    /// <summary>Every landing, sorted by name.</summary>
    public static IReadOnlyList<Landing> All => _current.Landings;

    /// <summary>The landings with a steamer berth that floats it (<see cref="LandingBerth.Fits"/>).</summary>
    public static IEnumerable<Landing> Berths => _current.Landings.Where(l => l.Berth is { Fits: true });

    /// <summary>The landing named <paramref name="name"/>, or whose name starts with it ("Nyon" finds "Nyon (lac)"); null if none.</summary>
    public static Landing? Find(string name) => _current.Find(name);

    /// <summary>The landing nearest an LV95 point; null when there are none.</summary>
    public static Landing? Nearest(double e, double n) => _current.Nearest(e, n);

    /// <summary>
    /// The landings to start with: <c>--landings &lt;file&gt;</c> when given (a <c>landings.json</c> tried
    /// out beside tiles it was not written into), else the chunk source's.
    /// </summary>
    public static async Task<LandingIndex?> LoadAsync(Terrain.IChunkSource source)
    {
        if (CmdArgs.Value("--landings", notFlag: true) is { } file)
        {
            try { return LandingIndex.FromJson(await System.IO.File.ReadAllTextAsync(file)); }
            catch (Exception e) { GD.PushWarning($"[landings] {file}: {e.Message}"); }
        }
        return await source.LoadLandingsAsync();
    }

    /// <summary>Adopts an index (null: none). Main thread.</summary>
    public static void Use(LandingIndex? index)
    {
        _current = index ?? new LandingIndex();
        if (index is { Landings.Count: > 0 } || index is { Jetties.Count: > 0 })
            GD.Print($"[landings] {_current.Landings.Count} landings ({Berths.Count()} steamer berths), {_current.Jetties.Count} jetties");
        Changed?.Invoke();
    }

    /// <summary>The steamer's keel at a berth, this peer's world frame: on the still water, at the hull's draught.</summary>
    public static Vector3 KeelWorld(LandingBerth berth, WorldOrigin origin, float draught) =>
        origin.ToWorld(berth.E, berth.N, berth.Level - draught);

    /// <summary>The steamer's node yaw at a berth (Godot: 0 faces -Z, north).</summary>
    public static float Yaw(LandingBerth berth) => -Mathf.DegToRad((float)berth.Heading);

    /// <summary>The bow's direction at a berth, this peer's world frame (level).</summary>
    public static Vector3 Bow(LandingBerth berth)
    {
        float h = Mathf.DegToRad((float)berth.Heading);
        return new Vector3(Mathf.Sin(h), 0, -Mathf.Cos(h));
    }
}
