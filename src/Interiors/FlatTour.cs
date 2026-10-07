using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --flattour[,out.png] [--block "a deep block"]</c> (#557): the inside of a
/// synthetic apartment block (<see cref="FlatCheck"/>'s), planned, furnished and built by the real
/// code with no map and no server, and photographed where a visitor would look: the lobby from the
/// front door, a landing with the elevator, the cabin's panel, a flat's hall and living room, the
/// car park and the storage cellar. Without a shot path it cycles the views.
/// </summary>
public partial class FlatTour : Node3D
{
    private readonly string? _shot;
    private readonly string _block;
    private InteriorLayout _l = null!;
    private Camera3D _eye = null!;
    private readonly List<(string Name, Vector3 Eye, Vector3 At)> _views = new();
    private int _view = -1;
    private double _t;

    public FlatTour(string? shot, string? block)
    {
        _shot = shot;
        _block = block ?? "a deep block";
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--flattour");

    public static string? BlockArg()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--block");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.85f),
            },
        });
        if (_block == "a garage block")
        {
            // an 80 x 18 m block whose key rolled an underground garage (#558): its ramp is the tour
            var (garageTile, roads) = FlatCheck.RampTile();
            int garage = BuildingFootprint.ComputeDoors(garageTile, roads, null).First(d => d.Link.Any).Index;
            _l = InteriorGenerator.Generate(garageTile, garage, roads, null)!;
        }
        else if (_block.StartsWith("real:", StringComparison.Ordinal))
        {
            // a block of a real tile (#694): --block real:E_N_index --chunks <terrain_chunks>, planned with its streets as the game does
            var p = _block[5..].Split('_');
            var id = new Terrain.Format.TileId(int.Parse(p[0]), int.Parse(p[1]));
            var src = new Terrain.LocalChunkSource(CmdArgs.Value("--chunks") ?? "terrain_chunks");
            var realTile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult()!;
            var realRoads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            GarageRule.AlwaysRolls = true;
            _l = InteriorGenerator.Generate(realTile, int.Parse(p[2]), realRoads, null)!;
            GarageRule.AlwaysRolls = false;
        }
        else
        {
            var tile = FlatCheck.Tile();
            int index = Math.Max(0, FlatCheck.IndexOf(_block));
            _l = InteriorGenerator.Generate(tile, index, null, null)!;
        }
        var material = Styles.StyleKit.Material(Styles.MaterialRole.Interior);
        AddChild(InteriorNode.Create(_l, InteriorMeshBuilder.Build(_l), material, Transform3D.Identity));
        _eye = new Camera3D { Name = "Eye", Fov = 75f, Near = 0.05f, Far = 400f };
        AddChild(_eye);
        Plan();
        GD.Print($"[flattour] {_block}: {_l.Floors.Count} floors, {_views.Count} views");
    }

    /// <summary>Where to stand and look, from the plan.</summary>
    private void Plan()
    {
        const float eyeH = 1.6f;
        int ground = _l.Below, upper = Math.Min(_l.Floors.Count - 1, _l.Below + 2);
        float hd = _l.Depth / 2;
        Vector3 At(int floor, float x, float z, float up = eyeH) => new(x, _l.FloorY(floor) + up, z);

        var main = _l.AllEntrances()[0];
        // the garage ramp (#558): from the doorway, from the top of the descent, half way down, from the
        // car park looking back up, and the aisle at its foot
        if (_l.Floors[Math.Max(0, ground - 1)].AllFlights().FirstOrDefault(f => f.Ramp) is { } ramp)
        {
            float cx = (ramp.X0 + ramp.X1) / 2, h = _l.StoreyHeight, len = RampProfile.Length(h);
            float dir = ramp.RunDir;
            // along the ramp's own run, whichever way it goes (square to the front wall or along the facade)
            Vector3 OnRamp(float t, float up)
            {
                var (px, pz) = ramp.Point(cx, ramp.ZTop + dir * t);
                return new(px, _l.FloorY(ground) - RampProfile.Drop(t, h) + up, pz);
            }
            Vector3 Past(float up, float across, float beyond)
            {
                var (px, pz) = ramp.Point(across, ramp.ZBottom + dir * beyond);
                return new(px, _l.FloorY(ground - 1) + up, pz);
            }
            var way = _l.AllEntrances().First(e => e.Vehicle);
            _views.Add(("ramp_door", At(ground, way.X, way.Z + 0.4f, 1.4f), OnRamp(Math.Min(9f, len - 1f), 0.2f)));
            _views.Add(("ramp_top", OnRamp(0.5f, 1.6f), OnRamp(8f, 0.2f)));
            _views.Add(("ramp_mid", OnRamp(5f, 1.5f), OnRamp(len, 0.5f)));
            _views.Add(("ramp_foot", Past(1.6f, cx, 3.5f), OnRamp(3f, 0.3f)));
            _views.Add(("ramp_aisle", Past(1.7f, ramp.X0 - 2f, 0.5f), Past(0.6f, cx + 8f, 2.5f)));
        }
        _views.Add(("lobby", At(ground, main.X + 0.3f, main.Z + 0.5f), At(ground, main.X + 1.2f, main.Z + 5f, 1.2f)));
        // the stairwell (#571): up the stair from the front landing, from the half landing, and
        // down the well from the top floor
        if (_l.Floors[upper].Rooms.FirstOrDefault(r => r.Type == RoomType.Stairwell) is { } stair
            && _l.Floors[upper].Landings.FirstOrDefault() is { } half)
        {
            float laneA = stair.X0 + 0.55f, laneB = stair.X1 - 0.55f, h = _l.StoreyHeight;
            _views.Add(("stair_up", At(upper, laneB - 0.2f, stair.Z0 - 1.6f, 1.6f), At(upper, laneA, stair.Z1, 1.9f)));
            _views.Add(("stair_halflanding", At(upper, (half.X0 + half.X1) / 2, (half.Z0 + half.Z1) / 2, h / 2 + 1.6f),
                At(upper, laneA, stair.Z0, 0.2f)));
            int topFloor = _l.Floors.Count - 1;
            _views.Add(("stair_well", At(topFloor, laneA + 0.2f, stair.Z0 - 0.5f, 1.7f), At(topFloor, laneA + 0.6f, stair.Z1 - 1.5f, -2.5f * h)));
        }
        if (_l.Lifts.FirstOrDefault() is { } lift)
        {
            var (ox, oz) = lift.Outward;
            float cx = (lift.X0 + lift.X1) / 2, cz = (lift.Z0 + lift.Z1) / 2;
            var door = lift.WallPoint(0, 0, 0);
            _views.Add(("landing", At(upper, door.X + ox * 2.4f - 0.6f, door.Z + oz * 2.4f), At(upper, door.X, door.Z, 1.3f)));
            var panel = lift.PanelPoint(_l.FloorY(upper) + LiftPlan.ButtonHeight);
            _views.Add(("cabin", At(upper, cx - 0.2f, cz + 0.3f), panel));
        }
        var flat = _l.InnerDoors.FirstOrDefault(d => d.Floor == upper);
        if (flat != null)
        {
            var hall = _l.Floors[upper].Rooms[flat.Room];
            var (fx, fz) = flat.Side switch { Side.Front => (0f, 1f), Side.Back => (0f, -1f), Side.Left => (1f, 0f), _ => (-1f, 0f) };
            float dx = flat.Side is Side.Front or Side.Back ? flat.Center : flat.Side == Side.Left ? hall.X0 : hall.X1;
            float dz = flat.Side is Side.Front or Side.Back ? (flat.Side == Side.Front ? hall.Z0 : hall.Z1) : flat.Center;
            _views.Add(("hall", At(upper, dx + fx * 0.4f, dz + fz * 0.4f), At(upper, dx + fx * 5f, dz + fz * 5f, 1.3f)));
            var living = _l.Floors[upper].Rooms.FirstOrDefault(r => r.Unit == flat.Unit && r.Type == RoomType.Living);
            if (living != null) _views.Add(FromDoor("living", upper, living));
            // from the sofa, to its TV (#680)
            if (living != null
                && _l.Furniture.FirstOrDefault(p => p.Floor == upper && p.Type == FurnitureType.Sofa && p.X > living.X0 && p.X < living.X1 && p.Z > living.Z0 && p.Z < living.Z1) is { } sofa
                && _l.Furniture.FirstOrDefault(p => p.Floor == upper && p.Type == FurnitureType.Tv && p.X > living.X0 && p.X < living.X1 && p.Z > living.Z0 && p.Z < living.Z1) is { } tv)
                _views.Add(("sofa_tv", At(upper, sofa.X, sofa.Z, 1.1f), At(upper, tv.X, tv.Z, 0.9f)));
            var bed = _l.Floors[upper].Rooms.FirstOrDefault(r => r.Unit == flat.Unit && r.Type == RoomType.Bedroom);
            if (bed != null) _views.Add(FromDoor("bedroom", upper, bed));
            var bath = _l.Floors[upper].Rooms.FirstOrDefault(r => r.Unit == flat.Unit && r.Type == RoomType.Bathroom);
            if (bath != null) _views.Add(FromDoor("bathroom", upper, bath));
            var kitchen = _l.Floors[upper].Rooms.FirstOrDefault(r => r.Unit == flat.Unit && r.Type == RoomType.Kitchen);
            if (kitchen != null) _views.Add(FromDoor("kitchen", upper, kitchen));
        }
        if (_l.Below > 0)
        {
            var park = _l.Floors[0].Rooms.FirstOrDefault(r => r.Type == RoomType.CarPark);
            if (park != null) _views.Add(FromDoor("carpark", 0, park, 2.0f));
            foreach (var t in new[] { RoomType.Cellar, RoomType.Laundry, RoomType.Shelter, RoomType.TechRoom })
                if (_l.Floors[0].Rooms.FirstOrDefault(r => r.Type == t) is { } room)
                    _views.Add(FromDoor(t.ToString().ToLowerInvariant(), 0, room));
        }
        if (_l.GroundFloor.Rooms.FirstOrDefault(r => r.Type == RoomType.Shop) is { } shop)
            _views.Add(FromDoor("shop", ground, shop));
        _ = hd;
    }

    /// <summary>Standing in a room's first doorway, looking at its far corner.</summary>
    private (string, Vector3, Vector3) FromDoor(string name, int floor, RoomPlan r, float up = 1.65f)
    {
        float y = _l.FloorY(floor);
        var o = r.Openings.FirstOrDefault(o => o.Kind is OpeningKind.Door or OpeningKind.Entry) ?? r.Openings.FirstOrDefault();
        if (o == null)
            return (name, new Vector3(r.X0 + 0.5f, y + up, r.Z0 + 0.5f), new Vector3(r.X1, y + 0.6f, r.Z1));
        var (x, z, ix, iz) = o.Side switch
        {
            Side.Front => (o.Center, r.Z0, 0f, 1f),
            Side.Back => (o.Center, r.Z1, 0f, -1f),
            Side.Left => (r.X0, o.Center, 1f, 0f),
            _ => (r.X1, o.Center, -1f, 0f),
        };
        // the far corner from the doorway, across the room
        float fx = ix != 0 ? (ix > 0 ? r.X1 : r.X0) : (x - r.X0 > r.X1 - x ? r.X0 : r.X1);
        float fz = iz != 0 ? (iz > 0 ? r.Z1 : r.Z0) : (z - r.Z0 > r.Z1 - z ? r.Z0 : r.Z1);
        return (name, new Vector3(x + ix * 0.25f, y + up, z + iz * 0.25f), new Vector3(fx, y + 0.5f, fz));
    }

    public override void _Process(double delta)
    {
        _t += delta;
        int view = (int)(_t / 1.5);
        if (view >= _views.Count)
        {
            if (_shot != null) { GD.Print("[flattour] RESULT: ok"); GetTree().Quit(0); return; }
            _t = 0;
            view = 0;
        }
        if (view != _view)
        {
            _view = view;
            var (_, eye, at) = _views[view];
            _eye.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(at, Vector3.Up);
        }
        // a second to settle, then the picture
        if (_shot != null && _t - view * 1.5 > 1.0 && !_saved.Contains(view))
        {
            _saved.Add(view);
            var path = _shot.Replace(".png", $"_{_views[view].Name}.png");
            if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) GD.Print($"[flattour] wrote {path}");
        }
    }

    private readonly HashSet<int> _saved = new();
}
