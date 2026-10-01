using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// An Africa Twin, a different one each time, stands in front of one building near Riddes
/// (46.166643 N, 7.214411 E — the game's creator's pick). Whenever the tile under that point comes
/// into the streamed rings and no egg bike is parked there, one is placed; ridden away, it is put
/// back the next time the area loads. One check per tile load, nothing per frame.
///
/// <para>
/// The server decides and spawns it through <see cref="VehicleManager.Place"/>, so every player
/// sees the same bike and any of them can claim it; offline the client does the same locally.
/// The slot is worked out once from the tile's own files (buildings, cover, roads, trees, the
/// full height grid) read through the terrain source and then dropped: a parking cell near the
/// building if the cover raster has one, else a 2.5 m bay on the far side of the lane that runs
/// past its entrance (where the aerial photo shows the rows, and TLM maps no car park).
/// </para>
/// </summary>
public partial class AfricaTwinEgg : Node
{
    public const double Lat = 46.16664300470599, Lon = 7.214410897465706;
    private const string EggName = "veh_africatwin_egg";

    private readonly ChunkManager _chunks;
    private readonly TileId _tile;
    private readonly double _e, _n;
    private List<Slot>? _slots;   // computed once; the data does not change while running
    private bool _busy;

    public readonly record struct Slot(double E, double N, double Height, float Yaw, bool Parking);

    public AfricaTwinEgg(ChunkManager chunks)
    {
        Name = "AfricaTwinEgg";
        _chunks = chunks;
        (_e, _n) = Gpx.SwissProjection.ToLv95(Lat, Lon);
        _tile = TileId.FromLv95(_e, _n);
    }

    public override void _Ready()
    {
        _chunks.TileEntered += OnTileEntered;
        // a local egg from an offline session would double the server's: the server's word wins
        // (a method, not a lambda: the multiplayer API outlives this node, and a static lambda would
        // stay connected after the world is left, and be connected again by the next one)
        Multiplayer.ConnectedToServer += DropLocalEgg;
    }

    private void DropLocalEgg() => VehicleManager.Instance?.GetNodeOrNull(EggName)?.QueueFree();

    public override void _ExitTree()
    {
        _chunks.TileEntered -= OnTileEntered;
        Multiplayer.ConnectedToServer -= DropLocalEgg;
    }

    /// <summary>The dedicated server decides; a client only when it has no server at all.</summary>
    private bool Decides => Net.NetworkManager.DedicatedServer
        || Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer;

