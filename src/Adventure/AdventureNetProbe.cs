using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure;

/// <summary>
/// <c>--adventurenet start|back|fresh</c> with <c>--connect</c> and <c>--adventure-test</c> (driven by
/// <c>tools/adventurenetcheck.sh</c>, #1223). The server keeps the progress; each role checks what reached
/// this client, the owner:
/// <list type="bullet">
/// <item><c>start</c>: its progress loads once it has proven its key; it starts the probe errand and walks
/// to area A, and the server moves the quest on (remote authority); then it leaves.</item>
/// <item><c>back</c> (the same <c>user://</c>, after a server restart): the errand is still at step 2; it
/// hands an apple over at area B and gets the rewards. Then, in a Battle Royale lobby (<c>/br open here</c>,
/// <c>/br join</c>), a quest start is refused; out of it again, it starts.</item>
/// <item><c>fresh</c> (its own <c>user://</c>, under the same name as the others): fresh progress, never
/// theirs.</item>
/// </list>
/// </summary>
public partial class AdventureNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--adventurenet");
    private const string Password = "adventurepw";

    public AdventureNetProbe(ItemController items) : base(items, "adventurenet", "AV", "adventurenet_") { }
    public AdventureNetProbe() : this(null!) { }

    private static AdventureService? Service => AdventureService.Instance;
    private static QuestsProgress Quests => Service!.Mine.Get(QuestsProgress.Section);
    private static AreaTarget A => ProbeQuests.PointA(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
    private static AreaTarget B => ProbeQuests.PointB(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    private string? _refused;

    public override async void _Ready()
    {
        _role = Role ?? "START";
        if (!await Joined(150, () => Service != null)) return;
        Service!.Refused += why => _refused = why;
        // the quests section arrives once the server has loaded this player's progress (on its proof)
        Expect(await Until(() => Service.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it (SectionSync)");
        Expect(!Service.Authority, "the server keeps the progress, not this client");
        Expect(!Service.Unsaved, "the server keeps it under a proven identity");
        switch (_role)
        {
            case "START": await Start(); return;
            case "BACK": await Back(); break;
            default: await Fresh(); break;
        }
        await Finish(1.0);
    }

    private async Task Start()
    {
        Expect(Quests.Active.Count == 0 && Quests.Done.Count == 0, "a new player starts with no quests");
        Service!.AskStart(ProbeQuests.Errand);
        Expect(await Until(() => Quests.Active.ContainsKey(ProbeQuests.Errand), 10), "the server started the errand and sent it back");
        await GoTo(A.E, A.N);
        Expect(await Until(() => Quests.Active.TryGetValue(ProbeQuests.Errand, out var s) && s.Step == 1, 15),
            "the server saw this player at area A and moved the errand on");
        GD.Print($"{Log} errand at step 2, leaving");
        GD.Print(_failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({_failures})");
        Multiplayer.MultiplayerPeer?.Close();
        await Seconds(1);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Back()
    {
        Expect(Quests.Active.TryGetValue(ProbeQuests.Errand, out var s) && s.Step == 1,
            $"after the server's restart the errand is still at step 2 ({(Quests.Active.TryGetValue(ProbeQuests.Errand, out var st) ? st.Step + 1 : 0)})");
        long balance = Bank.Instance?.Balance ?? 0;
        _items.Inventory.Add(ItemId.Apple, 1);
        await GoTo(B.E, B.N);
        // the server must have our new position before it checks the hand-over's distance: retry while it says "too far"
        for (int i = 0; i < 10 && !Quests.Done.ContainsKey(ProbeQuests.Errand); i++)
        {
            Service!.AskDeliver(ProbeQuests.Errand, 1);
            if (await Until(() => Quests.Done.ContainsKey(ProbeQuests.Errand), 2)) break;
        }
        Expect(Quests.Done.ContainsKey(ProbeQuests.Errand), "the hand-over at area B completed the errand on the server");
        Expect(await Until(() => CountOf(ItemId.Apple) == 0, 5), "the apple left the pack on the server's word");
        Expect(await Until(() => CountOf(ItemId.Chocolate) >= 1, 5), "the chocolate reward arrived");
        Expect(await Until(() => Bank.Instance?.Balance >= balance + 25, 5), $"CHF 25 credited to the account ({balance} -> {Bank.Instance?.Balance})");
        Expect(Quests.Titles.Contains("Errand runner"), "the title is earned");

        // a Battle Royale round: the adventure is off for its entrants
        if (!await LogIn(Password)) return;
        Chat!.Send("/br open here");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.State.Phase is BattleRoyale.BrPhase.Lobby, 15), "a Battle Royale lobby is open");
        Chat.Send("/br join");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.MyEntry != null, 15), "this player joined it");
        _refused = null;
        Service!.AskStart(ProbeQuests.Walk);
        Expect(await Until(() => _refused != null, 10) && !Quests.Active.ContainsKey(ProbeQuests.Walk),
            $"in the round, a quest start is refused ({_refused})");
        Chat.Send("/br leave");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.MyEntry == null, 15), "out of the round");
        Service.AskStart(ProbeQuests.Walk);
        Expect(await Until(() => Quests.Active.ContainsKey(ProbeQuests.Walk), 10), "out of it, the quest starts");
        Chat.Send("/br cancel");
    }

    private async Task Fresh()
    {
        await Until(() => false, 1.0);   // anything the server would wrongly send arrives within this
        Expect(Quests.Active.Count == 0 && Quests.Done.Count == 0 && Quests.Titles.Count == 0,
            $"another key under the same name gets fresh progress ({Quests.Active.Count} running, {Quests.Done.Count} done)");
        Expect(Service!.Mine.Ids.Count() == 1, $"and only its own sections ({string.Join(",", Service.Mine.Ids)})");
    }

    /// <summary>Puts the body at an LV95 point and waits for it to stand there, so its published position moves.</summary>
    private async Task GoTo(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.3f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Until(() => me.IsOnFloor(), 5);
        GD.Print(FormattableString.Invariant($"{Log} at {me.Global.E:F1}/{me.Global.N:F1}"));
    }
}
