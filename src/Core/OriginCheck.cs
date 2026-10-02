using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Core;

/// <summary>
/// <c>--origincheck</c>: the floating origin's rules (#185), headless and without a world. Builds a
/// small scene of every kind of node the shift must handle, shifts it, and checks that each one
/// still means the same LV95 place, that the frames compose, and that a lane graph's junction keys
/// do not depend on the origin; then, with physics running, that a shift gives nothing a velocity:
/// a kinematic body moved by a shift must not fling what stands on it (a parked car under a
/// player, a radio on its roof). And the wire (phase 2): a position written by one peer means the
/// same LV95 place to another whose origin is elsewhere, a race road sent from a server's race
/// frame lands where it was built and follows the receiver's shifts, and an interpolated remote
/// 3,600 km out moves as smoothly as one next to the origin. Prints each failure and a RESULT
/// line; exits non-zero on any.
/// </summary>
public static class OriginCheck
{
    public static bool Requested => CmdArgs.Has("--origincheck");

    private static int _failures;

    /// <summary>Runs every case, then quits with the result.</summary>
    public static async void Run(Node host)
    {
        Frames();
        Lanes();
        Wire();
        Tree(host);
        await Physics(host);
        GD.Print($"[origincheck] RESULT {(_failures == 0 ? "PASS" : $"FAIL ({_failures})")}");
        host.GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static void Frames()
    {
        var origin = new WorldOrigin(2_600_000, 1_200_000);
        var a = origin.Frame;
        var p = origin.ToWorld(2_601_234.5, 1_199_876.25, 812);
        origin.MoveTo(2_640_000, 1_150_000);
        var b = origin.Frame;
        origin.MoveTo(2_640_017, 1_150_003);

        var once = origin.Since(a);
        var twice = b.Since(a).Then(origin.Since(b));
        Expect(once.Point(p).IsEqualApprox(twice.Point(p)), "two shifts compose into one");
        Expect(origin.ToGlobal(once.Point(p)) == a.ToGlobal(p), "a shifted point is the same LV95 place");
        Expect(origin.Epoch == 2 && b.Epoch == 1, "every move bumps the epoch");
        Expect(origin.Since(origin.Frame).IsIdentity, "no move, no shift");

        // a tile corner stays an exact integer, whatever the frame
        var corner = origin.ToWorld(new TileId(2641, 1150).MinE, new TileId(2641, 1150).MaxN, 0);
        Expect(corner.X == 983 && corner.Z == -997, $"tile corner exact ({corner})");
    }

    private static void Wire()
    {
        // one place, written by a peer whose origin is here, read by one whose origin is 300 km off
        var sender = new WorldOrigin(2_600_000, 1_200_000);
        var receiver = new WorldOrigin(2_900_000, 1_100_000);
        var at = sender.ToWorld(2_601_234.567, 1_199_876.125, 812.5);
        var d = new Godot.Collections.Dictionary();
        sender.ToGlobal(at).Write(d);
        var read = GlobalPos.Read(d);
        Expect(read.DistanceTo(new GlobalPos(2_601_234.567, 1_199_876.125, 812.5)) < 0.001, $"a dictionary carries the place ({read})");
        Expect(receiver.ToWorld(read).DistanceTo(receiver.ToWorld(2_601_234.567, 1_199_876.125, 812.5)) < 0.001,
            "the receiver puts it where its own origin says");

        // a race road: built in a frame at the host, sent as offsets, mapped by a client, then shifted with it
        var race = OriginFrame.Anchor(2_731_000, 1_105_000);
        var pts = new[] { race.ToWorld(2_731_010.5, 1_105_020.25, 600), race.ToWorld(2_731_210.75, 1_105_420.5, 640) };
        var client = new WorldOrigin(2_729_000, 1_104_000);
        var map = client.Since(OriginFrame.Anchor(race.E, race.N));
        var route = RaceRoute.FromPoints(pts.Select(map.Point).ToList(), [6f, 6f], frame: client.Frame);
        Expect(client.ToGlobal(route.Centre[1]).DistanceTo(race.ToGlobal(pts[1])) < 0.01, "a sent road lands where it was built");
        client.MoveTo(2_733_000, 1_106_000);
        route.Follow(client.Frame);
        route.Follow(client.Frame);   // a second holder of the same route: no second move
        Expect(client.ToGlobal(route.Centre[0]).DistanceTo(race.ToGlobal(pts[0])) < 0.01, "a road follows the receiver's shift, once");
        Expect(client.ToGlobal(route.Line.Points[0]).HorizontalDistanceTo(race.ToGlobal(pts[0])) < 3, "its racing line follows too");

        // a remote 3,600 km out: float world positions there step by 25 cm
        Expect(Net.RemoteInterpolator.SelfCheck(), "an interpolated remote far from everything is smooth");
    }

    private static void Lanes()
    {
        RoadTile Tile() => new()
        {
            Id = new TileId(2600, 1200),
            Segments =
            [
                new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [10, 500, 990, 400, 500, 600] },
                new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [400, 500, 600, 900, 501, 100] },
            ],
        };
        bool Car(RoadSegment s) => true;
        var here = LaneGraph.Build([Tile()], new OriginFrame(2_600_500, 1_200_500, 0), Car);
        var there = LaneGraph.Build([Tile()], new OriginFrame(2_655_321, 1_111_111, 0), Car);
        Expect(here.Edges.Select(e => (e.KeyStart, e.KeyEnd)).SequenceEqual(there.Edges.Select(e => (e.KeyStart, e.KeyEnd))),
            "junction keys do not depend on the origin");
        Expect(here.Edges[0].KeyEnd == here.Edges[1].KeyStart, "the two roads meet at one junction");

