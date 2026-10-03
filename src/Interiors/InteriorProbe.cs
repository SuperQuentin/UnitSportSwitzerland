using System.Diagnostics;
using Godot;
using UnitSport.Net;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --interiorcheck[,out.png] [--at E,N]</c>
///
/// <para>
/// Two halves. <b>Plans</b>: every building in the 3x3 tiles around the spawn gets a footprint,
/// a door and a plan, and every plan is run through <see cref="InteriorValidator"/> — rooms
/// reachable, doorways cut both sides, stairs between all floors, furniture clear of doors.
/// Floor plans of a few of each kind are written as SVG to <c>user://interior_plans</c>.
/// <b>Live</b>: a player walks up to a real house door, presses E to open it, and walks through
/// it: they must end up standing on the interior's ground floor; the stair ramp must be there to
/// stand on; walking back out through the front door (opening it again if it shut meanwhile)
/// must put them on the street by it; walked away from, the door must shut by itself. Then, if a
/// church stands nearby: in through its tower door, into the one church interior; up every flight
/// to the bell chamber; out through the nave's door and back in through it, into the same
/// interior. Non-zero exit on any failure.
/// </para>
/// </summary>
public partial class InteriorProbe : Node, Core.IOriginShiftAware
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly IChunkSource _source;
    private readonly string? _shot;
    private FootPlayer? _player;
    private bool _ok = true;
    private int _step = -1;
    private double _t, _total;
    private DoorIndex.Entry _door;
    private int _doorless;
    private BuildingGroup? _church;
    private TileId _churchTile;
    private string _churchKey = "";
    private DoorIndex.Entry _churchIn, _churchOut;
    private bool _viewed;

    /// <summary>Connected to a server: the probe drives this client's own networked player.</summary>
    private bool Online => NetLink.Online(this);
    private long Me => Online ? Multiplayer.GetUniqueId() : 1;

    public InteriorProbe(ChunkManager chunks, WorldOrigin origin, IChunkSource source, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _source = source;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--interiorcheck");

    /// <summary>
    /// <c>--doorkind Agricultural</c>: the check and the door watch use the nearest door of that
    /// kind of building instead of the nearest door (a barn's outward pair, say).
    /// </summary>
    public static BuildingKind? DoorKindArg()
    {
        var args = CmdArgs.All;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--doorkind" && Enum.TryParse<BuildingKind>(args[i + 1], true, out var kind))
                return kind;
        return null;
    }

    /// <summary>The door the check uses, and the door watch watches.</summary>
    public static DoorIndex.Entry? ChooseDoor(Vector3 at) =>
        DoorKindArg() is { } kind ? DoorIndex.Nearest(at, 400f, kind) : DoorIndex.Nearest(at, 400f);

    private void Check(bool condition, string what)
    {
        GD.Print($"[interior] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override async void _Ready()
    {
        await CheckPlans();
        _step = 0;
    }

    private async Task CheckPlans()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        var centre = TileId.FromLv95(e, n);
        string svgDir = ProjectSettings.GlobalizePath("user://interior_plans");
        Directory.CreateDirectory(svgDir);

        var stats = new Dictionary<BuildingKind, (int Count, int Bad, int Floors, int Rooms, int Cored)>();
        var written = new Dictionary<BuildingKind, int>();
        int problems = 0, shown = 0, total = 0;
        var clock = Stopwatch.StartNew();

        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var id = new TileId(centre.E + dx, centre.N + dy);
                var tile = await _source.LoadBuildingsAsync(id);
                if (tile == null) continue;
                var roads = await _source.LoadRoadsAsync(id);
                var grid = await _source.LoadChunkAsync(id);
                var result = await Task.Run(() =>
                {
                    var list = new List<(Building B, InteriorLayout? L, List<string> P)>();
                    var types = BuildingTypes.For(tile);
                    for (int i = 0; i < tile.Buildings.Count; i++)
                    {
                        var fp = BuildingFootprint.Compute(tile, i, roads, grid);
                        if (fp == null) { list.Add((tile.Buildings[i], null, new List<string> { "no footprint" })); continue; }
                        // a church is planned once, from its primary; its other doors are still checked
                        var group = types.GroupOf(i);
                        var layout = group == null || group.Primary == i ? InteriorGenerator.Generate(tile, i, roads, grid) : null;
                        var problems = layout != null ? InteriorValidator.Validate(layout) : new List<string>();
                        if (group?.Type == BuildingType.Church && group.Primary == i && layout != null)
                        {
                            GD.Print($"[interior] church {layout.Key}: {group.Members.Count} solid(s) ({string.Join(", ", group.Parts)}), "
                                + $"{layout.AllEntrances().Count} entrance(s), {layout.Floors.Count} floor(s) of {layout.StoreyHeight:F1} m, "
                                + $"{layout.Furniture.Count(f => f.Type is FurnitureType.Pew or FurnitureType.FrontPew)} pews, at LV95 {tile.Id.MinE + layout.CenterX:F0}/{tile.Id.MaxN - layout.CenterZ:F0}");
                            if (layout.Type != BuildingType.Church) problems.Add("church planned as a plain building");
                            if (layout.Entrances.Count < group.Members.Count)
                                GD.Print($"[interior]   (only {layout.Entrances.Count} of {group.Members.Count} solids have a door)");
                            if (_church == null || group.Members.Count > _church.Members.Count)
                            {
                                _church = group;
                                _churchTile = tile.Id;
                                _churchKey = layout.Key;
                            }
                        }
                        if (fp.Door.Width <= 0) Interlocked.Increment(ref _doorless);
                        else if (!BuildingFootprint.DoorOnWall(tile.Buildings[i], fp.Door))
                            problems.Add($"door at {fp.Door.Position:F1} is not on a wall");
                        list.Add((tile.Buildings[i], layout, problems));
                    }
                    return list;
                });

                foreach (var (b, l, p) in result)
                {
                    if (l == null && p.Count == 0) continue; // a church's other solid: planned with its primary
                    total++;
                    var s = stats.GetValueOrDefault(b.Kind);
                    s.Count++;
                    if (p.Count > 0)
                    {
                        s.Bad++;
                        problems++;
                        if (shown++ < 12) GD.Print($"[interior] {l?.Key ?? "?"} {b.Kind}: {string.Join("; ", p.Take(3))}");
                    }
                    if (l != null)
                    {
                        s.Floors += l.Floors.Count;
                        s.Rooms += l.Floors.Sum(f => f.Rooms.Count);
                        if (l.Floors[0].Rooms.Count > 1) s.Cored++;
                        int w = written.GetValueOrDefault(b.Kind);
                        bool interesting = l.Floors.Count > 1 || w == 0;
                        if (l.Type != BuildingType.None)
                            File.WriteAllText(Path.Combine(svgDir, $"{l.Type}_{l.Key}.svg"), InteriorValidator.ToSvg(l));
                        else if (w < 3 && interesting)
                        {
                            written[b.Kind] = w + 1;
                            File.WriteAllText(Path.Combine(svgDir, $"{b.Kind}_{l.Key}.svg"), InteriorValidator.ToSvg(l));
                        }
                    }
                    stats[b.Kind] = s;
                }
            }

        GD.Print($"[interior] planned {total} buildings in {clock.ElapsedMilliseconds} ms, plans in {svgDir}");
        foreach (var (kind, s) in stats.OrderBy(k => k.Key))
            GD.Print($"[interior]   {kind,-18} {s.Count,5} buildings  {s.Bad,4} bad  cored {s.Cored,5}  "
                + $"avg floors {(float)s.Floors / Math.Max(1, s.Count):F1}  avg rooms {(float)s.Rooms / Math.Max(1, s.Count):F1}");
        GD.Print($"[interior] {_doorless} buildings have no wall at door height (canopies, sunk structures) and no door");
        Check(total > 0, $"found buildings around {e:F0}/{n:F0}");
        Check(problems == 0, $"every plan valid ({problems} of {total} with problems)");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_step < 0) return;
        _t += delta;
        _total += delta;
        if (_total > 400) { Check(false, $"timed out in step {_step}"); Finish(); return; }
        var interiors = InteriorManager.Instance!;

        switch (_step)
        {
            case 0: // a player on the ground, and a house door drawn near it
            {
                var (e, n) = SpawnPoint.ParseTarget();
                var at = _origin.ToWorld(e, n, 0);
                if (_player == null)
                {
                    if (!_chunks.TryGetHeight(at, out float g)) return;
                    if (Online)
                    {
                        // the player the server spawned for us: its position is what the server checks
                        _player = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
                            .FirstOrDefault(p => p.IsMultiplayerAuthority());
                        if (_player == null) return;
                        _player.Camera.Current = true;
                        _player.LeaveInterior(new Vector3(at.X, g + 1f, at.Z), 0);
                        GD.Print($"[interior] online as peer {Me}");
                    }
                    else
                    {
                        _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
                        AddChild(_player);
                        _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
                    }
                    var probePlayer = _player;
                    interiors.LocalPlayer = () => probePlayer;
                    return;
                }
                if (!_player.IsOnFloor()) return;
                var door = ChooseDoor(_player.GlobalPosition);
                if (door == null) return;
                _door = door.Value;
                StandOutside(_door);
                GD.Print($"[interior] door of {_door.Key} at {_door.World:F1}");
                Next();
                break;
            }
            case 1:
                if (_t < 1.0 || !_player!.IsOnFloor()) { if (_t > 20) { Check(false, "stood at the door"); Finish(); } return; }
                Check(!interiors.IsOpen(_door.Key.ToString()), "the door starts shut");
                Next();
                break;

            case 2:
            {
                if (WalkThrough(interiors, _door.Key.ToString(), inward: true, delta, "_door_open") is not { } done) return;
                if (!done) { Finish(); return; }
                Next();
                break;
            }

            case 3:
                if (_t < 1.5) return;
                // online the server says so: a round trip after the step over the sill
                Check(interiors.SpaceOf(Me) == interiors.Current?.Key, "the player's space is the building walked into");
                Check(_player!.GlobalPosition.Y < InteriorManager.InteriorBaseY + 5f && _player.GlobalPosition.Y > InteriorManager.InteriorBaseY - 1f,
                    $"standing on the ground floor (y {_player.GlobalPosition.Y:F1})");
                Check(_player.IsOnFloor(), "on a floor, not falling");
                if (_shot != null)
                {
                    var image = GetViewport().GetTexture().GetImage();
                    if (image.SavePng(_shot) == Error.Ok) GD.Print($"[interior] wrote {_shot}");
                }
                StairRay(interiors);
                if (CmdArgs.Has("--doorcam")) { _step = 100; _t = 0; break; }
                Input.ActionPress(PlayerInput.MoveForward);
                Next();
                break;

            case 100:
                if (DoorCam(interiors, delta) is not { } camDone) return;
                Finish();
                break;

            case 4:
                if (_t < 1.0) return;
                Input.ActionRelease(PlayerInput.MoveForward);
                Check(_player!.GlobalPosition.Y > InteriorManager.InteriorBaseY - 1f, "walked without falling through");
                Next();
                break;

            case 5: // stand in front of something with loot in it, facing it, and press E
            {
                var li = interiors.Current!;
                var ni = interiors.CurrentNode!;
                _lootIndex = li.Furniture.FindIndex(f => f.Floor == 0 && Loot.LootTables.IsLootable(f.Type) && !Loot.LootTables.IsLocked(f.Type));
                if (Online)
                {
                    GD.Print("[interior] (online: loot not tested here)");
                    _step = 7;
                    return;
                }
                if (_lootIndex < 0 || Loot.LootService.Instance == null)
                {
                    GD.Print("[interior] (no lootable furniture on the ground floor: loot not tested)");
                    _step = 7;
                    return;
                }
                var f = li.Furniture[_lootIndex];
                var front = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Vector3(0, 0, f.D / 2 + 0.55f);
                var at = new Vector3(f.X, 0.1f, f.Z) + front;
                var face = ni.GlobalTransform.Basis * -front;
                _player.EnterInterior(li.Key, ni.GlobalTransform * at, Mathf.Atan2(-face.X, -face.Z));
                _player.Velocity = Vector3.Zero;
                _lootBefore = CountItems();
                // a restock period no earlier run has touched, so there is something to find
                // — and one where this container is not empty, or the take path goes untested
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                for (int tries = 0; tries < 200; tries++)
                {
                    Loot.LootTables.EpochOffset = 1000 + System.Random.Shared.Next(1_000_000);
                    if (Loot.LootTables.ContentsOf(li, _lootIndex, Loot.LootTables.Epoch(li.Key, now)).Count > 0) break;
                }
                Next();
                break;
            }

            case 6:
            {
                if (_t < 0.4) return;
                var loot = Loot.LootService.Instance!;
                var l6 = interiors.Current!;
                switch (_lootPhase)
                {
                    case 0:
                        Check(Loot.LootService.NearestContainer(_player!, l6, interiors.CurrentNode!) == _lootIndex,
                            $"facing the {l6.Furniture[_lootIndex].Type} finds it");
                        Check(_player!.TryInteract() && loot.IsOpen, "E in front of it opens the search");
                        _lootPhase = 1;
                        return;
                    case 1:
                    {
                        if (loot.Waiting && _t < 5) return;
                        Check(!loot.Waiting, "the contents arrived");
                        var contents = loot.OpenContents().ToList();
                        int expected = contents.Sum(c => c.Stack.Count);
                        GD.Print($"[interior] it holds: {(contents.Count == 0 ? "nothing" : string.Join(", ", contents.Select(c => $"{c.Stack.Id} x{c.Stack.Count}")))}");
                        if (_shot != null)
                            GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_loot.png"));
                        loot.TakeAll();
                        Check(!loot.OpenContents().Any(), "take all empties it");
                        Check(CountItems() - _lootBefore == expected, $"the pack gained {CountItems() - _lootBefore} of {expected}");
                        loot.Close();
                        Check(!loot.IsOpen, "the panel closes");
                        _lootPhase = 2;
                        return;
                    }
                    case 2:
                        Check(_player!.TryInteract() && loot.IsOpen, "searching again opens it");
                        _lootPhase = 3;
                        return;
                    default:
                        if (loot.Waiting && _t < 8) return;
                        Check(!loot.OpenContents().Any(), "a second search finds it empty");
                        loot.Close();
                        break;
                }
                _step = 7;
                _t = 0;
                break;
            }

            case 7:
                // back to the door we came in by, facing out
                StandInside(interiors, _door.Key.ToString());
                _step = 8;
                _t = 0;
                break;

            case 8:
            {
                if (_t < 0.5) return;
                if (WalkThrough(interiors, _door.Key.ToString(), inward: false, delta, "_door_inside") is not { } done) return;
                if (!done) { Finish(); return; }
                Next();
                break;
            }

            case 9:
            {
                if (_t < 1.0) return;
                if (ShutBehind(interiors) is not { } shut) return;
                if (!shut) { Finish(); return; }
                var d = _player!.GlobalPosition - _door.World;
                Check(new Vector2(d.X, d.Z).Length() < 3f && Mathf.Abs(d.Y) < 2f,
                    $"back by the same door ({new Vector2(d.X, d.Z).Length():F1} m, dy {d.Y:F1})");
                Check(_player.IsOnFloor(), "standing on the street");
                Check(interiors.SpaceOf(Me) == "", "the player's space is outside again");
                // walk away: nobody near it, the door shuts by itself
                var away = _door.World + _door.Outward * 25f;
                if (_chunks.TryGetHeight(away, out float g)) away.Y = g + 1f;
                _player.LeaveInterior(away, 0);
                Next();
                break;
            }

            case 10:
            {
                bool open = interiors.IsOpen(_door.Key.ToString());
                if (open && _t < 75) return;
                Check(!open, $"the door shut by itself with nobody near ({_t:F1} s)");
                if (_church == null) { GD.Print("[interior] (no church nearby: churches not tested live)"); Finish(); }
                else Next();
                break;
            }

            // ---- a church: every door into one interior, and up the tower ------------------------
            case 11:
            {
                // the tower's door if it has one, so the way in and the way out differ
                var doors = _church!.Members
                    .Select(m => DoorIndex.Find(new BuildingKey(_churchTile.E, _churchTile.N, m)))
                    .Where(d => d != null).Select(d => d!.Value).ToList();
                if (doors.Count == 0)
                {
                    if (_t > 30) { Check(false, $"church {_churchKey} has a drawn door"); Finish(); }
                    return;
                }
                _churchIn = doors[^1];
                _churchOut = doors[0];
                if (_shot != null && !_viewed)
                {
                    // a look at it from across the street first, for the screenshot
                    if (_t < 0.1)
                    {
                        GD.Print($"[interior] church {_churchKey}: in by {_churchIn.Key}, out by {_churchOut.Key} ({doors.Count} door(s))");
                        var from = ViewOf(_churchIn.World + Vector3.Up * 12f, _churchIn.Outward);
                        var look = (_churchIn.World - from) with { Y = 0 };
                        _player!.LeaveInterior(from, Mathf.Atan2(-look.X, -look.Z));
                        _player.Velocity = Vector3.Zero;
                    }
                    if (_t < 3.0) return;
                    GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_church_outside.png"));
                    _viewed = true;
                }
                StandOutside(_churchIn);
                Next();
                break;
            }

            case 12:
            {
                if (_t < 1.0) return;
                if (WalkThrough(interiors, _churchIn.Key.ToString(), inward: true, delta, "_church_door") is not { } done) return;
                if (!done) { Finish(); return; }
                Next();
                break;
            }

            case 13:
            {
                if (_t < 1.5) return;
                var l = interiors.Current!;
                Check(l.Type == BuildingType.Church && l.Key == _churchKey, $"one church interior, {l.Key} ({l.Type})");
                Check(interiors.SpaceOf(Me) == _churchKey, "the player's space is the church, not the door's building");
                Check(_player!.IsOnFloor() && _player.GlobalPosition.Y < InteriorManager.InteriorBaseY + 2f, "standing on the church floor");
                if (_shot != null) GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_church.png"));
                for (int f = 0; f < l.Floors.Count; f++) StairRay(interiors, f);
                if (l.Floors[0].Flight is { } flight)
                {
                    // walk up the first flight for real: a ramp a body cannot climb is no stair
                    float dir = Math.Sign(flight.ZTop - flight.ZBottom);
                    var start = new Vector3((flight.X0 + flight.X1) / 2, 0.1f, flight.ZBottom - dir * 0.7f);
                    var up = interiors.CurrentNode!.GlobalTransform.Basis * new Vector3(0, 0, dir);
                    _player.EnterInterior(l.Key, interiors.CurrentNode.GlobalTransform * start, Mathf.Atan2(-up.X, -up.Z));
                    _player.Velocity = Vector3.Zero;
                    Input.ActionPress(PlayerInput.MoveForward);
                }
                Next();
                break;
            }

            case 14:
            {
                var l = interiors.Current!;
                if (l.Floors[0].Flight != null)
                {
                    // walking uphill is slowed to under half pace: a flight takes several seconds
                    float y = interiors.CurrentNode!.ToLocal(_player!.GlobalPosition).Y;
                    if (y < l.StoreyHeight - 0.2f && _t < 10.0) return;
                    Input.ActionRelease(PlayerInput.MoveForward);
                    Check(y > l.StoreyHeight - 0.4f, $"walked up the first tower flight ({y:F2} m of {l.StoreyHeight:F1} in {_t:F1} s)");
                    if (_shot != null) GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_stair.png"));
                }
                if (l.Floors.Count > 1)
                {
                    // to the top of the tower
                    int top = l.Floors.Count - 1;
                    var room = l.Floors[top].Rooms[0];
                    var bell = l.Furniture.FirstOrDefault(p => p.Type == FurnitureType.Bell);
                    Check(bell != null, "a bell in the bell chamber");
                    var spot = bell != null ? new Vector3(bell.X, 0, bell.Z) : new Vector3((room.X0 + room.X1) / 2, 0, (room.Z0 + room.Z1) / 2);
                    _player!.GlobalPosition = interiors.CurrentNode!.GlobalTransform * (spot + Vector3.Up * (top * l.StoreyHeight + 0.3f));
                    _player.Velocity = Vector3.Zero;
                }
                Next();
                break;
            }

            case 15:
            {
                // the camera eases after a teleport this tall: give it time before the shot
                if (_t < 3.0) return;
                var l = interiors.Current!;
                var node = interiors.CurrentNode!;
                if (l.Floors.Count > 1)
                {
                    float y = node.ToLocal(_player!.GlobalPosition).Y;
                    float want = (l.Floors.Count - 1) * l.StoreyHeight;
                    Check(_player.IsOnFloor() && Math.Abs(y - want) < 0.5f, $"standing in the bell chamber ({y:F1} m, floor at {want:F1})");
                    if (_shot != null) GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_belfry.png"));
                }
                // down to the other door and out through it
                StandInside(interiors, _churchOut.Key.ToString());
                Next();
                break;
            }

            case 16:
            {
                if (_t < 0.8) return;
                if (WalkThrough(interiors, _churchOut.Key.ToString(), inward: false, delta, null) is not { } done) return;
                if (!done) { Finish(); return; }
                var off = _player!.GlobalPosition - _churchOut.World;
                Check(new Vector2(off.X, off.Z).Length() < 3f && Mathf.Abs(off.Y) < 2f,
                    $"out by the door used, {_churchOut.Key} ({new Vector2(off.X, off.Z).Length():F1} m)");
                StandOutside(_churchOut);
                Next();
                break;
            }

            case 17:
            {
                if (_t < 1.0) return;
                if (WalkThrough(interiors, _churchOut.Key.ToString(), inward: true, delta, null) is not { } done) return;
                Check(done && interiors.Current?.Key == _churchKey, "the nave door leads into the same church");
                Next();
                break;
            }

            // ---- several doors at once, and one seen through another ----------------------------
            case 18:
                // the nave door is still open behind us; open the tower door from inside too
                StandInside(interiors, _churchIn.Key.ToString());
                Next();
                break;

            case 19:
            {
                if (_t < 0.5) return;
                string tower = _churchIn.Key.ToString(), nave = _churchOut.Key.ToString();
                if (!interiors.IsOpen(tower) && !_pressed) { _pressed = true; Check(_player!.TryInteract(), "E opens the tower door from inside"); }
                if (!(interiors.Links.TryGetValue(tower, out var b) && b.Swing >= 1f
                      && interiors.Links.TryGetValue(nave, out var a) && a.Swing >= 1f))
                {
                    if (_t > 12) { Check(false, "both church doors open"); Finish(); }
                    return;
                }
                _demo = new Camera3D { Name = "DemoCamera", Fov = 75 };
                AddChild(_demo);
                if (SpotSeeing(interiors, a, b) is { } spot)
                {
                    _demo.GlobalTransform = spot;
                    _demo.MakeCurrent();
                }
                else GD.Print("[interior] (no spot inside the church sees both doors: not shown)");
                Next();
                break;
            }

            case 20:
            {
                if (_t < 1.0) return;
                if (_demo!.Current)
                {
                    var a = interiors.Links[_churchOut.Key.ToString()];
                    var b = interiors.Links[_churchIn.Key.ToString()];
                    Check(interiors.Portals!.IsShown(a) && interiors.Portals.IsShown(b), "from inside, both open church doors show the street at once");
                    Save("_both_doors");
                }
                FindStreetPairs();
                _queue.Clear();
                foreach (var d in new[] { _same.A, _same.B, _facing.A, _facing.B })
                    if (d is { } e && !_queue.Any(q => q.Key == e.Key)) _queue.Add(e);
                GD.Print($"[interior] doors to open for the street scenes: {string.Join(", ", _queue.Select(q => q.Key))}");
                _player!.Camera.Current = true;
                Next();
                break;
            }

            case 21:
            {
                // open each in turn, standing at it
                if (_queueAt >= _queue.Count) { Next(); break; }
                var d = _queue[_queueAt];
                if (_t < 0.05) { StandOutside(d); _pressed = false; return; }
                if (_t < 0.8) return;
                if (!interiors.IsOpen(d.Key.ToString()) && !_pressed) { _pressed = true; _player!.TryInteract(); }
                if (!(interiors.Links.TryGetValue(d.Key.ToString(), out var l) && l.Swing >= 1f))
                {
                    if (_t > 12) { Check(false, $"door {d.Key} opens"); _queueAt++; _t = 0; }
                    return;
                }
                _queueAt++;
                _t = 0;
                break;
            }

            case 22:
            {
                // two neighbours' doors, open side by side, from across the street
                if (_same.A is not { } s1 || _same.B is not { } s2)
                {
                    if (_t < 0.1) GD.Print("[interior] (no two doors side by side nearby: not shown)");
                    Next();
                    break;
                }
                var mid = (s1.World + s2.World) / 2;
                if (_t < 0.05)
                {
                    if (StreetSpot(s1, s2) is not { } eye)
                    {
                        GD.Print("[interior] (no clear view of the two doors side by side: not shown)");
                        _step = 23;
                        _t = 0;
                        return;
                    }
                    _player!.LeaveInterior(eye - Vector3.Up * 1.4f, 0);
                    _demo!.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(mid + Vector3.Up * 1.2f, Vector3.Up);
                    _demo.MakeCurrent();
                    return;
                }
                if (_t < 1.5) return;
                Check(interiors.Portals!.IsShown(interiors.Links[s1.Key.ToString()]) && interiors.Portals.IsShown(interiors.Links[s2.Key.ToString()]),
                    $"two neighbours' open doors, {s1.Key} and {s2.Key}, each show their own interior");
                Save("_two_houses");
                Next();
                break;
            }

            case 23:
            {
                // from inside one house, out through its door and into the house across the street
                if (_facing.A is not { } x || _facing.B is not { } y)
                {
                    GD.Print("[interior] (no two doors facing each other nearby: not shown)");
                    Finish();
                    return;
                }
                // back to that street: the links there were dropped while we stood elsewhere
                if (_t < 0.05) { StandOutside(x); return; }
                if (!interiors.Links.TryGetValue(x.Key.ToString(), out var lx) || !interiors.Links.TryGetValue(y.Key.ToString(), out var ly))
                {
                    if (_t > 12) { Check(false, $"the doors {x.Key} and {y.Key} are shown again"); Finish(); }
                    return;
                }
                if (!_aimed)
                {
                    _aimed = true;
                    _t = 0.1;
                    var eye = lx.Inside * new Vector3(0, 1.6f, -1.8f);
                    var target = lx.ToInside * (y.World + Vector3.Up * 1.1f);
                    _demo!.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(target, Vector3.Up);
                    _demo.MakeCurrent();
                    return;
                }
                if (_t < 1.5) return;
                Check(interiors.Portals!.IsShown(lx) && interiors.Portals.IsShownThrough(ly),
                    $"from inside {x.Key}, the house across the street ({y.Key}) is seen through both doors");
                Save("_across_the_street");
                Finish();
                break;
            }
        }
    }

    private void Save(string suffix)
    {
        if (_shot != null) GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", suffix + ".png"));
    }

    /// <summary>
    /// A camera spot on the church's ground floor with a clear view of both doorways inside a
    /// 70-degree cone, as far back as possible, looking between them.
    /// </summary>
    private Transform3D? SpotSeeing(InteriorManager interiors, DoorLink a, DoorLink b)
    {
        var l = interiors.Current!;
        var node = interiors.CurrentNode!;
        var space = node.GetWorld3D().DirectSpaceState;
        var ta = a.Inside * new Vector3(0, 1.2f, -0.3f);
        var tb = b.Inside * new Vector3(0, 1.2f, -0.3f);
        Vector3? best = null;
        float bestScore = 0;
        foreach (var r in l.Floors[0].Rooms)
            for (float x = r.X0 + 0.6f; x < r.X1 - 0.6f; x += 0.6f)
                for (float z = r.Z0 + 0.6f; z < r.Z1 - 0.6f; z += 0.6f)
                {
                    var p = node.GlobalTransform * new Vector3(x, 1.6f, z);
                    var da = ta - p;
                    var db = tb - p;
                    if (da.AngleTo(db) > Mathf.DegToRad(70)) continue;
                    if (Blocked(space, p, ta) || Blocked(space, p, tb)) continue;
                    float score = Math.Min(da.Length(), db.Length());
                    if (score > bestScore) { bestScore = score; best = p; }
                }
        if (best is not { } eye) return null;
        return Transform3D.Identity.Translated(eye).LookingAt((ta + tb) / 2, Vector3.Up);
    }

    /// <summary>A spot across the street with a clear view of two doors side by side, at eye height.</summary>
    private Vector3? StreetSpot(DoorIndex.Entry a, DoorIndex.Entry b)
    {
        var space = _player!.GetWorld3D().DirectSpaceState;
        var n = (a.Outward + b.Outward).Normalized();
        var along = new Vector3(-n.Z, 0, n.X);
        var mid = (a.World + b.World) / 2;
        var ta = a.World + a.Outward * 0.3f + Vector3.Up * 1.2f;
        var tb = b.World + b.Outward * 0.3f + Vector3.Up * 1.2f;
        for (float dist = 6f; dist <= 22f; dist += 1f)
            foreach (float side in new[] { 0f, 2f, -2f, 4f, -4f })
            {
                var at = mid + n * dist + along * side;
                if (!_chunks.TryGetHeight(at, out float g)) continue;
                var eye = new Vector3(at.X, g + 1.7f, at.Z);
                if ((ta - eye).AngleTo(tb - eye) > Mathf.DegToRad(65)) continue;
                if (!Blocked(space, eye, ta) && !Blocked(space, eye, tb)) return eye;
            }
        return null;
    }

    private bool Blocked(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, to);
        q.Exclude = new Godot.Collections.Array<Rid> { _player!.GetRid() };
        return space.IntersectRay(q).Count > 0;
    }

    /// <summary>
    /// Doors near the player for the street scenes: two side by side on neighbouring buildings
    /// (same way, 4-20 m apart), and two facing each other across a street (8-35 m, in the
    /// open between them).
    /// </summary>
    private void FindStreetPairs()
    {
        // around the church: the player may be standing in it, 3 km down
        var here = _churchOut.World;
        var doors = DoorIndex.All().Where(d => d.World.DistanceTo(here) < 250f && d.Width > 0.7f).ToList();
        var space = _player!.GetWorld3D().DirectSpaceState;
        float bestSame = float.MaxValue, bestFacing = float.MaxValue;
        foreach (var x in doors)
            foreach (var y in doors)
            {
                if (x.Key.Index >= y.Key.Index && x.Key.Tile == y.Key.Tile) continue;
                var d = y.World - x.World;
                d.Y = 0;
                float dist = d.Length();
                if (dist < 4f || dist > 35f) continue;
                var dir = d / dist;
                float ahead = Mathf.Abs(d.Dot(x.Outward));
                if (x.Outward.Dot(y.Outward) > Mathf.Cos(Mathf.DegToRad(15)) && ahead < 3f && dist < 20f && dist < bestSame)
                {
                    bestSame = dist;
                    _same = (x, y);
                }
                if (dist >= 8f && x.Outward.Dot(dir) > Mathf.Cos(Mathf.DegToRad(25)) && y.Outward.Dot(-dir) > Mathf.Cos(Mathf.DegToRad(35))
                    && dist < bestFacing
                    && !Blocked(space, x.World + x.Outward * 1f + Vector3.Up * 1.5f, y.World + y.Outward * 1f + Vector3.Up * 1.5f))
                {
                    bestFacing = dist;
                    _facing = (x, y);
                }
            }
        GD.Print($"[interior] side by side: {_same.A?.Key} / {_same.B?.Key}; facing: {_facing.A?.Key} / {_facing.B?.Key}");
    }

    /// <summary>Puts the player on the street 1.2 m out from a door, facing it.</summary>
    private void StandOutside(DoorIndex.Entry d)
    {
        // out of the interior the manager knows about, not just the body moved
        if (_player!.Indoors) InteriorManager.Instance?.Leave(_player);
        var face = -d.Outward;
        _player!.LeaveInterior(d.World + d.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-face.X, -face.Z));
        _player.Velocity = Vector3.Zero;
    }

    /// <summary>Puts the player inside, 1.3 m in from a door of the current interior, facing out through it.</summary>
    private void StandInside(InteriorManager interiors, string door)
    {
        var l = interiors.Current!;
        var node = interiors.CurrentNode!;
        var way = l.EntranceFor(door);
        var at = node.GlobalTransform * new Vector3(way.X + way.InX * 1.3f, 0.1f, way.Z + way.InZ * 1.3f);
        var face = node.GlobalTransform.Basis * new Vector3(-way.InX, 0, -way.InZ);
        _player!.EnterInterior(l.Key, at, Mathf.Atan2(-face.X, -face.Z));
        _player.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Out, back in; shut, walking into the door must not get you out; then E on the door from
    /// just inside and straight out while it swings shut:
    /// the leaf is not solid yet and the door no longer passable, and there must still be a way
    /// out, not a step into the void under the terrain (#78). Null while under way; ends outside.
    /// </summary>
    private bool? ShutBehind(InteriorManager interiors)
    {
        string door = _door.Key.ToString();
        switch (_shut)
        {
            case -1:
                // just out, facing the street: turned round in front of the door
                StandOutside(_door);
                _shut = -2;
                _shutT = 0;
                return null;
            case -2:
                if ((_shutT += GetPhysicsProcessDeltaTime()) < 1.0 || !_player!.IsOnFloor()) return null;
                _shut = 0;
                return null;
            case 0:
                if (WalkThrough(interiors, door, inward: true, GetPhysicsProcessDeltaTime(), null) is not { } inside) return null;
                if (!inside) return false;
                _shut = 10;
                _shutT = 0;
                return null;
            case 10:
            {
                // first: shut, the door keeps you in. A barn's pair hangs outside, so only its
                // shutter stands in the hole (DoorLeaf.CreateShutter)
                var l = interiors.Current!;
                var node = interiors.CurrentNode!;
                var way = l.EntranceFor(door);
                var at = node.GlobalTransform * new Vector3(way.X + way.InX * 1.5f, 0.1f, way.Z + way.InZ * 1.5f);
                var face = node.GlobalTransform.Basis * new Vector3(-way.InX, 0, -way.InZ);
                _player!.EnterInterior(l.Key, at, Mathf.Atan2(-face.X, -face.Z));
                _player.Velocity = Vector3.Zero;
                _shut = 11;
                _shutT = 0;
                return null;
            }
            case 11:
                if ((_shutT += GetPhysicsProcessDeltaTime()) < 0.5) return null;
                Check(interiors.IsOpen(door) && _player!.TryInteract(), $"E from inside shuts {door}, to walk into it");
                _shut = 12;
                _shutT = 0;
                return null;
            case 12:
            {
                _shutT += GetPhysicsProcessDeltaTime();
                bool shut = interiors.Links.TryGetValue(door, out var link) ? link.Swing <= 0f : !interiors.IsOpen(door);
                if (!shut && _shutT < 8) return null;
                if (DoorLeaf.SwingsOut(_door.Kind) && DoorLeaf.LeafWidth(_door.Kind, _door.Width) > 2f)
                {
                    var edge = _door.World + _door.Outward * DoorLeaf.LeafWidth(_door.Kind, _door.Width) + Vector3.Up;
                    Check(interiors.OutsideDoorInReach(edge) == null, $"shut, {door} is not in reach from where its leaves stood");
                }
                Input.ActionPress(PlayerInput.MoveForward);
                _shut = 13;
                _shutT = 0;
                return null;
            }
            case 13:
                if ((_shutT += GetPhysicsProcessDeltaTime()) < 2.5) return null;
                Input.ActionRelease(PlayerInput.MoveForward);
                Check(_player!.Indoors, $"shut, {door} keeps you in");
                if (!_player.Indoors) return false;
                Check(_player.TryInteract(), $"E from inside opens {door} again");
                _shut = 14;
                _shutT = 0;
                return null;
            case 14:
            {
                _shutT += GetPhysicsProcessDeltaTime();
                bool open = interiors.Links.TryGetValue(door, out var link) && link.Swing >= 1f;
                if (!open && _shutT < 8) return null;
                Check(open, $"{door} swung open from inside");
                _shut = 1;
                _shutT = 0;
                return null;
            }
            case 1:
            {
                // half a metre in, facing out: at the hole while the leaf is still swinging
                var l = interiors.Current!;
                var node = interiors.CurrentNode!;
                var way = l.EntranceFor(door);
                var at = node.GlobalTransform * new Vector3(way.X + way.InX * 0.5f, 0.1f, way.Z + way.InZ * 0.5f);
                var face = node.GlobalTransform.Basis * new Vector3(-way.InX, 0, -way.InZ);
                _player!.EnterInterior(l.Key, at, Mathf.Atan2(-face.X, -face.Z));
                _player.Velocity = Vector3.Zero;
                _shut = 2;
                _shutT = 0;
                return null;
            }
            case 2:
                if ((_shutT += GetPhysicsProcessDeltaTime()) < 0.5) return null;
                Check(interiors.IsOpen(door) && _player!.TryInteract(), $"E from inside shuts {door}");
                Input.ActionPress(PlayerInput.MoveForward);
                _shut = 3;
                _shutT = 0;
                return null;
            case 3:
            {
                _shutT += GetPhysicsProcessDeltaTime();
                bool swinging = interiors.Links.TryGetValue(door, out var link) && link.Swing > 0f;
                if (_player!.Indoors && _shutT < 3) return null;
                Input.ActionRelease(PlayerInput.MoveForward);
                Check(!_player.Indoors, $"walked out while {door} swung shut{(swinging ? "" : " (it had shut first)")}");
                Check(_player.GlobalPosition.Y > InteriorManager.InteriorBaseY + 1000f,
                    $"on the street, not in the void under it (y {_player.GlobalPosition.Y:F1})");
                _shut = 4;
                _shutT = 0;
                return null;
            }
            case 4:
                // opened again from the street, so the walk away below still sees it shut by itself
                if ((_shutT += GetPhysicsProcessDeltaTime()) < 1.0) return null;
                Check(!interiors.IsOpen(door) && _player!.TryInteract(), $"E outside opens {door} again");
                _shut = 5;
                _shutT = 0;
                return null;
            default:
                _shutT += GetPhysicsProcessDeltaTime();
                if (!interiors.IsOpen(door) && _shutT < 5) return null;
                Check(interiors.IsOpen(door), $"{door} open again");
                _shut = -1;
                return !_player!.Indoors;
        }
    }

    private int _shut = -1;
    private double _shutT;

    private int _walk, _frame;
    private Camera3D? _demo;
    private bool _pressed, _aimed;
    private readonly List<DoorIndex.Entry> _queue = new();
    private int _queueAt;
    private (DoorIndex.Entry? A, DoorIndex.Entry? B) _same, _facing;

    /// <summary>The origin moved (#185): the doors this check noted are somewhere else in world space.</summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        DoorIndex.Entry Move(DoorIndex.Entry e) => e with { World = shift.Point(e.World), Outward = shift.Direction(e.Outward) };
        DoorIndex.Entry? MoveOrNull(DoorIndex.Entry? e) => e is { } d ? Move(d) : null;
        _door = Move(_door);
        _churchIn = Move(_churchIn);
        _churchOut = Move(_churchOut);
        for (int i = 0; i < _queue.Count; i++) _queue[i] = Move(_queue[i]);
        _same = (MoveOrNull(_same.A), MoveOrNull(_same.B));
        _facing = (MoveOrNull(_facing.A), MoveOrNull(_facing.B));
    }
    private double _crossedAt = -1;
    private double _walkT;

    /// <summary>
    /// Walks the player, standing at a door and facing it, through it: E opens it if it is shut,
    /// the leaf must swing open, then forward until they are on the other side. Null while under
    /// way, then whether it worked. <paramref name="shot"/> names a screenshot of the open doorway.
    /// </summary>
    private bool? WalkThrough(InteriorManager interiors, string door, bool inward, double delta, string? shot)
    {
        _walkT += delta;
        switch (_walk)
        {
            case 0:
                if (!interiors.IsOpen(door)) Check(_player!.TryInteract(), $"E at the door {door} opens it");
                _walk = 1;
                _walkT = 0;
                return null;
            case 1:
                if (!(interiors.Links.TryGetValue(door, out var link) && link.Passable))
                {
                    if (_walkT < 12) return null;
                    Check(false, $"the door {door} swings open");
                    _walk = 0;
                    return false;
                }
                if (link.Swing < 1f || _walkT < 1.2) return null; // the portal picture settles
                Check(true, $"the door {door} swung open ({_walkT:F1} s)");
                if (inward && BuildingKey.TryParse(door, out var key) && DoorIndex.Find(key) is { } spot && DoorLeaf.SwingsOut(spot.Kind))
                {
                    // open, a barn door is worked from out by its leaves' free edges, not only at the sill
                    float leaf = DoorLeaf.LeafWidth(spot.Kind, spot.Width);
                    var edge = spot.World + spot.Outward * leaf + Vector3.Up;
                    Check(interiors.OutsideDoorInReach(edge) == door, $"open, {door} is in reach {leaf:F1} m out, by its leaves");
                }
                if (_shot != null && shot != null)
                {
                    Check(interiors.Portals?.IsShown(link) == true, "the doorway shows the other side");
                    GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", shot + ".png"));
                }
                Input.ActionPress(PlayerInput.MoveForward);
                _walk = 2;
                _walkT = 0;
                return null;
            case 2:
                // --film: every frame of the way through, to see the step over the sill
                if (_shot != null && shot != null && _frame < 400 && CmdArgs.Has("--film"))
                    GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", $"{shot}_f{_frame++:000}.png"));
                if (_player!.Indoors != inward && _walkT < 6) return null;
                // a step past the sill, then stop
                if (_crossedAt < 0) _crossedAt = _walkT;
                if (_walkT - _crossedAt < 0.4) return null;
                Input.ActionRelease(PlayerInput.MoveForward);
                _walk = 0;
                _crossedAt = -1;
                Check(_player.Indoors == inward, inward ? $"walked in through {door}" : $"walked out through {door}");
                return _player.Indoors == inward;
        }
        return null;
    }

    /// <summary>
    /// Somewhere to stand to see <paramref name="target"/>: out along <paramref name="outward"/>,
    /// swung either side until nothing stands in the way of an eye 3 m up.
    /// </summary>
    private Vector3 ViewOf(Vector3 target, Vector3 outward)
    {
        var space = _player!.GetWorld3D().DirectSpaceState;
        foreach (float dist in new[] { 40f, 55f, 30f })
            foreach (float deg in new[] { 0f, 25f, -25f, 45f, -45f, 60f, -60f })
            {
                var dir = outward.Rotated(Vector3.Up, Mathf.DegToRad(deg));
                var at = target with { Y = _churchIn.World.Y } + dir * dist;
                if (!_chunks.TryGetHeight(at, out float g)) continue;
                var eye = new Vector3(at.X, g + 3f, at.Z);
                var query = PhysicsRayQueryParameters3D.Create(eye, target);
                query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
                var hit = space.IntersectRay(query);
                if (hit.Count == 0 || hit["position"].AsVector3().DistanceTo(target) < 6f)
                    return new Vector3(at.X, g + 0.5f, at.Z);
            }
        return _churchIn.World + outward * 30f + Vector3.Up * 2f;
    }

    /// <summary>Straight down onto the middle of a floor's flight: the ramp must be there.</summary>
    private void StairRay(InteriorManager interiors, int floor = 0)
    {
        var l = interiors.Current;
        var node = interiors.CurrentNode;
        if (l == null || floor >= l.Floors.Count || l.Floors[floor].Flight is not { } f || node == null)
        {
            if (floor == 0) GD.Print("[interior] (single storey: no stairs to test)");
            return;
        }
        float y0 = floor * l.StoreyHeight;
        var local = new Vector3((f.X0 + f.X1) / 2, y0, (f.ZBottom + f.ZTop) / 2);
        var from = node.GlobalTransform * (local + Vector3.Up * (l.StoreyHeight - 0.5f));
        var to = node.GlobalTransform * (local + Vector3.Down * 0.5f);
        var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        float y = hit.Count > 0 ? node.ToLocal(hit["position"].AsVector3()).Y - y0 : -1;
        Check(y > l.StoreyHeight * 0.3f && y < l.StoreyHeight * 0.7f, $"floor {floor} stair ramp under mid-flight at {y:F2} m of {l.StoreyHeight:F2}");
    }

    private int _lootIndex = -1, _lootBefore, _lootPhase;

    private static int CountItems()
    {
        var items = Loot.LootService.Instance?.Items?.Inventory;
        if (items == null) return 0;
        int n = items.Cash;   // francs are counted as cash, not held in a slot
        for (int i = 0; i < Items.Inventory.Size; i++) if (!items[i].IsEmpty) n += items[i].Count;
        return n;
    }

    private void Next()
    {
        _step++;
        _t = 0;
    }

    private void Finish()
    {
        Input.ActionRelease(PlayerInput.MoveForward);
        GD.Print(_ok ? "[interior] RESULT: ok" : "[interior] RESULT: FAILED");
        SetPhysicsProcess(false);
        GetTree().Quit(_ok ? 0 : 1);
    }
}
