using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// <c>--brprobe A|B</c> with <c>--connect</c> (driven by <c>tools/brcheck.sh</c>): one whole match over loopback
/// against a server started with <c>--admin-password brcheck --brpace 0.05</c> (a match in about a minute).
/// <list type="bullet">
/// <item>A logs in as admin, opens a lobby and starts the match once B has joined.</item>
/// <item>Both are dropped in the region with an empty pack, a knife and bandages; the travel menu is locked.</item>
/// <item>B stands outside the zone until it hurts, then next to A.</item>
/// <item>A stabs B until it is down.</item>
/// <item>B must stay down (eliminated, no revive), spectate, and see the kill in its feed.</item>
/// <item>Both see A win; after the results both are back where they started, with their own packs.</item>
/// </list>
/// Scratch inventories; screenshots in <c>test_output/br_*.png</c>.
/// </summary>
public partial class BrProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--brprobe");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public BrProbe(ItemController items) => _items = items;
    public BrProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private BrManager? Br => BrManager.Instance;
    private FootPlayer? Me => GetParent().GetNodeOrNull<FootPlayer>("Players/" + Multiplayer.GetUniqueId());

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me is { } m && m.IsOnFloor() && Br != null, 150))
        { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(2.0);
        var start = Me!.GlobalPosition;
        if (_role == "A") await RunA(); else await RunB();
        if (_failures == 0) await Released(start);
        GD.Print(_failures == 0 ? $"[br {_role}] RESULT: ok" : $"[br {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA()
    {
        Chat!.Send("/login brcheck");
        await Until(() => Permissions.IsAdmin, 10);
        Expect(Permissions.IsAdmin, "logged in as admin");
        Chat.Send("/br open 5 short");
        if (!await Until(() => Br!.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Chat.Send("/br join");
        for (int i = 0; i < 60 && Br!.State.Entrants.Count < 2; i++)
        {
            Say("lobby");
            await Seconds(2.0);
        }
        if (Br!.State.Entrants.Count < 2) { Fail("B never joined"); return; }
        Chat.Send("/br start");
        if (!await Dropped()) return;

        // where the last circle closes: inside every circle until the very end
        var me = Me!;
        var c = Br.Zone!.CentreOf(ZoneSchedule.Phases);
        Br.Teleport(Br.State.AreaE + c.X, Br.State.AreaN + c.Y, "final zone");
        await Seconds(3.0);

        // wait for B next to us, then stab it until it goes down
        Say(Fmt($"posA {me.GlobalPosition.X:F2} {me.GlobalPosition.Y:F2} {me.GlobalPosition.Z:F2}"));
        if (!await Until(() => Said("B", "near"), 90)) { Expect(false, "B came near"); return; }
        await Seconds(1.5);
        var b = GetParent().GetNodeOrNull<FootPlayer>("Players/" + PeerOf("B"));
        if (b == null) { Expect(false, "B's body is here"); return; }
        int knife = SlotOf(ItemId.Knife);
        _items.Inventory.Select(knife);
        for (int i = 0; i < 12 && b.Down == 0; i++)
        {
            var dir = (b.GlobalPosition + Vector3.Up * 1.1f - me.EyePosition).Normalized();
            me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            await Seconds(0.2);
            _items.UseSlot(me, knife);
            await Seconds(0.6);
        }
        Expect(await Until(() => b.Down != 0, 3), "B is down");
        await Ended();
    }

    private async Task RunB()
    {
        if (!await Until(() => Br!.State.Phase == BrPhase.Lobby, 90)) { Fail("no lobby"); return; }
        Chat!.Send("/br join");
        if (!await Dropped()) return;

        // outside the zone until it hurts
        var me = Me!;
        var s = Br!.State;
        Br.Teleport(s.AreaE + s.Side * 0.9, s.AreaN, "outside");
        await Seconds(3.0);
        float hp = me.Health;
        bool hurt = await Until(() => me.Health < hp - 0.9f, 60);
        Expect(hurt, $"the zone hurts outside it ({hp:F1} -> {me.Health:F1}, phase {Br.ZoneNow?.Phase})");
        Snap("b_outside");

        // next to A
        if (!await Until(() => _heard.Any(l => l.Contains("BR A posA")), 60)) { Expect(false, "A reported"); return; }
        var p = _heard.Last(l => l.Contains("BR A posA")).Split("posA ")[1].Split(' ');
        var a = new Vector3(Float(p[0]), Float(p[1]), Float(p[2]));
        var (e, n) = Br.Origin!.ToLv95(a);
        Br.Teleport(e, n + 1.3, "next to A");
        await Seconds(3.0);
        me.LookYaw = Mathf.Pi;
        Say("near");

        if (!await Until(() => me.Eliminated, 40)) { Expect(false, $"B was eliminated (health {me.Health:F1})"); return; }
        Expect(true, "B was eliminated");
        await Seconds(5.0);
        Expect(me.Eliminated && me.KnockedOut, "still down 5 s later: no revive in a match");
        Expect(Br.Feed.Any(f => f.Text.Contains("✕")), $"the kill is in the feed ({string.Join(" | ", Br.Feed.Select(f => f.Text))})");
        await Ended();
    }

    /// <summary>The countdown runs, then this player lands in the region with a fresh match pack.</summary>
    private async Task<bool> Dropped()
    {
        if (!await Until(() => Br!.InMatch, 60)) { Fail("never dropped"); return false; }
        Expect(_items.Inventory.InMatch && _items.Inventory.Contains(ItemId.Knife) && !_items.Inventory.Contains(ItemId.Binoculars),
            "a match pack: a knife, nothing from free roam");
        Expect(Permissions.RidesLocked, "the travel menu is locked");
        await Seconds(4.0);
        var s = Br!.State;
        var (e, n) = Br.Origin!.ToLv95(Me!.GlobalPosition);
        Expect(s.Area.Contains(e, n), Fmt($"landed in the region ({e:F0}/{n:F0} in {s.AreaName} {s.AreaE:F0}/{s.AreaN:F0})"));
        Expect(await Until(() => Br.MapTexture != null, 40), "the region's map is built (minimap)");
        Br.Waypoint = new Vector2(400, 300);
        await Seconds(1.0);
        Snap($"{_role.ToLowerInvariant()}_dropped");
        if (_role == "A")
        {
            Expect(Br.ToggleMap() && Br.MapOpen, "M opens the match map");
            await Seconds(1.0);
            Snap("a_map");
            Br.ToggleMap();
            Expect(!Br.MapOpen, "M closes it");
        }
        return true;
    }

    private async Task Ended()
    {
        if (!await Until(() => Br!.State.Phase == BrPhase.Ended, 30)) { Expect(false, "the match ended"); return; }
        var s = Br!.State;
        Expect(s.Winner == PeerOf("A") && s.Find(s.Winner)?.Kills == 1, $"A won with one kill (winner {s.Find(s.Winner)?.Name})");
        await Seconds(1.0);
        Snap($"{_role.ToLowerInvariant()}_results");
    }

    /// <summary>After the results: back where the player stood, with its own pack.</summary>
    private async Task Released(Vector3 start)
    {
        if (!await Until(() => !Br!.InMatch, 40)) { Expect(false, "released after the results"); return; }
        Expect(!_items.Inventory.InMatch && _items.Inventory.Contains(ItemId.Binoculars) && !_items.Inventory.Contains(ItemId.Knife),
            "the free-roam pack is back, the knife is gone");
        Expect(!Permissions.RidesLocked, "the travel menu is open again");
        await Seconds(5.0);
        var me = Me!;
        Expect(!me.Eliminated && !me.KnockedOut, "standing again");
        float d = new Vector2(me.GlobalPosition.X - start.X, me.GlobalPosition.Z - start.Z).Length();
        Expect(d < 30f, $"back where it started ({d:F0} m away)");
    }

    private long PeerOf(string role)
    {
        string name = "BR" + role;
        return Br?.State.Entrants.FirstOrDefault(e => e.Name == name)?.Peer ?? 0;
    }

    private bool Said(string role, string what) => _heard.Any(l => l.Contains($"BR {role} {what}"));

    private void Snap(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"br_{name}.png"));
    }

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    private static string Fmt(FormattableString s) => FormattableString.Invariant(s);
    private static float Float(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private void Say(string what)
    {
        GD.Print($"[br {_role}] say {what}");
        Chat?.Send($"BR {_role} {what}");
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
        GD.Print($"[br {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[br {_role}] RESULT: FAILED - {why}");
        _failures++;
        GetTree().Quit(1);
    }
}