        var shift = there.Frame.Since(here.Frame);
        here.Shift(shift, there.Frame, new());
        Expect(here.Edges[1].Points[1].IsEqualApprox(there.Edges[1].Points[1]), "a shifted graph lies where a fresh one does");
    }

    private static void Tree(Node host)
    {
        var origin = new WorldOrigin(2_600_000, 1_200_000);
        var shifter = new OriginShifter(origin, () => null, () => true) { StepM = 100 };
        host.AddChild(shifter);

        var root = new OriginCheckContainer { Name = "OriginCheckRoot" };
        host.AddChild(root);
        var manager = new OriginCheckContainer { Name = "Manager" };
        root.AddChild(manager);
        var tile = new Node3D { Name = "Tile", Position = origin.ToWorld(2_601_000, 1_200_000, 0) };
        manager.AddChild(tile);
        var mesh = new Node3D { Name = "Mesh", Position = new Vector3(12, 3, 40) };
        tile.AddChild(mesh);
        var nested = new Node3D { Name = "TopLevel", TopLevel = true };
        mesh.AddChild(nested);
        nested.GlobalPosition = origin.ToWorld(2_600_300, 1_199_700, 55);
        var plain = new Node { Name = "PlainManager" };
        root.AddChild(plain);
        var underPlain = new Node3D { Name = "UnderPlain", Position = origin.ToWorld(2_599_000, 1_201_000, 10) };
        plain.AddChild(underPlain);
        var grouped = new Node3D { Name = "Players" };
        grouped.AddToGroup(OriginShifter.ContainerGroup);
        root.AddChild(grouped);
        var player = new Node3D { Name = "Player", Position = origin.ToWorld(2_600_100, 1_200_100, 500) };
        grouped.AddChild(player);
        var preview = new SubViewport { Name = "Preview", OwnWorld3D = true };
        root.AddChild(preview);
        var previewModel = new Node3D { Name = "Model", Position = new Vector3(1, 2, 3) };
        preview.AddChild(previewModel);
        var ui = new CanvasLayer { Name = "Ui" };
        root.AddChild(ui);
        var awareInUi = new OriginCheckAware { Name = "AwareInUi" };
        ui.AddChild(awareInUi);
        var aware = new OriginCheckAware { Name = "Aware" };
        root.AddChild(aware);
        int raised = 0;
        origin.Shifted += _ => raised++;

        GlobalPos Where(Node3D n) => origin.ToGlobal(n.GlobalPosition);
        var before = new[] { tile, mesh, nested, underPlain, player }.ToDictionary(n => n, Where);

        Expect(shifter.ShiftTo(2_604_400, 1_196_600), "the shift happens");
        foreach (var (node, was) in before)
        {
            var now = Where(node);
            Expect(now.HorizontalDistanceTo(was) < 0.01 && Math.Abs(now.Alt - was.Alt) < 0.01,
                $"{node.Name} is still at {was} (now {now})");
        }
        Expect(root.Transform == Transform3D.Identity && manager.Transform == Transform3D.Identity
            && grouped.Transform == Transform3D.Identity, "containers stay at the identity");
        Expect(mesh.Position == new Vector3(12, 3, 40), "a child keeps its local position");
        Expect(previewModel.Position == new Vector3(1, 2, 3), "a world of its own is left alone");
        Expect(aware.Calls == 1 && awareInUi.Calls == 1, $"every handler called once ({aware.Calls}, {awareInUi.Calls})");
        Expect(raised == 1, "Shifted raised once");
        Expect(aware.Last.Point(Vector3.Zero).IsEqualApprox(new Vector3(-4400, 0, -3400)), $"the shift is old minus new ({aware.Last})");
        Expect(tile.Position == origin.ToWorld(2_601_000, 1_200_000, 0), "a tile stays exactly on its corner");
        Expect(!shifter.ShiftTo(2_604_400.2, 1_196_599.9), "a shift to where it already is does nothing");

        root.QueueFree();
        shifter.QueueFree();
    }

    /// <summary>
    /// Settled bodies, shifted 31 m east, must not pick the shift up as motion. The case that broke:
    /// Jolt does not teleport a kinematic body whose transform is set; it sweeps it there with
    /// MoveKinematic during the next step, so for a step its collision is still where it was. A
    /// parked vehicle (a CharacterBody3D) 31 m west of a player then stood, in the physics world,
    /// right under the shifted player, and swept off at 1,860 m/s carrying them. Here: a parked
    /// kinematic box, a character and a rigid box standing 31 m east of it on the ground, and a
    /// character and a rigid box standing on it.
    /// </summary>
    private static async Task Physics(Node host)
    {
        var tree = host.GetTree();
        var origin = new WorldOrigin(2_600_000, 1_200_000);
        var shifter = new OriginShifter(origin, () => null, () => true) { StepM = 1 };
        host.AddChild(shifter);
        var root = new OriginCheckContainer { Name = "OriginCheckPhysics" };
        host.AddChild(root);

        var ground = new StaticBody3D { Name = "Ground", Position = new Vector3(0, -0.5f, 0) };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 1, 200) } });
        root.AddChild(ground);
        var parked = new CharacterBody3D { Name = "Parked", Position = new Vector3(0, 0.75f, 0) };
        parked.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 1.5f, 4) } });
        root.AddChild(parked);

        OriginCheckRider Rider(string name, Vector3 at)
        {
            var r = new OriginCheckRider { Name = name, Position = at };
            r.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f } });
            root.AddChild(r);
            return r;
        }
        RigidBody3D Box(string name, Vector3 at)
        {
            var b = new RigidBody3D { Name = name, Position = at };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 0.5f, 0.5f) } });
            root.AddChild(b);
            return b;
        }
        var riders = new[] { Rider("OnParked", new Vector3(-1, 2.5f, 0)), Rider("Beside", new Vector3(31, 1, 0)) };
        var boxes = new[] { Box("OnParked", new Vector3(1, 1.8f, 1)), Box("Beside", new Vector3(31, 0.3f, 1)) };

        for (int i = 0; i < 120; i++) await host.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        var was = riders.Cast<Node3D>().Concat(boxes).ToDictionary(n => n, n => origin.ToGlobal(n.GlobalPosition));
        foreach (var r in riders) r.MaxFlatSpeed = 0;
        var boxMax = boxes.ToDictionary(b => b, _ => 0f);

        shifter.ShiftTo(2_600_031, 1_200_000);
        for (int i = 0; i < 30; i++)
        {
            await host.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            foreach (var b in boxes) boxMax[b] = Math.Max(boxMax[b], b.LinearVelocity.Length());
        }
        foreach (var r in riders)
            Expect(r.MaxFlatSpeed < 0.5f, $"character {r.Name} is not carried off by the shift ({r.MaxFlatSpeed:F1} m/s)");
        foreach (var b in boxes)
            Expect(boxMax[b] < 0.5f, $"rigid body {b.Name} is not flung by the shift ({boxMax[b]:F1} m/s)");
        foreach (var (n, at) in was)
            Expect(origin.ToGlobal(n.GlobalPosition).HorizontalDistanceTo(at) < 0.1,
                $"{n.GetType().Name} {n.Name} stays where it was ({origin.ToGlobal(n.GlobalPosition).HorizontalDistanceTo(at):F2} m off)");

        root.QueueFree();
        shifter.QueueFree();
    }

    private static void Expect(bool ok, string what)
    {
        if (ok) return;
        _failures++;
        GD.PrintErr($"[origincheck] FAIL: {what}");
    }
}

/// <summary>A manager for <see cref="OriginCheck"/>: stays put, its children move.</summary>
internal sealed partial class OriginCheckContainer : Node3D, IOriginContainer { }

/// <summary>A handler for <see cref="OriginCheck"/>: counts the shifts it is told about.</summary>
internal sealed partial class OriginCheckAware : Node, IOriginShiftAware
{
    public int Calls;
    public OriginShift Last;
    public void OnOriginShifted(OriginShift shift) { Calls++; Last = shift; }
}

/// <summary>A character for <see cref="OriginCheck"/>: stands still under gravity, notes how fast it moved.</summary>
internal sealed partial class OriginCheckRider : CharacterBody3D
{
    public float MaxFlatSpeed;

    public override void _PhysicsProcess(double delta)
    {
        Velocity = new Vector3(0, IsOnFloor() ? 0 : Velocity.Y - 9.8f * (float)delta, 0);
        MoveAndSlide();
        var real = GetRealVelocity();
        MaxFlatSpeed = Math.Max(MaxFlatSpeed, new Vector2(real.X, real.Z).Length());
    }
}
