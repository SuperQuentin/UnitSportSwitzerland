using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --ikeacheck [--systems ui]</c> (#501): the nine IKEA stores and
/// what is inside one.
///
/// <para>
/// Two halves. First, <b>the table</b>: every row of <see cref="Landmarks.Ikea"/>, its LV95 point,
/// its tile and what the data has there. With real tiles — <c>CHUNKS=&lt;dir&gt;</c>, or
/// <c>--chunks &lt;dir&gt;</c> — it reads them and says, per store, which solid matched or that none
/// did. **A coordinate that has drifted off its building shows up here as a MISS, and is one line
/// to correct**; that is the whole reason this prints rather than just asserting. Without real
/// tiles it says so and skips that half rather than passing quietly.
/// </para>
///
/// <para>
/// Second, <b>the store itself</b>, built here: a hand-made <see cref="BuildingTile"/> carrying the
/// real Dietlikon tile id with one store-sized box on the real point. That exercises the whole path
/// — detection, grouping, the plan, the furniture, the mesh — with no map and no server, which is
/// necessary rather than merely convenient: the fixture world has no buildings at all
/// (<c>FixtureChunkSource</c> returns an empty list for every tile) and the generated world puts
/// none at Dietlikon. <see cref="PortalDemo"/> and <c>ChurchStageProbe</c> build their own the same
/// way.
/// </para>
/// </summary>
public partial class IkeaProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--ikeacheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[ikea] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        Table();
        OneStore();
        GD.Print($"[ikea] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    /// <summary>Where the real tiles are, if this machine has any.</summary>
    private static string? ChunkDir()
    {
        string? dir = CmdArgs.Value("--chunks") ?? OS.GetEnvironment("CHUNKS");
        return !string.IsNullOrEmpty(dir) && DirAccess.DirExistsAbsolute(dir) ? dir : null;
    }

    /// <summary>The nine rows, and what the data says about each.</summary>
    private void Table()
    {
        string? dir = ChunkDir();
        GD.Print($"[ikea] {Landmarks.Ikea.Length} stores; chunks: {dir ?? "none (set CHUNKS=<dir> to check against real tiles)"}");

        int matched = 0, readable = 0;
        foreach (var store in Landmarks.Ikea)
        {
            var own = TileId.FromLv95(store.E, store.N);
            string where = $"{store.Name,-12} {store.Lat,10:F6} {store.Lon,9:F6}  E {store.E,9:F0} N {store.N,9:F0}  tile {own}";
            if (dir == null) { GD.Print($"[ikea]      {where}"); continue; }

            // the store's record belongs to the tile holding its centre, which for a 300 m solid
            // need not be the tile its point fell in: look at the ring
            var hits = new List<string>();
            bool any = false;
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                {
                    var t = new TileId(own.E + de, own.N + dn);
                    string path = dir.PathJoin(BuildingFormat.FileName(t));
                    if (!Godot.FileAccess.FileExists(path)) continue;
                    any = true;
                    var tile = Read(path);
                    if (tile == null) continue;
                    var map = BuildingTypes.For(tile);
                    for (int i = 0; i < tile.Buildings.Count; i++)
                    {
                        if (map.TypeOf(i) != BuildingType.Ikea) continue;
                        var box = map.Boxes[i]!.Value;
                        var b = tile.Buildings[i];
                        hits.Add($"{t} #{i} {box.Area:F0} m2 {box.Width:F0}x{box.Depth:F0} h{b.MaxY - b.MinY:F0} {b.Kind}");
                    }
                }

            if (!any) { GD.Print($"[ikea] ---- {where}  no tiles here"); continue; }
            readable++;
            if (hits.Count == 1) matched++;
            string tag = hits.Count == 1 ? "OK  " : hits.Count == 0 ? "MISS" : "AMBG";
            GD.Print($"[ikea] {tag} {where}  {(hits.Count == 0 ? "nothing store-shaped covers the point" : string.Join(" | ", hits))}");
        }

        if (dir == null)
        {
            GD.Print("[ikea] skip the table's verdict: no real tiles on this machine");
            return;
        }
        // only the stores whose tiles are actually present can be judged
        Check(matched == readable, $"every readable store matched exactly one solid ({matched}/{readable})");
    }

    private static BuildingTile? Read(string path)
    {
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (f == null) return null;
        using var ms = new MemoryStream(f.GetBuffer((long)f.GetLength()));
        return BuildingCodec.Decode(ms);
    }

    /// <summary>A store built here, from detection through to its mesh.</summary>
    private void OneStore()
    {
        var store = Landmarks.Ikea[0];                       // Dietlikon
        var id = TileId.FromLv95(store.E, store.N);
        var at = Landmarks.LocalPoint(id, store);
        // about the size of the real one, square to the tile and centred on the noted point
        var tile = new BuildingTile
        {
            Id = id,
            Buildings = new List<Building> { Box(at.X, at.Y, 170f, 110f, 21f) },
        };

        var map = BuildingTypes.For(tile);
        Check(map.TypeOf(0) == BuildingType.Ikea, $"the box on {store.Name}'s point is an IKEA");
        Check(map.PartOf(0) == BuildingPart.Store, "and its part is Store");

        var doors = BuildingFootprint.ComputeDoors(tile, null, null);
        Check(doors[0].Shop == Loot.ShopType.Ikea, "its door reports ShopType.Ikea, so the sign reads IKEA");
        Check(doors[0].Width > 0, "it has a door at all");

        var layout = InteriorGenerator.Generate(tile, 0, null, null);
        if (layout == null) { Check(false, "an interior was planned"); return; }

        Check(layout.Type == BuildingType.Ikea, "the plan is an IKEA's");
        Check(layout.Shop == Loot.ShopType.Ikea, "the plan sells as an IKEA");
        Check(layout.Group != "", "the plan is fingerprinted, so it regenerates when the rules change");
        Check(layout.Floors.Count == 1 && layout.Below == 0, "one floor, no cellar");

        var hall = layout.Floors[0].Rooms[0];
        Check(layout.Floors[0].Rooms.Count == 1 && hall.Type == RoomType.IkeaMarket, "one market hall");
        Check(hall.Area > 0.9f * layout.Width * layout.Depth, "the hall is the whole slab");
        Check(hall.Openings.Any(o => o.Kind == OpeningKind.Entry && o.Width >= 3f),
            "the entrance is a store's, not a house's");

        int bins = layout.Furniture.Count(f => f.Type == FurnitureType.BlahajBin);
        Check(bins >= 20, $"the floor is covered in bins of Blåhajs ({bins})");
        Check(bins <= 120, $"and the bin count is capped ({bins})");
        Check(layout.Furniture.Any(f => f.Type == FurnitureType.ShopCounter), "there is a till by the door");

        // nothing stands in the lane straight in from the door
        float half = layout.EntryWidth / 2;
        Check(!layout.Furniture.Any(f => f.Type == FurnitureType.BlahajBin
                && Math.Abs(f.X - layout.EntryX) < half && f.Z < hall.Z0 + 4f),
            "the way in from the door is clear");

        // every bin is inside the hall
        Check(layout.Furniture.Where(f => f.Type == FurnitureType.BlahajBin).All(f =>
                f.X - f.W / 2 > hall.X0 && f.X + f.W / 2 < hall.X1
                && f.Z - f.D / 2 > hall.Z0 && f.Z + f.D / 2 < hall.Z1),
            "no bin is in a wall");

        var problems = InteriorValidator.Validate(layout);
        Check(problems.Count == 0, $"the plan validates{(problems.Count == 0 ? "" : ": " + string.Join("; ", problems.Take(4)))}");

        var mesh = InteriorMeshBuilder.Build(layout);
        Check(mesh.Vertices.Length > 0, $"the interior meshes ({mesh.Vertices.Length} vertices)");
        Check(mesh.Collision.Length > 0, "and it has collision, so the bins can be walked into");

        // the loot a bin holds is Blåhajs, which is the point of the issue
        Check(Loot.LootTables.IsLootable(FurnitureType.BlahajBin), "a bin is a container");
        var pool = Loot.LootTables.Targets().ToList();
        Check(pool.Contains(Items.ItemId.Blahaj), "and a Blåhaj is somewhere in the loot tables");
        Check(Loot.ShopTables.Catalogue(Loot.ShopType.Ikea).Any(l => l.Id == Items.ItemId.Blahaj),
            "the till sells Blåhajs");
    }

    /// <summary>A flat-topped box, as swissBUILDINGS3D would draw a shed: four walls and a roof.</summary>
    private static Building Box(float cx, float cz, float w, float d, float h)
    {
        float x0 = cx - w / 2, x1 = cx + w / 2, z0 = cz - d / 2, z1 = cz + d / 2;
        var t = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) =>
            t.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });

        var corners = new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) };
        for (int i = 0; i < 4; i++)
        {
            var (ax, az) = corners[i];
            var (bx, bz) = corners[(i + 1) % 4];
            Tri(new Vector3(ax, 0, az), new Vector3(bx, 0, bz), new Vector3(bx, h, bz));
            Tri(new Vector3(ax, 0, az), new Vector3(bx, h, bz), new Vector3(ax, h, az));
        }
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1));
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z1), new Vector3(x0, h, z1));
        return new Building { Kind = BuildingKind.Commercial, MinY = 0, MaxY = h, Triangles = t.ToArray() };
    }
}
