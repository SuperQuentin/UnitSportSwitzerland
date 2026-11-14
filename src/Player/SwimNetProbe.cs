using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Fixture;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--swimnet A|B</c> with <c>--connect</c> on <c>--chunks fixture:lake</c> (driven by
/// <c>tools/swimnetcheck.sh</c>, #301): what another peer sees of a swimmer.
/// <list type="bullet">
/// <item>A swims: a crawl across B's view, treading water, a dive to 5-6 m and back up.</item>
/// <item>B floats 7 m away, looking at A, and checks A's copy at each step: the swim pose and the
/// right style (crawl, tread, under water), and the depth: the copy's feet as far under the surface
/// as A says its own are.</item>
/// <item>A is thrown limp into the lake as from a crash (#380): B sees A's copy go limp, its own
/// copy of the ragdoll float with its hips as deep as A's, and then A swimming.</item>
/// </list>
/// Windowed (<c>SHOTS=1</c>), B saves its view of A to <c>test_output/swimnet_B_*.png</c>. Scratch inventory.
/// </summary>
public partial class SwimNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--swimnet");

    public SwimNetProbe(ItemController items) : base(items, "swimnet", "SW", "swimnet_") { }
    public SwimNetProbe() : this(null!) { }

    /// <summary>A failure counts and carries on; the RESULT line comes at the end.</summary>
    protected override void Fail(string why) => Expect(false, why);

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    private static Vector3 At(double x, double y)
    {
        var (e, n) = Start;
        WaterField.TryWorld(e + x, n + y, out var w);
        return w;
    }

    private static Vector3 East => (At(100, 0) - At(0, 0)).Normalized();
    private static Vector3 North => (At(0, 100) - At(0, 0)).Normalized();
    private static float YawOf(Vector3 d) => Mathf.Atan2(-d.X, -d.Z);

    private const double AX = Lake.ShoreX + 165, BX = Lake.ShoreX + 158;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => WaterField.TryGetStill(At(AX, 0), out _, out _)))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

    // ---- A: the swimmer --------------------------------------------------------------------

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

        Expect(me.StartSwimmingAtSurface(At(AX, -6)), "A is in the water");
        await Seconds(1.5);
        // across B's view
        wish = North;
        await Seconds(2.5);
        Expect(me.IsSwimming && (int)me.Anim.Z == (int)SwimStyle.Crawl, $"A crawls (style {me.Anim.Z})");
        Say($"crawling {F(me.SwimDepth)}");
        await Heard("B", "seen crawling", 8);

        wish = Vector3.Zero;
        await Seconds(2.5);
        Expect((int)me.Anim.Z == (int)SwimStyle.Tread, $"A treads water (style {me.Anim.Z})");
        Say($"treading {F(me.SwimDepth)}");
        await Heard("B", "seen treading", 8);

        // down: crouch until 5 m under the surface, then hold there (crouch on and off)
        Input.ActionPress(PlayerInput.CrouchSlide);
        await Until(() => me.SwimDepth > 6.0f, 10);
        Expect(me.HeadUnderwater, $"A is under ({F(me.SwimDepth)} m)");
        for (int i = 0; i < 40; i++)
        {
            if (me.SwimDepth > 6.6f) Input.ActionRelease(PlayerInput.CrouchSlide);
            else if (me.SwimDepth < 6.2f) Input.ActionPress(PlayerInput.CrouchSlide);
            if (i % 8 == 0) Say($"under {F(me.SwimDepth)}");
            if (_heard.Any(l => l.Contains("SW B seen under"))) break;
            await Seconds(0.25);
        }
        Input.ActionRelease(PlayerInput.CrouchSlide);
        await Heard("B", "seen under", 2);

        Input.ActionPress(PlayerInput.Jump);
        await Until(() => !me.HeadUnderwater, 10);
        Input.ActionRelease(PlayerInput.Jump);
        await Seconds(2.0);
        Say($"surfaced {F(me.SwimDepth)}");
        await Heard("B", "seen surfaced", 8);
        me.WalkControls = null;
        await Ragdoll(me);
    }

    /// <summary>A: thrown from 4 m up into B's view, limp; it says how deep its hips float once back up from the plunge.</summary>
    private async Task Ragdoll(FootPlayer me)
    {
        WaterField.TryLevelAt(At(AX, -3), out float level);
        me.DebugThrow(At(AX, -3) with { Y = level + 4f }, North * 3f + Vector3.Up * 3f);
        Expect(me.Ragdolled, "A is thrown limp");
        Say("thrown 0");
        bool wet = false, up = false;
        double start = GameClock.Now;
        while (me.Ragdolled && GameClock.Now - start < 14)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (me.RagdollPelvis is not { } hip || !WaterField.TryLevelAt(hip, out level)) continue;
            float sub = level - hip.Y;
            if (sub > 0.8f) wet = true;
            if (wet && !up && sub < 0.5f)
            {
                up = true;
                await Seconds(0.4);
                if (me.RagdollPelvis is { } h && WaterField.TryLevelAt(h, out level)) Say($"afloat {F(level - h.Y)}");
            }
        }
        Expect(up, "A's ragdoll came back up from the plunge");
        Expect(await Until(() => me.IsSwimming, 4), "and A swims");
        await Seconds(1.0);
        Say($"rescued {F(me.SwimDepth)}");
        await Heard("B", "seen rescued", 8);
    }

    // ---- B: the watcher --------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Expect(me.StartSwimmingAtSurface(At(BX, 0)), "B is in the water, 7 m from where A swims");
        me.LookYaw = YawOf(East);
        me.LookPitch = -0.06f;
        await Seconds(1.5);
        Say("ready");

        await Watch("crawling", SwimStyle.Crawl, under: false, "crawl");
        await Watch("treading", SwimStyle.Tread, under: false, "tread");
        // held at depth by crouching on and off: the underwater stroke, or sculling upright between
        await Watch("under", null, under: true, null);
        await Watch("surfaced", null, under: false, "surfaced");
        await WatchRagdoll();
        await Watch("rescued", null, under: false, "rescued");
    }

    /// <summary>B: A's copy limp, its ragdoll floating as deep as A's own, then (the next Watch) swimming.</summary>
    private async Task WatchRagdoll()
    {
        if (!await Until(() => _heard.Any(l => l.Contains("SW A thrown ")), 40)) { Fail("A was never thrown"); return; }
        Expect(await Until(() => Copy() is { Ragdolled: true }, 3), "A's copy goes limp (PoseRagdoll)");
        if (!await Until(() => _heard.Any(l => l.Contains("SW A afloat ")), 12)) { Fail("A never said 'afloat'"); return; }
        string line = _heard.Last(l => l.Contains("SW A afloat "));
        float said = Float(line[(line.LastIndexOf(' ') + 1)..]);
        float depth = float.NaN;
        bool ok = await Until(() =>
        {
            if (Copy() is not { Ragdolled: true } a || a.RagdollPelvis is not { } hip || !WaterField.TryLevelAt(hip, out float level)) return false;
            depth = level - hip.Y;
            return Mathf.Abs(depth - said) < 0.45f;
        }, 2);
        Expect(ok, $"A's copy floats limp: its hips {F(depth)} m under this peer's surface (A says {F(said)})");
        if (DisplayServer.GetName() != "headless" && Copy() is { } c)
        {
            var to = c.GlobalPosition - (Me!.GlobalPosition + Vector3.Up * 1.68f);
            Me.LookYaw = YawOf(to with { Y = 0 });
            Me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GD.Print($"{Log} shot {Shot("B_ragdoll")}");
        }
        Say("seen afloat");
    }

    /// <summary>A's copy here, the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority() && p.Ride == RideKind.OnFoot) return p;
        return null;
    }

    private async Task Watch(string what, SwimStyle? style, bool under, string? shot)
    {
        if (!await Until(() => _heard.Any(l => l.Contains($"SW A {what} ")), 40)) { Fail($"A never said '{what}'"); return; }
        string line = _heard.Last(l => l.Contains($"SW A {what} "));
        float said = Float(line[(line.LastIndexOf(' ') + 1)..]);
        float depth = float.NaN;
        int seenStyle = -1;
        bool ok = await Until(() =>
        {
            var a = Copy();
            if (a == null || !WaterField.TryLevelAt(a.GlobalPosition, out float level)) return false;
            depth = level - a.GlobalPosition.Y;
            seenStyle = (int)a.Anim.Z;
            bool styleOk = style == null || seenStyle == (int)style;
            bool depthOk = Mathf.Abs(depth - said) < (under ? 1.0f : 0.35f) && (!under || depth > 3f);
            return a.IsSwimming && a.PoseKind == FootPlayer.PoseSwim && styleOk && depthOk;
        }, 6);
        Expect(ok, $"A's copy {what}: swim pose, style {seenStyle} (want {(style?.ToString() ?? "any")}), feet {F(depth)} m under (A says {F(said)})");
        if (shot != null && DisplayServer.GetName() != "headless")
        {
            var a = Copy();
            if (a != null)
            {
                var to = a.GlobalPosition + Vector3.Up * 1.3f - (Me!.GlobalPosition + Vector3.Up * 1.68f);
                Me.LookYaw = YawOf(to with { Y = 0 });
                Me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            }
            await Seconds(0.4);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GD.Print($"{Log} shot {Shot("B_" + shot)}");
        }
        Say($"seen {what}");
    }
}
