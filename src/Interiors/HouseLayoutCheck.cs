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
/// </summary>
public partial class HouseLayoutCheck : Node
{
    public static bool Requested => CmdArgs.Has("--houselayoutcheck");

    private int _failures;
    private readonly List<string> _warned = new();

    public override void _Ready()
    {
        HouseLayouts.Warn = m => { _warned.Add(m); GD.Print("[houselayout] warned: " + m); };
        foreach (float turn in new[] { 0f, 30f, 90f })
            House(turn);
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
        Check(l.Floors.Count == 2 && l.Floors[0].Rooms.Count == 3 && l.Floors[1].Rooms.Count == 4,
            $"{at}: rooms {string.Join("/", l.Floors.Select(f => f.Rooms.Count))}, want 3/4");
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

        // withdrawn: the stored plan no longer matches, so the building is planned again, generated
        HouseLayouts.Set([]);
        Check(!l.Matches(b, l.Group), $"{at}: a stored plan outlives its withdrawn layout");
        var plain = InteriorGenerator.Generate(tile, 0, null, null);
        Check(plain != null && plain.House == "", $"{at}: a building without a layout is not generated as before");

        // a layout a good way off the box is refused, and the generated interior stays
        var skewed = Layout(o);
        skewed.YawDeg += 45;
        HouseLayouts.Set([skewed]);
        _warned.Clear();
        var kept = InteriorGenerator.Generate(tile, 0, null, null);
        Check(kept != null && kept.House == "", $"{at}: a layout 45 degrees off the box was used");
        Check(_warned.Any(w => w.Contains("not used")), $"{at}: a refused layout says nothing in the log");
        GD.Print($"[houselayout] {at}: {l.Floors.Sum(f => f.Rooms.Count)} rooms, {l.Furniture.Count} pieces, entry at {l.EntryX:F1}");
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
                    new HouseRoom { X0 = -6, Z0 = -4, X1 = 0, Z1 = 4, Type = "Living" },
                    new HouseRoom { X0 = 0, Z0 = -4, X1 = 2, Z1 = 4, Type = "Hall" },
                    new HouseRoom { X0 = 2, Z0 = -4, X1 = 6, Z1 = 4, Type = "Kitchen" },
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
                    new HouseRoom { X0 = -6, Z0 = -4, X1 = 0, Z1 = 4, Type = "Bedroom" },
                    new HouseRoom { X0 = 0, Z0 = -4, X1 = 2, Z1 = 4, Type = "Hall" },
                    new HouseRoom { X0 = 2, Z0 = -4, X1 = 6, Z1 = 0, Type = "Bedroom" },
                    new HouseRoom { X0 = 2, Z0 = 0, X1 = 6, Z1 = 4, Type = "Bathroom" },
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
