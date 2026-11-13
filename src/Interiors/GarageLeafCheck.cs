using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--garageleafcheck --chunks &lt;flat tiles&gt; --leaves &lt;cube leaves&gt; [--at E,N]</c> (#1772): the building at a
/// place, its doors as the flat tile builds them and as its earth leaf's stand-in builds them. The garage door (a
/// door with a ramp) must be there on both, or on neither. Geneva's 2502137,1117793 had one on flat and none on earth.
/// Builds no world; prints a RESULT line and exits 0 or 1.
/// </summary>
public static class GarageLeafCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--garageleafcheck") >= 0;

    public static int Run()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string chunks = Arg("--chunks") ?? "D:/UnitSportSwitzerland/terrain_chunks";
        string leavesDir = Arg("--leaves") ?? "D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube";
        var at = (Arg("--at") ?? "2502137,1117793").Split(',');
        double e = double.Parse(at[0], System.Globalization.CultureInfo.InvariantCulture);
        double n = double.Parse(at[1], System.Globalization.CultureInfo.InvariantCulture);
        int failures = 0;

        // ---- flat: the LV95 tile, its 1 m grid, roads and buildings, as ChunkManager builds its doors
        var src = new LocalChunkSource(chunks);
        var id = TileId.FromLv95(e, n);
        var bTile = src.LoadBuildingsAsync(id).Result;
        var roads = src.LoadRoadsAsync(id).Result;
        var grid = src.LoadChunkAsync(id).Result;
        var (lx, lz) = id.Local(e, n);
        int flatGarages = -1;
        DoorSpot[] flatDoors = [];
        if (bTile == null || grid == null) { GD.Print($"[garageleafcheck] FAIL no flat tile {id} in {chunks}"); failures++; }
        else
        {
            int bi = Nearest(bTile, (float)lx, (float)lz);
            flatDoors = BuildingFootprint.ComputeDoors(bTile, roads, grid.Stride == 1 ? grid : null, null);
            flatGarages = Report("flat", bTile, bi, flatDoors, roads, grid);
        }

        // ---- earth: the leaf under the place, prepared as the collision source prepares it (its neighbours' roads too)
        var store = LeafStore.Open(leavesDir);
        var ecef = SwissGeodesy.ToEcef(e, n, grid?.SampleMeshHeight(e, n) ?? 400);
        var key = CubeKey.FromEcef(ecef, LeafFrames.LeafLevel);
        int earthGarages = -1;
        LeafStand? stand = null;
        if (store.Get(key) is not { Heights: not null } content) { GD.Print($"[garageleafcheck] FAIL no leaf {key} in {leavesDir}"); failures++; }
        else
        {
            var prep = LeafCollision.Prepare(content, k => store.Get(k)?.Roads);
            stand = prep.Stand;
            var (x, _, z) = key.Anchor.ToLocal(ecef);
            float sx = (float)(x + LeafStandIn.Offset), sz = (float)(z + LeafStandIn.Offset);
            if (stand.Buildings == null) { GD.Print($"[garageleafcheck] FAIL leaf {key} has no buildings"); failures++; }
            else
            {
                int bi = Nearest(stand.Buildings, sx, sz);
                GD.Print($"[garageleafcheck] leaf {key} stand-in {stand.Id}, place at {sx:F1},{sz:F1}");
                earthGarages = Report("earth", stand.Buildings, bi, stand.Doors ?? [], stand.Roads, stand.Grid);
            }
        }

        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[garageleafcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        // ---- what /locate answers on earth (#1772): every building of the LV95 tile that stands on this leaf, its parking
        // entrance as LocateService judges it under earth, against the doors the leaf itself builds
        if (bTile != null && stand is { Buildings: { } built, Doors: { } leafDoors })
        {
            var ids = BuildingId.Assign(bTile.Buildings, b => BuildingId.OnLv95Tile(bTile.Id, b));
            var here = Enumerable.Range(0, bTile.Buildings.Count)
                .Where(i => ids[i] is { } bid && LeafFrames.LeafAt(bid.LatDeg, bid.LonDeg) == key).ToList();
            var answers = World.Locate.LocateService.GaragesOnLeaves(bTile, here, k => k == key ? stand : null);
            // the leaf's garage doors, by the Earth identity of their building
            var leafGarages = new HashSet<string>();
            foreach (var d in leafDoors)
                if (d.Ramp != GarageRule.RampKind.None && d.Width > 0 && Fnv.StandIn($"{stand.Id.E}_{stand.Id.N}_{d.Index}") is { } name)
                    leafGarages.Add(name);
            int agree = 0, said = 0, lv95Wrong = 0;
            for (int i = 0; i < here.Count; i++)
            {
                bool leafHas = leafGarages.Contains(ids[here[i]]!.Value.ToString());
                bool locateSays = answers[i] != null;
                bool lv95Says = flatDoors.Any(d => d.Index == here[i] && d.Ramp != GarageRule.RampKind.None);
                if (locateSays == leafHas) agree++;
                else GD.Print($"[garageleafcheck]   building {here[i]} ({ids[here[i]]}): /locate says {locateSays}, the leaf builds {leafHas}");
                if (locateSays) said++;
                if (lv95Says != leafHas) lv95Wrong++;
            }
            // where /locate on earth now points: the nearest answer, and which way its door faces (for a picture of it)
            int near = -1;
            double nearD = double.MaxValue;
            for (int i = 0; i < here.Count; i++)
                if (answers[i] is var (ae, an) && (ae - e) * (ae - e) + (an - n) * (an - n) is double dd && dd < nearD) (near, nearD) = (i, dd);
            if (near >= 0 && answers[near] is var (ne, nn))
            {
                var bid = ids[here[near]]!.Value.ToString();
                var door = leafDoors.First(d => d.Ramp != GarageRule.RampKind.None && Fnv.StandIn($"{stand.Id.E}_{stand.Id.N}_{d.Index}") == bid);
                double bearing = (Math.Atan2(door.Outward.X, -door.Outward.Z) * 180 / Math.PI + 360) % 360;
                GD.Print(System.FormattableString.Invariant($"[garageleafcheck] /locate on earth now names {ne:F1},{nn:F1} ({Math.Sqrt(nearD):F0} m away), its garage door facing {bearing:F0} deg"));
            }
            GD.Print($"[garageleafcheck] the LV95 tile's answer (what /locate gave on earth before #1772) differs from the leaf on {lv95Wrong} of {here.Count} buildings");
            Expect(agree == here.Count, $"/locate on earth agrees with the leaf on every building: {agree} of {here.Count}");
            Expect(said > 0 && said == leafGarages.Count(g => here.Any(i => ids[i]!.Value.ToString() == g)),
                $"/locate on earth finds the leaf's garage doors: {said} of {leafGarages.Count} on the leaf");
            // the index's candidates (the roll and the box) seeded as the leaves seed them: every leaf garage is a candidate,
            // where the LV95 roll left some out (so /locate never offered them)
            var earthIndex = World.Locate.BuildingIndexBuilder.Entries(bTile, earthSeeds: true);
            var lv95Index = World.Locate.BuildingIndexBuilder.Entries(bTile);
            int leafHere = 0, earthMissed = 0, lv95Missed = 0;
            foreach (int i in here)
            {
                if (!leafGarages.Contains(ids[i]!.Value.ToString())) continue;
                leafHere++;
                if (!earthIndex[i].Is(World.Locate.IndexedFlags.GarageRoll)) earthMissed++;
                if (!lv95Index[i].Is(World.Locate.IndexedFlags.GarageRoll)) lv95Missed++;
            }
            GD.Print($"[garageleafcheck] the LV95-seeded index (before #1772) left out {lv95Missed} of the leaf's {leafHere} garages");
            Expect(leafHere > 0 && earthMissed == 0, $"the earth-seeded index offers every leaf garage as a candidate: {leafHere - earthMissed} of {leafHere}");
            // the building of the playtest (note 15): no garage on earth, so /locate must not name it
            if (bTile != null && Nearest(bTile, (float)lx, (float)lz) is int spot && here.IndexOf(spot) is int k && k >= 0)
                Expect((answers[k] != null) == (earthGarages > 0), $"the building at {e:F0},{n:F0}: /locate {(answers[k] != null ? "names" : "does not name")} it, the leaf builds {earthGarages} garage door(s)");
        }
        else Expect(false, "the flat tile and the leaf were both read");
        GD.Print($"[garageleafcheck] RESULT {(failures == 0 ? "PASS" : "FAIL")} ({failures} failure(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The building whose plan holds the point, or the one whose vertices come nearest.</summary>
    private static int Nearest(BuildingTile tile, float x, float z)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            var t = tile.Buildings[i].Triangles;
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                x0 = Math.Min(x0, t[k]); x1 = Math.Max(x1, t[k]);
                z0 = Math.Min(z0, t[k + 2]); z1 = Math.Max(z1, t[k + 2]);
            }
            float dx = Math.Max(0, Math.Max(x0 - x, x - x1)), dz = Math.Max(0, Math.Max(z0 - z, z - z1));
            float d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static int Report(string frame, BuildingTile tile, int index, DoorSpot[] doors, RoadTile? roads, ChunkGrid grid)
    {
        var b = tile.Buildings[index];
        var mine = doors.Where(d => d.Index == index).ToList();
        int garages = mine.Count(d => d.Ramp != GarageRule.RampKind.None);
        GD.Print($"[garageleafcheck] {frame}: building {index} ({b.Kind}, {b.Floors} floors, y {b.MinY:F1}..{b.MaxY:F1}), {mine.Count} doors, {garages} garage, roads {roads?.Segments.Count ?? 0}, grid stride {grid.Stride}");
        string key = new BuildingKey(tile.Id.E, tile.Id.N, index).ToString();
        GD.Print($"[garageleafcheck]   key {key} seeds as {Fnv.StandIn(key) ?? key}: mixed roll {Fnv.Unit(key + "|mixed"):F3} (< {GarageRule.MixedShare}), garage roll {Fnv.Unit(key + "|garage"):F3} (mixed under {GarageRule.MixedRollShare})");
        foreach (var d in mine)
            GD.Print($"[garageleafcheck]   slot {d.Slot} at {d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1} out {d.Outward.X:F2},{d.Outward.Z:F2} {d.Width:F1}x{d.Height:F1} ramp {d.Ramp} link {d.Link.Kind} {d.Link.Length:F1}");
        // the reason, when there is none: the footprint alone, again
        if (garages == 0)
        {
            BuildingFootprint.Compute(tile, index, roads, grid.Stride == 1 ? grid : null);
            GD.Print($"[garageleafcheck]   no garage door: {BuildingFootprint.GarageWhyNot ?? "(no reason given: the rule, or the ramp the generator could not plan)"}");
        }
        return garages;
    }
}
