using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Net;

/// <summary>
/// <c>--transfer A|B</c> with <c>--connect</c> (driven by <c>tools/transfercheck.sh</c>, #649): A types
/// <c>/transfer me TransferB</c>; A's pack and pocket must end up empty and B's must have grown by
/// at least what fitted, with a chat line saying whose it was. Both start with the starter kit.
/// </summary>
public partial class TransferProbe : ChatProbe
{
    public static string? Role => RoleArg("--transfer");

    public TransferProbe(ItemController items) : base(items, "transfer", "TR") { }
    public TransferProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    private async Task RunA()
    {
        // B may join after us: say hello until it answers
        bool met = false;
        for (int i = 0; i < 40 && !met; i++)
        {
            Say("hello");
            met = await Heard("B", "ready", 3);
        }
        if (!met) { Fail("B never answered"); return; }

        int before = PackTotal();
        Expect(before > 0, $"A has something to give ({before})");
        Chat?.Send("/transfer me TransferB");
        Expect(await Until(() => PackTotal() == 0 && _items.Inventory.Cash == 0, 15), $"A's pack and pocket are empty ({PackTotal()})");
        Expect(_heard.Any(l => l.Contains("You gave your inventory to TransferB")), "A was told");
        Say($"gave {before}");
        Expect(await Heard("B", "got", 20), "B got it");
    }

    private async Task RunB()
    {
        if (!await Heard("A", "hello", 120)) { Fail("A never said hello"); return; }
        int before = PackTotal();
        Say("ready");
        Expect(await Until(() => _heard.Any(l => l.Contains("TransferA's inventory is now yours")), 20), "B was told whose inventory it got");
        Expect(PackTotal() > before, $"B's pack grew ({before} -> {PackTotal()})");
        if (!await Heard("A", "gave", 20)) { Fail("A never said what it gave"); return; }
        Say("got");
    }
}
