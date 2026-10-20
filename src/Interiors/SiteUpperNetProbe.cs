using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--siteuppernet A|B|C</c> with <c>--connect</c> on the generated world (#827): a site office's
/// upper container over the network, through its raised door off the gallery. A walks from the
/// stair's foot up the stair and along the walkway (its walk driven through
/// <see cref="FootPlayer.WalkControls"/>, the intent a server-run bot walks with), opens the upper
/// door, walks in onto the plan's floor 1 and takes a stack there. B, on the walkway from the start,
/// sees the door open and its portal, sees A through the doorway, then walks in and finds the stack
/// gone. C joins late, after A's take: the door must already be open; C climbs the stair too, walks
/// in, finds the stack gone, and walks out onto the walkway (not the ground under it). Coordinated
/// through chat lines; every peer finds the same door and gallery from the tile alone.
/// </summary>
public partial class SiteUpperNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--siteuppernet");

    private readonly WorldOrigin _origin;

    public SiteUpperNetProbe(ItemController items, WorldOrigin origin) : base(items, "siteupper", "SU") => _origin = origin;

    public SiteUpperNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    /// <summary>The gallery's walking points, tile-local (the tile's world corner is taken again each time: the origin moves).</summary>
    private Vector3 _foot, _top, _step;
    private TileId _tile;

    private Vector3 World(Vector3 tileLocal) => _origin.ToWorld(_tile.MinE, _tile.MaxN, 0) + tileLocal;

    /// <summary>The upper door's sill, world: the deck's top.</summary>
    private DoorKey _key;
    private ConstructionSite? _site;
    private SiteDressingPlan? _yard;
    private float DeckY() => DoorIndex.Find(_key)?.World.Y ?? float.NaN;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(180)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        var loot = LootService.Instance!;

        // to the site, and the upper door of its office nearest it: the same on every peer
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? found = null;
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut
                && (found = DoorIndex.NearestOfKind(me.GlobalPosition, 200f, BuildingKind.UnderConstruction, also: d => d.Level == 1)) != null, 90))
        { Fail("no raised site office door near the spot"); return; }
        await Seconds(1.0);
        var door = found!.Value;
        string doorKey = door.Key.ToString();
        _tile = door.Key.Tile;
        _key = door.Key;
        GD.Print($"{Log} the upper door {doorKey} at {door.World} (level {door.Level})");
        Expect(door.Level == 1 && door.Kind == BuildingKind.UnderConstruction, $"{doorKey} is a building site's raised door");
        if (!await Gallery(door)) return;
        var inward = -door.Outward;
        DoorIndex.Entry Door() => DoorIndex.Find(door.Key) ?? door;
        float Deck() => Door().World.Y;

        if (_role == "A")
        {
            // at the stair's foot, on the ground, then up it and along the walkway to the door
            if (!await StandAt(me, World(_foot), "the stair's foot")) return;
            Expect(Deck() - me.GlobalPosition.Y > 2f, $"A starts on the ground, {Deck() - me.GlobalPosition.Y:F2} m under the deck");
            if (!await Heard("B", "watching", 120)) { Fail("B never came to watch"); return; }
            if (!await Climb(me, "A")) return;
            Face(me, inward);
            await Seconds(0.5);
            Expect(!interiors.IsOpen(doorKey), $"{doorKey} starts shut");
            me.TryInteract();
            if (!await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable && l.Swing >= 1f, 15))
            { Fail($"the door {doorKey} never opened"); return; }
            Say("opened");
            if (!await WalkIn(me, interiors, doorKey)) return;

            var layout = interiors.Current!;
            var node = interiors.CurrentNode!;
            int floor = layout.EntranceFloor(layout.EntranceFor(doorKey));
            long epoch = LootTables.Epoch(layout.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            int index = -1;
            for (int i = 0; i < layout.Furniture.Count && index < 0; i++)
                if (layout.Furniture[i].Floor == floor && LootTables.IsLootable(layout.Furniture[i].Type) && !LootTables.IsLocked(layout.Furniture[i].Type)
                    && LootTables.ContentsOf(layout, i, epoch).Count > 0) index = i;
            if (index < 0) { Fail("no container upstairs holds anything this restock period: run again with another EPOCH"); return; }
            if (!await Search(me, layout, node, loot, index)) return;
            var start = loot.OpenContents().ToList();
            var first = start[0];
            loot.Take(first.Index);
            Expect(await Until(() => !loot.Waiting && !loot.OpenContents().Any(c => c.Index == first.Index), 5), "A's take upstairs was granted");
            loot.Close();
            // back by the door, inside, so it is not shut for want of anybody near, and out of the way
            var placement = InteriorManager.PlacementFor(layout, _origin);
            var way = layout.EntranceFor(doorKey);
            me.EnterInterior(layout.Key, placement * new Vector3(way.X + way.InX * 3.0f, layout.EntranceY(way) + 0.1f, way.Z + way.InZ * 3.0f), me.Rotation.Y);
            bool b = false, c = false;
            for (int t = 0; t < 150 && !(b && c); t++)
            {
                Say($"took {index} {first.Index}");
                if (t == 0) GD.Print($"{Log} took {first.Stack.Id} x{first.Stack.Count} from the {layout.Furniture[index].Type} ({index}) on floor {floor}");
                await Seconds(2);
                b |= _heard.Any(l => l.Contains("SU B done"));
                c |= _heard.Any(l => l.Contains("SU C done"));
            }
            Expect(b, "B came in upstairs and found the stack gone");
            Expect(c, "C, who joined late, climbed, came in and found the stack gone");
        }
        else
        {
            if (_role == "B")
            {
                // on the walkway from the start, along it from the door away from the stair (A comes
                // up the stair and along the walkway to the door: not through B), facing the door
                var away = ((World(_step) - World(_top)) with { Y = 0 }).Normalized();
                var by = World(_step) + away * 0.9f;
                if (!await StandAt(me, by, "the walkway")) return;
                if (!await Until(() => Mathf.Abs(me.GlobalPosition.Y - Deck()) < 0.6f, 5))
                    Expect(false, $"B stands on the walkway (y {me.GlobalPosition.Y:F2}, deck {Deck():F2})");
                Face(me, (Door().World - me.GlobalPosition) with { Y = 0 });
                Expect(!interiors.IsOpen(doorKey), $"{doorKey} is shut before A opens it");
                for (int t = 0; t < 90 && !interiors.IsOpen(doorKey); t++)
                {
                    Say("watching");
                    await Until(() => interiors.IsOpen(doorKey), 2);
                }
                Expect(interiors.IsOpen(doorKey), $"B sees A open {doorKey}");
                Expect(await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable, 20), $"B's portal of {doorKey} is open");
                // A, inside upstairs, is in the space B sees through the open door
                Expect(await Until(() => RemoteIn(interiors) != null, 20), "B sees A inside through the doorway");
                if (DisplayServer.GetName() != "headless") GD.Print($"{Log} picture: {Shot("siteupper_B_sees_door_open")}");
            }
            else
            {
                if (!await StandAt(me, World(_foot), "the stair's foot")) return;
                // joined after A's take: the door is open from the table the server sends a newcomer
                Expect(await Until(() => interiors.IsOpen(doorKey), 10), $"C, joining late, finds {doorKey} open");
                if (!await Climb(me, "C")) return;
            }
            if (!await Heard("A", "took", 200)) { Fail("A never took anything"); return; }
            string took = _heard.Last(l => l.Contains("SU A took"));
            var parts = took[(took.IndexOf("took", StringComparison.Ordinal) + 5)..].Split(' ');
            int index = int.Parse(parts[0]), gone = int.Parse(parts[1]);

            var stand = World(_step);
            me.LeaveInterior(stand + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
            me.Velocity = Vector3.Zero;
            await Seconds(1.0);
            Expect(await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable, 10), $"{_role}'s portal of {doorKey} is open");
            if (!await WalkIn(me, interiors, doorKey)) return;
            var layout = interiors.Current!;
            if (!await Search(me, layout, interiors.CurrentNode!, loot, index)) return;
            var left = loot.OpenContents().ToList();
            GD.Print($"{Log} the {layout.Furniture[index].Type} holds {string.Join(", ", left.Select(x => $"{x.Stack.Id} x{x.Stack.Count}"))}");
            Expect(!left.Any(x => x.Index == gone), $"{_role} finds the stack A took gone from the {layout.Furniture[index].Type} upstairs");
            loot.Close();
            Say("done");
            if (_role == "C")
            {
                // and out again, onto the walkway: a raised door's way out is its deck, not the yard under it
                var way = layout.EntranceFor(doorKey);
                var node = interiors.CurrentNode!;
                var at = node.GlobalTransform * new Vector3(way.X + way.InX * 1.2f, layout.EntranceY(way) + 0.1f, way.Z + way.InZ * 1.2f);
                var face = node.GlobalTransform.Basis * new Vector3(-way.InX, 0, -way.InZ);
                me.EnterInterior(layout.Key, at, Mathf.Atan2(-face.X, -face.Z));
                me.Velocity = Vector3.Zero;
                await Seconds(0.8);
                Input.ActionPress(PlayerInput.MoveForward);
                bool outside = await Until(() => !me.Indoors, 6);
                await Seconds(0.3);
                Input.ActionRelease(PlayerInput.MoveForward);
                await Seconds(1.0);
                Expect(outside, $"C walked out through {doorKey}");
                Expect(Mathf.Abs(me.GlobalPosition.Y - Deck()) < 0.6f && me.IsOnFloor(),
                    $"C stands on the walkway after (y {me.GlobalPosition.Y:F2}, deck {Deck():F2})");
            }
        }
        await Finish(2);
    }

    /// <summary>The gallery's foot, top and this door's doorstep, from the site's plan as every peer makes it.</summary>
    private async Task<bool> Gallery(DoorIndex.Entry door)
    {
        var chunks = FindChunks(GetTree().Root);
        if (chunks?.Source is not { } source) { Fail("no chunk source to plan the site from"); return false; }
        var bt = await source.LoadBuildingsAsync(_tile);
        var roads = await source.LoadRoadsAsync(_tile);
        if (bt == null || SitePlans.Of(bt, roads, door.Key.Index) is not { } site
            || SiteShellBuilder.Office(bt, site, null) is not { Gallery: { } gallery } office)
        { Fail($"no gallery planned for {door.Key}"); return false; }
        int first = office.Doors.Count - office.UpperDoors;
        int k = door.Key.Slot - first;
        if (k < 0 || k >= gallery.Doorsteps.Count) { Fail($"{door.Key} is not one of the office's upper doors"); return false; }
        _site = site;
        _yard = SiteShellBuilder.Dressing(bt, site, null);
        _foot = SiteShellBuilder.ToTile(site, gallery.Foot);
        _top = SiteShellBuilder.ToTile(site, gallery.Top);
        _step = SiteShellBuilder.ToTile(site, gallery.Doorsteps[k]);
        GD.Print($"{Log} the gallery: {gallery.Steps} steps, side {gallery.Side}, the door's doorstep {k}");
        return true;
    }

    private static Terrain.ChunkManager? FindChunks(Node n)
    {
        if (n is Terrain.ChunkManager c) return c;
        foreach (var child in n.GetChildren())
            if (FindChunks(child) is { } found) return found;
        return null;
    }

    /// <summary>Puts the player at a world point (from above, landing on what is there), and waits for the ground.</summary>
    private async Task<bool> StandAt(FootPlayer me, Vector3 at, string where)
    {
        me.LeaveInterior(at + Vector3.Up * 0.5f, me.Rotation.Y);
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        if (await Until(() => me.IsOnFloor(), 10)) return true;
        Fail($"{_role} never stood at {where}");
        return false;
    }

    private static void Face(FootPlayer me, Vector3 dir) => me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);

    /// <summary>
    /// Up the stair and along the walkway to the door's doorstep, walked through
    /// <see cref="FootPlayer.WalkControls"/> (a bot's seam): the top of the stair at the deck's height,
    /// and never lower than the deck once on it.
    /// </summary>
    private async Task<bool> Climb(FootPlayer me, string who)
    {
        // the yard's collision is committed a cell at a time round the body: wait for the stair's ramp
        // and the deck under the landing before setting off
        var rays = me.GetWorld3D().DirectSpaceState;
        bool Solid(Vector3 tileLocal, float want)
        {
            var at = World(tileLocal);
            var q = PhysicsRayQueryParameters3D.Create(at with { Y = DeckY() + 1.5f }, at with { Y = DeckY() - 4f });
            q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
            var hit = rays.IntersectRay(q);
            return hit.Count > 0 && Mathf.Abs(hit["position"].AsVector3().Y - want) < 0.35f;
        }
        var mid = (_foot + _top) / 2;
        if (!await Until(() => Solid(_top, DeckY()) && Solid(mid, DeckY() - 1.3f), 30))
            GD.Print($"{Log} (the stair's collision did not show under the landing and mid-flight within 30 s)");
        foreach (var (target, what) in new[] { (_top, "the stair's top"), (_step, "the doorstep") })
        {
            Vector3 Goal() => World(target);
            me.WalkControls = () =>
            {
                var d = (Goal() - me.GlobalPosition) with { Y = 0 };
                return (d.Length() > 0.25f ? d.Normalized() * 0.7f : Vector3.Zero, false);
            };
            double next = GameClock.Now;
            bool there = await Until(() =>
            {
                if (GameClock.Now >= next)
                {
                    next = GameClock.Now + 2;
                    GD.Print($"{Log} walking to {what}: at {me.GlobalPosition}, {((Goal() - me.GlobalPosition) with { Y = 0 }).Length():F2} m to go, on the floor {me.IsOnFloor()}");
                }
                return ((Goal() - me.GlobalPosition) with { Y = 0 }).Length() < 0.3f;
            }, 25);
            me.WalkControls = null;
            await Seconds(0.4);
            // the deck's real height is the door's sill (the site plan here guesses the ground as the building's base)
            float dy = me.GlobalPosition.Y - DeckY();
            Expect(there && Mathf.Abs(dy) < 0.6f, $"{who} walked to {what} ({(there ? "there" : "not there")}, {dy:F2} m off the deck)");
            if (!there)
            {
                // what stands round the body, in the site's frame (#827 diagnosis)
                if (_site != null && _yard != null)
                {
                    var t = me.GlobalPosition - _origin.ToWorld(_tile.MinE, _tile.MaxN, 0);
                    var d = new Vector2(t.X, t.Z) - _site.Box.Center;
                    var p = new Vector3(d.Dot(_site.Box.AxisU), t.Y, d.Dot(_site.Box.AxisV));
                    GD.Print($"{Log} at site ({p.X:F2}, {p.Y:F2}, {p.Z:F2}); goal {((Goal() - _origin.ToWorld(_tile.MinE, _tile.MaxN, 0)))}; velocity {me.Velocity}, on a wall {me.IsOnWall()}");
                    var chunks = FindChunks(GetTree().Root);
                    var toGoal = ((Goal() - me.GlobalPosition) with { Y = 0 }).Normalized();
                    for (float k = -0.4f; k <= 1.21f; k += 0.4f)
                        if (chunks != null && chunks.TryGetHeight(me.GlobalPosition + toGoal * k, out float gh))
                            GD.Print($"{Log}   the terrain {k:F1} m on: {gh:F2} (feet {me.GlobalPosition.Y:F2})");
                    var space = me.GetWorld3D().DirectSpaceState;
                    var q = PhysicsRayQueryParameters3D.Create(me.GlobalPosition + Vector3.Up * 0.3f, me.GlobalPosition + Vector3.Up * 0.3f + toGoal * 1.2f);
                    q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
                    var hit = space.IntersectRay(q);
                    GD.Print($"{Log}   a knee ray toward the goal hits {(hit.Count == 0 ? "nothing" : $"{hit["collider"]} at {hit["position"]}")}");
                    foreach (var b in _yard.Boxes.Where(b => b.Solid && p.X > b.Min.X - 0.6f && p.X < b.Max.X + 0.6f && p.Z > b.Min.Z - 0.6f && p.Z < b.Max.Z + 0.6f
                        && p.Y + 1.8f > b.Min.Y && p.Y < b.Max.Y))
                        GD.Print($"{Log}   near a solid {b.Part} {b.Min} .. {b.Max}");
                }
                Fail($"{who} could not walk to {what}");
                return false;
            }
        }
        return true;
    }

    /// <summary>Walks through the open door: the player's space must become the office's, on the door's floor.</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, string doorKey)
    {
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);   // the server learns the space a round trip after the sill
        var layout = interiors.Current!;
        var local = interiors.CurrentNode!.GlobalTransform.AffineInverse() * me.GlobalPosition;
        int floor = InteriorManager.FloorAt(layout, local.Y);
        var room = layout.Floors[floor].Rooms.FirstOrDefault(r => local.X > r.X0 && local.X < r.X1 && local.Z > r.Z0 && local.Z < r.Z1);
        Expect(layout.Kind == BuildingKind.UnderConstruction && interiors.SpaceOf(Multiplayer.GetUniqueId()) == layout.Key,
            $"{_role} walked into the site office {layout.Key}, its space the server's");
        Expect(floor == layout.EntranceFloor(layout.EntranceFor(doorKey)) && room?.Type is RoomType.SiteManager or RoomType.DrawingOffice,
            $"{_role} stands upstairs, on floor {floor} in the {room?.Type.ToString() ?? "no room"}");
        return true;
    }

    /// <summary>A remote player standing in the interior this peer has built, if any.</summary>
    private FootPlayer? RemoteIn(InteriorManager interiors) => Find(GetTree().Root);

    private FootPlayer? Find(Node n)
    {
        if (n is FootPlayer p && p != Me && !p.IsMultiplayerAuthority() && p.Visible && InteriorManager.InInteriorSpace(p.GlobalPosition)) return p;
        foreach (var c in n.GetChildren())
            if (Find(c) is { } found) return found;
        return null;
    }

    /// <summary>Stands in front of a piece of furniture, on its floor, and opens it.</summary>
    private async Task<bool> Search(FootPlayer me, InteriorLayout layout, InteriorNode node, LootService loot, int index)
    {
        var f = layout.Furniture[index];
        var front = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, layout.FloorY(f.Floor) + 0.1f, f.Z) + front), Mathf.Atan2(-face.X, -face.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(0.8);
        Expect(LootService.NearestContainer(me, layout, node) == index, $"standing in front of the {f.Type}");
        Expect(loot.TrySearch(me) && loot.IsOpen, $"opened the {f.Type}");
        if (!await Until(() => !loot.Waiting, 10)) { Fail("its contents never arrived"); return false; }
        return true;
    }
}
