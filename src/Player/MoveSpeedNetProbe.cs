using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--movespeednet A|B</c> with <c>--connect</c> (driven by <c>tools/movespeednetcheck.sh</c>, #1485): an admin's
/// <c>/movespeed</c> over the network.
/// <list type="bullet">
/// <item>A and B, plain players, are refused; their factor stays 1.</item>
/// <item>A logs in, sets x<see cref="Factor"/> and walks: its own body walks that much faster, and B sees A's copy
/// cover the ground at that speed (no snap back), then at normal speed again after <c>/movespeed normal</c>.</item>
/// <item>A adds a bot and sets its factor by name: the server's path (<c>ChatManager.SetMoveSpeed</c>) finds it.</item>
/// </list>
/// </summary>
public partial class MoveSpeedNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--movespeednet");

    /// <summary>The server's <c>--admin-password</c> in the script.</summary>
    private const string Password = "movespeedcheck";

    private const float Factor = 8f;

    public MoveSpeedNetProbe(ItemController items) : base(items, "movespeednet", "MS", "movespeednet_") { }
    public MoveSpeedNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }

        // a plain player is refused, online
        Chat!.Send("/movespeed 5");
        Expect(await Until(() => _heard.Any(l => l.Contains("'/movespeed' is an admin command")), 10), $"{_role} is refused as a plain player");
        Expect(FootPlayer.OwnSpeedFactor == 1f, $"{_role}'s factor stays 1 (x{F(FootPlayer.OwnSpeedFactor)})");

        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    // ---- A: the admin who walks ---------------------------------------------------------------

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

        Chat!.Send($"/login {Password}");
        Expect(await Until(() => Permissions.IsAdmin, 10), "/login: the server says admin");
        Chat.Send($"/movespeed {Factor.ToString(CultureInfo.InvariantCulture)}");
        Expect(await Until(() => FootPlayer.OwnSpeedFactor == Factor, 10), $"the server sent x{Factor} (x{F(FootPlayer.OwnSpeedFactor)})");
        Expect(me.SpeedFactor == Factor, $"A's body moves at it (x{F(me.SpeedFactor)})");

        foreach (var (factor, way) in new[] { (Factor, Vector3.Forward), (1f, Vector3.Back) })
        {
            if (factor == 1f)
            {
                Chat.Send("/movespeed normal");
                Expect(await Until(() => FootPlayer.OwnSpeedFactor == 1f, 10), "/movespeed normal: back to x1");
            }
            float want = me.WalkSpeed;
            wish = way;
            bool steady = await Until(() => Mathf.Abs(MathX.FlatLength(me.Velocity) - want) < want * 0.05f, 5);
            Expect(steady, $"A walks at {F(want)} m/s at x{F(factor)} (walks {F(MathX.FlatLength(me.Velocity))})");
            Say($"walking {F(want)}");
            Expect(await Heard("B", $"seen {F(want)}", 10), $"B saw A walk at {F(want)} m/s");
            wish = Vector3.Zero;
            await Seconds(0.5);
        }
        me.WalkControls = null;

        // a bot, by its name: the server's path
        Chat.Send("/bot add 1");
        if (!await Until(() => _heard.Any(l => l.StartsWith("Added 1 bot")), 10)) { Fail("no bot added"); return; }
        Chat.Send("/bot list");
        string? bot = null;
        if (!await Until(() => (bot = BotName()) != null, 10)) { Fail("no bot listed"); return; }
        Chat.Send($"/movespeed 3 {bot}");
        Expect(await Until(() => _heard.Any(l => l == $"{bot}'s movement speed: x3."), 10), $"the bot {bot} moves at x3");
        Chat.Send("/bot clear");
    }

    /// <summary>The bot named by a <c>/bot list</c> reply ("1 bot(s): Name (npc_...)").</summary>
    private string? BotName()
    {
        foreach (string line in _heard)
            if (line.Contains(" bot(s): ") && line.Split(": ", 2)[1].Split(" (")[0] is { Length: > 0 } name) return name;
        return null;
    }

    // ---- B: the watcher -------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        for (int walk = 0; walk < 2; walk++)
        {
            string? said = null;
            if (!await Until(() => (said = _heard.LastOrDefault(l => l.Contains("MS A walking "))) != null && !_seen.Contains(said), 40))
            { Fail("A never walked"); return; }
            _seen.Add(said!);
            float want = float.Parse(said!.Split("walking ")[1], CultureInfo.InvariantCulture);
            // A's copy here, over a second of this peer's clock: the ground it covers, as the snapshots bring it
            if (Copy() is not { } a) { Fail("no copy of A here"); return; }
            var from = a.Global;
            double t0 = GameClock.Now;
            await Seconds(1.0);
            var to = a.Global;
            float seen = (float)(from.HorizontalDistanceTo(to) / (GameClock.Now - t0));
            Expect(Mathf.Abs(seen - want) < want * 0.25f, $"A's copy covers {F(seen)} m/s here (A walks {F(want)})");
            Say($"seen {F(want)}");
        }
    }

    private readonly HashSet<string> _seen = new();

    /// <summary>A's copy here: the one player node that is not ours and not a bot.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority() && !p.Bot && !p.Npc) return p;
        return null;
    }
}
