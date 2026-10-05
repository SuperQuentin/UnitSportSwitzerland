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
                    // A main door is sized by its kind alone, and on a low solid it can reach over
                    // the eave: a 2.6 m shed takes the 2.8 m door of a works. Older than this
                    // check and left alone by #498, which only adds doors; noted, not asserted.
                    if (d.Position.Y + d.Height > b.MaxY + 0.01f)
                        GD.Print($"[doorcheck] note {box.What}: its main door is {d.Height:F1} m on a {b.MaxY:F1} m wall (pre-existing)");
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
                Expect(Math.Abs(way.X) <= layout.Width / 2 + 0.01f && Math.Abs(way.Z) <= layout.Depth / 2 + 0.01f,
                    $"{box.What} slot {d.Slot}: its doorway is inside the plan box");
            }
            GD.Print($"[doorcheck] {box.What}: {mine.Count} door(s), {layout.AllEntrances().Count} entrance(s), "
                + $"{layout.Floors.Count} floor(s), {string.Join("/", mine.Select(d => $"{d.Width:F1}m {d.Hang}"))}");
        }

        GD.Print($"[doorcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
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
