using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Fixture;

namespace UnitSport.Player;

/// <summary>
/// <c>--climbnet A|B</c> with <c>--connect</c> on <c>--chunks fixture:crag</c> (driven by
/// <c>tools/climbnetcheck.sh</c>, #1225): what another peer sees of a climber, and the server's rules.
/// <list type="bullet">
/// <item>Both peers got the server's <c>--stamina off --lift off</c> through <c>Arrival.Rules</c>: no meter
/// counts and no air rises, while free climbing stays on (it is the Adventure layer's).</item>
/// <item>A climbs the rock wall; B, 9 m off, sees A's copy in the wall-climbing pose, where A says it
/// is (0.3 m) and leant into the face.</item>
/// <item>A tops out onto the ledge by the mantle; B sees A's copy stand there.</item>
/// </list>
/// Windowed (<c>SHOTS=1</c>), B saves its view of A to <c>test_output/climbnet_B_*.png</c>. Scratch inventory.
/// </summary>
public partial class ClimbNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--climbnet");

    public ClimbNetProbe(ItemController items) : base(items, "climbnet", "CL", "climbnet_") { }
    public ClimbNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    /// <summary>A course point (m from the start, X east, Y north) in world space, at its ground plus <paramref name="above"/>.</summary>
    private Vector3 At(double x, double y, double above = 0)
    {
        var (e, n) = Start;
        return Me!.Origin!.ToWorld(e + x, n + y, Crag.Ground(x, y) + above);
    }

    /// <summary>A world point as course metres (x east, y north, h over the flat).</summary>
    private (float X, float Y, float H) Course(Vector3 world)
    {
        var g = Me!.Origin!.ToGlobal(world);
        var (e, n) = Start;
        return ((float)(g.E - e), (float)(g.N - n), (float)(g.Alt - Crag.Flat));
    }

    private Vector3 East => (At(100, 0) - At(0, 0)) with { Y = 0 } is var d ? d.Normalized() : Vector3.Right;
    private static float YawOf(Vector3 d) => Mathf.Atan2(-d.X, -d.Z);
    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => Me is { Origin: not null, Terrain: { } t } me && t.HasCollisionAt(At(Crag.WallX + 5, 0))))
        {
            await Finish(0);
            return;
        }
        // the server's rules, as Arrival.Rules brought them
        await Until(() => !Permissions.ServerStamina, 10);
        Expect(!Permissions.ServerStamina && !Permissions.ServerLift, $"the server's --stamina off and --lift off arrived (stamina {Permissions.ServerStamina}, lift {Permissions.ServerLift})");
        Expect(!Me!.StaminaApplies && !Permissions.LiftOn, "so no meter counts and no air rises here");
        Expect(Me.ClimbingAllowed, $"free climbing stays on (the Adventure layer's, adventure {Me.AdventureOn})");
        if (_role == "A") await RunA(Me); else await RunB(Me);
        await Finish(2.0);
    }

    // ---- A: the climber --------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        Vector3 wish = Vector3.Zero;
        me.WalkControls = () => (wish, false);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        me.GlobalPosition = At(Crag.WallX - 1.5, 0, 0.3);
        me.Velocity = Vector3.Zero;
        me.LookYaw = YawOf(East);
        await Until(() => me.IsOnFloor(), 5);
        wish = East;
        Expect(await Until(() => me.IsClimbing, 3), $"A grabs the wall ({me.ClimbRefused})");
        await Until(() => Course(me.GlobalPosition).H > 5f, 10);
        wish = Vector3.Zero;   // holding on, still: B compares where it is
        await Seconds(1.0);
        var c = Course(me.GlobalPosition);
        Say($"climbing {F(c.X)} {F(c.Y)} {F(c.H)}");
        await Heard("B", "seen climbing", 10);

        wish = East;
        bool ledge = await Until(() => !me.IsClimbing && me.IsOnFloor() && Course(me.GlobalPosition).H > Crag.LedgeH - 1f, 20);
        wish = Vector3.Zero;
        Expect(ledge, $"A tops out onto the ledge ({me.ClimbRefused}, {F(Course(me.GlobalPosition).H)} m)");
        await Seconds(1.0);
        c = Course(me.GlobalPosition);
        Say($"standing {F(c.X)} {F(c.Y)} {F(c.H)}");
        await Heard("B", "seen standing", 10);
        me.WalkControls = null;
    }

    // ---- B: the watcher --------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        me.GlobalPosition = At(Crag.WallX - 10, 2, 0.3);
        me.LookYaw = YawOf(East);
        me.LookPitch = 0.25f;
        await Seconds(1.5);
        Say("ready");

        if (await Said("climbing") is not { } climbing) return;
        float lean = 0f;
        Expect(await Until(() => Copy() is { IsClimbing: true, PoseKind: FootPlayer.PoseWallClimb }, 3), "A's copy is in the wall-climbing pose");
        Expect(await Until(() => Near(climbing, 0.3f), 3), $"on the face where A is ({Where()} vs A's {climbing.X:F2} {climbing.Y:F2} {climbing.H:F2})");
        if (Copy() is { } a) lean = Mathf.Acos(Mathf.Clamp(a.BodyPose.Basis.Y.Dot(Vector3.Up), -1f, 1f));
        Expect(lean > 0.08f, $"and leant into the 80° face ({Mathf.RadToDeg(lean):F0}°)");
        await Picture("climbing");
        Say("seen climbing");

        if (await Said("standing") is not { } standing) return;
        Expect(await Until(() => Copy() is { IsClimbing: false } && Near(standing, 0.3f), 3), $"A's copy stands on the ledge ({Where()})");
        Expect(Copy() is { PoseKind: FootPlayer.PoseStride }, $"in the walk's pose ({Copy()?.PoseKind})");
        await Picture("ledge");
        Say("seen standing");
    }

    private async Task<(float X, float Y, float H)?> Said(string what)
    {
        string? line = await HeardStarting("A", what, 40);
        if (line == null) { Fail($"A never said '{what}'"); return null; }
        var p = line[(line.IndexOf(what, StringComparison.Ordinal) + what.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Float(p[0]), Float(p[1]), Float(p[2]));
    }

    private bool Near((float X, float Y, float H) said, float within) =>
        Copy() is { } a && Course(a.GlobalPosition) is var c
        && new Vector3(c.X - said.X, c.Y - said.Y, c.H - said.H).Length() <= within;

    private string Where() => Copy() is { } a && Course(a.GlobalPosition) is var c ? $"{c.X:F2} {c.Y:F2} {c.H:F2}" : "no copy";

    private async Task Picture(string name)
    {
        if (DisplayServer.GetName() == "headless" || Copy() is not { } a) return;
        var to = a.GlobalPosition + Vector3.Up * 1.1f - (Me!.GlobalPosition + Vector3.Up * 1.68f);
        Me.LookYaw = YawOf(to with { Y = 0 });
        Me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
        await Seconds(0.5);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GD.Print($"{Log} shot {Shot("B_" + name)}");
    }

    /// <summary>A's copy here, the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }
}
