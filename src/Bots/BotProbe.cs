using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// <c>--botcheck A|B</c> with <c>--connect</c> (driven by <c>tools/botcheck.sh</c>, #837): bots the
/// server runs, seen from two clients. The server starts with <c>--bots 3</c> round the clients'
/// spawn. A finds the bots (marked with a <see cref="BotTag"/>, a name), sees one walk on the server's
/// collision (its replicated position moves, it stands on the ground), tells B which, walks up to it
/// and shoots it with a pistol through the real item path until its replicated <c>Down</c> flag comes
/// back: the server took the hits itself. In free roam a bot is knocked out, not eliminated, so A then
/// sees it stand up again. B sees the same bot, and sees it go down.
/// </summary>
public partial class BotProbe : ChatProbe
{
    public static string? Role => RoleArg("--botcheck");

    public BotProbe(ItemController items) : base(items, "bots", "BT", "bots_") { }
    public BotProbe() : this(null!) { }

    /// <summary>A pistol on the hotbar and a box of rounds.</summary>
    public static void Stock(Inventory inv)
    {
        inv.Put(1, new ItemStack(ItemId.Pistol, 1));
        inv.Add(ItemId.Ammo9mm, 60);
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => ItemEvents.Instance != null)) return;
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB();
        await Finish(1.0);
    }

    private FootPlayer[] Bots() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().Where(p => p.Bot).ToArray();

    private async Task RunA(FootPlayer me)
    {
        if (!await Until(() => Bots().Length > 0, 60)) { Fail("no bot came into view"); return; }
        await Seconds(1.0);
        var bots = Bots();
        GD.Print($"{Log} {bots.Length} bot(s) here: {string.Join(", ", bots.Select(b => $"{b.Name} \"{b.BotName}\""))}");
        Expect(bots.All(b => b.GetNodeOrNull("BotTag") != null && b.BotName.Length > 0), "every bot is marked (tag and name)");

        // walking on the server's collision: someone moved, and stands on the ground
        var start = bots.ToDictionary(b => b, b => b.Global);
        await Seconds(8.0);
        var walker = bots.Where(GodotObject.IsInstanceValid).OrderByDescending(b => b.Global.DistanceTo(start[b])).First();
        float moved = (float)walker.Global.DistanceTo(start[walker]);
        Expect(moved > 1f, $"a bot walks here: {walker.Name} moved {moved:F1} m in 8 s");
        bool grounded = me.Terrain?.TryGetSurface(walker.GlobalPosition, out float g) == true && Mathf.Abs(walker.GlobalPosition.Y - g) < 1.5f;
        Expect(grounded, $"it stands on the ground ({walker.GlobalPosition.Y:F1} m)");
        Say($"seen {walker.Name}");
        if (!await Heard("B", "seen", 40)) { Expect(false, "B sees the same bot"); return; }

        // up to it, then the pistol until it goes down
        me.WalkControls = () =>
        {
            var to = walker.GlobalPosition - me.GlobalPosition;
            to.Y = 0;
            return (to.Length() > 8f ? to.Normalized() : Vector3.Zero, false);
        };
        await Until(() => !GodotObject.IsInstanceValid(walker) || walker.GlobalPosition.DistanceTo(me.GlobalPosition) < 9f, 30);
        me.WalkControls = () => (Vector3.Zero, false);
        int slot = SlotOf(ItemId.Pistol);
        _items.Inventory.Select(slot);
        int shots = 0;
        for (; shots < 14 && walker.Down == 0; shots++)
        {
            var dir = (walker.GlobalPosition + Vector3.Up * 1.2f - me.EyePosition).Normalized();
            me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            await Seconds(0.2);
            _items.UseSlot(me, slot);
            await Seconds(Weapons.Get(ItemId.Pistol)!.Interval + 0.25);
        }
        await Until(() => walker.Down != 0, 3);
        Expect(walker.Down != 0, $"the bot is down here after {shots} pistol round(s) (the server took the hits)");
        if (DisplayServer.GetName() != "headless") Shot("a_down");   // a headless client has no picture to save
        Say("down");
        // free roam: knocked out for 3.5 s, then up again (Battle Royale keeps a bot down)
        Expect(await Until(() => walker.Down == 0, 10), "the bot stands up again (knocked out, not eliminated, in free roam)");

        // the bots shoot back (#837, Bots/BotHands): armed, each one in reach fires once at A
        Chat!.Send("/login botcheck");
        if (!await Until(() => Permissions.IsAdmin, 10)) { Expect(false, "logged in as admin"); return; }
        Chat.Send("/bot arm pistol 10");
        await Seconds(1.0);
        float before = me.Health;
        Chat.Send("/bot fire");
        bool hurt = await Until(() => me.Health < before, 5);
        Expect(hurt, $"a bot's pistol hurt A: health {before:F0} -> {me.Health:F0} (the server fired, A's machine took it)");
        Say("fired");
        me.WalkControls = null;
        Say("finished");
        await Heard("B", "done", 20);
    }

    private async Task RunB()
    {
        if (!await Until(() => _heard.Any(l => l.Contains("BT A seen ")), 120)) { Fail("A never picked a bot"); return; }
        string name = _heard.First(l => l.Contains("BT A seen ")).Split("seen ")[1].Trim();
        var bot = Bots().FirstOrDefault(b => b.Name == name);
        if (bot == null && await Until(() => Bots().Any(b => b.Name == name), 10)) bot = Bots().First(b => b.Name == name);
        Expect(bot != null, $"B sees {name} too");
        Say("seen");
        if (bot == null) return;
        bool botShot = false;
        ItemEvents.Received += e => { if (e.Kind == ItemEventKind.Shot && e.Peer < 0) botShot = true; };
        if (!await Heard("A", "down", 60)) { Expect(false, "A shot the bot down"); return; }
        Expect(bot.Down != 0 || await Until(() => bot.Down != 0, 3), $"B sees {name} down");
        if (await Heard("A", "fired", 60)) Expect(botShot || await Until(() => botShot, 3), "B heard a bot's shot (the server's Shot event)");
        await Heard("A", "finished", 30);
        Say("done");
    }
}
