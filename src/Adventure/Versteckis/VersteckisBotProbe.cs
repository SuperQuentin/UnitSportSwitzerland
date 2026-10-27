using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// <c>--vsbot</c> with <c>--connect</c> (driven by <c>tools/versteckisbotcheck.sh</c>, #1268): one human and five bots play
/// three rounds on the hamlet. The human opens and starts, seeks round 1 without tagging, and hides in a far corner in
/// the others. Checked from what reached this client: a bot seeker catches a bot hider, a bot hider completes an objective,
/// every bot hid inside the circle (the round-end pins), and the human's reward arrives.
/// </summary>
public partial class VersteckisBotProbe : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--vsbot");
    private const string Password = "vspw";

    public VersteckisBotProbe(ItemController items) : base(items, "vsbot", "", "vsbot_") { }
    public VersteckisBotProbe() : this(null!) { }

    private static VersteckisManager Vs => VersteckisManager.Instance!;

    private readonly List<(long Hider, long By)> _caught = new();
    private readonly HashSet<(int Round, VsSite Site, long By)> _objectives = new();
    private readonly List<string> _outside = new();
    private int _pinsChecked;

    public override async void _Ready()
    {
        if (!await Joined(150, () => VersteckisManager.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        if (!await SpeedUp(Password)) return;
        Chat!.Send("/vs open nearest 3 bots 6 normal");
        Expect(await Until(() => Vs.Mine is { Phase: VsPhase.Lobby, Kinds.Count: > 0 }, 60), "the game is open and its village read");
        Chat.Send("/vs start");
        Expect(await Until(() => Vs.Mine is { Phase: VsPhase.Gather }, 15), "the game gathers");
        var s0 = Vs.Mine!;
        Expect(s0.Entrants.Count(e => e.Bot) == 5, $"five bots fill it ({s0.Entrants.Count(e => e.Bot)})");
        long me = Vs.Me;
        bool hidden = false;
        int round = 0;
        double balance = Bank.Instance?.Balance ?? 0;
        // watch the game to its end, frame by frame
        while (Vs.Mine is { } s)
        {
            Record(s);
            if (s.Round != round) { round = s.Round; hidden = false; }
            if (s.Phase == VsPhase.Hide && s.Find(me)?.Role == VsRole.Hider && !hidden)
            {
                hidden = true;
                Vs.Disguise(VsProp.HayBale);
                _ = Hide(s);
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (GameClock.Now > 3000) break;
        }
        GD.Print($"{Log} catches: {string.Join(", ", _caught.Select(c => $"{c.By}->{c.Hider}"))}; objectives: {string.Join(", ", _objectives)}");
        Expect(_caught.Any(c => c.By < 0 && c.Hider < 0), "a bot seeker caught a bot hider");
        Expect(_objectives.Any(o => o.By < 0), "a bot hider completed an objective");
        Expect(_pinsChecked > 0 && _outside.Count == 0, $"every bot hid inside the circle ({_pinsChecked} pins; outside: {string.Join(", ", _outside)})");
        Expect(await Until(() => (Bank.Instance?.Balance ?? 0) > balance, 20), $"the human's reward arrived ({balance} -> {Bank.Instance?.Balance})");
        await Finish(0);
    }

    private VsState? _last;
    private int _lastPins = -1;

    private void Record(VsState s)
    {
        foreach (var o in s.Objectives)
            if (o.Done) _objectives.Add((s.Round, o.Site, o.By));
        foreach (var e in s.Entrants)
            if (e.Role == VsRole.Hider && e.Caught && !_caught.Contains((e.Peer, s.Seeker))) _caught.Add((e.Peer, s.Seeker));
        if (s.Phase == VsPhase.RoundEnd && s.Pins.Count != _lastPins)
        {
            _lastPins = s.Pins.Count;
            foreach (var p in s.Pins.Where(p => p.Peer < 0))
            {
                _pinsChecked++;
                if (!VsRules.Inside(s.E, s.N, s.Radius + 1, p.E, p.N)) _outside.Add($"{p.Name} {VsRules.ToLine(s.E, s.N, s.Radius, p.E, p.N):F0} m");
            }
        }
        _last = s;
    }

    /// <summary>The human hides in the far corner of the circle, slowly (under the disguised walk's check).</summary>
    private async Task Hide(VsState s)
    {
        double te = s.E + s.Radius * 0.35, tn = s.N - s.Radius * 0.3;
        var me = Me!;
        for (int i = 0; i < 300 && Vs.Mine?.Phase is VsPhase.Hide; i++)
        {
            var g = me.Global;
            double de = te - g.E, dn = tn - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 1) break;
            me.GlobalPosition += new Vector3((float)(de / d), 0.1f, (float)(-dn / d));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
    }
}
