using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--swissnet A|B</c> with <c>--connect</c> (driven by <c>tools/swisscheck.sh</c>, #478): the Swiss items
/// over loopback, in free roam. B stands 2 m in front of A, both hurt.
/// <list type="bullet">
/// <item>A sets out a fondue: both gain <see cref="SwissItems.FondueHeal"/>.</item>
/// <item>A throws a smoke canister at B: B stands in the cloud on its own peer (<see cref="SwissItems.InSmoke"/>).</item>
/// <item>A blows the alphorn: B hears it (a <see cref="ItemEventKind.Horn"/> event from A).</item>
/// </list>
/// </summary>
public partial class SwissNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--swissnet");

    public SwissNetProbe(ItemController items) : base(items, "swiss", "SW") { }
    public SwissNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private GlobalPos _a;
    private float _healthBefore = -1, _gained = float.NaN;
    private bool _heardHorn;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => ItemEvents.Instance != null)) { await Finish(0); return; }
        ItemEvents.Received += OnEvent;
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(1.0);
    }

    public override void _ExitTree() => ItemEvents.Received -= OnEvent;

    /// <summary>What this peer saw: the fondue's gift to our own player, A's horn.</summary>
    private void OnEvent(ItemEvent e)
    {
        if (e.Kind == ItemEventKind.Fondue && Me is { } me && _healthBefore >= 0) _gained = me.Health - _healthBefore;
        if (e.Kind == ItemEventKind.Horn && !e.Local) _heardHorn = true;
    }

    /// <summary>Hurt a little every second: no regeneration while the items are tried (it starts 6 s after a hurt).</summary>
    public override void _Process(double delta)
    {
        if (Me is not { } me || _healthBefore < 0) return;
        _sinceNip += delta;
        if (_sinceNip > 1.5 && float.IsNaN(_gained)) { _sinceNip = 0; me.TakeDamage(0.1f); }
        if (float.IsNaN(_gained)) _healthBefore = me.Health;
    }

    private double _sinceNip;

    private void Hurt(FootPlayer me)
    {
        me.TakeDamage(60f);
        _healthBefore = me.Health;
    }

    private async Task RunA(FootPlayer me)
    {
        me.LookYaw = 0f;
        me.LookPitch = -0.15f;
        var at = me.Global;
        for (int tries = 0; tries < 60 && !Said("B", "ready"); tries++)
        {
            Say(Fmt($"posA {at.E:F2} {at.N:F2} {at.Alt:F2}"));
            await Seconds(2.5);
        }
        if (!Said("B", "ready")) { Fail("B never got ready"); return; }
        Hurt(me);

        // the fondue: a hold of a few seconds, then everyone near eats
        _items.Inventory.Put(3, new ItemStack(ItemId.FonduePot, 1));
        _items.Inventory.Put(4, new ItemStack(ItemId.SmokeCanister, 1));
        _items.Inventory.Put(5, new ItemStack(ItemId.Alphorn, 1));
        Use(me, ItemId.FonduePot);
        Expect(await Until(() => !float.IsNaN(_gained), 12), "the fondue was set out");
        Expect(Mathf.Abs(_gained - SwissItems.FondueHeal) < 1f, $"A ate: +{_gained:F1}");
        Say("fondue");
        if (!await Until(() => Said("B", "fed"), 15)) { Fail("B never ate"); return; }

        Use(me, ItemId.SmokeCanister);
        Expect(await Until(() => SwissItems.InSmoke(Body("B")?.Global ?? default), 4), "the canister landed on B: B is in the smoke here");
        Say("smoke");
        if (!await Until(() => Said("B", "in smoke"), 15)) { Fail("B never saw the smoke"); return; }

        Use(me, ItemId.Alphorn);
        await Seconds(4.0);
        Say("horn");
        if (!await Until(() => Said("B", "heard"), 15)) Fail("B never heard the horn");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Until(() => _heard.Any(l => l.Contains("SW A posA")), 150)) { Fail("A never reported"); return; }
        var p = _heard.First(l => l.Contains("SW A posA")).Split("posA ")[1].Split(' ');
        _a = new GlobalPos(Lv95(p[0]), Lv95(p[1]), Lv95(p[2]));
        await Until(() => !GetParent().GetChildren().OfType<SpawnPoint>().Any() && me.IsOnFloor(), 60);
        for (int tries = 0; tries < 4; tries++)
        {
            me.GlobalPosition = me.Origin!.ToWorld(_a) + new Vector3(0f, 1.0f, -2f);
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
            await Until(() => me.IsOnFloor(), 10);
            await Seconds(1.0);
            if (me.GlobalPosition.DistanceTo(me.Origin!.ToWorld(_a)) < 3.5f) break;
        }
        Hurt(me);
        Say("ready");

        bool fed = await Until(() => !float.IsNaN(_gained), 20);
        Expect(fed && Mathf.Abs(_gained - SwissItems.FondueHeal) < 1f, $"B, 2 m from A's fondue, ate: +{_gained:F1}");
        Say("fed");

        if (!await Until(() => Said("A", "smoke"), 20)) { Fail("A never threw the smoke"); return; }
        Expect(await Until(() => SwissItems.InSmoke(me.Global), 4), "B stands in A's smoke on its own peer");
        await Seconds(1.0);
        if (DisplayServer.GetName() != "headless") Shot("b_smoke");
        Say("in smoke");

        Expect(await Until(() => _heardHorn, 10), "B heard A's alphorn");
        Say("heard");
    }

    private void Use(FootPlayer me, ItemId id)
    {
        int slot = Enumerable.Range(0, Inventory.HotbarSize).FirstOrDefault(i => _items.Inventory[i].Id == id, -1);
        if (slot < 0) { Expect(false, $"{id} on the hotbar"); return; }
        _items.Inventory.Select(slot);
        _items.UseSlot(me, slot);
    }

    private FootPlayer? Body(string role)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    private bool Said(string role, string what) => _heard.Any(l => l.Contains($"SW {role} {what}"));

    private static string Fmt(FormattableString s) => FormattableString.Invariant(s);

    private static double Lv95(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}
