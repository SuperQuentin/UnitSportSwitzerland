using System.Diagnostics;
using Godot;
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
/// <b>Live</b>: a player walks up to a real house door, presses E, must end up standing on the
/// interior's ground floor; the stair ramp must be there to stand on; E at the front door must
/// put them back on the street by the door. Non-zero exit on any failure.
/// </para>
/// </summary>
public partial class InteriorProbe : Node
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

    public InteriorProbe(ChunkManager chunks, WorldOrigin origin, IChunkSource source, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _source = source;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--interiorcheck"))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }

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
                    for (int i = 0; i < tile.Buildings.Count; i++)
                    {
                        var fp = BuildingFootprint.Compute(tile, i, roads, grid);
                        if (fp == null) { list.Add((tile.Buildings[i], null, new List<string> { "no footprint" })); continue; }
                        var layout = InteriorGenerator.Generate(fp, tile.Buildings[i]);
                        var problems = InteriorValidator.Validate(layout);
                        if (fp.Door.Width <= 0) Interlocked.Increment(ref _doorless);
                        else if (!BuildingFootprint.DoorOnWall(tile.Buildings[i], fp.Door))
                            problems.Add($"door at {fp.Door.Position:F1} is not on a wall");
                        list.Add((tile.Buildings[i], layout, problems));
                    }
                    return list;
                });

                foreach (var (b, l, p) in result)
                {
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
                        if (w < 3 && interesting)
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
        if (_total > 240) { Check(false, $"timed out in step {_step}"); Finish(); return; }
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
                    _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
                    AddChild(_player);
                    _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
                    interiors.LocalPlayer = () => _player;
                    return;
                }
                if (!_player.IsOnFloor()) return;
                var door = DoorIndex.Nearest(_player.GlobalPosition, 400f);
                if (door == null) return;
                _door = door.Value;
                var stand = _door.World + _door.Outward * 0.8f + Vector3.Up * 0.3f;
                _player.GlobalPosition = stand;
                _player.Velocity = Vector3.Zero;
                GD.Print($"[interior] door of {_door.Key} at {_door.World:F1}");
                Next();
                break;
            }
            case 1:
                if (_t < 1.0 || !_player!.IsOnFloor()) { if (_t > 20) { Check(false, "stood at the door"); Finish(); } return; }
                Check(_player.TryInteract(), "E at the door is taken as entering");
                Next();
                break;

            case 2:
                if (!_player!.Indoors && _t < 15) return;
                Check(_player.Indoors, "player is inside");
                if (!_player.Indoors) { Finish(); return; }
                Next();
                break;

            case 3:
                if (_t < 1.5) return;
                Check(_player!.GlobalPosition.Y < InteriorManager.InteriorBaseY + 5f && _player.GlobalPosition.Y > InteriorManager.InteriorBaseY - 1f,
                    $"standing on the ground floor (y {_player.GlobalPosition.Y:F1})");
                Check(_player.IsOnFloor(), "on a floor, not falling");
                if (_shot != null)
                {
                    var image = GetViewport().GetTexture().GetImage();
                    if (image.SavePng(_shot) == Error.Ok) GD.Print($"[interior] wrote {_shot}");
                }
                StairRay(interiors);
                Input.ActionPress(PlayerInput.MoveForward);
                Next();
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
                _lootIndex = li.Furniture.FindIndex(f => f.Floor == 0 && Loot.LootTables.IsLootable(f.Type));
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
            {
                // back to the front door and out
                var l = interiors.Current!;
                var node = interiors.CurrentNode!;
                _player.GlobalPosition = node.GlobalTransform * new Vector3(l.EntryX, 0.1f, -l.Depth / 2 + 0.9f);
                _player.Velocity = Vector3.Zero;
                _step = 8;
                _t = 0;
                break;
            }

            case 8:
                if (_t < 0.5) return;
                Check(_player!.TryInteract(), "E at the front door is taken");
                Next();
                break;

            case 9:
                if (_t < 2.5) return;
                Check(!_player!.Indoors, "player is outside again");
                var d = _player.GlobalPosition - _door.World;
                Check(new Vector2(d.X, d.Z).Length() < 3f && Mathf.Abs(d.Y) < 2f,
                    $"back by the same door ({new Vector2(d.X, d.Z).Length():F1} m, dy {d.Y:F1})");
                Check(_player.IsOnFloor(), "standing on the street");
                Finish();
                break;
        }
    }

    /// <summary>Straight down onto the middle of the first flight: the ramp must be there.</summary>
    private void StairRay(InteriorManager interiors)
    {
        var l = interiors.Current;
        var node = interiors.CurrentNode;
        if (l?.Floors[0].Flight is not { } f || node == null) { GD.Print("[interior] (single storey: no stairs to test)"); return; }
        var local = new Vector3((f.X0 + f.X1) / 2, 0, (f.ZBottom + f.ZTop) / 2);
        var from = node.GlobalTransform * (local + Vector3.Up * (l.StoreyHeight - 0.5f));
        var to = node.GlobalTransform * (local + Vector3.Down * 0.5f);
        var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        float y = hit.Count > 0 ? node.ToLocal(hit["position"].AsVector3()).Y : -1;
        Check(y > l.StoreyHeight * 0.3f && y < l.StoreyHeight * 0.7f, $"stair ramp under mid-flight at {y:F2} m of {l.StoreyHeight:F2}");
    }

    private int _lootIndex = -1, _lootBefore, _lootPhase;

    private static int CountItems()
    {
        var items = Loot.LootService.Instance?.Items?.Inventory;
        if (items == null) return 0;
        int n = 0;
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
