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

    /// <param name="Turn">
    /// Degrees the solid is turned in plan. A box square to the world cannot catch a mirrored
    /// direction convention — a mirror and the truth agree on it — so one building is turned (#524).
    /// </param>
    private sealed record Box(string What, BuildingKind Kind, float Width, float Depth, float Height,
        int Least, int Most, float Turn = 0f);

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
        // turned to no axis, so a mirrored plan frame puts its doorways on visibly wrong walls
        new("a turned block", BuildingKind.Commercial, 48, 18, 12, 3, DoorBudget.MaxPerBuilding, Turn: 31f),
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
        Digest.Clear();
        Note("boxes", tile, null);

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
                    Expect(site != BuildingType.None || d.Link.Any,
                        $"{box.What} slot {d.Slot}: a loading bay, and its building is an industrial site (or a block's garage door, #558)");
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
                // ...and faces the way the real door does. Distance and containment survive a
                // mirrored plan frame; a direction does not (#524). On a turned solid a mirror
                // lands the doorway on a visibly different wall, so this is the assertion that
                // can tell a correct convention from a flipped one.
                var axisU = new Vector2(Mathf.Cos(layout.Yaw), -Mathf.Sin(layout.Yaw));
                var axisV = new Vector2(-axisU.Y, axisU.X);
                var inward = axisU * way.InX + axisV * way.InZ;
                var into = new Vector2(-d.Outward.X, -d.Outward.Z);
                Expect(inward.Normalized().Dot(into.Normalized()) > 0.3f,
                    $"{box.What} slot {d.Slot}: the doorway inside faces the way the door does "
                    + $"(in {inward.Normalized()} vs {into.Normalized()})");
            }
            GD.Print($"[doorcheck] {box.What}: {mine.Count} door(s), {layout.AllEntrances().Count} entrance(s), "
                + $"{layout.Floors.Count} floor(s), {string.Join("/", mine.Select(d => $"{d.Width:F1}m {d.Hang}"))}");
        }

        failures += Garages();
        failures += FacedDoors();
        failures += RaisedDoors();
        failures += StackedDoors();
        failures += Baseline("doorcheck", BaselinePath, Digest);

        GD.Print($"[doorcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// E works the door the player faces (#822): a real building site in Graubünden (LV95
    /// 2742681/1254064) has its toilet cabins 1.4 m in front of the office door, their doors facing
    /// it, 2 m lower down the slope. Standing at the office door, a cabin's door is the nearer one,
    /// so the nearest-door pick opened the toilet and the office could never be entered. The three
    /// doors and the doorstep as that site's probe logged them, in a stand-in tile.
    /// </summary>
    private static int FacedDoors()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[doorcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        var tile = new TileId(-2, -2);
        var outOffice = new Vector3(-0.48452148f, 0, -0.87477934f);
        DoorSpot Spot(int slot, Vector3 at, Vector3 outward, float width) =>
            new(0, at, outward, width, 2.0f) { Slot = slot, Kind = BuildingKind.UnderConstruction };
        DoorIndex.SetTile(tile, Vector3.Zero,
        [
            Spot(0, new Vector3(681.026f, 651.8086f, -63.925232f), outOffice, 0.9f),
            Spot(1, new Vector3(680.8676f, 649.7846f, -65.346436f), -outOffice, 0.8f),
            Spot(2, new Vector3(679.7741f, 649.7846f, -64.74078f), -outOffice, 0.8f),
        ]);
        var player = new Vector3(680.4446f, 652.0358f, -64.97497f);
        const float reach = 1.6f;
        string? Slot(DoorIndex.Entry? e) => e?.Key.Slot.ToString();
        try
        {
            var plain = DoorIndex.Candidates(player, null).FirstOrDefault(c => c.Rejected == null && c.Distance < reach);
            Expect(plain.Door.Key.Slot != 0 && plain.Door.Key.Tile == tile,
                $"by plain distance the nearest door to the office's doorstep is a cabin's (slot {plain.Door.Key.Slot}): the case still bites");
            // the cabins' sills are 2 m under the doorstep, the office's at it: the door at the
            // player's level is taken first (#827), so with no look at all E works the office
            Expect(Slot(DoorIndex.NearestEntrance(player, reach)) == "0",
                $"the nearest door at the player's level is the office's (slot {Slot(DoorIndex.NearestEntrance(player, reach))}), not a cabin's 2 m below");
            Expect(Slot(DoorIndex.FacedEntrance(player, -outOffice, reach)) == "0",
                $"looking at the office, E works the office door (slot {Slot(DoorIndex.FacedEntrance(player, -outOffice, reach))})");
            Expect(Slot(DoorIndex.FacedEntrance(player, outOffice, reach)) is "1" or "2",
                $"turned round to the cabins, E works a cabin's (slot {Slot(DoorIndex.FacedEntrance(player, outOffice, reach))})");
            Expect(Slot(DoorIndex.FacedEntrance(player, Vector3.Up, reach)) == "0",
                "looking straight up, the nearest door at the player's level (#827): the office's");
        }
        finally { DoorIndex.ClearTile(tile); }
        return failures;
    }

    /// <summary>
    /// A raised door (#827) through the real door code: a hand-made two-storey plan, a room on each
    /// floor, a ground door and a door one storey up the same facade (the site office's upper
    /// container over the lower one). The upper doorway stands on floor 1 inside, its link is the
    /// same drop as the ground door's, its opening is floor 1's cut, the plan validates with no
    /// stair up (the upper floor has its own way in) and fails without that door, and a ground
    /// entrance's JSON carries no <c>Level</c>.
    /// </summary>
    private static int RaisedDoors()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[doorcheck] {(ok ? "ok  " : "FAIL")} raised door: {what}");
        }

        const float storey = 2.6f, ground = 10f;
        var key = new BuildingKey(2583, 1113, 900);
        InteriorLayout Plan(bool upperDoor, int upperLevel = 1)
        {
            RoomPlan Room(float width, float top) => new()
            {
                X0 = -3, Z0 = -3, X1 = 3, Z1 = 3, Type = RoomType.Office,
                Openings = { new OpeningPlan { Side = Side.Front, Center = 0, Width = width, Bottom = 0, Top = top, Kind = OpeningKind.Entry } },
            };
            EntrancePlan Way(int slot, int level, float width) => new()
            {
                Door = new DoorKey(key, slot).ToString(), X = 0, Z = -3, InX = 0, InZ = 1, Width = width,
                DoorX = 500, DoorY = ground + level * storey, DoorZ = 997 - 0.03f, DoorOutX = 0, DoorOutZ = -1,
                DoorWidth = width, DoorHeight = 2.1f, Hang = DoorHang.Inward, Level = level,
            };
            var l = new InteriorLayout
            {
                Key = key.ToString(), Kind = BuildingKind.Commercial, CenterX = 500, CenterZ = 1000, Yaw = 0,
                Width = 6, Depth = 6, StoreyHeight = storey, DoorX = 500, DoorY = ground, DoorZ = 997 - 0.03f,
                DoorOutX = 0, DoorOutZ = -1, DoorWidth = 0.9f, DoorHeight = 2.1f, EntryX = 0, EntryWidth = 0.9f,
            };
            l.Floors.Add(new FloorPlan { Rooms = { Room(0.9f, 2.1f) } });
            l.Floors.Add(new FloorPlan { Rooms = { Room(0.8f, 2.0f) } });
            l.Entrances.Add(Way(0, 0, 0.9f));
            if (upperDoor) l.Entrances.Add(Way(1, upperLevel, 0.8f));
            return l;
        }

        var plan = Plan(true);
        var problems = InteriorValidator.Validate(plan);
        Expect(problems.Count == 0, $"a plan whose upper floor has its own door and no stair validates{(problems.Count > 0 ? ": " + string.Join("; ", problems.Take(3)) : "")}");
        var noDoor = InteriorValidator.Validate(Plan(false));
        Expect(noDoor.Any(p => p.Contains("no stairs from floor 0")) && noDoor.Any(p => p.Contains("unreachable")),
            $"without the upper door it does not: {string.Join("; ", noDoor.Take(3))}");
        var tooHigh = InteriorValidator.Validate(Plan(true, 2));
        Expect(tooHigh.Any(p => p.Contains("which the plan has not got")), $"a door on a floor the plan has not got is refused: {string.Join("; ", tooHigh.Take(2))}");

        var low = plan.Entrances[0];
        var high = plan.Entrances[1];
        Expect(plan.EntranceY(low) == 0f && Math.Abs(plan.EntranceY(high) - storey) < 1e-5f && plan.EntranceFloor(high) == 1,
            $"its sill inside is a storey up: {plan.EntranceY(high):F2} m on floor {plan.EntranceFloor(high)} (the ground door's {plan.EntranceY(low):F2} m)");
        Expect(plan.OpeningOf(low) == (0.9f, 2.1f) && plan.OpeningOf(high) == (0.8f, 2.0f),
            $"each opens on its own floor's cut: {plan.OpeningOf(low)} and {plan.OpeningOf(high)}");

        var origin = new Core.WorldOrigin(2583500, 1113500);
        var a = DoorLink.Create(plan, low, origin, 0.9f, 2.1f);
        var b = DoorLink.Create(plan, high, origin, 0.8f, 2.1f);
        Expect(Math.Abs(b.Outside.Origin.Y - a.Outside.Origin.Y - storey) < 1e-3f && Math.Abs(b.Inside.Origin.Y - a.Inside.Origin.Y - storey) < 1e-3f,
            $"both doorways of the upper door are a storey over the ground door's (outside {b.Outside.Origin.Y - a.Outside.Origin.Y:F3} m, inside {b.Inside.Origin.Y - a.Inside.Origin.Y:F3} m)");
        Expect(a.ToInside.IsEqualApprox(b.ToInside),
            $"its link is the same drop as the ground door's ({b.ToInside.Origin} against {a.ToInside.Origin})");
        Expect((b.ToInside * b.Outside.Origin).IsEqualApprox(b.Inside.Origin), "its facade sill maps onto its doorway inside");

        string json = plan.ToJson();
        var back = InteriorLayout.FromJson(json);
        Expect(!System.Text.RegularExpressions.Regex.IsMatch(Plan(false).ToJson(), "\"Level\""),
            "a ground entrance's JSON has no Level field, as before");
        Expect(back != null && back.Entrances[0].Level == 0 && back.Entrances[1].Level == 1, "a stored plan keeps the door's level");
        return failures;
    }

    /// <summary>
    /// E at a door stacked over another (#827): two doors of one building on the same facade, one
    /// a storey over the other, as a site office's upper container is over the lower one. The pick
    /// takes the door at the player's floor, from the ground and from the walkway, looking at the
    /// facade or not, and a VR hand the door whose opening it is in. Asserted with the upper sill
    /// at the containers' 2.6 m, and again at 2.0 m, inside <see cref="DoorIndex.MaxRise"/>: the
    /// pick must not owe it to the reach band being shorter than the storey.
    /// </summary>
    private static int StackedDoors()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[doorcheck] {(ok ? "ok  " : "FAIL")} stacked doors: {what}");
        }

        var tile = new TileId(-3, -3);
        var outward = new Vector3(0, 0, -1);
        const float sill = 400f;
        string? Slot(DoorIndex.Entry? e) => e?.Key.Slot.ToString();
        foreach (float rise in new[] { 2.6f, 2.0f })
        {
            Expect(rise > DoorIndex.LevelBand && (rise == 2.6f || rise < DoorIndex.MaxRise),
                $"the upper sill {rise:F1} m up {(rise < DoorIndex.MaxRise ? "is inside" : "is past")} the {DoorIndex.MaxRise:F1} m reach band");
            DoorIndex.SetTile(tile, Vector3.Zero,
            [
                new DoorSpot(0, new Vector3(100, sill, 100), outward, 0.9f, 2.1f) { Slot = 0, Kind = BuildingKind.UnderConstruction },
                new DoorSpot(0, new Vector3(100, sill + rise, 100), outward, 0.9f, 2.1f) { Slot = 1, Kind = BuildingKind.UnderConstruction, Level = 1 },
            ]);
            try
            {
                // in front of the doors, on the ground and on the walkway, 0.8 m out
                var below = new Vector3(100.1f, sill, 99.2f);
                var above = new Vector3(100.1f, sill + rise, 99.2f);
                var look = -outward;
                foreach (var (feet, want, where) in new[] { (below, "0", "the ground"), (above, "1", "the walkway") })
                {
                    Expect(Slot(DoorIndex.NearestEntrance(feet, 1.6f)) == want, $"{rise:F1} m: from {where}, the nearest door is slot {want} ({Slot(DoorIndex.NearestEntrance(feet, 1.6f))})");
                    Expect(Slot(DoorIndex.FacedEntrance(feet, look, 1.6f)) == want, $"{rise:F1} m: from {where} looking at the facade, E works slot {want} ({Slot(DoorIndex.FacedEntrance(feet, look, 1.6f))})");
                    Expect(Slot(DoorIndex.FacedEntrance(feet, -look, 1.6f)) == want, $"{rise:F1} m: from {where} turned away, E works slot {want} ({Slot(DoorIndex.FacedEntrance(feet, -look, 1.6f))})");
                    // a VR hand at the handle, 1.1 m over the feet, reaching into the doorway
                    var hand = feet + new Vector3(0, 1.1f, 0.6f);
                    var held = DoorIndex.NearestEntrance(hand, 0.7f, null, d => hand.Y > d.World.Y + 0.3f && hand.Y < d.World.Y + d.Height + 0.3f);
                    Expect(Slot(held) == want, $"{rise:F1} m: from {where}, a hand at the handle takes slot {want} ({Slot(held)})");
                }
            }
            finally { DoorIndex.ClearTile(tile); }
        }
        return failures;
    }

    /// <summary>
    /// The underground garage door of a block of flats (#558): eight long blocks beside a street at
    /// a distance each, through the real <see cref="BuildingFootprint.ComputeDoors"/> (which needs a
    /// road tile) and the real generator. A garage door is exempt from the pedestrian-door rules
    /// above, as a loading bay is, and every property that exemption lets go is asserted here: it
    /// is a roll-up vehicle door of its own size on a wall, its link is the one the road's distance
    /// and class call for, there is no door at all with no street in reach or a motorway as near,
    /// and the plan behind it validates, with a car park for the garage to belong to and (PR 2) a
    /// doorway 3 m wide into the ground floor ramp room, in line with the door along its wall, and a
    /// ramp from it down into the car park.
    /// </summary>
    private static int Garages()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[doorcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        // a block 80 x 18 m, 15 m tall: three front doors, a car park strip behind the stairwells and
        // just the depth a ramp down to it takes (GarageRule.RampDepth)
        var block = new Box("a long block", BuildingKind.Apartment, 80, 18, 15, 3, DoorBudget.MaxPerBuilding);
        const int copies = 8;
        // street centreline, metres in front of the block's south wall, its class, and what comes of it
        (string What, RoadClass Class, float Gap, LinkKind Want)[] cases =
        [
            ("a minor street 6 m off", RoadClass.Minor, 6f, LinkKind.Sidewalk),
            ("a street 16 m off", RoadClass.Road, 16f, LinkKind.Stub),
            ("a main road 8 m off", RoadClass.Major, 8f, LinkKind.Stub),
            ("a street 40 m off", RoadClass.Road, 40f, LinkKind.None),
            ("a motorway 12 m off", RoadClass.Motorway, 12f, LinkKind.None),
        ];
        foreach (var (what, cls, gap, want) in cases)
        {
            var tile = new BuildingTile
            {
                Id = new TileId(2583, 1113),
                Buildings = Enumerable.Range(0, copies).Select(i => Solid(block, 110f * i + 60f)).ToList(),
            };
            var roads = new RoadTile
            {
                Id = tile.Id,
                Segments = Enumerable.Range(0, copies).Select(i => new RoadSegment
                {
                    Class = cls, Surface = RoadSurface.Paved, Width = RoadFormat.DefaultWidth(cls),
                    Points = [380f, 0f, 110f * i + 60f + 9f + gap, 620f, 0f, 110f * i + 60f + 9f + gap],
                }).ToList(),
            };
            var doors = BuildingFootprint.ComputeDoors(tile, roads, null);
            Note($"garages {cls} {gap:F0}", tile, roads);
            var garages = doors.Where(d => d.Link.Any).ToList();
            if (want == LinkKind.None)
            {
                Expect(garages.Count == 0, $"{what}: no garage door ({garages.Count} found)");
                continue;
            }
            Expect(garages.Count >= 1 && garages.Count <= copies, $"{what}: {garages.Count} of {copies} blocks roll a garage door");
            // 40 % by key hash: not none, not all
            Expect(garages.Count < copies, $"{what}: not every block has one");
            foreach (var g in garages)
            {
                var b = tile.Buildings[g.Index];
                Expect(g.Link.Kind == want, $"{what}: slot {g.Slot} links with {g.Link.Kind}, wanted {want}");
                Expect(g.Vehicle && g.Hang == DoorHang.RollUp && Math.Abs(g.Width - GarageRule.Width) < 0.01f && g.Height >= 2.2f,
                    $"{what}: slot {g.Slot} is a {g.Width:F1} x {g.Height:F1} m roll-up vehicle door");
                Expect(BuildingFootprint.DoorOnWall(b, g), $"{what}: slot {g.Slot} is on a wall");
                Expect(g.Link.Length is > 0.3f and < 25f, $"{what}: its link is {g.Link.Length:F1} m long");
                int total = doors.Count(d => d.Index == g.Index && d.Width > 0);
                Expect(total >= 2, $"{what}: the block has {total} doors, garage included");
                var layout = InteriorGenerator.Generate(tile, g.Index, roads, null);
                if (layout == null) { Expect(false, $"{what}: no plan"); continue; }
                var problems = InteriorValidator.Validate(layout);
                Expect(problems.Count == 0, $"{what}: the plan validates{(problems.Count > 0 ? " � " + string.Join("; ", problems.Take(3)) : "")}");
                Expect(layout.Floors.Any(f => f.Rooms.Any(r => r.Type == RoomType.CarPark)),
                    $"{what}: the basement behind it has a car park (the rule and the generator agree)");
                // PR 2 (#558): the door leads in, to the ramp room under it, and the ramp goes down to the car park
                var way = layout.EntranceOf(g.KeyIn(tile.Id).ToString());
                Expect(way is { Vehicle: true, Hang: DoorHang.RollUp } && way.Width >= 3f - 0.01f, $"{what}: the garage door arrives at a doorway 3 m wide inside ({way?.Width:F1})");
                var ramp = layout.Floors[layout.Below - 1].AllFlights().FirstOrDefault(x => x.Ramp);
                Expect(ramp != null && layout.Version == InteriorLayout.CurrentVersion, $"{what}: a ramp runs down from the ground floor (plan version {layout.Version})");
                if (ramp != null && way != null)
                {
                    var room = layout.GroundFloor.Rooms.FirstOrDefault(r => r.Type == RoomType.Ramp && r.Openings.Any(o => o.Kind == OpeningKind.Entry));
                    Expect(room != null && ramp.TopEnd.X > room.X0 && ramp.TopEnd.X < room.X1 && Math.Abs(room.X0 + (room.X1 - room.X0) / 2 - way.X) < 0.5f,
                        $"{what}: the doorway opens into the ground floor ramp room, in line with the ramp");
                    Expect(layout.Floors[layout.Below - 1].Rooms.Any(r => r.Type == RoomType.CarPark && ramp.Bottom.Z > r.Z0 && ramp.Bottom.Z < r.Z1),
                        $"{what}: the ramp's foot is in the car park");
                    // the plan frame is the building's: the lane stands where the door does along the wall
                    var axisU = new Vector2(Mathf.Cos(layout.Yaw), -Mathf.Sin(layout.Yaw));
                    float along = (new Vector2(g.Position.X, g.Position.Z) - new Vector2(layout.CenterX, layout.CenterZ)).Dot(axisU);
                    Expect(Math.Abs(along - way.X) < 0.5f && along > ramp.X0 && along < ramp.X1, $"{what}: the door is {along:F1} m along the wall, the doorway and the ramp at {way.X:F1}");
                }
            }
        }
        // shops under flats (#558): the rarer 50 % roll and 3 front doors, on 100 x 20 m commercial blocks
        {
            const int mixedCopies = 40;
            var tile = new BuildingTile
            {
                Id = new TileId(2583, 1113),
                Buildings = Enumerable.Range(0, mixedCopies)
                    .Select(i => Solid(new Box("a mixed block", BuildingKind.Commercial, 100, 18, 15, 1, DoorBudget.MaxPerBuilding), 110f * i + 60f)).ToList(),
            };
            var roads = new RoadTile
            {
                Id = tile.Id,
                Segments = Enumerable.Range(0, mixedCopies).Select(i => new RoadSegment
                {
                    Class = RoadClass.Minor, Surface = RoadSurface.Paved, Width = RoadFormat.DefaultWidth(RoadClass.Minor),
                    Points = [360f, 0f, 110f * i + 60f + 9f + 6f, 640f, 0f, 110f * i + 60f + 9f + 6f],
                }).ToList(),
            };
            var doors = BuildingFootprint.ComputeDoors(tile, roads, null);
            Note("mixed", tile, roads);
            var types = BuildingTypes.For(tile);
            var garages = doors.Where(d => d.Link.Any).ToList();
            Expect(garages.Count > 0 && garages.Count < mixedCopies, $"mixed blocks: {garages.Count} of {mixedCopies} roll a garage door (about seven in ten, shops under flats only)");
            foreach (var g in garages)
            {
                Expect(InteriorGenerator.ApartmentTypeFor(BuildingFootprint.Compute(tile, g.Index, roads, null)!, BuildingKind.Commercial, 5, false) == BuildingType.MixedUse,
                    $"mixed blocks: slot {g.Slot} of building {g.Index} is a block with shops under its flats");
                Expect(doors.Count(x => x.Index == g.Index && x.Width > 0) >= 2, $"mixed blocks: building {g.Index} has an entrance beside its garage door");
                var problems = InteriorValidator.Validate(InteriorGenerator.Generate(tile, g.Index, roads, null)!);
                Expect(problems.Count == 0, $"mixed blocks: building {g.Index} plans and validates");
            }
        }
        // ramp first (#694): 80 x 14.5 m is too shallow for a ramp square to the front wall, so it runs along the
        // facade: the garage door at the end of the wall, the entrance beyond the band and the car park hall, a
        // doorway into the ramp room, and the ramp running along X from it to the hall
        {
            const int alongCopies = 16;
            var shallow = new Box("a shallow block", BuildingKind.Apartment, 80, 14.5f, 15, 3, DoorBudget.MaxPerBuilding);
            var tile = new BuildingTile
            {
                Id = new TileId(2583, 1113),
                Buildings = Enumerable.Range(0, alongCopies).Select(i => Solid(shallow, 110f * i + 60f)).ToList(),
            };
            var roads = new RoadTile
            {
                Id = tile.Id,
                Segments = Enumerable.Range(0, alongCopies).Select(i => new RoadSegment
                {
                    Class = RoadClass.Minor, Surface = RoadSurface.Paved, Width = RoadFormat.DefaultWidth(RoadClass.Minor),
                    Points = [380f, 0f, 110f * i + 60f + 7.25f + 6f, 620f, 0f, 110f * i + 60f + 7.25f + 6f],
                }).ToList(),
            };
            var doors = BuildingFootprint.ComputeDoors(tile, roads, null);
            Note("shallow", tile, roads);
            var garages = doors.Where(d => d.Link.Any).ToList();
            Expect(garages.Count > 0 && garages.All(g => g.Ramp == GarageRule.RampKind.Along), $"along the facade: {garages.Count} of {alongCopies} shallow blocks roll a garage door, all with the ramp along the facade");
            foreach (var g in garages)
            {
                var layout = InteriorGenerator.Generate(tile, g.Index, roads, null);
                if (layout == null) { Expect(false, $"along the facade: building {g.Index} has no plan"); continue; }
                var problems = InteriorValidator.Validate(layout);
                Expect(problems.Count == 0, $"along the facade: building {g.Index} validates{(problems.Count > 0 ? " - " + string.Join("; ", problems.Take(3)) : "")}");
                var way = layout.EntranceOf(g.KeyIn(tile.Id).ToString());
                var ramp = layout.Floors[layout.Below - 1].AllFlights().FirstOrDefault(x => x.Ramp);
                Expect(way is { Vehicle: true } && ramp is { AlongX: true }, $"along the facade: building {g.Index}: the door leads to a ramp that runs along X");
                if (way == null || ramp == null) continue;
                // the entrance and its stairwell stand beyond the band and the hall, the doorway at the band's end
                var axisU = new Vector2(Mathf.Cos(layout.Yaw), -Mathf.Sin(layout.Yaw));
                float gx = (new Vector2(g.Position.X, g.Position.Z) - new Vector2(layout.CenterX, layout.CenterZ)).Dot(axisU);
                float entryX = layout.EntryX;
                Expect(ramp.RunDir == g.RampDir && Math.Abs(gx - way.X) < 0.5f, $"along the facade: building {g.Index}: the descent runs {(ramp.RunDir > 0 ? "+X" : "-X")} from the doorway at {way.X:F1}");
                Expect(ramp.RunDir > 0 ? entryX > ramp.ZBottom + GarageRule.AlongParkLength - 0.6f : entryX < ramp.ZBottom - GarageRule.AlongParkLength + 0.6f,
                    $"along the facade: building {g.Index}: the entrance at {entryX:F1} is beyond the car park hall (the foot is at {ramp.ZBottom:F1})");
                Expect(layout.Floors[layout.Below - 1].Rooms.Any(r => r.Type == RoomType.CarPark && ramp.Bottom.X > r.X0 && ramp.Bottom.X < r.X1),
                    $"along the facade: building {g.Index}: the ramp's foot is in the car park");
            }
        }
        return failures;
    }

    /// <summary>
    /// The ground doors' golden digest (#827): every door of every building this check plans, as
    /// the footprint places it and as its plan's entrance, opening and <see cref="DoorLink"/> frames
    /// read it, with a hash of the plan less its version. Written once on <c>main</c>'s door code
    /// (<c>--doorcheck --door-baseline write</c>) and compared on every run, so a change to the door
    /// system that moves any existing door by a hundredth of a millimetre, or changes any byte of a
    /// plan but its version, fails here.
    /// </summary>
    private const string BaselinePath = "res://tests/door-baseline.txt";

    private static readonly List<string> Digest = new();

    private static string F(float v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    /// <summary>A plan's hash, its version left out: a bump alone regenerates the same plan.</summary>
    public static string PlanHash(InteriorLayout l)
    {
        int version = l.Version;
        l.Version = 0;
        var bytes = System.Text.Encoding.UTF8.GetBytes(l.ToJson());
        l.Version = version;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    /// <summary>One door as the digest reads it: the spot, and its entrance, opening and frames.</summary>
    public static string DoorLine(string label, TileId tile, DoorSpot d, InteriorLayout? layout)
    {
        string key = d.KeyIn(tile).ToString();
        var line = $"{label} door {key} at {V(d.Position)} out {V(d.Outward)} {F(d.Width)}x{F(d.Height)} {d.Hang} vehicle {d.Vehicle} "
            + $"{d.Kind} link {d.Link.Kind} ramp {d.Ramp}/{d.RampDir} bank {d.Bank} shop {d.Shop}";
        var e = layout?.EntranceOf(key);
        if (layout == null || e == null) return line + " | locked";
        var (w, top) = layout.OpeningOf(e);
        var origin = new Core.WorldOrigin(tile.MinE + 500, tile.MaxN - 500);
        var link = DoorLink.Create(layout, e, origin, d.Width, d.Height);
        return line + $" | in {F(e.X)},{F(e.Z)} dir {F(e.InX)},{F(e.InZ)} w {F(e.Width)} door {F(e.DoorX)},{F(e.DoorY)},{F(e.DoorZ)} "
            + $"h {F(e.DoorHeight)} {e.Hang} {e.Vehicle} opening {F(w)}x{F(top)} inside {V(link.Inside.Origin)} facing {V(link.Inside.Basis.Z)} "
            + $"outside {V(link.Outside.Origin)} pass {F(link.HalfPass)}x{F(link.PassHeight)} swing {F(link.SwingSeconds)}";
    }

    /// <summary>Every door of a tile's buildings into <see cref="Digest"/>, with each building's plan hash.</summary>
    private static void Note(string label, BuildingTile tile, RoadTile? roads)
    {
        var doors = BuildingFootprint.ComputeDoors(tile, roads, null);
        foreach (var group in doors.Where(d => d.Width > 0).GroupBy(d => d.Index))
        {
            var layout = InteriorGenerator.Generate(tile, group.Key, roads, null);
            Digest.Add($"{label} building {group.Key} {layout?.Kind} plan {(layout == null ? "none" : PlanHash(layout))}");
            foreach (var d in group.OrderBy(d => d.Slot)) Digest.Add(DoorLine(label, tile.Id, d, layout));
        }
    }

    /// <summary>
    /// Compares <paramref name="lines"/> with the committed baseline at <paramref name="res"/>, or
    /// writes it with <c>--door-baseline write</c>. The number of lines that differ (each printed,
    /// the first twenty), a missing file counting as one.
    /// </summary>
    public static int Baseline(string tag, string res, IReadOnlyList<string> lines)
    {
        string path = ProjectSettings.GlobalizePath(res);
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--door-baseline");
        if (at >= 0 && at + 1 < args.Length && args[at + 1] == "write")
        {
            System.IO.File.WriteAllText(path, string.Join('\n', lines) + '\n');
            GD.Print($"[{tag}] wrote the door baseline: {lines.Count} line(s) to {path}");
            return 0;
        }
        if (!System.IO.File.Exists(path))
        {
            GD.Print($"[{tag}] FAIL no door baseline at {path}");
            return 1;
        }
        var want = System.IO.File.ReadAllLines(path);
        int diffs = 0;
        for (int i = 0; i < Math.Max(want.Length, lines.Count); i++)
        {
            string? a = i < want.Length ? want[i] : null, b = i < lines.Count ? lines[i] : null;
            if (a == b) continue;
            if (++diffs <= 20) GD.Print($"[{tag}] FAIL door baseline line {i + 1}: was {a ?? "(none)"} / now {b ?? "(none)"}");
        }
        GD.Print($"[{tag}] {(diffs == 0 ? "ok  " : "FAIL")} every existing door as the baseline has it ({lines.Count} line(s), {diffs} differ)");
        return diffs;
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
        foreach (var r in l.EntranceRooms(e))
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
        float turn = Mathf.DegToRad(box.Turn);
        Vector3 Corner(float x, float z) =>
            new(500 + x * Mathf.Cos(turn) - z * Mathf.Sin(turn), 0, cz + x * Mathf.Sin(turn) + z * Mathf.Cos(turn));
        var nw = Corner(-hw, -hd);
        var ne = Corner(hw, -hd);
        var se = Corner(hw, hd);
        var sw = Corner(-hw, hd);
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
