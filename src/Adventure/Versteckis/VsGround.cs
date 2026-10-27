using System.Threading.Tasks;
using Godot;
using UnitSport.Farming;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// A game's area as one peer reads it from its tiles (#1268): the buildings (their footprints and doors, as the
/// interiors work them out), the roads, the cover and the farm fields, the herds' grazing spots and the coarse ground,
/// turned into the plain inputs of <see cref="HidePlan"/> and <see cref="VsSites"/>. The server and every client load
/// the same tiles and compute the same decor; nothing of it goes on the wire. Loaded on the thread pool.
/// </summary>
public sealed class VsGround
{
    public double E { get; private init; }
    public double N { get; private init; }
    /// <summary>The radius the decor and the sites were planned for (<see cref="Plan"/>).</summary>
    public float Radius { get; private set; }
    public int Seed { get; private init; }
    public List<VsBuilding> Buildings { get; } = new();
    public List<(double E, double N)[]> Roads { get; } = new();
    public List<VsDecor> Decor { get; private set; } = new();
    public List<VsObjective> Sites { get; private set; } = new();
    public List<VsProp> Kinds { get; private set; } = new();
    /// <summary>The church (or chapel) door nearest the centre, else the centre.</summary>
    public (double E, double N) Door { get; private set; }
    public VsAreaFacts Facts { get; private set; }
    private readonly Dictionary<TileId, (ChunkGrid? Grid, byte[]? Cover, FieldTile? Fields)> _tiles = new();
    private readonly List<(double E, double N)> _herds = new();

    /// <summary>The coarse ground at a point (the tiles read), or null outside them.</summary>
    public float? GroundAt(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        return _tiles.TryGetValue(id, out var t) && t.Grid != null ? (float)t.Grid.SampleHeight(e, n) : null;
    }

    /// <summary>Open grass at a point: open cover, and no crop but meadow or pasture.</summary>
    public bool Grass(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (!_tiles.TryGetValue(id, out var t)) return false;
        if (t.Cover is { } cover)
        {
            int x = (int)(e - id.MinE), z = (int)(id.MaxN - n);
            const int side = ChunkFormat.GridSize;
            if ((uint)x < side && (uint)z < side && cover.Length >= side * side && (CoverClass)cover[z * side + x] != CoverClass.Open) return false;
        }
        if (t.Fields is { } f)
        {
            int cx = (int)((e - id.MinE) / FieldFormat.CellSize), cy = (int)((n - id.MinN) / FieldFormat.CellSize);
            if ((uint)cx < FieldFormat.CellsPerSide && (uint)cy < FieldFormat.CellsPerSide
                && f.CropAt(cy * FieldFormat.CellsPerSide + cx) is not (CropKind.None or CropKind.Meadow or CropKind.Pasture)) return false;
        }
        return true;
    }

    /// <summary>Whether a point stands inside one of the area's buildings (the server's "not inside a building" check).</summary>
    public bool InBuilding(double e, double n)
    {
        foreach (var b in Buildings)
            if (b.Contains(e, n)) return true;
        return false;
    }

    /// <summary>
    /// Reads the tiles round a centre (out to <paramref name="radius"/>) and plans the area for that radius. Never throws:
    /// a tile that fails is left out. <see cref="Plan"/> plans it again for a smaller radius (the head count at Gather).
    /// </summary>
    public static async Task<VsGround> LoadAsync(IChunkSource? source, double e, double n, float radius, int seed)
    {
        var site = new VsGround { E = e, N = n, Radius = radius, Seed = seed };
        if (source == null) return site;
        var herds = site._herds;
        double reach = radius + 30;
        int e0 = (int)Math.Floor((e - reach) / 1000), e1 = (int)Math.Floor((e + reach) / 1000);
        int n0 = (int)Math.Floor((n - reach) / 1000), n1 = (int)Math.Floor((n + reach) / 1000);
        for (int te = e0; te <= e1; te++)
            for (int tn = n0; tn <= n1; tn++)
            {
                var id = new TileId(te, tn);
                try { await site.ReadTile(source, id, herds); }
                catch (Exception ex) { GD.PushWarning($"[vs] tile {id}: {ex.Message}"); }
            }
        await Task.Run(() => site.Plan(radius));
        return site;
    }

