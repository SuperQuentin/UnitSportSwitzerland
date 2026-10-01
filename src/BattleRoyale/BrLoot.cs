using Godot;
using UnitSport.Core;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Server: what a match puts on the ground (#194). Densities are per km² of the region; positions
/// come from the region's own road network (the server has the same road files as the clients),
/// seeded by the match, so nothing here is stored. Docs: <c>docs/notes/br/loot.md</c>.
/// </summary>
public static class BrLoot
{
    public const float SupplyPerKm2 = 7f, MilitaryPerKm2 = 0.5f, VehiclesPerKm2 = 0.7f;
    /// <summary>One airdrop per this many km², at least two, spread over <see cref="DropPhases"/>.</summary>
    public const float KmPerDrop = 12f;
    public static readonly int[] DropPhases = { 2, 4, 6 };
    public const string VehiclePrefix = "br_v";
    /// <summary>Metres between two road points.</summary>
    private const float Spacing = 25f;
    /// <summary>Fewer road points than this (a generated world, a roadless alp): crates go anywhere in the square.</summary>
    private const int FewRoads = 200;

    /// <summary>A road point: LV95 position, altitude, the road's heading (rad, 0 = north), its class.</summary>
    public readonly record struct RoadPoint(double E, double N, float Alt, float Heading, RoadClass Class);

    /// <summary>Every usable road vertex in the square (no motorways, tunnels, bridges or footpaths).</summary>
    public static async Task<List<RoadPoint>> RoadPoints(IChunkSource source, BrArea area)
    {
        var points = new List<RoadPoint>();
        double half = area.Side * 0.5;
        for (int e = (int)Math.Floor((area.E - half) / 1000); e <= (int)Math.Floor((area.E + half - 1) / 1000); e++)
            for (int n = (int)Math.Floor((area.N - half) / 1000); n <= (int)Math.Floor((area.N + half - 1) / 1000); n++)
            {
                var id = new TileId(e, n);
                RoadTile? tile = null;
                try { tile = await source.LoadRoadsAsync(id); }
                catch (Exception ex) { GD.PushWarning($"[br] roads {id}: {ex.Message}"); }
                if (tile == null) continue;
                foreach (var s in tile.Segments)
                {
                    if (s.Class is not (RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Track)) continue;
                    if ((s.Flags & (RoadFlags.Tunnel | RoadFlags.Bridge)) != 0) continue;
                    for (int i = 0; i + 1 < s.PointCount; i++)
                    {
                        float x0 = s.Points[i * 3], y0 = s.Points[i * 3 + 1], z0 = s.Points[i * 3 + 2];
                        float dx = s.Points[i * 3 + 3] - x0, dy = s.Points[i * 3 + 4] - y0, dz = s.Points[i * 3 + 5] - z0;
                        float heading = Mathf.Atan2(dx, -dz);
                        // a point every 25 m along the road, whatever its vertex spacing
                        int steps = Math.Max(1, (int)(Mathf.Sqrt(dx * dx + dz * dz) / Spacing));
                        for (int k = 0; k < steps; k++)
                        {
                            float t = k / (float)steps;
                            double pe = id.MinE + x0 + dx * t, pn = id.MaxN - (z0 + dz * t);
                            if (area.Contains(pe, pn)) points.Add(new RoadPoint(pe, pn, y0 + dy * t, heading, s.Class));
                        }
                    }
                }
            }
        return points;
    }

    /// <summary>Supply crates beside the roads and the odd military crate on a track, seeded by the match.</summary>
    public static List<Crate> RoadsideCrates(List<RoadPoint> roads, BrArea area, int seed)
    {
        var crates = new List<Crate>();
        var rng = new Random(seed ^ 0x2c1b3c6d);
        float km2 = area.Side * area.Side / 1e6f;
        if (roads.Count < FewRoads)
        {
            // too few roads to line them with crates: scattered over the square instead, on the ground
            // wherever each client finds it (no altitude is known here)
            roads = new List<RoadPoint>(roads);
            for (int i = 0; i < 2000; i++)
                roads.Add(new RoadPoint(area.E + (rng.NextDouble() - 0.5) * area.Side, area.N + (rng.NextDouble() - 0.5) * area.Side,
                    BrCrates.Ground, (float)(rng.NextDouble() * Math.Tau), RoadClass.Track));
        }
        var tracks = roads.Where(r => r.Class is RoadClass.Track or RoadClass.Lane).ToList();
        void Add(RoadPoint r, CrateStyle style, MatchTable table, string label, Random roll)
        {
            // a few metres off the road, to one side
            float side = (rng.Next(2) == 0 ? -1f : 1f) * (2.5f + (float)rng.NextDouble() * 2f);
            double e = r.E + Math.Cos(r.Heading) * side, n = r.N - Math.Sin(r.Heading) * side;
            var c = new Crate { Style = style, E = e, N = n, Label = label };   // on the ground: each client snaps it
            c.SetStacks(MatchLoot.Roll(table, roll));
            if (c.Ids.Length > 0) crates.Add(c);
        }
        int supply = (int)(km2 * SupplyPerKm2), military = Math.Max(1, (int)(km2 * MilitaryPerKm2));
        for (int i = 0; i < supply; i++)
            Add(roads[rng.Next(roads.Count)], CrateStyle.Supply, MatchTable.Supply, "the supply crate", new Random(seed + i * 7919));
        // military crates stand in threes, out on the tracks: an army depot
        var depots = tracks.Count > 0 ? tracks : roads;
        for (int i = 0; i < military; i++)
        {
            var at = depots[rng.Next(depots.Count)];
            for (int k = 0; k < 3; k++)
                Add(at with { E = at.E + rng.Next(-4, 5), N = at.N + rng.Next(-4, 5) }, CrateStyle.Military, MatchTable.Military,
                    "the army crate", new Random(seed + 1_000_003 + i * 31 + k));
        }
        return crates;
    }

    /// <summary>Cars and motorbikes parked by the roads, a helicopter in a big region; returns how many.</summary>
    public static int ParkVehicles(List<RoadPoint> roads, BrArea area, int seed, WorldOrigin origin)
    {
        if (VehicleManager.Instance is not { } vehicles || roads.Count == 0) return 0;
        var rng = new Random(seed ^ 0x6a09e667);
        var parkable = roads.Where(r => r.Class is RoadClass.Road or RoadClass.Minor or RoadClass.Major).ToList();
        if (parkable.Count == 0) parkable = roads;
        var bikes = MotorbikeCatalog.All.Select(b => b.Kind).ToArray();
        int count = (int)(area.Side * area.Side / 1e6f * VehiclesPerKm2), placed = 0;
        for (int i = 0; i < count; i++)
        {
            var r = parkable[rng.Next(parkable.Count)];
            var kind = rng.NextDouble() < 0.3 && bikes.Length > 0
                ? bikes[rng.Next(bikes.Length)]
                : (RideKind)(CarCatalog.First + rng.Next(CarCatalog.All.Count));
            if (Place(vehicles, origin, kind, r, 3.5f, $"{VehiclePrefix}{i}")) placed++;
        }
        if (area.Side >= 6000f && parkable.Count > 0 && Place(vehicles, origin, RideKind.Helicopter, parkable[rng.Next(parkable.Count)], 14f, $"{VehiclePrefix}heli"))
            placed++;
        return placed;
    }

    private static bool Place(VehicleManager vehicles, WorldOrigin origin, RideKind kind, RoadPoint r, float side, string name)
    {
        if (Rideable.Create(kind) is not { IsVehicle: true } ride) return false;
        // on the verge, nose along the road
        double e = r.E + Math.Cos(r.Heading) * side, n = r.N - Math.Sin(r.Heading) * side;
        var state = new VehicleState(kind, origin.ToWorld(e, n, r.Alt + 0.6f), -r.Heading, Vector3.Zero, ride.MaxHealth,
            EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
        return vehicles.Place(state, name) != null;
    }

    /// <summary>The match's vehicles, wrecks included, out of the world.</summary>
    public static void RemoveVehicles()
    {
        if (VehicleManager.Instance is not { } vehicles) return;
        foreach (var v in vehicles.GetChildren().OfType<Node>().Where(n => n.Name.ToString().StartsWith(VehiclePrefix)).ToList())
            v.QueueFree();
    }

    /// <summary>How many airdrops fall at <paramref name="phase"/>'s start in a region of this size (0 for other phases).</summary>
    public static int DropsAt(int phase, float side)
    {
        int index = Array.IndexOf(DropPhases, phase);
        if (index < 0) return 0;
        int total = Math.Max(2, (int)Math.Round(side * side / 1e6f / KmPerDrop));
        return total / DropPhases.Length + (index < total % DropPhases.Length ? 1 : 0);
    }

    /// <summary>An airdrop somewhere inside the next circle, landing <paramref name="fall"/> seconds from now.</summary>
    public static Crate Airdrop(BrArea area, ZoneState zone, Random rng, double now, double fall)
    {
        float r = zone.NextRadius * 0.7f * Mathf.Sqrt((float)rng.NextDouble()), a = (float)(rng.NextDouble() * Math.Tau);
        var at = zone.NextCentre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        var c = new Crate { Style = CrateStyle.Airdrop, E = area.E + at.X, N = area.N + at.Y, LandsAt = now + fall, Label = "the supply drop" };
        c.SetStacks(MatchLoot.Roll(MatchTable.Airdrop, rng));
        return c;
    }
}
