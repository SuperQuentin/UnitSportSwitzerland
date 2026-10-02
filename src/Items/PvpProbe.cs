using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--pvpcheck A|B</c> with <c>--connect</c> (driven by <c>tools/pvpcheck.sh</c>): foot weapons against a player over
/// loopback (#178). A holds an assault rifle, a pistol and a knife and shoots B through the real item path; B stands
/// 10 m in front of A and checks its own health. With <c>--pvpexpect off</c> (a server without <c>--pvp</c>) nothing
/// may hurt B. With PvP on: two rifle rounds (2 × 26), a pistol round half soaked by B's vest (10 off, armour 40), a
/// knife stab at arm's length, then rifle rounds until B goes down — B's Died event must name A as the killer,
/// and A must see B's replicated Down flag. Scratch inventories; outputs in <c>test_output/</c>.
/// </summary>
public partial class PvpProbe : Node
{
    public static string? Role => Arg("--pvpcheck")?.ToUpperInvariant();
    private static bool ExpectOn => Arg("--pvpexpect") != "off";

    private static string? Arg(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public PvpProbe(ItemController items) => _items = items;
    public PvpProbe() : this(null!) { }

    /// <summary>Puts the kit in a fresh inventory: weapons on the hotbar, ammunition and a vest in the pack.</summary>
    public static void Stock(Inventory inv)
    {
        inv.Put(1, new ItemStack(ItemId.Rifle, 1));
        inv.Put(2, new ItemStack(ItemId.Pistol, 1));
        inv.Put(3, new ItemStack(ItemId.Knife, 1));
        inv.Put(4, new ItemStack(ItemId.ArmorVest, 1));
        inv.Add(ItemId.Ammo75, 30);
        inv.Add(ItemId.Ammo9mm, 30);
    }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor() && ItemEvents.Instance != null, 150))
        { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        GD.Print(_failures == 0 ? $"[pvp {_role}] RESULT: ok" : $"[pvp {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------------------------
    // A: the shooter
    // ------------------------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        me.LookYaw = 0f;
        string pos = Fmt($"posA {me.GlobalPosition.X:F2} {me.GlobalPosition.Y:F2} {me.GlobalPosition.Z:F2}");
        for (int tries = 0; tries < 60 && !Said("B", "ready"); tries++)
        {
            Say(pos);
            await Seconds(2.5);
        }
        if (!Said("B", "ready")) { Fail("B never joined"); return; }
        var b = Body(me);
        if (b == null) { Fail("B's body is not here"); return; }
        await Seconds(1.0);

        // B moved itself 10 m out: wait until its body is there on this machine, not still at the spawn
        await Near(me, b, 10f);
        await Fire(me, b, ItemId.Rifle, 2, "rifle");
        await Step("B", "vest");
        await Fire(me, b, ItemId.Pistol, 1, "pistol");
        if (!ExpectOn) { Say("finished"); await Step("B", "done"); return; }

        await Step("B", "close");
        await Near(me, b, 1.5f);
        await Fire(me, b, ItemId.Knife, 1, "knife");
        await Step("B", "far");
        await Near(me, b, 10f);

        // finish B: rifle rounds until its Down flag arrives here
        for (int i = 0; i < 12 && b.Down == 0; i++)
            await Fire(me, b, ItemId.Rifle, 1, null);
        await Until(() => b.Down != 0, 3);
        Expect(b.Down != 0, "B's body is down here (replicated Down flag)");
        Snap("a_down");
        int before = Hits;
        await Fire(me, b, ItemId.Rifle, 1, null);
        Expect(Hits == before, "a downed body is not hit again");
        Say("finished");
        await Step("B", "done");
    }

    private int Hits;

    /// <summary>Until B's body, as replicated here, stands about <paramref name="d"/> m away (and a moment more).</summary>
    private async Task Near(FootPlayer me, FootPlayer b, float d)
    {
        bool there = await Until(() => Mathf.Abs(Flat(b.GlobalPosition - me.GlobalPosition) - d) < 0.8f, 20);
        if (!there) Expect(false, $"B's body {d} m away here ({Flat(b.GlobalPosition - me.GlobalPosition):F1} m)");
        await Seconds(0.5);
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>Aims at B's chest, selects the weapon and pulls the trigger <paramref name="shots"/> times through the item path.</summary>
    private async Task Fire(FootPlayer me, FootPlayer b, ItemId weapon, int shots, string? announce)
    {
        int slot = SlotOf(weapon);
        if (slot < 0) { Expect(false, $"{weapon} in the inventory"); return; }
        _items.Inventory.Select(slot);
        for (int i = 0; i < shots; i++)
        {
            var dir = (b.GlobalPosition + Vector3.Up * 1.2f - me.EyePosition).Normalized();
            me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            await Seconds(0.3);
            int ammo = Weapons.Get(weapon)!.Ammo is var a && a != ItemId.None ? CountOf(a) : -1;
            _items.UseSlot(me, slot);
            if (ammo >= 0) Expect(CountOf(Weapons.Get(weapon)!.Ammo) == ammo - 1, $"{weapon} spent one round");
            await Seconds(Weapons.Get(weapon)!.Interval + 0.25);
        }
        if (announce != null) Say($"shot {announce}");
        await Seconds(0.5);
    }

    private FootPlayer? Body(FootPlayer me)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != me && !p.Npc) return p;
        return null;
    }

    // ------------------------------------------------------------------------------------
    // B: the target
    // ------------------------------------------------------------------------------------

    private Vector3 _a;
    private long _killer = -1;

    private async Task RunB(FootPlayer me)
    {
        if (!await Until(() => _heard.Any(l => l.Contains("PV A posA")), 150)) { Fail("A never reported"); return; }
        var parts = _heard.First(l => l.Contains("PV A posA")).Split("posA ")[1].Split(' ');
        _a = new Vector3(Float(parts[0]), Float(parts[1]), Float(parts[2]));
        _items.Inventory.Select(0);
        me.Died += (killer, cause) => { _killer = killer; GD.Print($"[pvp B] died, killer {killer}, cause {cause}"); };
        await StandAt(me, 10f);
        Say("ready");

        float on = ExpectOn ? 1f : 0f;
        Expect(await HealthAfter(me, "rifle", 100f - 52f * on), $"two rifle rounds: health {me.Health:F1}");
        _items.UseSlot(me, SlotOf(ItemId.ArmorVest));
        await Until(() => me.Armor > 0, 4);
        Expect(Mathf.IsEqualApprox(me.Armor, FootPlayer.MaxArmor), $"vest on: armour {me.Armor:F1}");
        float h = me.Health;
        Say("vest");
        Expect(await HealthAfter(me, "pistol", h - 10f * on), $"pistol round through the vest: health {me.Health:F1}");
        Expect(Mathf.IsEqualApprox(me.Armor, FootPlayer.MaxArmor - 10f * on), $"the vest soaked half: armour {me.Armor:F1}");
        if (!ExpectOn)
        {
            await Until(() => Said("A", "finished"), 30);
            Say("done");
            return;
        }

        await StandAt(me, 1.5f);
        h = me.Health;
        Say("close");
        Expect(await HealthAfter(me, "knife", h - (34f - Mathf.Min(me.Armor, 17f))), $"knife stab: health {me.Health:F1}");
        await StandAt(me, 10f);
        Say("far");

        await Until(() => me.KnockedOut, 30);
        Expect(me.KnockedOut, "B went down");
        Snap("b_down");
        await Until(() => _killer >= 0, 3);
        long a = PeerOfA();
        Expect(_killer == a && a > 0, $"Died names A (peer {a}) as the killer ({_killer})");
        await Until(() => Said("A", "finished"), 40);
        Say("done");
    }

    private long PeerOfA()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return FootPlayer.NetId(p.Name) ?? 0;
        return 0;
    }

    /// <summary>B stands <paramref name="d"/> m in front of A (A faces -Z) and waits to settle.</summary>
    private async Task StandAt(FootPlayer me, float d)
    {
        // after this client's own spawn has settled: a spawn still pending puts the body back on its point
        await Until(() => !GetParent().GetChildren().OfType<Core.SpawnPoint>().Any() && me.IsOnFloor(), 60);
        await Seconds(0.5);
        var want = _a + new Vector3(0f, 1.0f, -d);
        // set down again until it stays: under load the arrival from the spawn can still land late
        for (int tries = 0; tries < 4; tries++)
        {
            me.GlobalPosition = want;
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
            await Until(() => me.IsOnFloor(), 10);
            await Seconds(1.5);
            if (Flat(me.GlobalPosition - want) < 1.5f) break;
            GD.Print(Fmt($"[pvp {_role}] put back at {me.GlobalPosition.X:F1},{me.GlobalPosition.Z:F1}: again"));
        }
        me.LookYaw = Mathf.Pi;   // facing A
        await Seconds(1.0);
    }

    /// <summary>Waits for A's "shot what" line, then a moment for the hits; true when health is <paramref name="want"/>.</summary>
    private async Task<bool> HealthAfter(FootPlayer me, string what, float want)
    {
        await Until(() => Said("A", $"shot {what}"), 40);
        await Until(() => Mathf.Abs(me.Health - want) < 0.6f, 2.5);
        return Mathf.Abs(me.Health - want) < 0.6f;
    }

    // ------------------------------------------------------------------------------------

    public override void _EnterTree() => ItemEvents.Received += Count;
    public override void _ExitTree() => ItemEvents.Received -= Count;
    private void Count(ItemEvent e) { if (e.Kind == ItemEventKind.Hit && e.Local) Hits++; }

    private bool Said(string role, string what) => _heard.Any(l => l.Contains($"PV {role} {what}"));

    private async Task Step(string role, string what)
    {
        if (!await Until(() => Said(role, what), 40)) Expect(false, $"{role} said {what}");
    }

    private void Snap(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"pvp_{name}.png"));
    }

    private static string Fmt(FormattableString s) => FormattableString.Invariant(s);
    private static float Float(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    private int CountOf(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private void Say(string what)
    {
        GD.Print($"[pvp {_role}] say {what}");
        Chat?.Send($"PV {_role} {what}");
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[pvp {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[pvp {_role}] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
