using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--houselayoutcheck</c> (#1186): a house drawn on the web and approved is the interior the game plans. A box
/// house, square and then turned, gets a two-storey layout (hall with a stair, living room, kitchen; landing,
/// bedrooms, bathroom) through <see cref="HouseLayouts"/>; the real generator must plan it, the game's validator
/// pass it, the mesh build. A layout that does not fit (turned 45 degrees off the box) leaves the generated
/// interior; withdrawing a layout makes the stored plan stale. No world; prints a RESULT line.
/// <c>--houselayoutcheck &lt;house-layouts.json&gt; --chunks &lt;dir&gt;</c> plans every layout of an export on its real
/// building from the tiles instead, and fails on any the game would not use. The plans are written as SVG to
/// <c>test_output/houselayout/</c>.
/// </summary>
public partial class HouseLayoutCheck : Node
{
    public static bool Requested => CmdArgs.Has("--houselayoutcheck");

    private int _failures;
    private const string Out = "test_output/houselayout";
    private readonly List<string> _warned = new();

    public override void _Ready()
    {
        HouseLayouts.Warn = m => { _warned.Add(m); GD.Print("[houselayout] warned: " + m); };
        if (CmdArgs.Value("--houselayoutcheck", notFlag: true) is { } file) Export(file, CmdArgs.Value("--chunks", notFlag: true) ?? "terrain_chunks");
        else
            {
            foreach (float turn in new[] { 0f, 30f, 90f })
                House(turn);
            Angled();
        }
        HouseLayouts.Set([]);
        GD.Print($"[houselayout] RESULT: {(_failures == 0 ? "ok" : $"FAILED ({_failures})")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        if (ok) return;
        _failures++;
        GD.Print($"[houselayout] FAILED: {what}");
    }

    private void House(float turnDeg)
    {
        var id = new TileId(2594, 1120);
        var b = Box(500, 500, 12, 8, 6, turnDeg);
        var tile = new BuildingTile { Id = id, Buildings = [b] };
        var o = BuildingOutline.Compute(b, id);
        var layout = Layout(o);
        var drawn = layout.Validate(o.LocalRings());
        Check(drawn.Count == 0, $"turn {turnDeg}: the drawn layout breaks the web's rules: {string.Join("; ", drawn)}");

        HouseLayouts.Set([layout]);
        var l = InteriorGenerator.Generate(tile, 0, null, null);
        string at = $"turn {turnDeg} (box {o.Length:F1} x {o.Width:F1}, yaw {o.YawDeg:F0})";
        if (l == null) { Check(false, $"{at}: no plan"); return; }
        Check(l.House == layout.Hash(), $"{at}: planned without the drawn layout ({l.Floors.Count} floors)");
        Check(l.Floors.Count == 2 && l.Below == 0, $"{at}: {l.Floors.Count} floors, {l.Below} below, want 2 and 0");
        // the hall's stair is carved out as a lane of its own: count the rooms by type, not the pieces
        Check(l.Floors.Count == 2 && l.Floors[0].Rooms.Select(r => r.Type).Distinct().Count() == 3 && l.Floors[1].Rooms.Count == 4,
            $"{at}: rooms {string.Join("/", l.Floors.Select(f => f.Rooms.Count))}, want 3 types / 4");
        Check(l.Floors[0].AllFlights().Count() == 1 && l.Floors.Count > 1 && l.Floors[1].Holes.Count == 1, $"{at}: no stair with its hole above");
        Check(l.Floors[0].Rooms.Any(r => r.Type == RoomType.Kitchen) && l.Floors.Last().Rooms.Count(r => r.Type == RoomType.Bedroom) == 2,
            $"{at}: room types lost");
        Check(l.GroundFloor.Rooms.Any(r => r.Openings.Any(x => x.Kind == OpeningKind.Entry)), $"{at}: no entry");
        Check(l.Floors.All(f => f.Rooms.Any(r => r.Openings.Any(x => x.Kind == OpeningKind.Window))), $"{at}: a floor without windows");
        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"{at}: the game's validator: {string.Join("; ", problems.Take(4))}");
        Check(l.Furniture.Count > 0, $"{at}: not furnished");
        try { InteriorMeshBuilder.Build(l); }
        catch (Exception ex) { Check(false, $"{at}: the mesh builder threw {ex.GetType().Name}: {ex.Message}"); }
        Check(l.Matches(b, l.Group), $"{at}: the stored plan is stale at once");
        Svg($"box_turned_{turnDeg:F0}", l);

        // withdrawn: the stored plan no longer matches, so the building is planned again, generated
        HouseLayouts.Set([]);
        Check(!l.Matches(b, l.Group), $"{at}: a stored plan outlives its withdrawn layout");
        var plain = InteriorGenerator.Generate(tile, 0, null, null);
        Check(plain != null && plain.House == null, $"{at}: a building without a layout is not generated as before");

        // a layout a good way off the box is refused, and the generated interior stays
        var skewed = Layout(o);
        skewed.YawDeg += 45;
        HouseLayouts.Set([skewed]);
        _warned.Clear();
        var kept = InteriorGenerator.Generate(tile, 0, null, null);
        Check(kept != null && kept.House == null, $"{at}: a layout 45 degrees off the box was used");
        Check(_warned.Any(w => w.Contains("not used")), $"{at}: a refused layout says nothing in the log");
        GD.Print($"[houselayout] {at}: {l.Floors.Sum(f => f.Rooms.Count)} rooms, {l.Furniture.Count} pieces, entry at {l.EntryX:F1}");
    }

    /// <summary>Every layout of an export file planned on its own building, from the tiles in <paramref name="chunks"/>.</summary>
    private void Export(string file, string chunks)
    {
        var all = HouseLayouts.Parse(System.IO.File.ReadAllText(file), m => Check(false, m));
        Check(all.Count > 0, $"no layout in {file}");
        foreach (var h in all.Values)
        {
            HouseLayouts.Set([h]);
            _warned.Clear();
            var at = TileId.FromLv95(h.E, h.N);
            bool found = false;
            for (int de = -1; de <= 1 && !found; de++)
            for (int dn = -1; dn <= 1 && !found; dn++)
            {
                var id = new TileId(at.E + de, at.N + dn);
                var path = System.IO.Path.Combine(chunks, BuildingFormat.FileName(id));
                if (!System.IO.File.Exists(path)) continue;
                BuildingTile tile;
                using (var st = System.IO.File.OpenRead(path)) tile = BuildingCodec.Decode(st);
                int i = tile.Buildings.FindIndex(b => b.Egid == h.Egid);
                if (i < 0) continue;
                found = true;
                var l = InteriorGenerator.Generate(tile, i, null, null);
                string what = $"EGID {h.Egid} ({id}_{i})";
                Check(l != null && l.House == h.Hash(), $"{what}: not planned from its layout: {string.Join("; ", _warned)}");
                if (l != null && l.House != null)
                {
                    var problems = InteriorValidator.Validate(l);
                    Check(problems.Count == 0, $"{what}: {string.Join("; ", problems.Take(4))}");
                    try { InteriorMeshBuilder.Build(l); }
                    catch (Exception ex) { Check(false, $"{what}: the mesh builder threw {ex.Message}"); }
                    Svg($"egid_{h.Egid}", l);
                    GD.Print($"[houselayout] {what}: {l.Floors.Count} floors, {l.Floors.Sum(f => f.Rooms.Count)} rooms, box {l.Width:F1} x {l.Depth:F1}, entry at {l.EntryX:F1}");
                }
            }
            Check(found, $"EGID {h.Egid}: no building carries it in {chunks}");
        }
    }

    private static void Svg(string name, InteriorLayout l)
    {
        System.IO.Directory.CreateDirectory(Out);
        System.IO.File.WriteAllText(System.IO.Path.Combine(Out, name + ".svg"), InteriorValidator.ToSvg(l));
    }

    /// <summary>
    /// One storey drawn as polygons: a living room and a kitchen split by an angled wall with a door in it. The game
    /// builds each as one room with its walls as drawn (#1197, cut into strips only to plan it), and the door finds
    /// the angled wall.
    /// </summary>
    private void Angled()
    {
        var id = new TileId(2594, 1120);
        var b = Box(500, 500, 12, 8, 3, 0);
        var tile = new BuildingTile { Id = id, Buildings = [b] };
        var o = BuildingOutline.Compute(b, id);
        var layout = new HouseLayout
        {
            Egid = 777, E = o.CenterE, N = o.CenterN, YawDeg = o.YawDeg,
            Floors =
            [
                new HouseFloor
                {
                    Level = 0,
                    Rooms =
                    [
                        new HouseRoom { Type = "Living", Points = [[-6, -4], [2, -4], [-1, 4], [-6, 4]] },
                        new HouseRoom { Type = "Kitchen", Points = [[2, -4], [6, -4], [6, 4], [-1, 4]] },
                    ],
                    Openings =
                    [
                        new HouseOpening { Kind = "door", X0 = 0.875, Z0 = -1, X1 = 0.5, Z1 = 0 },
                        new HouseOpening { Kind = "door", X0 = -5, Z0 = -4, X1 = -4, Z1 = -4 },
                        new HouseOpening { Kind = "window", X0 = 6, Z0 = -1, X1 = 6, Z1 = 0.5 },
                    ],
                },
            ],
        };
        var drawn = layout.Validate(o.LocalRings());
        Check(drawn.Count == 0, $"angled: the drawn layout breaks the web's rules: {string.Join("; ", drawn)}");
        HouseLayouts.Set([layout]);
        _warned.Clear();
        InteriorGenerator.OutlineFailure = null;
        var l = InteriorGenerator.Generate(tile, 0, null, null);
        if (l == null) { Check(false, "angled: no plan"); return; }
        Check(l.House == layout.Hash(), $"angled: planned without the drawn layout: {string.Join("; ", _warned)}");
        var rooms = l.Floors[0].Rooms;
        Check(rooms.Count == 2 && rooms.All(r => r.Shape != null && !r.Piece), $"angled: {rooms.Count} rooms ({rooms.Count(r => r.Shape != null)} shaped), want the two drawn, each one shaped room{(InteriorGenerator.OutlineFailure is { } why ? $": {why}" : "")}");
        Check(rooms.Count(r => r.Type == RoomType.Living) == 1 && rooms.Count(r => r.Type == RoomType.Kitchen) == 1, "angled: room types lost");
        Check(rooms.Sum(r => r.FloorArea) > 12 * 8 * 0.97, $"angled: the rooms cover {rooms.Sum(r => r.FloorArea):F1} of 96 m²");
        // the angled wall drawn as one straight wall: an edge of each room neither along x nor along z
        Check(rooms.All(r => r.Corners().Zip(r.Corners().Skip(1).Append(r.Corners()[0])).Any(e => Math.Abs(e.First.X - e.Second.X) > 0.5f && Math.Abs(e.First.Y - e.Second.Y) > 0.5f)),
            "angled: the angled wall is not one straight wall");
        Check(rooms.Any(r => r.Openings.Any(x => x.Kind == OpeningKind.Door && x.Edge != null && x.Other >= 0 && rooms[x.Other].Type != r.Type)),
            "angled: no door in the angled wall between the living room and the kitchen");
        Check(l.Furniture.Count(f => f.Type == FurnitureType.Sofa) <= 1, "angled: the living room furnished once per piece");
        Check(l.Furniture.Count > 0, "angled: nothing furnished (no piece big enough to hold the furniture)");
        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"angled: the game's validator: {string.Join("; ", problems.Take(4))}");
        try { InteriorMeshBuilder.Build(l); }
        catch (Exception ex) { Check(false, $"angled: the mesh builder threw {ex.GetType().Name}: {ex.Message}"); }
        Svg("angled", l);
        GD.Print($"[houselayout] angled: {rooms.Count} rooms, {l.Furniture.Count} pieces of furniture");
    }

    /// <summary>The house's layout in its outline's frame: x along its 12 m, z across its 8 m.</summary>
    private static HouseLayout Layout(BuildingOutline o) => new()
    {
        Egid = 777, E = o.CenterE, N = o.CenterN, YawDeg = o.YawDeg, StoreyHeight = 2.8,
        Floors =
        [
            new HouseFloor
            {
                Level = 0,
                Rooms =
                [
                    HouseRoom.Rect(-6, -4, 0, 4, "Living"),
                    HouseRoom.Rect(0, -4, 2, 4, "Hall"),
                    HouseRoom.Rect(2, -4, 6, 4, "Kitchen"),
                ],
                Openings =
                [
                    new HouseOpening { Kind = "door", X0 = 0, Z0 = -2, X1 = 0, Z1 = -1.1 },
                    new HouseOpening { Kind = "arch", X0 = 2, Z0 = -3, X1 = 2, Z1 = -1.6 },
                    new HouseOpening { Kind = "door", X0 = 0.5, Z0 = -4, X1 = 1.5, Z1 = -4 },
                    new HouseOpening { Kind = "window", X0 = -4, Z0 = 4, X1 = -2.5, Z1 = 4 },
                    new HouseOpening { Kind = "window", X0 = 6, Z0 = -1, X1 = 6, Z1 = 0.2 },
                ],
                Stairs = [new HouseStair { X0 = 0.4, Z0 = 0.4, X1 = 1.6, Z1 = 3.9, Up = "+z" }],
            },
            new HouseFloor
            {
                Level = 1,
                Rooms =
                [
                    HouseRoom.Rect(-6, -4, 0, 4, "Bedroom"),
                    HouseRoom.Rect(0, -4, 2, 4, "Hall"),
                    HouseRoom.Rect(2, -4, 6, 0, "Bedroom"),
                    HouseRoom.Rect(2, 0, 6, 4, "Bathroom"),
                ],
                Openings =
                [
                    new HouseOpening { Kind = "door", X0 = 0, Z0 = -3, X1 = 0, Z1 = -2.1 },
                    new HouseOpening { Kind = "door", X0 = 2, Z0 = -3, X1 = 2, Z1 = -2.1 },
                    new HouseOpening { Kind = "door", X0 = 2, Z0 = 1, X1 = 2, Z1 = 1.8 },
                    new HouseOpening { Kind = "window", X0 = -4, Z0 = -4, X1 = -2.8, Z1 = -4 },
                ],
            },
        ],
    };

    /// <summary>A flat-roofed box house turned <paramref name="turnDeg"/> about its centre (tile-local X east, Z south).</summary>
    private static Building Box(float cx, float cz, float w, float d, float h, float turnDeg)
    {
        float c = MathF.Cos(Mathf.DegToRad(turnDeg)), s = MathF.Sin(Mathf.DegToRad(turnDeg));
        (float X, float Z) P(float u, float v) => (cx + u * c - v * s, cz + u * s + v * c);
        var corners = new[] { P(-w / 2, -d / 2), P(w / 2, -d / 2), P(w / 2, d / 2), P(-w / 2, d / 2) };
        var t = new List<float>();
        void Tri((float X, float Z) a, float ya, (float X, float Z) b, float yb, (float X, float Z) e, float ye) =>
            t.AddRange([a.X, ya, a.Z, b.X, yb, b.Z, e.X, ye, e.Z]);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(a, 0, b, 0, b, h);
            Tri(a, 0, b, h, a, h);
        }
        Tri(corners[0], h, corners[1], h, corners[2], h);
        Tri(corners[0], h, corners[2], h, corners[3], h);
        return new Building { Kind = BuildingKind.House, Egid = 777, Floors = 2, MinY = 0, MaxY = h, Triangles = t.ToArray() };
    }
}
