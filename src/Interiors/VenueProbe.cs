using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --venuecheck --systems ui</c> (#1232): the venue list and the
/// rules that put a venue in a building.
///
/// <para>
/// Three parts. <b>The list</b>: the embedded <c>venues.tsv.gz</c> reads, holds every type, is sorted
/// and inside Switzerland. <b>The rules</b> on a hand-made tile (no map, no server: the fixture world
/// has no buildings): a point in a solid makes it that venue, a point outside every solid makes
/// nothing, a mall with a cinema and a restaurant is the cinema, the main door carries the venue and
/// the shop sign leaves it to the neon, and every type's sign builds. <b>The real tiles</b>, with
/// <c>CHUNKS=&lt;dir&gt;</c> or <c>--chunks &lt;dir&gt;</c>: every venue whose point the list placed
/// against tiles (on a solid, or snapped onto one) must match a solid here; the halls (bowling,
/// cinema, arcade) are listed one by one with the solid they matched, so a miss is one line to read.
/// </para>
/// </summary>
public partial class VenueProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--venuecheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[venues] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        TheList();
        TheRules();
        Venues.Override = null;
        RealTiles();
        GD.Print($"[venues] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void TheList()
    {
        var list = Venues.List;
        Check(list.Count > 20000, $"the embedded list reads ({list.Count} venues)");
        foreach (var t in Enum.GetValues<VenueType>())
            if (t != VenueType.None)
                Check(list.Any(r => r.Type == t), $"it has {t} ({list.Count(r => r.Type == t)})");
        bool sorted = true;
        for (int i = 1; i < list.Count && sorted; i++)
            sorted = list[i - 1].E < list[i].E || list[i - 1].E == list[i].E && list[i - 1].N <= list[i].N;
        Check(sorted, "it is sorted by E, then N");
        Check(list.All(r => r.E is > 2480000 and < 2840000 && r.N is > 1070000 and < 1300000), "every point is in Switzerland's box");
        Check(list.All(r => r.Type != VenueType.None), "no row without a type");
        Check(list.Count(r => r.Name.Length > 0) > list.Count * 0.8, $"most have a name ({list.Count(r => r.Name.Length > 0)})");
    }

    private void TheRules()
    {
        var id = new TileId(2600, 1200);
        // a mall (a cinema and a restaurant in it), a house with a café, a bare house, and a beer garden in the open
        var tile = new BuildingTile
        {
            Id = id,
            Buildings = new List<Building>
            {
                Box(200, 300, 80, 60, 18, BuildingKind.Commercial),
                Box(500, 500, 12, 10, 9, BuildingKind.Apartment),
                Box(700, 500, 12, 10, 9, BuildingKind.House),
            },
        };
        double E(float x) => id.MinE + x;
        double N(float z) => id.MaxN - z;
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Restaurant, E(190), N(290), VenueFix.Inside, VenueFlags.Outdoor, 0, "pizza", "Pizzeria Test"),
            new("n2", VenueType.Cinema, E(220), N(310), VenueFix.Inside, VenueFlags.None, 3, "", "Cinéma Test"),
            new("n3", VenueType.Cafe, E(501), N(499), VenueFix.Inside, VenueFlags.None, 0, "", ""),
            new("n4", VenueType.Biergarten, E(300), N(800), VenueFix.Open, VenueFlags.None, 0, "", "Garten"),
        };
        var map = BuildingTypes.Detect(tile);
        Check(map.HasVenues, "the tile has venues");
        Check(map.VenueOf(0) >= 0 && Venues.At(map.VenueOf(0)).Type == VenueType.Cinema,
            "a mall holding a cinema and a restaurant is the cinema");
        Check(map.VenueOf(1) >= 0 && Venues.At(map.VenueOf(1)).Type == VenueType.Cafe, "the house holding the café is the café");
        Check(map.VenueOf(2) == -1, "the house with no point in it is nothing");
        Check(Venues.NameOf(Venues.At(map.VenueOf(1))) == "Café", "a venue with no name shows its type's");

        // the same tile seen from its neighbour: points within Reach of it are looked at, matched only by containment
        var west = new BuildingTile { Id = new TileId(id.E - 1, id.N), Buildings = new List<Building> { Box(990, 300, 40, 20, 8, BuildingKind.Commercial) } };
        Check(Venues.Match(west) == null, "a neighbouring tile's solid does not take a point it does not cover");

        var doors = BuildingFootprint.ComputeDoors(tile, null, null);
        var main = doors.FirstOrDefault(d => d.Index == 0 && d.Slot == 0);
        Check(main.Venue == map.VenueOf(0) + 1, "the mall's main door carries the cinema");
        Check(doors.Where(d => d.Index == 0 && d.Slot > 0).All(d => d.Venue == 0), "its other doors do not");
        Check(doors.First(d => d.Index == 2).Venue == 0, "the bare house's door carries nothing");
        Check(VenueSigns.Place(main) is { } at && at.Origin.Y > main.Position.Y + 2f, "the sign hangs over the door");
        // the street hears the venue from in front of its door, not through its own wall (#1386: muffled by Hearing's wall)
        var music = VenueMusic.AtDoor(main, Venues.At(map.VenueOf(0)), new BuildingKey(id.E, id.N, 0));
        Check(music == null || (music.Position - main.Position).Dot(main.Outward) > 0.1f,
            $"the door's music stands outside the wall ({(music == null ? "none" : $"{(music.Position - main.Position).Dot(main.Outward):F2} m out")})");

        foreach (var t in Enum.GetValues<VenueType>())
        {
            if (t == VenueType.None) continue;
            var mesh = NeonSign.IconMesh(t);
            Check(mesh != null && mesh.GetSurfaceCount() == 1 && mesh.SurfaceGetArrayLen(0) > 30, $"{t}'s neon icon builds");
            var sign = NeonSign.Make(Venues.GenericName(t), t);
            Check(sign.GetChildren().OfType<Label3D>().Count() == 2, $"{t}'s sign is lettered on both faces");
            sign.Free();
            // its music on the relay (#1244): a station every kind has, in every region
            foreach (var region in Enum.GetValues<VenueMusic.Region>())
                Check(Audio.Live.Stations.For(VenueMusic.StationFor(t, "", region)) != null, $"{t} plays a station in {region} Switzerland");
        }
        Check(NeonSign.IconMesh(VenueType.Restaurant, "pizza") is { } pizza && pizza.SurfaceGetArrayLen(0) != NeonSign.IconMesh(VenueType.Restaurant)!.SurfaceGetArrayLen(0),
            "a pizzeria's icon is a slice, not the fork and knife");
        Check(NeonSign.Lettering("Restaurant de la Gare et du Commerce Réunis").Length <= 26, "a long name is cut");
    }

    /// <summary>Where the real tiles are, if this machine has any.</summary>
    private static string? ChunkDir()
    {
        string? dir = CmdArgs.Value("--chunks") ?? OS.GetEnvironment("CHUNKS");
        return !string.IsNullOrEmpty(dir) && DirAccess.DirExistsAbsolute(dir) ? dir : null;
    }

    private void RealTiles()
    {
        string? dir = ChunkDir();
        if (dir == null)
        {
            GD.Print("[venues] skip the real tiles: none on this machine (set CHUNKS=<dir>)");
            return;
        }
        var list = Venues.List;
        var tiles = list.Select(r => TileId.FromLv95(r.E, r.N)).Distinct()
            .SelectMany(t => new[] { t, t with { E = t.E - 1 }, t with { E = t.E + 1 }, t with { N = t.N - 1 }, t with { N = t.N + 1 },
                new TileId(t.E - 1, t.N - 1), new TileId(t.E + 1, t.N + 1), new TileId(t.E - 1, t.N + 1), new TileId(t.E + 1, t.N - 1) })
            .Distinct()
            .Where(t => Godot.FileAccess.FileExists(dir.PathJoin(BuildingFormat.FileName(t)))).ToList();
        GD.Print($"[venues] {tiles.Count} built tiles hold or touch a venue");

        // venue -> (tile, solid) covering its point, and the venue each building shows once ranked
        var hit = new Dictionary<int, (TileId Tile, int Index, Building B)>();
        var shown = new Dictionary<(TileId, int), int>();
        // a hall's main door in LV95 and the way it faces, so you can go and stand at it
        var doorAt = new Dictionary<(TileId, int), string>();
        var doubled = new List<string>();
        // --venuedoors <text>: also the door of every venue whose name holds it, any kind, to shoot it
        string? named = CmdArgs.Value("--venuedoors");
        foreach (var t in tiles)
        {
            using var f = Godot.FileAccess.Open(dir.PathJoin(BuildingFormat.FileName(t)), Godot.FileAccess.ModeFlags.Read);
            if (f == null) continue;
            using var ms = new MemoryStream(f.GetBuffer((long)f.GetLength()));
            var tile = BuildingCodec.Decode(ms);
            foreach (var (v, i) in Venues.Contained(tile))
                if (!hit.TryAdd(v, (t, i, tile.Buildings[i])))
                    doubled.Add($"{Venues.NameOf(list[v])} ({t} #{i} and {hit[v].Tile} #{hit[v].Index})");
            if (Venues.Match(tile) is { } match)
            {
                DoorSpot[]? doors = null;
                for (int i = 0; i < match.Length; i++)
                {
                    if (match[i] < 0) continue;
                    shown[(t, i)] = match[i];
                    bool asked = named != null && Venues.NameOf(list[match[i]]).Contains(named, StringComparison.OrdinalIgnoreCase);
                    if (!Venues.IsHall(list[match[i]].Type) && !asked) continue;
                    // with the tile's roads, as the game places doors: without them a door can land on another facade
                    doors ??= BuildingFootprint.ComputeDoors(tile, Roads(dir, t), null);
                    var d = doors.FirstOrDefault(x => x.Index == i && x.Slot == 0);
                    doorAt[(t, i)] = d.Width > 0
                        ? $" door E {t.MinE + d.Position.X:F0} N {t.MaxN - d.Position.Z:F0} out {d.Outward.X:F2},{-d.Outward.Z:F2}"
                        : " NO DOOR";
                    if (asked)
                    {
                        GD.Print($"[venues] {list[match[i]].Type} {Venues.NameOf(list[match[i]])} {t} #{i}{doorAt[(t, i)]}");
                        // and where its rooms are (#1242): a shot's camera inside stands where it is told
                        var fp = BuildingFootprint.Compute(tile, i, Roads(dir, t), null);
                        if (fp != null && InteriorGenerator.Generate(tile, i, Roads(dir, t), null) is { } plan)
                        {
                            // each venue's own street door (#1292), its glazed front, and the room it opens into
                            foreach (var own in fp.Doors.Where(x => x.Unit && x.Venue > 0))
                            {
                                var e = plan.EntranceOf(new DoorKey(fp.Key, own.Slot).ToString());
                                int? ri = e == null ? null : InteriorGenerator.EntryRoom(plan.GroundFloor.Rooms, e);
                                string into = ri is int k ? $"{plan.GroundFloor.Rooms[k].Type} {plan.GroundFloor.Rooms[k].Width:F1}x{plan.GroundFloor.Rooms[k].Depth:F1} m" : "no doorway (locked)";
                                GD.Print($"[venues]   own door of {Venues.NameOf(Venues.At(own.Venue - 1))}: slot {own.Slot} E {t.MinE + own.Position.X:F1} N {t.MaxN - own.Position.Z:F1} "
                                    + $"out {own.Outward.X:F2},{-own.Outward.Z:F2}, front {own.Span:F1} m to {own.FrontTop:F1} m, into {into}");
                            }
                            // every room (#1250: a hall's auditoriums and booth too), and the plan beside the shots
                            for (int fl = 0; fl < plan.Floors.Count; fl++)
                                foreach (var r in plan.Floors[fl].Rooms.Where(r => r.Type is not (RoomType.Corridor or RoomType.Hall) || r.Area >= 20f))
                                {
                                    var c = fp.Center + fp.AxisU * ((r.X0 + r.X1) / 2) + fp.AxisV * ((r.Z0 + r.Z1) / 2);
                                    var inside = plan.Furniture.Where(p => p.Floor == fl && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1)
                                        .GroupBy(p => p.Type).Select(g => $"{g.Key} {g.Count()}");
                                    GD.Print($"[venues]   floor {fl - plan.Below} {r.Type} {r.Width:F1}x{r.Depth:F1} m at E {t.MinE + c.X:F1} N {t.MaxN - c.Y:F1}: {string.Join(", ", inside)}");
                                }
                            try
                            {
                                string outDir = ProjectSettings.GlobalizePath("res://test_output/venues");
                                System.IO.Directory.CreateDirectory(outDir);
                                System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"{t}_{i}.svg"), InteriorValidator.ToSvg(plan));
                            }
                            catch (System.IO.IOException) { }
                        }
                    }
                }
            }
        }
        // overlapping solids in the data: peers still agree (each tile is a pure function), the venue just shows twice
        GD.Print($"[venues] points two solids cover (overlapping data): {doubled.Count}"
                 + (doubled.Count > 0 ? " - " + string.Join("; ", doubled.Take(5)) : ""));

        // a row the stage placed against tiles must be covered here; a raw row was never checked against any
        var placed = Enumerable.Range(0, list.Count).Where(v => list[v].Fix is VenueFix.Inside or VenueFix.Snapped
            && Godot.FileAccess.FileExists(dir.PathJoin(BuildingFormat.FileName(TileId.FromLv95(list[v].E, list[v].N))))).ToList();
        int placedHit = placed.Count(hit.ContainsKey);
        Check(placedHit == placed.Count, $"every venue placed on a solid when the list was written is covered by one ({placedHit}/{placed.Count})");
        foreach (int v in placed.Where(v => !hit.ContainsKey(v)).Take(10))
            GD.Print($"[venues]      not covered: {list[v].Osm} {list[v].Type} {Venues.NameOf(list[v])} E {list[v].E:F1} N {list[v].N:F1} {list[v].Fix}");
        GD.Print($"[venues] {hit.Count} venues in {shown.Count} buildings ({hit.Count - shown.Count} share one with a higher-ranked venue)");
        GD.Print("[venues] buildings by the venue they show: " + string.Join(", ", Enum.GetValues<VenueType>().Where(t => t != VenueType.None)
            .Select(t => $"{t} {shown.Values.Count(v => list[v].Type == t)}")));

        foreach (int v in Enumerable.Range(0, list.Count).Where(v => Venues.IsHall(list[v].Type)))
        {
            var r = list[v];
            var own = TileId.FromLv95(r.E, r.N);
            if (!tiles.Contains(own)) continue;
            string where = $"{r.Type,-8} {Venues.NameOf(r),-34} E {r.E,9:F0} N {r.N,9:F0} {r.Fix,-7}";
            if (hit.TryGetValue(v, out var h))
            {
                var box = PlanBox.Of(h.B);
                int won = shown.GetValueOrDefault((h.Tile, h.Index), -1);
                string lost = won != v && won >= 0 ? $"  (the building shows {list[won].Type} {Venues.NameOf(list[won])})" : "";
                GD.Print($"[venues] OK   {where} {h.Tile} #{h.Index} {box?.Width:F0}x{box?.Depth:F0} m h{h.B.MaxY - h.B.MinY:F0} {h.B.Kind}{doorAt.GetValueOrDefault((h.Tile, h.Index), "")}{lost}");
            }
            else GD.Print($"[venues] MISS {where} no solid covers the point");
        }
    }

    private static RoadTile? Roads(string dir, TileId t)
    {
        string path = dir.PathJoin(RoadFormat.FileName(t));
        if (!Godot.FileAccess.FileExists(path)) return null;
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (f == null) return null;
        using var ms = new MemoryStream(f.GetBuffer((long)f.GetLength()));
        try { return RoadCodec.Decode(ms); }
        catch (Exception) { return null; }
    }

    /// <summary>A flat-topped box: four walls and a roof, as the IKEA probe builds its store.</summary>
    private static Building Box(float cx, float cz, float w, float d, float h, BuildingKind kind)
    {
        float x0 = cx - w / 2, x1 = cx + w / 2, z0 = cz - d / 2, z1 = cz + d / 2;
        var t = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) => t.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
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
        return new Building { Kind = kind, MinY = 0, MaxY = h, Triangles = t.ToArray() };
    }
}
