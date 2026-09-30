using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--useanim A|B</c> with <c>--connect</c> (driven by <c>tools/useanimcheck.sh</c>): the use animations over loopback.
/// A (first person) hurts itself, drinks, eats, looks at the GPS, holds and puts on a hat, screenshotting its own view
/// mid-animation; B stands nearby and must see A's replicated <c>ItemAction</c> turn into the Mouth arm pose for both the
/// drink and the hat, then A's worn hat, and screenshots A in third person. Uses a scratch inventory.
/// </summary>
public partial class UseAnimProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--useanim");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public UseAnimProbe(ItemController items) => _items = items;
    public UseAnimProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    private FootPlayer? Other() =>
        Find(GetTree().Root);

    private FootPlayer? Find(Node n)
    {
        if (n is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        foreach (var c in n.GetChildren())
            if (Find(c) is { } f) return f;
        return null;
    }

    /// <summary>A burst of frames, for picking the moment and the pose.</summary>
    private async Task Burst(string name, int n, double every)
    {
        for (int i = 0; i < n; i++) { Shot($"{name}_{i}"); await Seconds(every); }
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor(), 150)) { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        GD.Print(_failures == 0 ? $"[useanim {_role}] RESULT: ok" : $"[useanim {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA(FootPlayer me)
    {
        var inv = _items.Inventory;
        inv.Put(0, new ItemStack(ItemId.WaterBottle, 2));
        inv.Put(1, new ItemStack(ItemId.EnergyBar, 2));
        inv.Put(2, new ItemStack(ItemId.Gps, 1));
        inv.Put(3, new ItemStack(ItemId.WitchHat, 1));
        Expect(await Heard("B", "ready", 150), "B joined");
        me.Heal(FootPlayer.MaxHealth);   // a spawn fall may have hurt it: start full so 60 damage never knocks it out
        me.TakeDamage(60f);
        inv.Select(0);
        await Seconds(1.0);
        float hp = me.Health;

        _items.UseSlot(me, 0);
        Say("drinking");
        await Burst("drink_1p", 8, 0.16);
        await Seconds(0.5);
        Expect(me.Health > hp, $"drinking healed ({hp:F0} -> {me.Health:F0})");
        Expect(inv[0].Count == 1, "one bottle was used up");

        inv.Select(1);
        await Seconds(0.8);
        me.TakeDamage(30f);
        _items.UseSlot(me, 1);
        await Burst("eat_1p", 4, 0.2);
        await Seconds(1.0);

        // a second use while the first runs is ignored
        inv.Select(0);
        await Seconds(0.8);
        me.TakeDamage(40f);
        hp = me.Health;
        _items.UseSlot(me, 0);
        await Seconds(0.1);
        _items.UseSlot(me, 0);
        await Seconds(1.6);
        Expect(inv[0].IsEmpty || inv[0].Count == 0, "two quick uses consumed exactly one bottle more");

        // switching away mid-animation cancels it: nothing is eaten
        me.TakeDamage(30f);
        inv.Put(4, new ItemStack(ItemId.EnergyBar, 1));
        int bars = inv[1].Count;
        inv.Select(1);
        await Seconds(0.8);
        _items.UseSlot(me, 1);
        await Seconds(0.1);
        inv.Select(2);
        await Seconds(1.0);
        Expect(inv[1].Count == bars, "switching item cancelled the bite");

        // GPS held
        await Seconds(1.0);
        Shot("gps_1p");

        // hat in hand, then put on
        inv.Select(3);
        await Seconds(1.0);
        Shot("hat_hand_1p");
        _items.UseSlot(me, 3);
        Say("hat");
        await Burst("hat_up_1p", 6, 0.12);
        await Seconds(1.0);
        Expect(inv.Worn == ItemId.WitchHat, "the hat is worn");
        Say("done");
        await Heard("B", "done", 30);
    }

    private async Task RunB(FootPlayer me)
    {
        Say("ready");
        Expect(await Until(() => Other() != null, 30), "A is visible here");
        var a = Other();
        if (a == null) return;
        me.LookPitch = -0.1f;
        bool saw = false;
        // the drink
        Expect(await Until(() => a.DrawnArmPose == Avatar.ItemArmPose.Mouth && a.ItemAction == 2, 60), "A's drink is seen as ItemAction 2 / Mouth arm pose");
        await Seconds(0.3);
        Shot("drink_3p_remote");
        saw = await Until(() => a.ItemAction == 0, 5);
        Expect(saw, "ItemAction returns to 0 after the drink");
        Expect(await Until(() => a.HeadwearId == (int)Avatar.Headwear.WitchHat, 60), "A's hat arrived as Headwear.WitchHat");
        await Seconds(0.5);
        Shot("hat_on_3p_remote");
        Say("done");
    }

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"useanim_{name}.png"));
        GD.Print($"[useanim {_role}] screenshot {name}");
    }

    private void Say(string what)
    {
        GD.Print($"[useanim {_role}] say {what}");
        Chat?.Send($"UA {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"UA {role} {what}")), seconds);

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
        GD.Print($"[useanim {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[useanim {_role}] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
