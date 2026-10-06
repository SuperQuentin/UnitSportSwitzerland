using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Net;

/// <summary>
/// <c>--sleepers leave|watch|wake</c> with <c>--connect</c> (driven by <c>tools/sleepercheck.sh</c>, #644).
/// <list type="bullet">
/// <item><c>leave</c>: steps 30 m from the spawn, lets that replicate, then disconnects cleanly.</item>
/// <item><c>watch</c> (another identity): must see one sleeper, then see it go when its owner returns.</item>
/// <item><c>wake</c> (the same <c>user://</c>, so the same key as <c>leave</c>): the server must wake it
/// where it fell asleep, and the body must end up there, not at the spawn.</item>
/// <item><c>look</c> (windowed, by hand): stands by the sleeper and saves <c>test_output/sleepers_*.png</c>.</item>
/// </list>
/// </summary>
public partial class SleeperProbe : ChatProbe
{
    public static string? Role => RoleArg("--sleepers");

    public SleeperProbe(ItemController items) : base(items, "sleepers", "SL", "sleepers_") { }
    public SleeperProbe() : this(null!) { }

    private static Sleepers? Book => Sleepers.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "WATCH";
        if (!await Joined(150)) return;
        switch (_role)
        {
            case "LEAVE": await Leave(); return;
            case "WATCH": await Watch(); break;
            case "LOOK": await Look(); break;
            default: await Wake(); break;
        }
        await Finish(1.0);
    }

    private async Task Leave()
    {
        Me!.GlobalPosition += new Vector3(30, 0, 0);
        await Seconds(3);
        GD.Print($"{Log} leaving at {Me.Global.E:F1}/{Me.Global.N:F1}");
        GD.Print($"{Log} RESULT: ok");
        // a clean ENet disconnect, so the server knows at once rather than at the peer's time-out
        Multiplayer.MultiplayerPeer?.Close();
        await Seconds(1);
        GetTree().Quit(0);
    }

    private async Task Watch()
    {
        Expect(await Until(() => Book is { Count: 1 }, 90), $"one sleeper shown ({Book?.Count})");
        Expect(await Until(() => Book is { Count: 0 }, 400), $"the sleeper woke and went ({Book?.Count})");
    }

    private async Task Look()
    {
        Expect(await Until(() => Book?.AnyShown != null, 90), "a sleeper to look at");
        if (Book?.AnyShown is not { } at) return;
        // three views: close from its side, its head end, and from further off with the tag
        (Vector3 off, float pitch, string name)[] views =
            { (new(2.2f, 0, 1.2f), -0.55f, "side"), (new(0, 0, -2.6f), -0.45f, "head"), (new(6f, 0, 5f), -0.2f, "far") };
        foreach (var (off, pitch, name) in views)
        {
            Me!.GlobalPosition = at + off + Vector3.Up * 0.2f;
            var dir = at - Me.GlobalPosition;
            float yaw = Mathf.Atan2(-dir.X, -dir.Z);
            Me.Rotation = new Vector3(0, yaw, 0);
            Me.LookYaw = yaw;
            Me.LookPitch = pitch;
            await Seconds(2.5);
            GD.Print($"{Log} shot {Shot(name)}");
        }
    }

    private async Task Wake()
    {
        Expect(await Until(() => Book?.LastWake != null, 30), "the server woke this player");
        if (Book?.LastWake is not { } at) return;
        // at once: the Remove comes before WakeAt on the same reliable channel, and the watcher may
        // fall asleep itself once it has seen this one go
        Expect(Book.Count == 0, "its own sleeper is gone");
        // the teleport settles on the ground once the tile is there
        bool there = await Until(() => Me is { } me && Distance(me, at) < 3, 20);
        Expect(there, $"woke where it fell asleep ({(Me is { } m ? Distance(m, at) : -1):F1} m away)");
    }

    private static double Distance(Player.FootPlayer me, (double E, double N) at)
    {
        var g = me.Global;
        return System.Math.Sqrt((g.E - at.E) * (g.E - at.E) + (g.N - at.N) * (g.N - at.N));
    }
}
