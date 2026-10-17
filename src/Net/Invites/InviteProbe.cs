using System.Threading.Tasks;
using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Net;

/// <summary>
/// <c>--invitecheck A|B</c> with <c>--connect</c> (driven by <c>tools/invitecheck.sh</c>, #840): every
/// invite kind between two clients, through the paths a player uses.
/// <list type="bullet">
/// <item>Teleport: A <c>/tpa</c> B; B taps F2 (the quick menu's accept); A is teleported to B.</item>
/// <item>Party: A <c>/party invite</c> B; B accepts as the Activities screen's row does; both see the party.</item>
/// <item>Battle Royale: A (admin) opens the lobby with the Activities screen's button; B gets the
/// invite and <c>/accept br</c>; B is in the lobby on A's party team; A cancels.</item>
/// <item>Fight: A <c>/fight</c> B; B accepts with E at A (<see cref="FightManager.Engage"/>); both in it; A leaves.</item>
/// <item>Race duel: A <c>/race duel</c> B; B declines with Shift+F2; A hears the duel end.</item>
/// <item>The quick menu opens on a held F2 and lists its eight wedges.</item>
/// </list>
/// With <c>--windowed</c> B saves <c>test_output/invite_*.png</c>: the toast, the quick menu, the Activities tabs.
/// </summary>
public partial class InviteProbe : ChatProbe
{
    public static string? Role => RoleArg("--invitecheck");