    private async void OnTileEntered(TileId id)
    {
        if (id != _tile || _busy || !Decides) return;
        if (VehicleManager.Instance is not { } vehicles) return;
        if (vehicles.GetNodeOrNull<VehicleBody>(EggName) is { Wrecked: false }) return;   // still parked there
        if (_chunks.Source is not { } source || _chunks.Origin is not { } origin) return;

        _busy = true;
        try
        {
            // off the main thread: a full grid and a tile's trees are tens of milliseconds to decode
            _slots ??= await Task.Run(() => FindSlots(source, _e, _n));
            if (!IsInsideTree() || _slots.Count == 0 || !Decides) return;
            if (vehicles.GetNodeOrNull(EggName) is { } old) old.Free();   // a wreck, or long gone

            var free = _slots.Where(s => !vehicles.GetChildren().OfType<VehicleBody>().Any(v =>
                    v.GlobalPosition.DistanceTo(origin.ToWorld(s.E, s.N, s.Height)) < 3f)).ToList();
            if (free.Count == 0) return;
            var slot = free[Random.Shared.Next(free.Count)];
            var kind = AfricaTwins[Random.Shared.Next(AfricaTwins.Length)];
            var ride = Rideable.Create(kind)!;
            // nose in or backed in, as the cars beside it would be
            float yaw = slot.Yaw + (Random.Shared.Next(2) == 0 ? 0f : Mathf.Pi);
            var state = new VehicleState(kind, origin.ToWorld(slot.E, slot.N, slot.Height), yaw,
                Vector3.Zero, ride.MaxHealth, EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
            if (vehicles.Place(state, EggName) != null)
                GD.Print($"[egg] {MotorbikeCatalog.For(kind)!.Label} parked at LV95 {slot.E:F1}/{slot.N:F1} "
                    + $"({(slot.Parking ? "mapped parking" : "bay across the entrance lane")}, {_slots.Count} slots)");
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[egg] no Africa Twin this time: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static readonly RideKind[] AfricaTwins = MotorbikeCatalog.All
        .Where(b => b.Label.Contains("Africa Twin")).Select(b => b.Kind).ToArray();

    // ---- the slot --------------------------------------------------------------------------

    private const int Window = 60;          // metres around the point that are searched
    private const float MaxFromBuilding = 40f;

    /// <summary>
    /// Every good place for the bike in front of the building at (e, n), best first; empty if the
    /// tile is missing. Pure data: runs on a worker.
    /// </summary>
    public static List<Slot> FindSlots(IChunkSource source, double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
        var buildings = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
        if (grid == null || grid.Stride != 1 || buildings == null) return new();
        var cover = source.LoadCoverAsync(id).GetAwaiter().GetResult();
        var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
        var trees = source.LoadTreesAsync(id).GetAwaiter().GetResult();

        // tile-local metres, X east and Z south from the NW corner, like every tile file.
        // ponytail: one tile only; a point within 60 m of a tile edge would need its neighbours
        int px = (int)(e - id.MinE), pz = (int)(id.MaxN - n);
        int x0 = Math.Max(0, px - Window), x1 = Math.Min(ChunkFormat.GridSize - 2, px + Window);
        int z0 = Math.Max(0, pz - Window), z1 = Math.Min(ChunkFormat.GridSize - 2, pz + Window);
        int w = x1 - x0 + 1, h = z1 - z0 + 1;
        var owner = new int[w * h];
        Array.Fill(owner, -1);
        int Idx(int x, int z) => (z - z0) * w + (x - x0);
        bool In(int x, int z) => x >= x0 && x <= x1 && z >= z0 && z <= z1;

        // building footprints: every triangle's plan view, rasterised onto 1 m cells
        for (int b = 0; b < buildings.Buildings.Count; b++)
        {
            var t = buildings.Buildings[b].Triangles;
            for (int o = 0; o < t.Length; o += 9)
                FillTriangle(t[o], t[o + 2], t[o + 3], t[o + 5], t[o + 6], t[o + 8], (x, z) =>
                {
                    if (In(x, z)) owner[Idx(x, z)] = b;
                });
        }

        // the building: the one under the point, else the nearest within 30 m
        int target = In(px, pz) ? owner[Idx(px, pz)] : -1;
        if (target < 0)
        {
            float best = 30f;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (owner[Idx(x, z)] is >= 0 and var o && Dist(x, z, px, pz) is var d && d < best) { best = d; target = o; }
        }

        // its outline: cells of it with a neighbour that is not
        var edge = new List<(int X, int Z)>();
        if (target >= 0)
            for (int z = z0 + 1; z < z1; z++)
                for (int x = x0 + 1; x < x1; x++)
                    if (owner[Idx(x, z)] == target && (owner[Idx(x + 1, z)] != target || owner[Idx(x - 1, z)] != target
                        || owner[Idx(x, z + 1)] != target || owner[Idx(x, z - 1)] != target))
                        edge.Add((x, z));
        if (edge.Count == 0) edge.Add((px, pz));   // no building: the point itself stands in for it

        // roads a car drives on, as 2D polylines
        var drivable = roads?.Segments.Where(s => s.Class <= RoadClass.Square && s.Class is not (RoadClass.Track or RoadClass.Path))
            .ToList() ?? new();
        float RoadClearance(float x, float z)
        {
            float worst = float.MaxValue;
            foreach (var s in drivable)
                for (int i = 0; i + 1 < s.PointCount; i++)
                    worst = Math.Min(worst, SegDist(x, z, s.Points[i * 3], s.Points[i * 3 + 2], s.Points[i * 3 + 3], s.Points[i * 3 + 5]) - s.Width * 0.5f);
            return worst;
        }

        var treeSpots = (trees ?? new()).Where(t => In((int)t.X, (int)t.Z)).Select(t => (t.X, t.Z)).ToList();

        // Somewhere a bike can stand: 2 m clear of every building and 1.5 m of every tree, on
        // open ground (not in the orchard or wood the bays border), within the searched window
        // and level — a bike on its stand wants flat ground.
        bool Free(float fx, float fz)
        {
            int x = (int)fx, z = (int)fz;
            if (x < x0 + 3 || x > x1 - 3 || z < z0 + 3 || z > z1 - 3) return false;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                    if (owner[Idx(x + dx, z + dz)] >= 0) return false;
            if (treeSpots.Any(t => Dist(t.X, t.Z, fx, fz) < 1.5f)) return false;
            var c = cover != null ? (CoverClass)cover[z * CoverFormat.Size + x] : CoverClass.Open;
            if (CoverFormat.IsWooded(c) || CoverFormat.IsPlanted(c)
                || c is CoverClass.Water or CoverClass.Wetland or CoverClass.Vineyard or CoverClass.Glacier)
                return false;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int dz = -1; dz <= 2; dz++)
                for (int dx = -1; dx <= 2; dx++)
                {
                    double y = grid.HeightMetersAt(x + dx, z + dz);
                    lo = Math.Min(lo, y); hi = Math.Max(hi, y);
                }
            return hi - lo <= 0.3;
        }

        var slots = new List<Slot>();
        void Add(float fx, float fz, float faceX, float faceZ, bool parking)
        {
            if (slots.Any(o => Dist(o.E, o.N, id.MinE + fx, id.MaxN - fz) < BaySpacing - 0.1f)) return;
            // forward is the body's -Z turned by the yaw
            slots.Add(new Slot(id.MinE + fx, id.MaxN - fz, grid.SampleMeshHeight(id.MinE + fx, id.MaxN - fz),
                Mathf.Atan2(-faceX, -faceZ), parking));
        }

        // 1. Parking the cover raster knows about (tlm_areale_verkehrsareal, or the rows traced from
        //    the aerial photo in docs/data/cover_overrides.json), near the building: mid-bay, half a
        //    5 m bay off the aisle's asphalt like the cars there, nose to the nearest road.
        if (cover != null)
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float fx = x + 0.5f, fz = z + 0.5f;
                    if (!CoverFormat.IsParking((CoverClass)cover[z * CoverFormat.Size + x])
                        || NearestEdge(edge, fx, fz).Dist > MaxFromBuilding || !Free(fx, fz)
                        || RoadClearance(fx, fz) < BayDepth * 0.5f - 0.1f) continue;
                    var (qx, qz) = NearestRoadPoint(drivable, fx, fz);
                    // Square to the row, not to the nearest asphalt: at a row's end that is the
                    // cross street. The row's long axis is the principal axis of the parking cells
                    // within 8 m; the bay faces across it, towards the road side.
                    var (ax, az) = RowAxis(cover, x, z, 8);
                    float nx = -az, nz = ax;
                    if (nx * (qx - fx) + nz * (qz - fz) < 0) { nx = -nx; nz = -nz; }
                    Add(fx, fz, nx, nz, parking: true);
                }

        // 2. TLM maps no car park here (checked: nothing in tlm_areale_verkehrsareal within 300 m),
        //    but the aerial photo shows the bays: two rows along the far side of the access lane
        //    that runs past the entrance, between the lane and the orchard, perpendicular to it.
        //    So: a bay every 2.5 m along every drivable road passing in front of the building
        //    (within 25 m of it), on the side away from it, its centre half a 5 m bay off the
        //    asphalt, facing away from the lane (nose in; the spawn may back it in instead).
        if (slots.Count == 0)
            foreach (var s in drivable)
            {
                float next = 0f;   // how far into this piece the next bay falls, so they stay 2.5 m apart across vertices
                for (int i = 0; i + 1 < s.PointCount; i++)
                {
                    float ax = s.Points[i * 3], az = s.Points[i * 3 + 2], bx = s.Points[i * 3 + 3], bz = s.Points[i * 3 + 5];
                    float len = Dist(ax, az, bx, bz);
                    if (len < 1e-3f) continue;
                    float tx = (bx - ax) / len, tz = (bz - az) / len;
                    float d = next;
                    for (; d < len; d += BaySpacing)
                    {
                        float qx = ax + tx * d, qz = az + tz * d;
                        var (toBuilding, dir) = NearestEdge(edge, qx, qz);
                        if (toBuilding > 25f || Dist(qx, qz, px, pz) > MaxFromBuilding) continue;
                        // the normal pointing away from the building
                        float nx = -tz, nz = tx;
                        if (nx * dir.X + nz * dir.Z > 0) { nx = -nx; nz = -nz; }
                        float off = s.Width * 0.5f + BayDepth * 0.5f;
                        float fx = qx + nx * off, fz = qz + nz * off;
                        if (Free(fx, fz) && RoadClearance(fx, fz) >= BayDepth * 0.5f - 0.1f)
                            Add(fx, fz, nx, nz, parking: false);
                    }
                    next = d - len;
                }
            }
        return slots;
    }

    private const float BaySpacing = 2.5f, BayDepth = 5f;

    /// <summary>Unit long axis (principal axis) of the parking cells within r of (x, z).</summary>
    private static (float X, float Z) RowAxis(byte[] cover, int x, int z, int r)
    {
        double n = 0, sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int cx = x + dx, cz = z + dz;
                if (dx * dx + dz * dz > r * r || (uint)cx >= CoverFormat.Size || (uint)cz >= CoverFormat.Size
                    || !CoverFormat.IsParking((CoverClass)cover[cz * CoverFormat.Size + cx])) continue;
                n++; sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz;
            }
        double cxx = sxx / n - (sx / n) * (sx / n), czz = szz / n - (sz / n) * (sz / n), cxz = sxz / n - (sx / n) * (sz / n);
        double angle = 0.5 * Math.Atan2(2 * cxz, cxx - czz);   // major axis of the 2x2 covariance
        return ((float)Math.Cos(angle), (float)Math.Sin(angle));
    }

