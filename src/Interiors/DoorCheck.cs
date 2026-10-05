using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--doorcheck</c> (#498): several doors on one building, end to end through the real code,
/// without a world. Synthetic box solids go through <see cref="BuildingFootprint.ComputeDoors"/>
/// and <see cref="InteriorGenerator"/>, and this asks what a player would ask: does a 100 m block
/// have more than one way in, is every door on a wall, does a barn have a man-sized door beside
/// its pair, and does every facade door arrive at a doorway inside?
///
/// <para>
/// The spacing rules themselves are tier-0 (<c>DoorBudgetTests</c>); this is the part that needs
/// the wall triangles, the plan and the validator. Builds no world; prints a RESULT line and
/// exits 0 or 1.
/// </para>
/// </summary>
public static class DoorCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--doorcheck") >= 0;

    /// <summary>
    /// How far the opening inside a door may differ from the door on the facade (#509). Not zero:
    /// a cored plan's lobby keeps its own 2.2 m lintel, which is a hand's width over the 2.1 m
    /// door in front of it and reads as one hole. A shed's old 2.8 m outside / 2.1 m inside did not.
    /// </summary>
    private const float SameHole = 0.15f;

    private sealed record Box(string What, BuildingKind Kind, float Width, float Depth, float Height, int Least, int Most);

    private static readonly Box[] Boxes =
    [
        new("a house", BuildingKind.House, 10, 8, 7, 1, 1),
        new("a shed", BuildingKind.Annex, 4, 3, 2.6f, 1, 1),
        new("a barn", BuildingKind.Agricultural, 20, 10, 8, 2, 2),
        new("a garage", BuildingKind.Garage, 6, 6, 3.3f, 2, 2),
        new("a block of flats", BuildingKind.Apartment, 30, 15, 15, 3, 3),
        new("a 100 m shop front", BuildingKind.Commercial, 100, 20, 12, 5, DoorBudget.MaxPerBuilding),
        new("a works hall", BuildingKind.Industrial, 60, 30, 9, 3, DoorBudget.MaxPerBuilding),
        // low enough that the 2.8 m door of a works does not fit under its eave, tall enough that
        // a cut-down one does: the case #509 is about, where the clamp has to bite and the
        // doorway planned inside has to follow it down (the shed above has so little wall that
        // the MinDoorHeight floor wins instead)
        new("a low works", BuildingKind.Industrial, 12, 8, 3.4f, 1, 1),
    ];

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[doorcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        var tile = new BuildingTile
        {
            Id = new TileId(2583, 1113),
            Buildings = Boxes.Select((b, i) => Solid(b, 150f * i + 60f)).ToList(),
        };
        var doors = BuildingFootprint.ComputeDoors(tile, null, null);

        var names = new HashSet<string>();
        foreach (var d in doors)
            Expect(names.Add(d.KeyIn(tile.Id).ToString()), $"door {d.KeyIn(tile.Id)} is named once");

        for (int i = 0; i < Boxes.Length; i++)
        {
            var box = Boxes[i];
            var b = tile.Buildings[i];
            var mine = doors.Where(d => d.Index == i && d.Width > 0).OrderBy(d => d.Slot).ToList();
            // which industrial site it is, if it is one: only those carry loading bays (#528)
            var site = BuildingTypes.For(tile).Boxes[i] is { } plan
                ? BuildingTypes.SiteFor(new BuildingKey(tile.Id.E, tile.Id.N, i).ToString(),
                    b.Kind, plan.Width, plan.Depth, b.MaxY - b.MinY)
                : BuildingType.None;
            Expect(mine.Count >= box.Least && mine.Count <= box.Most,
                $"{box.What} ({box.Width:F0}x{box.Depth:F0} m) has {mine.Count} door(s), wanted {box.Least}..{box.Most}");
            Expect(mine.Select(d => d.Slot).SequenceEqual(Enumerable.Range(0, mine.Count)),
                $"{box.What}: its doors are slots 0..{mine.Count - 1}");

            foreach (var d in mine)
            {
                Expect(BuildingFootprint.DoorOnWall(b, d), $"{box.What} slot {d.Slot}: the door is on a wall, not in the air");
                if (d.Slot == 0)
                {
                    // A main door used to be sized by its kind alone, so on a low solid it reached
                    // over the eave: a 2.6 m shed took the 2.8 m door of a works and pushed it
                    // through its own roof. #509 cuts every kind's door down to the wall it stands
                    // on. These solids are flat-roofed, so their eave is their MaxY, and the wall
                    // runs from the sill the footprint settled on (this check builds no world, so
                    // that is BuildingFootprint.Compute's no-grid guess, 0.8 m up the solid).
                    float wall = b.MaxY - d.Position.Y;
                    float room = wall - BuildingFootprint.DoorUnderEave;
                    Expect(d.Height <= Math.Max(BuildingFootprint.MinDoorHeight, room) + 0.01f,
                        $"{box.What} slot 0: its main door is {d.Height:F2} m on {wall:F2} m of wall");
                    // and where the wall can take a door at all, its head really is under the eave
                    Expect(room < BuildingFootprint.MinDoorHeight
                           || d.Position.Y + d.Height <= b.MaxY - BuildingFootprint.DoorUnderEave + 0.01f,
                        $"{box.What} slot 0: its main door's head is under the {b.MaxY:F1} m eave, lintel and all");
                    Expect(d.Height >= BuildingFootprint.MinDoorHeight - 0.01f,
                        $"{box.What} slot 0: its main door is {d.Height:F2} m, still tall enough to walk through");
                    continue;
                }
                // an extra door is a plain pedestrian one, whatever the building is, and it fits —
                // unless it is an industrial site's loading bay (#528), which is the one extra door
                // that is wide, rolls up and is driven through
                Expect(d.Position.Y + d.Height <= b.MaxY + 0.01f, $"{box.What} slot {d.Slot}: the door is under the eave");
                if (d.Hang == DoorHang.RollUp && d.Vehicle)
                    Expect(site != BuildingType.None,
                        $"{box.What} slot {d.Slot}: a loading bay, and its building is an industrial site");
                else
                    Expect(d.Hang == DoorHang.Inward && !d.Vehicle && d.Width <= 1.8f,
                        $"{box.What} slot {d.Slot}: a pedestrian door ({d.Width:F1} m, {d.Hang}), not driven through");
                // and the sign over the door, and the shop behind it, belong to the main door
                Expect(!d.Bank && d.Shop == Loot.ShopType.None, $"{box.What} slot {d.Slot}: no second shop sign");
            }
            // ... and never two doors in the same piece of wall
            foreach (var a in mine)
                foreach (var c in mine)
                {
                    if (a.Slot >= c.Slot) continue;
                    float gap = new Vector2(a.Position.X - c.Position.X, a.Position.Z - c.Position.Z).Length()
                        - a.Width / 2 - c.Width / 2;
                    // Two loading bays stand a pier apart on purpose (#528) — the strip of wall
                    // that carries their lintels — and a bay sits closer to the main door than a
                    // pedestrian door would, because a works' office door is beside its first bay.
                    // Everything else keeps MinGap.
                    bool bays = a.Hang == DoorHang.RollUp && a.Vehicle && c.Hang == DoorHang.RollUp && c.Vehicle;
                    bool bayAndDoor = (a.Hang == DoorHang.RollUp && a.Vehicle) || (c.Hang == DoorHang.RollUp && c.Vehicle);
                    float least = bays ? 0.25f : bayAndDoor ? DoorBudget.BayToDoorGap : DoorBudget.MinGap;
                    Expect(gap >= least - 0.01f,
                        $"{box.What}: slots {a.Slot} and {c.Slot} are {gap:F1} m apart, wall to wall (least {least:F2})");
                }

            // a barn and a garage keep their vehicle door, and gain a door for a person
            if (box.Kind is BuildingKind.Agricultural or BuildingKind.Garage)
            {
                var main = mine.FirstOrDefault();
                Expect(main != null && main.Vehicle
                    && main.Hang == (box.Kind == BuildingKind.Garage ? DoorHang.RollUp : DoorHang.OutwardPair),
                    $"{box.What}: its main door is still the one a tractor goes through");
                Expect(mine.Any(d => d.Slot > 0 && !d.Vehicle), $"{box.What}: and a man-sized door beside it");
            }

            // the longest stretch of frontage with no door on it
            if (mine.Count > 1)
            {
                var front = mine.Where(d => d.Outward.IsEqualApprox(mine[0].Outward)).OrderBy(d => d.Position.X + d.Position.Z).ToList();
                for (int k = 1; k < front.Count; k++)
                {
                    float step = front[k].Position.DistanceTo(front[k - 1].Position);
                    Expect(step <= DoorBudget.Spacing + 1f,
                        $"{box.What}: {step:F0} m of its own wall between two doors (spacing {DoorBudget.Spacing:F0} m)");
                }
            }

            // and the inside: a doorway for every one of them, on a plan that still validates
            var layout = InteriorGenerator.Generate(tile, i, null, null);
            if (layout == null) { Expect(false, $"{box.What}: no plan"); continue; }
            var problems = InteriorValidator.Validate(layout);
            Expect(problems.Count == 0, $"{box.What}: the plan validates{(problems.Count > 0 ? " — " + string.Join("; ", problems.Take(3)) : "")}");
            foreach (var d in mine)
            {
                string name = d.KeyIn(tile.Id).ToString();
                var way = layout.EntranceOf(name);
                Expect(way != null, $"{box.What} slot {d.Slot}: the door arrives at a doorway inside");
                if (way == null) continue;
                Expect(way.Hang == d.Hang && way.Vehicle == d.Vehicle,
                    $"{box.What} slot {d.Slot}: inside and outside agree on how it hangs");
                // ... and on how big it is (#509). The facade door is the hole; the plan takes it
                // unless its own storey is too low, and a cored lobby keeps its own lintel, which
                // is why this is a tolerance and not an equality.
                var (_, inside) = layout.OpeningOf(way);
                // never a taller hole behind a shorter door — the half a player actually sees, and
                // what caught #497's 3.2 m loading bay standing behind a 2.8 m facade door
                Expect(inside <= d.Height + SameHole,
                    $"{box.What} slot {d.Slot}: the doorway inside is {inside:F2} m behind a {d.Height:F2} m door outside");
                // ... and not needlessly shorter either: it is the door's own height unless the
                // room it opens into has a lower ceiling than the storey (#498 with #497: a works
                // hall's 2.6 m service block stands inside a 9 m hall)
                float roomClear = RoomClearAt(layout, way);
                float want = Math.Min(d.Height, roomClear - 0.15f);
                Expect(inside >= want - SameHole,
                    $"{box.What} slot {d.Slot}: the doorway inside is {inside:F2} m, wanted {want:F2} m "
                    + $"({d.Height:F2} m door under a {roomClear:F2} m ceiling)");
                Expect(Math.Abs(way.X) <= layout.Width / 2 + 0.01f && Math.Abs(way.Z) <= layout.Depth / 2 + 0.01f,
                    $"{box.What} slot {d.Slot}: its doorway is inside the plan box");
            }
            GD.Print($"[doorcheck] {box.What}: {mine.Count} door(s), {layout.AllEntrances().Count} entrance(s), "
                + $"{layout.Floors.Count} floor(s), {string.Join("/", mine.Select(d => $"{d.Width:F1}m {d.Hang}"))}");
        }

        GD.Print($"[doorcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Headroom in the room an entrance arrives at: the ceiling the plan cut its doorway under,
    /// which is the room's own and not the storey's. Mirrors how <see cref="InteriorLayout.OpeningOf"/>
    /// picks the opening — nearest ground-floor entry — so the two agree on which room is meant.
    /// </summary>
    private static float RoomClearAt(InteriorLayout l, EntrancePlan e)
    {
        var at = new Vector2(e.X, e.Z);
        float clear = l.StoreyHeight - InteriorGenerator.Slab;
        float best = float.MaxValue;
        foreach (var r in l.GroundFloor.Rooms)
            foreach (var o in r.Openings)
            {
                if (o.Kind != OpeningKind.Entry) continue;
                var p = o.Side switch
                {
                    Side.Front => new Vector2(o.Center, r.Z0),
                    Side.Back => new Vector2(o.Center, r.Z1),
                    Side.Left => new Vector2(r.X0, o.Center),
                    _ => new Vector2(r.X1, o.Center),
                };
                float d = p.DistanceTo(at);
                if (d < best) { best = d; clear = l.ClearOf(r); }
            }
        return clear;
    }

    /// <summary>A plain box solid: four walls of two triangles each and a flat roof, at <paramref name="cz"/> south.</summary>
    private static Building Solid(Box box, float cz)
    {
        float hw = box.Width / 2, hd = box.Depth / 2, h = box.Height;
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            tris.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
        }
        void Wall(Vector3 p0, Vector3 p1)
        {
            Tri(p0, p1, p1 + Vector3.Up * h);
            Tri(p0, p1 + Vector3.Up * h, p0 + Vector3.Up * h);
        }
        var nw = new Vector3(500 - hw, 0, cz - hd);
        var ne = new Vector3(500 + hw, 0, cz - hd);
        var se = new Vector3(500 + hw, 0, cz + hd);
        var sw = new Vector3(500 - hw, 0, cz + hd);
        Wall(nw, ne); Wall(ne, se); Wall(se, sw); Wall(sw, nw);
        Tri(nw + Vector3.Up * h, ne + Vector3.Up * h, se + Vector3.Up * h);
        Tri(nw + Vector3.Up * h, se + Vector3.Up * h, sw + Vector3.Up * h);
        return new Building
        {
            Kind = box.Kind, MinY = 0, MaxY = h,
            Floors = (byte)Math.Max(1, (int)(h / 3f)),
            Triangles = tris.ToArray(),
        };
    }
}
