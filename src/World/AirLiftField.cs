using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The game's side of <see cref="AirLift"/> (#1225, <c>docs/notes/world/air-lift.md</c>): the ground
/// from the streamed tiles (<see cref="ChunkManager"/>), the hour from the shared world clock, burning
/// campfires registered as updraft volumes. <see cref="AirAt"/> is what a glider asks once a physics
/// step (the own flyer only, <c>FootPlayer.FlyPhysics</c>); it is zero when the rules say no lift
/// (<see cref="Permissions.LiftOn"/>: <c>--lift off</c>, a Battle Royale match).
/// </summary>
public static class AirLiftField
{
    /// <summary>A burning campfire's updraft: radius, height and speed (the player-lit Genshin updraft).</summary>
    public const float CampfireRadius = 4f, CampfireTop = 25f, CampfireSpeed = 6f;

    /// <summary>Checks: the hour the thermals read instead of the world clock's (null: the clock's).</summary>
    public static double? HourOverride { get; set; }

    /// <summary>The ground of the tiles in use, LV95 in and out.</summary>
    private sealed class ChunkTerrain : IAirLiftTerrain
    {
        public ChunkManager? Chunks;
        public WorldOrigin? Origin;

        public bool TryHeight(double e, double n, out float height)
        {
            height = 0f;
            return Chunks != null && Origin != null && Chunks.TryGetHeight(Origin.ToWorld(e, n, 0), out height);
        }

        public bool TryCover(double e, double n, out CoverClass cover)
        {
            cover = CoverClass.Open;
            return Chunks != null && Origin != null && Chunks.TryGetCover(Origin.ToWorld(e, n, 0), out cover);
        }

        public double HourAt(double serverNow) =>
            HourOverride ?? (WorldClock.Active ? WorldClock.HourAt(serverNow) : DayNight.Instance?.Hour ?? 12.0);

        public long DayAt(double serverNow) =>
            WorldClock.Active ? (long)Math.Floor(WorldClock.EnvAt(SimClock.SimAt(serverNow)) / 86400.0) : 0;
    }

    private static readonly ChunkTerrain Ground = new();
    private static double _nextFires = double.NegativeInfinity;
    private static readonly HashSet<long> Fires = new();
    private static readonly List<long> Gone = new();

    /// <summary>The terrain the lift reads (tests and probes may pass their own to <see cref="AirLift"/>).</summary>
    public static IAirLiftTerrain Terrain => Ground;

    /// <summary>Points the field at the tiles in use (the flyer's own chunks and origin); cheap when unchanged.</summary>
    public static void Use(ChunkManager? chunks, WorldOrigin? origin)
    {
        if (ReferenceEquals(Ground.Chunks, chunks) && ReferenceEquals(Ground.Origin, origin)) return;
        Ground.Chunks = chunks;
        Ground.Origin = origin;
        AirLift.Forget(Ground);
    }

    /// <summary>
    /// The air's velocity at a world point now (world axes, m/s), or zero where the rules say no lift
    /// or nothing is loaded. Once a physics step for the own flyer.
    /// </summary>
    public static Vector3 AirAt(Vector3 global)
    {
        if (!Permissions.LiftOn || Ground.Origin is not { } origin) return Vector3.Zero;
        double now = Net.ClockSync.ServerNow;
        SyncCampfires(now);
        var p = origin.ToGlobal(global);
        return AirLift.At(p.E, p.N, (float)p.Alt, now, Ground);
    }

    /// <summary>The thermals alive near a world point (the cues), strongest first; how many were written.</summary>
    public static int ThermalsNear(Vector3 global, double radius, Span<Thermal> into)
    {
        if (!Permissions.LiftOn || Ground.Origin is not { } origin) return 0;
        var p = origin.ToGlobal(global);
        return AirLift.ThermalsNear(p.E, p.N, radius, Net.ClockSync.ServerNow, Ground, into);
    }

    /// <summary>A thermal's source as a world point (the cues place their specks there).</summary>
    public static Vector3? ToWorld(in Thermal t) => Ground.Origin?.ToWorld(t.E, t.N, t.Ground);

    /// <summary>
    /// Burning campfires as updraft volumes, once a second at most: added when lit, removed when out or
    /// gone. The same on every peer (the placed objects and the env clock are shared).
    /// </summary>
    private static void SyncCampfires(double now)
    {
        if (now < _nextFires) return;
        _nextFires = now + 1.0;
        var placed = Items.PlacedObjects.Instance;
        double env = WorldClock.EnvNow;
        foreach (long key in Fires) Gone.Add(key);
        if (placed != null)
            foreach (var (id, o) in placed.All)
            {
                if (o.Kind != Items.PlacedKind.Campfire || !Crafting.CampfireClock.Burning(o.Payload, env)) continue;
                AirLift.Volumes.Set(id, o.E, o.N, o.Altitude, CampfireRadius, CampfireTop, CampfireSpeed);
                Fires.Add(id);
                Gone.Remove(id);
            }
        foreach (long key in Gone)
        {
            AirLift.Volumes.Remove(key);
            Fires.Remove(key);
        }
        Gone.Clear();
        AirLift.Volumes.Prune(now);
    }
}
