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
/// </list>
/// </summary>
public partial class SleeperProbe : ChatProbe
{
    public static string? Role => RoleArg("--sleepers");

    public SleeperProbe(ItemController items) : base(items, "sleepers", "SL") { }
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
        Expect(await Until(() => Book is { Count: 0 }, 120), $"the sleeper woke and went ({Book?.Count})");
    }

    private async Task Wake()
    {
        Expect(await Until(() => Book?.LastWake != null, 30), "the server woke this player");
        if (Book?.LastWake is not { } at) return;
        // the teleport settles on the ground once the tile is there
        bool there = await Until(() => Me is { } me && Distance(me, at) < 3, 20);
        Expect(there, $"woke where it fell asleep ({(Me is { } m ? Distance(m, at) : -1):F1} m away)");
        Expect(Book!.Count == 0, "its own sleeper is gone");
    }

    private static double Distance(Player.FootPlayer me, (double E, double N) at)
    {
        var g = me.Global;
        return System.Math.Sqrt((g.E - at.E) * (g.E - at.E) + (g.N - at.N) * (g.N - at.N));
    }
}
