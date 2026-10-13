using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--yardlootnet A|B|C</c> with <c>--connect</c> on the generated world (driven by
/// <c>tools/yardlootnetcheck.sh</c>, #627): a building site's skip searched outdoors over the
/// network, from the tile alone on every peer.
/// <list type="bullet">
/// <item>A finds the nearest generated site whose skip holds something this restock period, stands
/// beside the skip facing it (the prompt must say so) and opens it with E;</item>
/// <item>B, already there, has the same skip open; A takes the first stack, and B's open panel must
/// drop it by itself (the server's <c>Taken</c> to the peers outdoors near the site);</item>
/// <item>C joins once B has gone, searches the same skip and must find that stack gone, the rest
/// still there (the server's mask, nothing sent at the join).</item>
/// </list>
/// Windowed, A saves the prompt and the open panel to <c>test_output/yardloot_*.png</c>.
/// </summary>
public partial class YardLootNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--yardlootnet");

    public YardLootNetProbe(ItemController items) : base(items, "yardlootnet", "YL", "yardloot_") { }
    public YardLootNetProbe() : this(null!) { }

    private static LootService Loot => LootService.Instance!;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private bool Windowed => DisplayServer.GetName() != "headless";

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => LootService.Instance?.Source?.Invoke() != null))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!);
        else if (_role == "B") await RunB(Me!);
        else await RunC(Me!);
        await Finish(2.0);
    }

    /// <summary>The nearest generated site's skip with something in it this period, else its nearest container that has.</summary>
    private async Task<YardStock?> PickSkip(FootPlayer me)
    {
        var source = Loot.Source!.Invoke()!;
        var at = me.Global;
        var world = new ProceduralWorld(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        var sites = world.SitesNear(at.E, at.N, 4000).OrderBy(s => Sq(s.E - at.E) + Sq(s.N - at.N)).Take(6).ToList();
        GD.Print($"{Log} {sites.Count} generated sites within 4 km");
        foreach (var site in sites)
        {
            var tile = TileId.FromLv95(site.E, site.N);
            var yard = await Task.Run(() => SitePlans.YardOfTile(source, tile));
            var near = yard.OrderBy(c => Sq(c.E - site.E) + Sq(c.N - site.N)).ToList();
            foreach (var c in near.Where(c => c.Kind == SiteStockKind.Skip))
            {
                var stacks = LootTables.YardContents(c.Building, c.Index, c.Kind, LootTables.Epoch(c.Building, Now));
                GD.Print($"{Log} {c.Building} #{c.Index} skip at {F(c.E)},{F(c.N)}: {stacks.Count} stacks");
                if (stacks.Count > 0) return c;
            }
        }
        return null;
    }

    private static double Sq(double v) => v * v;

    /// <summary>Stands beside the container, facing it, on one of its long sides (B and C on the other first).</summary>
    private async Task<bool> StandAt(FootPlayer me, YardStock c)
    {
        var sides = new List<(Vector2 Out, float Off)>
        {
            (c.AxisV * (c.HalfV + 0.9f), 0f), (-c.AxisV * (c.HalfV + 0.9f), 0f),
            (c.AxisV * (c.HalfV + 0.9f), 1.2f), (-c.AxisV * (c.HalfV + 0.9f), 1.2f),
            (c.AxisU * (c.HalfU + 0.9f), 0f), (-c.AxisU * (c.HalfU + 0.9f), 0f),
        };
        if (_role != "A") sides.Reverse(0, 4);
        foreach (var (outward, off) in sides)
        {
            // the tile frame is the world's: x east, z south
            var along = new Vector2(-outward.Y, outward.X).Normalized() * off;
            double e = c.E + outward.X + along.X, n = c.N - (outward.Y + along.Y);
            var at = me.Origin!.ToWorld(e, n, c.Bottom + 0.6f);
            var face = -new Vector3(outward.X, 0, outward.Y).Normalized();
            me.PlaceAt(at, Mathf.Atan2(-face.X, -face.Z));
            me.Velocity = Vector3.Zero;
            await Until(() => me.IsOnFloor(), 5);
            await Seconds(0.6);
            if (Loot.NearestYard(me, out var faced) && faced.Index == c.Index && faced.Building == c.Building)
            {
                GD.Print($"{Log} standing {F(SiteLoot.EdgeDistance(c, me.Global.E, me.Global.N))} m from the {SiteLoot.Describe(c.Kind)}, feet {F(me.Global.Alt - c.Bottom)} m over its foot");
                return true;
            }
        }
        return false;
    }

    /// <summary>E at it: the panel opens and its contents arrive.</summary>
    private async Task<List<(int Index, ItemStack Stack)>?> Open(FootPlayer me, YardStock c)
    {
        Loot.Close();
        string? prompt = Loot.YardPromptFor(me);
        Expect(prompt?.Contains("Search the " + SiteLoot.Describe(c.Kind)) == true, $"the prompt reads \"{prompt}\"");
        if (Windowed && _role == "A")
        {
            await Seconds(0.5);
            GD.Print($"{Log} shot {Shot("prompt")}");
        }
        bool used = me.TryInteract();
        if (!Loot.IsOpen)
            GD.Print($"{Log} E did {(used ? "something else" : "nothing")}: ragdolled {me.Ragdolled}, downed {me.Downed}, fighting {me.Fighting}, aboard {me.Aboard}, ride {me.Ride}, indoors {me.Indoors}, vehicle aimed {Vehicles.VehicleReach.Current?.Vehicle.Name}, in reach {Vehicles.VehicleReach.Find(me)?.Vehicle.Name}");
        Expect(used && Loot.IsOpen, $"E opens {c.Building} #{c.Index} in the loot panel");
        if (!await Until(() => !Loot.Waiting, 10)) { Fail("its contents never arrived"); return null; }
        var stacks = Loot.OpenContents().ToList();
        GD.Print($"{Log} it holds {string.Join(", ", stacks.Select(s => $"[{s.Index}] {s.Stack.Id} x{s.Stack.Count}"))}");
        if (Windowed && _role == "A")
        {
            await Seconds(0.5);
            GD.Print($"{Log} shot {Shot("panel")}");
        }
        return stacks;
    }

    private string Target(YardStock c) => $"target {c.Building} {c.Index}";

    private async Task<YardStock?> TargetOf(string line)
    {
        // "YL A target <building> <index>"
        var w = line.Split(' ');
        int i = Array.IndexOf(w, "target");
        if (i < 0 || i + 2 >= w.Length || !int.TryParse(w[i + 2], out int index)) return null;
        string building = w[i + 1];
        var source = Loot.Source!.Invoke()!;
        var all = await Task.Run(() => SitePlans.YardAt(source, building));
        foreach (var c in all) if (c.Index == index) return c;
        return null;
    }

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        if (await PickSkip(me) is not { } skip) { Fail("no skip near the spawn holds anything this period: rerun with another --lootepoch"); return; }
        if (!await StandAt(me, skip)) { Fail($"could not stand facing {skip.Building} #{skip.Index}"); return; }
        Say(Target(skip));
        if (!await Heard("B", "open", 60)) { Fail("B never opened it"); return; }
        if (await Open(me, skip) is not { Count: > 0 } stacks) { Fail("A found it empty"); return; }

        var first = stacks[0];
        int before = CountOf(first.Stack.Id);
        Loot.Take(first.Index);
        Expect(await Until(() => !Loot.Waiting && !Loot.OpenContents().Any(s => s.Index == first.Index), 8), "A's take was granted");
        Expect(CountOf(first.Stack.Id) - before == first.Stack.Count, $"A's pack gained {CountOf(first.Stack.Id) - before} {first.Stack.Id} of {first.Stack.Count}");
        Say($"took {first.Index} {(int)first.Stack.Id} {stacks.Count}");
        if (!await Heard("B", "seen", 30)) { Fail("B never saw it go"); return; }

        // a late joiner: told again once it says hello, A still online
        if (!await Heard("C", "hello", 150)) { Fail("C never joined"); return; }
        Say(Target(skip));
        await Seconds(0.5);
        Say($"took {first.Index} {(int)first.Stack.Id} {stacks.Count}");
        if (!await Heard("C", "seen", 90)) { Fail("C never searched it"); return; }
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "target", 120)) { Fail("A never named a skip"); return; }
        if (await TargetOf(_heard.Last(l => l.Contains("YL A target"))) is not { } skip) { Fail("B cannot find A's skip from the tile"); return; }
        if (!await StandAt(me, skip)) { Fail($"B could not stand facing {skip.Building} #{skip.Index}"); return; }
        if (await Open(me, skip) is not { } stacks) return;
        Expect(stacks.Count > 0, "B sees it hold something too");
        Say("open");
        if (!await Heard("A", "took", 60)) { Fail("A never took"); return; }
        var took = _heard.Last(l => l.Contains("YL A took")).Split(' ');
        int gone = int.Parse(took[^3], CultureInfo.InvariantCulture);
        Expect(await Until(() => !Loot.OpenContents().Any(s => s.Index == gone), 5),
            "B's open panel lost the stack A took, without B doing anything");
        Expect(Loot.OpenContents().Count() == stacks.Count - 1, $"the rest still offered to B ({Loot.OpenContents().Count()} of {stacks.Count})");
        Say("seen");
    }

    private async Task RunC(FootPlayer me)
    {
        bool told = false;
        for (int i = 0; i < 40 && !told; i++)
        {
            Say("hello");
            told = await Heard("A", "took", 3);
        }
        if (!told) { Fail("A never told the late joiner"); return; }
        if (await TargetOf(_heard.Last(l => l.Contains("YL A target"))) is not { } skip) { Fail("C cannot find A's skip from the tile"); return; }
        var took = _heard.Last(l => l.Contains("YL A took")).Split(' ');
        int gone = int.Parse(took[^3], CultureInfo.InvariantCulture), total = int.Parse(took[^1], CultureInfo.InvariantCulture);
        if (!await StandAt(me, skip)) { Fail($"C could not stand facing {skip.Building} #{skip.Index}"); return; }
        if (await Open(me, skip) is not { } stacks) return;
        Expect(!stacks.Any(s => s.Index == gone), $"joining late, C finds stack {gone} gone");
        Expect(stacks.Count == total - 1, $"and the rest there ({stacks.Count} of {total})");
        Say("seen");
    }
}