    public InviteProbe(ItemController items) : base(items, "invitecheck", "IV", "invite_") { }
    public InviteProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static InviteService? Invites => InviteService.Client;
    private static bool Windowed => DisplayServer.GetName() != "headless";

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => Invites != null)) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    // ---- A: sends -------------------------------------------------------------------------------

    private async Task RunA()
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        // teleport request
        Chat!.Send("/tpa InviterB");
        Expect(await Until(() => Invites!.Sent.Any(v => v.Kind == ChatManager.TpKind), 10), "A sees its teleport request as sent");
        Expect(await Until(() => _heard.Any(l => l.StartsWith("Teleported to InviterB")), 20), "A is teleported to B once B taps F2");
        Expect(await Until(() => !Invites!.Sent.Any(), 10), "the request is gone from A's sent list");

        // party
        Chat.Send("/party invite InviterB");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "A's party holds A and B");
        Expect(Parties.Client?.IsLeader == true, "A leads the party");
        Say("party");

        // Battle Royale from the Activities screen
        Chat.Send("/login invitecheck");
        Expect(await Until(() => Permissions.IsAdmin, 10), "A is an admin");
        ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale);
        await Until(() => FindButton("Open the lobby") != null, 5);
        if (FindButton("Open the lobby") is { } open) open.EmitSignal(BaseButton.SignalName.Pressed);
        else Fail("no 'Open the lobby' button on the Battle Royale tab");
        Expect(await Until(() => BrManager.Instance?.State.Phase is BrPhase.Lobby or BrPhase.Countdown, 20), "the screen opened a lobby");
        Expect(await Until(() => Invites!.Sent.Any(v => v.Kind == BrManager.InviteId && v.Peer == Invite.Everyone), 10), "the lobby invites everyone");
        await Until(() => FindButton("Join the match") != null, 5);
        if (FindButton("Join the match") is { } join) join.EmitSignal(BaseButton.SignalName.Pressed);
        else Fail("no 'Join the match' button in the lobby");
        Expect(await Until(() => BrManager.Instance?.State.Find(Multiplayer.GetUniqueId()) != null, 10), "A joined from the screen");
        Expect(await Heard("B", "in-lobby", 30), "B accepted into the lobby");
        var s = BrManager.Instance!.State;
        var a = s.Find(Multiplayer.GetUniqueId());
        var b = s.Entrants.FirstOrDefault(e => e.Name == "InviterB");
        Expect(a != null && b != null && a.Party.Length > 0 && a.Party == b.Party, $"A and B share their party's team ('{a?.Party}', '{b?.Party}')");
        if (Windowed) await Seconds(1.0);
        GameShell.Instance?.CloseAll();
        Chat.Send("/br cancel");
        Expect(await Until(() => BrManager.Instance?.State.Phase == BrPhase.Idle, 10), "the lobby is cancelled");
        Expect(await Until(() => !Invites!.Sent.Any(), 10), "the lobby's invite is withdrawn with it");
        Say("br-done");

        // fist fight: challenged by chat, accepted by E
        Expect(await Heard("B", "fight-ready", 30), "B is ready to fight");
        Chat.Send("/fight InviterB");
        Expect(await Until(() => FightManager.Client?.Current != null, 20), "A is in the fight B accepted with E");
        Chat.Send("/fight leave");
        Expect(await Until(() => FightManager.Client?.Current == null, 30), "the fight is over");
        Say("fight-done");

        // race duel, declined
        Expect(await Heard("B", "race-ready", 30), "B is ready for the duel");
        // on the straight fixture course's road
        Chat.Send("/race duel InviterB 1000 foot");
        if (!await Until(() => Invites!.Sent.Any(v => v.Kind == World.RaceManager.InviteId), 40))
            Fail("no duel invite: " + string.Join(" / ", _heard.Where(l => l.Contains("[race]"))));
        else Expect(await Until(() => _heard.Any(l => l.Contains("declined the duel")), 30), "B's Shift+F2 ended the duel");
        Say("done");
    }

    // ---- B: answers -----------------------------------------------------------------------------

    private async Task RunB()
    {
        if (!await Heard("A", "hello", 60)) { Fail("never heard A"); return; }
        Say("ready");

        // teleport: the toast, then a tap of F2 accepts it
        if (!Expect2(await Until(() => Invites!.Incoming.Any(v => v.Kind == ChatManager.TpKind), 20), "B is asked to let A come")) return;
        await Seconds(0.6);
        if (Windowed) Shot("toast");
        await Tap(Key.F2);
        Expect(await Until(() => !Invites!.Incoming.Any(), 10), "F2 answered the request");

        // party: accepted as the screen's row does
        Expect(await Until(() => Invites!.Incoming.Any(v => v.Kind == Parties.Kind), 20), "B is invited to A's party");
        if (Invites!.Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } party) Invites.Accept(party.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "B's party holds A and B");
        Expect(await Heard("A", "party", 20), "A saw the party too");

        // Battle Royale: the lobby's invite to everyone, accepted with /accept br
        Expect(await Until(() => Invites.Incoming.Any(v => v.Kind == BrManager.InviteId), 40), "B is invited to the lobby");
        if (Windowed)
        {
            await Seconds(0.6);
            Shot("br_toast");
        }
        Chat!.Send("/accept br");
        Expect(await Until(() => BrManager.Instance?.State.Find(Multiplayer.GetUniqueId()) != null, 10), "B is in the lobby");
        Expect(!Invites.Incoming.Any(v => v.Kind == BrManager.InviteId), "the lobby's invite is gone for B");
        if (Windowed)
        {
            ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale);
            await Seconds(1.2);
            Shot("activities_br");
            GameShell.Instance?.CloseAll();
        }
        Say("in-lobby");
        Expect(await Heard("A", "br-done", 30), "A cancelled the lobby");

        // fight: E at A accepts
        Say("fight-ready");
        if (!Expect2(await Until(() => FightManager.Client?.Pending.Count > 0, 20), "B is challenged")) return;
        long from = FightManager.Client!.Pending.Keys.First();
        Expect(FightManager.Client.Prompt(from).StartsWith("Accept"), "E at A would accept");
        FightManager.Client.Engage(from);
        Expect(await Until(() => FightManager.Client.Current != null, 20), "B is in the fight");
        Expect(await Heard("A", "fight-done", 40), "the fight is over");

        // race duel: Shift+F2 declines it
        Say("race-ready");
        if (await Until(() => Invites.Incoming.Any(v => v.Kind == World.RaceManager.InviteId), 40))
        {
            await Seconds(0.4);
            await Tap(Key.F2, shift: true);
            Expect(await Until(() => !Invites.Incoming.Any(), 10), "Shift+F2 declined the duel");
        }

        // the quick menu, held open
        Press(Key.F2, true);
        // a hold, on the game clock the menu tells a tap from a hold by (fast-checks)
        double held = GameClock.Now;
        await Until(() => GameClock.Now - held > 0.6, 10);
        var menu = QuickMenu.Instance;
        Expect(menu?.IsOpen == true, "a held F2 opens the quick menu");
        Expect(menu?.Names.SequenceEqual(new[] { "Activities", "Invites", "Inventory", "Vehicles", "Map", "Emotes", "Bird journal", "Controls" }) == true,
            "its eight wedges, Activities at the top");
        if (Windowed) Shot("quickmenu");
        Press(Key.F2, false);
        await Seconds(0.3);
        Expect(menu?.IsOpen == false, "letting go with nothing aimed closes it");

        // a pad: a tap of Back is still the inventory, a hold the quick menu
        Pad(JoyButton.Back, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Pad(JoyButton.Back, false);
        Expect(await Until(() => _items.Ui.IsOpen, 5), "a tap of pad Back opens the inventory");
        Pad(JoyButton.Back, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Pad(JoyButton.Back, false);
        Expect(await Until(() => !_items.Ui.IsOpen, 5), "and Back closes it again");
        await Seconds(0.3);
        Pad(JoyButton.Back, true);
        held = GameClock.Now;
        await Until(() => GameClock.Now - held > 0.6, 10);
        Expect(menu?.IsOpen == true && !_items.Ui.IsOpen, "a held pad Back opens the quick menu, not the inventory");
        Pad(JoyButton.Back, false);
        await Seconds(0.3);
        Expect(menu?.IsOpen == false && !_items.Ui.IsOpen, "letting go of Back with nothing aimed closes it");

        if (Windowed)
        {
            ActivitiesScreen.Open(ActivitiesScreen.Tab.Invites);
            await Seconds(1.0);
            Shot("activities_invites");
            GameShell.Instance?.CloseAll();
            ActivitiesScreen.Open(ActivitiesScreen.Tab.Race);
            await Seconds(1.5);
            Shot("activities_race");
            GameShell.Instance?.CloseAll();
        }
        await Heard("A", "done", 30);
    }

    private bool Expect2(bool ok, string what)
    {
        Expect(ok, what);
        return ok;
    }

    private static void Press(Key key, bool down, bool shift = false) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = down, ShiftPressed = shift });

    private static void Pad(JoyButton button, bool down) =>
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = down, Device = 0 });

    private async Task Tap(Key key, bool shift = false)
    {
        Press(key, true, shift);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Press(key, false, shift);
    }

    private Button? FindButton(string text) => FindButton(GetTree().Root, text);

    private static Button? FindButton(Node node, string text)
    {
        if (node is Button { Visible: true } b && b.Text == text && b.IsVisibleInTree()) return b;
        foreach (var child in node.GetChildren())
            if (FindButton(child, text) is { } found) return found;
        return null;
    }
}
