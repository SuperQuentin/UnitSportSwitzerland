using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// <c>--swordnet A|B</c> with <c>--connect</c> (driven by <c>tools/swordnetcheck.sh</c>, #1371): the sword over loopback, through the
/// real paths (the held buttons, the machine, the service, the server's guard).
/// <list type="bullet">
/// <item>A puts a training dummy in front of itself (<c>/dummy</c>); B must see it. A taps the attack three times: B must see A's
/// copy swing, the dummy's hits and its recoil; a held attack must land the charged blow; a dodge moves A and costs stamina.</item>
/// <item>Forged messages: ten dodges announced in one frame and a hit that no swing announced; the server must refuse most of them.</item>
/// <item>A server-run bot with a sword (<c>/bot arm sword</c>, <c>/bot swing</c>) hits the dummy.</item>
/// <item>With PvP on, A walks up to B and hits it: B loses health and is knocked back on its own machine; with PvP off it does not.</item>
/// </list>
/// </summary>
public partial class SwordNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--swordnet");

    private const string Password = "swordnet";
    private readonly List<SwordStrike> _landed = new();
    private readonly List<(long Who, SwordMove Move)> _swung = new();

    public SwordNetProbe(ItemController items) : base(items, "swordnet", "SN", "swordnet_") { }
    public SwordNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static SwordService Svc => SwordService.Instance!;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        SwordService.Landed += s => _landed.Add(s);
        SwordService.Swung += (who, move) => _swung.Add((who, move));
        if (!await Joined(150, () => SwordService.Instance != null)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        Release();
        await Finish(3.0);
    }

    // ---- A: the swinger ---------------------------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }
        _items.Inventory.Put(0, new ItemStack(ItemId.Sword, 1));
        _items.Inventory.Select(0);
        if (!await LogIn(Password)) return;
        if (!Expect2(await Until(() => me.SwordDrawn, 5), "the sword is drawn")) return;

        Chat!.Send("/dummy");
        if (!Expect2(await Until(() => Svc.Dummies.Count == 1, 10), "A's /dummy stands in front of it")) return;
        var dummy = Svc.Dummies.Values.First();
        me.LookYaw = me.Rotation.Y;
        Say("dummy");
        await Seconds(0.6);

        // three taps, in the rhythm: the first three swings of the combo
        int before = _landed.Count;
        for (int i = 0; i < 3; i++)
        {
            await Tap(PlayerInput.UseItem);
            await Seconds(0.64);
        }
        await Until(() => _landed.Count >= before + 3, 5);
        var moves = _landed.Skip(before).Where(s => s.Kind == SwordTargetKind.Dummy).Select(s => s.Move).ToList();
        GD.Print($"{Log} combo landed {string.Join(",", moves)}; dummy hp {dummy.Hp}");
        Expect(moves.Count >= 3 && moves[0] == SwordMove.Slash1 && moves[1] == SwordMove.Slash2 && moves[2] == SwordMove.Slash3, "the first three swings of the combo land in order");
        Expect(dummy.Hp < SwordService.DummyHp - 30f, "the dummy lost health");
        Say("combo");
        await Seconds(1.0);

        // held after a swing: the charged blow
        before = _landed.Count;
        Input.ActionPress(PlayerInput.UseItem);
        await Seconds(1.5);
        Input.ActionRelease(PlayerInput.UseItem);
        await Until(() => _landed.Skip(before).Any(s => s.Move == SwordMove.Charged), 3);
        Expect(_landed.Skip(before).Any(s => s.Move == SwordMove.Charged), "a held attack lands the charged blow");
        float stamina = me.SwordState.Stamina.Points;
        Expect(stamina < SwordStamina.Max, $"the charged blow cost stamina ({stamina:F0})");
        Say("charged");
        await Seconds(0.8);

        // a dodge: moves the body, costs stamina
        var from = me.GlobalPosition;
        await Tap(PlayerInput.SwordDodge);
        Expect(await Until(() => me.SwordState.Move == SwordMove.Dodge, 1), "the dodge button starts a dodge");
        await Seconds(0.5);
        float moved = (me.GlobalPosition - from).Length();
        Expect(moved > 0.8f, $"the dodge carried A {moved:F1} m");
        Say("dodged");
        await Seconds(1.0);

        // forged: ten dodges in one frame, and a hit nobody swung
        for (int i = 0; i < 10; i++) Svc.SendAct(SwordMove.Dodge, 0f);
        int dodges = _landed.Count;
        for (int i = 0; i < 5; i++) Svc.SendHit(SwordMove.Slash1, SwordTargetKind.Dummy, dummy.Id);
        await Seconds(1.5);
        Expect(_landed.Count == dodges, "a hit no swing announced is refused");
        Say("forged");
        await Seconds(1.5);

        // a server-run bot with a sword swings at the dummy
        Chat.Send("/bot add 1");
        await Seconds(1.0);
        Chat.Send("/bot arm sword");
        await Seconds(0.5);
        before = _landed.Count;
        Chat.Send("/bot swing");
        Expect(await Until(() => _landed.Skip(before).Any(s => s.Attacker < 0 && s.Kind == SwordTargetKind.Dummy), 8), "a bot's sword hits the dummy");
        Say("bot");
        Chat.Send("/bot clear");
        await Seconds(1.0);

        // PvP on: A walks up to B and hits it
        Chat.Send("/pvp on");
        await Seconds(0.6);
        await WalkTo(me, 1.9f);
        Say("close");
        await Seconds(0.5);
        before = _landed.Count;
        for (int i = 0; i < 2; i++)
        {
            await Tap(PlayerInput.UseItem);
            await Seconds(0.66);
        }
        await Until(() => _landed.Skip(before).Any(s => s.Kind == SwordTargetKind.Body), 4);
        Expect(_landed.Skip(before).Any(s => s.Kind == SwordTargetKind.Body), "with PvP on a blow lands on B");
        Say("pvp");
        if (!await Heard("B", "hurt", 15)) Fail("B never reported the hit");

        // PvP off: the same blow does nothing to a player
        Chat.Send("/pvp off");
        await Seconds(0.8);
        await WalkTo(me, 1.9f);
        before = _landed.Count;
        for (int i = 0; i < 3; i++)
        {
            await Tap(PlayerInput.UseItem);
            await Seconds(0.66);
        }
        await Seconds(1.0);
        Expect(!_landed.Skip(before).Any(s => s.Kind == SwordTargetKind.Body), "with PvP off a blow does not land on B");
        Say("off");
        if (!await Heard("B", "done", 20)) Fail("B never finished");
    }

    // ---- B: the one who watches --------------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "dummy", 60)) { Fail("A never put the dummy down"); return; }
        Expect(Svc.Dummies.Count == 1, "B sees A's dummy");
        var dummy = Svc.Dummies.Values.FirstOrDefault();
        if (dummy == null) { Fail("no dummy"); return; }

        // windowed (SHOTS=1): B looks at the dummy and takes a picture of A's copy mid-swing, and of the dummy's recoil
        var look = dummy.GlobalPosition - me.GlobalPosition;
        me.LookYaw = Mathf.Atan2(-look.X, -look.Z);
        bool windowed = DisplayServer.GetName() != "headless";
        Expect2(await Until(() => Copy() is { SwordShown: not SwordMove.None }, 20), "A's copy swings (its sword is drawn turning)");
        if (windowed) { await Seconds(0.05); GD.Print($"{Log} shot {Shot("B_swing")}"); }
        float lean = 0f;
        Expect2(await Until(() => (lean = Mathf.Max(lean, dummy.Lean)) > 0.1f, 20), $"the dummy recoils on B's screen (lean {lean:F2})");
        if (windowed) GD.Print($"{Log} shot {Shot("B_recoil")}");
        Expect(await Until(() => _swung.Count(s => s.Move is >= SwordMove.Slash1 and <= SwordMove.Slash3) >= 3, 20), "B was told of A's three swings");
        Expect(await Until(() => dummy.Blows >= 3, 20), $"B saw the dummy take three blows ({dummy.Blows})");
        if (!await Heard("A", "combo", 30)) { Fail("A never finished the combo"); return; }
        Expect(dummy.Hp < SwordService.DummyHp, $"the dummy's health fell on B's screen ({dummy.Hp:F0})");

        if (!await Heard("A", "charged", 40)) { Fail("A never charged"); return; }
        Expect(_swung.Any(s => s.Move == SwordMove.Charge), "B saw the wind-up");
        Expect(_landed.Any(s => s.Move == SwordMove.Charged), "B saw the charged blow land");
        Expect(_swung.Any(s => s.Move == SwordMove.Charged), "B was told of the charged blow");

        if (!await Heard("A", "dodged", 40)) { Fail("A never dodged"); return; }
        Expect(_swung.Any(s => s.Move == SwordMove.Dodge), "B saw A dodge");

        int dodgesBefore = _swung.Count(s => s.Move == SwordMove.Dodge);
        if (!await Heard("A", "forged", 40)) { Fail("A never forged"); return; }
        int forged = _swung.Count(s => s.Move == SwordMove.Dodge) - dodgesBefore;
        GD.Print($"{Log} {forged} of 10 forged dodges came through");
        Expect(forged < 10, $"the server's stamina refused some of ten dodges in a frame (took {forged})");

        if (!await Heard("A", "bot", 60)) { Fail("A never saw the bot"); return; }
        Expect(_landed.Any(s => s.Attacker < 0 && s.Kind == SwordTargetKind.Dummy), "B saw the bot's blow on the dummy");

        // PvP: hit on this body, with a push
        if (!await Heard("A", "close", 60)) { Fail("A never came close"); return; }
        float healthBefore = me.Health;
        var startAt = me.GlobalPosition;
        if (!await Heard("A", "pvp", 30)) { Fail("A never hit"); return; }
        Expect2(await Until(() => me.Health < healthBefore - 5f, 8), $"a blow with PvP on hurts B ({healthBefore:F0} -> {me.Health:F0})");
        Expect(await Until(() => (me.GlobalPosition - startAt).Length() > 0.4f, 3), $"the blow knocked B back ({(me.GlobalPosition - startAt).Length():F1} m)");
        Expect(_landed.Any(s => s.Kind == SwordTargetKind.Body), "B heard the blow land on its own body");
        Say("hurt");

        // PvP off: nothing more
        float after = me.Health;
        if (!await Heard("A", "off", 40)) { Fail("A never finished"); return; }
        Expect(me.Health >= after - 0.01f, $"with PvP off the blows do nothing ({after:F0} -> {me.Health:F0})");
        Say("done");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private async Task Tap(string action)
    {
        Input.ActionPress(action);
        await Seconds(0.07);
        Input.ActionRelease(action);
    }

    /// <summary>Walks towards the other player's copy until within <paramref name="metres"/>, facing it.</summary>
    private async Task WalkTo(FootPlayer me, float metres)
    {
        for (int i = 0; i < 120; i++)
        {
            if (Copy() is not { } other) break;
            var d = other.GlobalPosition - me.GlobalPosition;
            d.Y = 0;
            me.LookYaw = Mathf.Atan2(-d.X, -d.Z);
            if (d.Length() <= metres) break;
            Input.ActionPress(PlayerInput.MoveForward);
            await Seconds(0.05);
        }
        Input.ActionRelease(PlayerInput.MoveForward);
        await Seconds(0.2);
    }

    private static void Release()
    {
        foreach (var a in new[] { PlayerInput.UseItem, PlayerInput.SwordDodge, PlayerInput.MoveForward })
            Input.ActionRelease(a);
    }

    private bool Expect2(bool ok, string what)
    {
        Expect(ok, what);
        return ok;
    }

    /// <summary>The other player's copy here.</summary>
    private FootPlayer? Copy()
    {
        foreach (var s in PlayerSnapshot.Of(GetTree()))
            if (s.Player != Me && !s.Player.IsMultiplayerAuthority() && !s.Player.Npc && !s.Player.Bot) return s.Player;
        return null;
    }
}
