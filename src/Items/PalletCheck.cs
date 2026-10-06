using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--palletcheck [shots] --world flat --systems physics,ui</c> (#583 phase 2, tier 1): a pallet
/// forked, carried and set down, in a real warehouse plan — the hand-made one <c>--sitecheck</c>
/// stands up (turned 31°, so no frame can hide behind an axis), built under the flat world with
/// its own collision, and an offline <see cref="PalletService"/> playing the server's part.
///
/// <list type="bullet">
/// <item>the hall's loose floor pallets are nodes of their own and out of the merged mesh;</item>
/// <item>driven at square, the forklift stops against one with its tines under it, and raising
/// them (the real <c>shift_up</c> binding, held) lifts it: the forks carry its load byte, the hall's
/// node is hidden and passed through, and it is no loot container any more;</item>
/// <item>it rides on the carriage, backed away and lifted high;</item>
/// <item>lowered, it is set down where it rode — a loose pallet with the same load, solid, the
/// hall's node still hidden — and driven back into, it is picked straight back up;</item>
/// <item>the parked flags keep any load byte.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/pallets/*.png</c>.
/// </summary>
public partial class PalletCheck : Node
{
    public static bool Requested => CmdArgs.Has("--palletcheck");
    private static bool Shots => CmdArgs.Value("--palletcheck") == "shots";

    private int _failures;
    private int _shot;
    private bool _started;
    private InteriorLayout _layout = null!;
    private InteriorNode _hall = null!;
    private PalletService _pallets = null!;
    private FootPlayer? _player;

    // the pallet picked, its id and plan, and the way the forklift comes at it (hall frame)
    private int _target = -1;
    private Vector3 _toward;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[pallet] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>Holds a paddle (through the real bindings) until <paramref name="done"/> or <paramref name="seconds"/> of game time.</summary>
    private async Task Paddle(string action, double seconds, Func<bool>? done = null)
    {
        XrPad.Press(action, true);
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end && done?.Invoke() != true) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        XrPad.Press(action, false);
        await Frames(2);
    }

    /// <summary>Drives on <paramref name="throttle"/> (negative: the brake, which reverses from a stop) until <paramref name="done"/> or it stalls.</summary>
    private async Task Drive(FootPlayer me, float throttle, double seconds, Func<bool> done)
    {
        me.RideControls = () => new RideInput(Mathf.Max(throttle, 0f), Mathf.Max(-throttle, 0f), 0f, false);
        double end = GameClock.Now + seconds, moving = GameClock.Now + 1.0;
        while (GameClock.Now < end && !done())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            // up against something and not moving: stop trying
            if (GameClock.Now > moving && me.Velocity.Length() < 0.02f)
            {
                GD.Print($"[pallet] stalled at {me.GlobalPosition}: "
                    + string.Join(", ", Enumerable.Range(0, me.GetSlideCollisionCount()).Select(i => me.GetSlideCollision(i))
                        .Select(c => $"{(c.GetCollider() as Node)?.GetPath()} n{c.GetNormal()}")));
                break;
            }
        }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        for (int i = 0; i < 240 && me.GroundSpeed > 0.05f; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Shot(string name, Vector3 eye, Vector3 at)
    {
        if (!Shots || DisplayServer.GetName() == "headless") return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 60f, Near = 0.05f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/pallets");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[pallet] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    public override void _Ready()
    {
        var (tile, index, key) = SiteProbe.FindSite(BuildingType.Warehouse);
        if (tile == null || key == null || InteriorGenerator.Generate(tile, index, null, null) is not { } layout)
        {
            Expect(false, "the hand-made warehouse is planned");
            Finish();
            return;
        }
        _layout = layout;
        // the hall's own frame: its tile's corner at the world's origin, so nothing is kilometres out
        var origin = new WorldOrigin(tile.Id.MinE, tile.Id.MaxN);
        _pallets = PalletService.Create(this, origin, server: false);
        _pallets.Layouts = k => Task.FromResult(k == layout.Key ? layout : null);
        var data = InteriorMeshBuilder.Build(layout);
        _hall = InteriorNode.Create(layout, data, Styles.StyleKit.Material(Styles.MaterialRole.Interior),
            InteriorManager.PlacementFor(layout, origin));
        AddChild(_hall);
        // light for the shots: an interior is lit by its own lamps, but the forklift by the sun
        AddChild(new OmniLight3D { Position = _hall.GlobalPosition + Vector3.Up * 7f, OmniRange = 60f, LightEnergy = 1.2f });

        int loose = layout.Furniture.Count(InteriorMeshBuilder.IsLoosePallet);
        int drawn = PalletNode.All.Values.Count(p => p.GetParent() == _hall);
        Expect(loose > 0 && drawn == loose, $"the warehouse's {loose} loose floor pallet(s) are nodes of their own ({drawn})");

        _target = PickTarget(out _toward);
        Expect(_target >= 0, $"a pallet with a clear run in to it (#{_target})");
        if (_target < 0) { Finish(); return; }

        // out of the merged mesh: drawn there once lifted off the floor, it adds exactly one pallet
        var f = layout.Furniture[_target];
        int without = data.Vertices.Length;
        f.Lift = 0.001f;
        int with = InteriorMeshBuilder.Build(layout).Vertices.Length;
        f.Lift = 0f;
        Expect(with - without == InteriorMeshBuilder.PalletPiece(InteriorMeshBuilder.PalletLoad(f)).Vertices.Length,
            $"a floor pallet is not in the hall's merged mesh ({with - without} vertices when it is)");
    }

    /// <summary>
    /// A loose pallet on the ground floor that a forklift can drive square at along its runners:
    /// 4 m of floor in front of one of its open ends, inside the hall and clear of everything else.
    /// </summary>
    private int PickTarget(out Vector3 toward)
    {
        toward = default;
        var l = _layout;
        var hall = l.GroundFloor.Rooms[0];
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (!InteriorMeshBuilder.IsLoosePallet(f) || f.Floor != l.Below) continue;
            var runners = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * Vector3.Right;
            foreach (float sign in new[] { 1f, -1f })
            {
                var dir = runners * sign;   // the way the forklift drives
                var side = new Vector3(-dir.Z, 0, dir.X);
                bool clear = true;
                for (float back = 0.75f; back <= 4.5f && clear; back += 0.25f)
                    foreach (float across in new[] { -0.8f, 0f, 0.8f })
                    {
                        var p = new Vector3(f.X, 0, f.Z) - dir * back + side * across;
                        if (p.X < hall.X0 + 0.3f || p.X > hall.X1 - 0.3f || p.Z < hall.Z0 + 0.3f || p.Z > hall.Z1 - 0.3f) { clear = false; break; }
                        for (int j = 0; j < l.Furniture.Count && clear; j++)
                        {
                            var o = l.Furniture[j];
                            if (j == i || o.Floor != f.Floor || o.Type is FurnitureType.FloorMarking or FurnitureType.Gantry
                                or FurnitureType.SafetySign or FurnitureType.Banner) continue;
                            float hw = (o.Turns % 2 == 0 ? o.W : o.D) / 2 + 0.1f, hd = (o.Turns % 2 == 0 ? o.D : o.W) / 2 + 0.1f;
                            if (Mathf.Abs(p.X - o.X) < hw && Mathf.Abs(p.Z - o.Z) < hd) clear = false;
                        }
                    }
                if (!clear) continue;
                toward = dir;
                return i;
            }
        }
        return -1;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_started || _target < 0) return;
        _started = true;
        var f = _layout.Furniture[_target];
        // 3.4 m back from the pallet's centre, facing it along its runners
        var start = _hall.GlobalTransform * (new Vector3(f.X, _layout.FloorY(f.Floor), f.Z) - _toward * 3.4f);
        _player = new FootPlayer { Name = "Probe" };
        AddChild(_player);
        // on the flat world's ground above the hall: a mount is got on outdoors, never in a building
        _player.GlobalPosition = start with { Y = TestWorld.GroundY + 1f };
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = Run(start);
    }

    private async Task Run(Vector3 start)
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect(me.IsOnFloor() && me.SetRide(RideKind.Forklift) && me.Vehicle is Forklift, "mounted a forklift");
        if (me.Vehicle is not Forklift fork) { Finish(); return; }
        // then into the hall on it, as a loading bay's portal puts a driver: the rescue from under
        // the ground stands down 3 km down, and the machine faces the pallet along its runners
        var worldToward = _hall.GlobalTransform.Basis * _toward;
        float yaw = Mathf.Atan2(-worldToward.X, -worldToward.Z);
        me.EnterInterior(_layout.Key, start + Vector3.Up * 0.05f, yaw);
        me.PlaceAt(start + Vector3.Up * 0.05f, yaw);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Frames(20);

        var f = _layout.Furniture[_target];
        string id = Pallets.HallId(_layout.Key, _target);
        byte load = InteriorMeshBuilder.PalletLoad(f);
        var node = PalletNode.All.GetValueOrDefault(id);
        Expect(node != null && node.Visible && !node.Taken, $"{id} stands in the hall ({Pallets.Goods(load)}, {Pallets.Depth(load):F1} m deck)");
        Expect(!Loot.LootService.Moved(_layout.Key, _target), "where the plan put it, it is a loot container");
        if (node == null) { Finish(); return; }
        var palletAt = node.GlobalPosition;

        // ---- driven in at square: the hull stops at the pallet with the tines under it --------
        await Drive(me, 0.3f, 8, () => Home(me, node));
        Expect(Forks(me, fork, node), $"driven in, the tines are under it ({Describe(me, node)})");
        await Shot("forks-in", me.GlobalPosition + Side(me) * 3.5f + Vector3.Up * 2f, palletAt + Vector3.Up * 0.5f);

        // ---- raised: it is lifted -------------------------------------------------------------
        await Paddle(PlayerInput.ShiftUp, 3.0, () => fork.Carrying != 0);
        await Frames(10);
        Expect(fork.Carrying == Pallets.Carried(load), $"raising the forks lifts it: they carry its load byte ({fork.Carrying - 1} = {load})");
        Expect(node.Taken && !node.Visible && _pallets.IsTaken(id), "the hall's node is hidden and the service has it taken");
        Expect(Loot.LootService.Moved(_layout.Key, _target), "moved, it is no loot container any more");
        var mast = ForkliftMeshBuilder.MastOf(me.Visual);
        Expect(mast?.Load != null, "the pallet is drawn on the carriage");
        var floor = palletAt.Y;
        await Paddle(PlayerInput.ShiftUp, 1.6);
        if (mast?.Load?.GetNodeOrNull<Node3D>("Mesh") is { } carried)
            Expect(Mathf.Abs(carried.GlobalPosition.Y - floor - (fork.Lift - Pallets.Seat)) < 0.03f,
                $"it rides up with the forks, {Pallets.Seat:F2} m under their top face ({carried.GlobalPosition.Y - floor:F2} m off the floor at {fork.Lift:F2} m)");

        // ---- carried away: backed off three metres --------------------------------------------
        var before = me.GlobalPosition;
        await Drive(me, -0.4f, 8, () => (me.GlobalPosition - before).Length() > 3f);
        Expect((me.GlobalPosition - before).Length() > 2.5f && fork.Carrying == Pallets.Carried(load),
            $"backed away {(me.GlobalPosition - before).Length():F1} m with it on the forks");
        if (mast?.Load is { } away)
            Expect(new Vector2(away.GlobalPosition.X - palletAt.X, away.GlobalPosition.Z - palletAt.Z).Length() > 2f,
                "and it went with them");
        await Shot("carried", me.GlobalPosition + Side(me) * 4f + Vector3.Up * 2.4f, me.GlobalPosition + Vector3.Up * 1.2f);

        // ---- lowered: set down where it rode ---------------------------------------------------
        await Paddle(PlayerInput.ShiftDown, 6.0, () => fork.Carrying == 0);
        await Frames(10);
        var set = _pallets.Loose.Values.FirstOrDefault();
        Expect(fork.Carrying == 0 && _pallets.Loose.Count == 1 && set?.Load == load,
            $"lowered, it is set down: empty forks, one loose pallet with its load ({_pallets.Loose.Count})");
        var down = set == null ? null : PalletNode.All.GetValueOrDefault(Pallets.LooseId(set.Id));
        var expected = me.GlobalTransform * CarMeshBuilder.Turned(ForkliftLayout.LoadCentre);
        Expect(down != null && new Vector2(down.GlobalPosition.X - expected.X, down.GlobalPosition.Z - expected.Z).Length() < 0.1f
               && Mathf.Abs(down.GlobalPosition.Y - me.GlobalPosition.Y) < 0.05f,
            $"where it rode, on the floor ({down?.GlobalPosition.DistanceTo(expected with { Y = me.GlobalPosition.Y }):F2} m off)");
        Expect(node.Taken && !node.Visible, "the hall's node stays hidden: the pallet is the loose one now");
        await Shot("set-down", me.GlobalPosition + Side(me) * 3.5f + Vector3.Up * 2.2f, expected);

        // ---- backed off and driven back in: picked straight back up --------------------------
        before = me.GlobalPosition;
        await Drive(me, -0.4f, 6, () => (me.GlobalPosition - before).Length() > 1.0f);
        Expect(fork.Carrying == 0, "backing off leaves it where it is");
        if (down != null)
        {
            await Drive(me, 0.3f, 8, () => Home(me, down));
            await Paddle(PlayerInput.ShiftUp, 3.0, () => fork.Carrying != 0);
            await Frames(10);
            Expect(fork.Carrying == Pallets.Carried(load) && _pallets.Loose.Count == 0
                   && (!IsInstanceValid(down) || down.IsQueuedForDeletion()),
                $"driven back in and raised, the loose one is lifted ({_pallets.Loose.Count} loose left)");
        }

        // ---- a parked forklift keeps any load ------------------------------------------------
        bool all = true;
        var parked = new Forklift();
        foreach (int l in new[] { 0, 1, 127, 128, 255 })
        {
            var lifting = new Forklift { Lift = 2.5f, Carrying = Pallets.Carried((byte)l) };
            parked.UnpackFlags(lifting.PackFlags());
            all &= parked.Carrying == lifting.Carrying && Mathf.Abs(parked.Lift - 2.5f) < 0.011f;
        }
        Expect(all, "the parked flags keep the fork height and every load byte");

        Finish();
    }

    /// <summary>The fork rule for one pallet, as <see cref="PalletService.Tend"/> asks it — at the height it lifts from.</summary>
    private static bool Forks(FootPlayer me, Forklift fork, PalletNode node)
    {
        var local = me.GlobalTransform.AffineInverse() * node.GlobalPosition;
        var ahead = new Vector2(-me.GlobalTransform.Basis.Z.X, -me.GlobalTransform.Basis.Z.Z).Normalized();
        var runners = node.GlobalTransform.Basis.X;
        float along = Mathf.Abs(ahead.Dot(new Vector2(runners.X, runners.Z).Normalized()));
        return Pallets.Forked(-local.X, -local.Z, along, Mathf.Max(fork.Lift, Pallets.Seat), 0f);
    }

    /// <summary>Driven all the way in: the pallet's near end at the hull's face, the tines under its whole length.</summary>
    private static bool Home(FootPlayer me, Node3D node) =>
        -(me.GlobalTransform.AffineInverse() * node.GlobalPosition).Z < ForkliftLayout.MastZ + 0.1f + Pallets.Length * 0.5f + 0.1f;

    private static string Describe(FootPlayer me, Node3D node)
    {
        var local = me.GlobalTransform.AffineInverse() * node.GlobalPosition;
        return $"centre at x {-local.X:F2}, z {-local.Z:F2} in the forklift's frame";
    }

    private static Vector3 Side(FootPlayer me) => me.GlobalTransform.Basis.X.Normalized();

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[pallet] RESULT: ok — forked, lifted, carried off, set down and picked back up"
            : $"[pallet] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