    private static (float X, float Z) NearestRoadPoint(List<RoadSegment> roads, float x, float z)
    {
        (float, float) best = (x, z - 1);
        float bestD = float.MaxValue;
        foreach (var s in roads)
            for (int i = 0; i + 1 < s.PointCount; i++)
            {
                var q = Closest(x, z, s.Points[i * 3], s.Points[i * 3 + 2], s.Points[i * 3 + 3], s.Points[i * 3 + 5]);
                float d = Dist(x, z, q.X, q.Z);
                if (d < bestD) { bestD = d; best = q; }
            }
        return best;
    }

    private static (float Dist, (float X, float Z) Dir) NearestEdge(List<(int X, int Z)> edge, float x, float z)
    {
        float best = float.MaxValue;
        (float, float) dir = (0, -1);
        foreach (var (ex, ez) in edge)
        {
            float d = Dist(x, z, ex + 0.5f, ez + 0.5f);
            if (d < best) { best = d; dir = (ex + 0.5f - x, ez + 0.5f - z); }
        }
        return (best, dir);
    }

    private static float Dist(double ax, double az, double bx, double bz) =>
        (float)Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    private static (float X, float Z) Closest(float px, float pz, float ax, float az, float bx, float bz)
    {
        float vx = bx - ax, vz = bz - az;
        float len = vx * vx + vz * vz;
        float t = len < 1e-6f ? 0 : Math.Clamp(((px - ax) * vx + (pz - az) * vz) / len, 0, 1);
        return (ax + vx * t, az + vz * t);
    }

    private static float SegDist(float px, float pz, float ax, float az, float bx, float bz)
    {
        var (qx, qz) = Closest(px, pz, ax, az, bx, bz);
        return Dist(px, pz, qx, qz);
    }

    /// <summary>Calls <paramref name="cell"/> for every 1 m cell whose centre lies in the plan triangle.</summary>
    private static void FillTriangle(float ax, float az, float bx, float bz, float cx, float cz, Action<int, int> cell)
    {
        float den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(den) < 1e-6f) return;   // a wall seen edge-on from above covers nothing
        int minX = (int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))), maxX = (int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx)));
        int minZ = (int)MathF.Floor(Math.Min(az, Math.Min(bz, cz))), maxZ = (int)MathF.Ceiling(Math.Max(az, Math.Max(bz, cz)));
        for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                float qx = x + 0.5f, qz = z + 0.5f;
                float l1 = ((bz - cz) * (qx - cx) + (cx - bx) * (qz - cz)) / den;
                float l2 = ((cz - az) * (qx - cx) + (ax - cx) * (qz - cz)) / den;
                if (l1 >= 0 && l2 >= 0 && l1 + l2 <= 1) cell(x, z);
            }
    }
}