    private async Task ReadTile(IChunkSource source, TileId id, List<(double E, double N)> herds)
    {
        var grid = await source.LoadCoarseChunkAsync(id);
        if (grid == null && await source.LoadChunkAsync(id) is { } full) grid = full.Decimate(ChunkFormat.CoarseStride);
        RoadTile? roads = null;
        try { roads = await source.LoadRoadsAsync(id); } catch (Exception) { /* no roads */ }
        BuildingTile? buildings = null;
        try { buildings = await source.LoadBuildingsAsync(id); } catch (Exception) { /* no buildings */ }
        byte[]? cover = null;
        try { cover = await source.LoadCoverAsync(id); } catch (Exception) { /* no cover */ }
        List<FieldPolygon>? fields = null;
        try { fields = await source.LoadFieldsAsync(id); } catch (Exception) { /* no fields */ }
        var fieldTile = fields is { Count: > 0 } ? FieldTile.Build(id, fields) : null;
        _tiles[id] = (grid, cover, fieldTile);

        if (roads != null)
            foreach (var seg in roads.Segments)
            {
                if (seg.PointCount < 2) continue;
                var line = new (double E, double N)[seg.PointCount];
                for (int i = 0; i < line.Length; i++) line[i] = seg.Lv95(id, i);
                if (line.Any(p => VsRules.Inside(E, N, Radius + 60, p.E, p.N))) Roads.Add(line);
            }
        if (buildings != null)
            for (int i = 0; i < buildings.Buildings.Count; i++)
            {
                var b = buildings.Buildings[i];
                if (b.Kind == BuildingKind.UnderConstruction || b.Triangles.Length < 9) continue;
                // a quick test on the first vertex before the footprint's work
                double ve = id.MinE + b.Triangles[0], vn = id.MaxN - b.Triangles[2];
                if (!VsRules.Inside(E, N, Radius + 40, ve, vn)) continue;
                if (BuildingFootprint.Compute(buildings, i, roads, grid) is not { } fp) continue;
                var door = fp.Door;
                Buildings.Add(new VsBuilding(id.MinE + fp.Center.X, id.MaxN - fp.Center.Y, fp.AxisU.X, -fp.AxisU.Y, fp.Width, fp.Depth, b.Kind,
                    id.MinE + door.Position.X, id.MaxN - door.Position.Z, door.Outward.X, -door.Outward.Z));
            }
        // the herds' grazing spots: the farm layer's, else the land's
        var plans = fieldTile != null ? HerdPlan.For(fieldTile)
            : cover != null ? HerdPlan.FromLand(id, cover, buildings, roads, grid is { Stride: 1 } ? grid : null) : new List<Herd>();
        foreach (var h in plans)
            foreach (var s in h.Spots.Take(h.Count))
                herds.Add((h.E + s.X, h.N + s.Y));
    }

    /// <summary>The decor, the sites, the kinds and the rejection facts for a radius (no wider than the tiles read).</summary>
    public void Plan(float radius)
    {
        Radius = radius;
        var herds = _herds;
        // the seeker's door: the sacral building's nearest the centre, else the centre
        var church = Buildings.Where(b => b.Kind == BuildingKind.Sacral)
            .OrderBy(b => (b.E - E) * (b.E - E) + (b.N - N) * (b.N - N)).Select(b => (VsBuilding?)b).FirstOrDefault();
        Door = church is { } c ? (c.DoorE + c.OutE * 1.5, c.DoorN + c.OutN * 1.5) : (E, N);
        Decor = HidePlan.For(E, N, Radius, Seed, Buildings, Roads, Grass, herds, church is { } ch ? (ch.DoorE, ch.DoorN) : null);
        Kinds = HidePlan.Kinds(Decor);
        float ground = GroundAt(E, N) ?? 0;
        Sites = VsSites.Plan(E, N, Radius, Door.E, Door.N, Buildings, Roads, Grass, (x, y) => GroundAt(x, y) ?? ground);
        // the rejection facts: buildings inside, decor, water (cover) and steep cells (the coarse grid), on a 10 m lattice
        int cells = 0, wet = 0, steep = 0;
        for (double x = -Radius; x <= Radius; x += 10)
            for (double y = -Radius; y <= Radius; y += 10)
            {
                double pe = E + x, pn = N + y;
                if (!VsRules.Inside(E, N, Radius, pe, pn)) continue;
                cells++;
                var id = TileId.FromLv95(pe, pn);
                if (_tiles.TryGetValue(id, out var t) && t.Cover is { Length: >= ChunkFormat.GridSize * ChunkFormat.GridSize } cover)
                {
                    int cx = (int)(pe - id.MinE), cz = (int)(id.MaxN - pn);
                    if ((uint)cx < ChunkFormat.GridSize && (uint)cz < ChunkFormat.GridSize && (CoverClass)cover[cz * ChunkFormat.GridSize + cx] == CoverClass.Water) wet++;
                }
                if (GroundAt(pe, pn) is float h0 && GroundAt(pe + 5, pn) is float h1 && GroundAt(pe, pn + 5) is float h2
                    && Math.Sqrt((h1 - h0) * (h1 - h0) + (h2 - h0) * (h2 - h0)) / 5 > Math.Tan(Math.PI / 6)) steep++;
            }
        int inside = Buildings.Count(b => VsRules.Inside(E, N, Radius, b.E, b.N));
        Facts = new VsAreaFacts(inside, Decor.Count(d => d.Kind != VsProp.Cow), cells == 0 ? 0 : wet / (float)cells, cells == 0 ? 0 : steep / (float)cells);
    }
}
