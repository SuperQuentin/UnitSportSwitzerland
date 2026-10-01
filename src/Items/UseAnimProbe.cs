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
public partial class UseAnimProbe : ChatProbe
{
    public static string? Role => RoleArg("--useanim");

    public UseAnimProbe(ItemController items) : base(items, "useanim", "UA", "useanim_") { }
    public UseAnimProbe() : this(null!) { }

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
        if (!await Joined(150)) return;
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(1.0);
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

    protected override string Shot(string name)
    {
        string path = base.Shot(name);
        GD.Print($"{Log} screenshot {name}");
        return path;
    }
}
