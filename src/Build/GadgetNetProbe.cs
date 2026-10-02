using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>
/// <c>--gadgetnet A|B</c> with <c>--connect</c> (driven by <c>tools/gadgetnetcheck.sh</c>): gadgets over loopback.
/// A strings a zipline 8 m down over 30 m from where it stands and a trampoline beside it. B joins
/// after: both are in its join snapshot and drawn. A rides the zipline; B must see A's body travel
/// along the cable (its replicated position, nothing gadget-specific is sent). A takes both away; B
/// must see them go.
/// </summary>
public partial class GadgetNetProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--gadgetnet");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public GadgetNetProbe(ItemController items) => _items = items;
    public GadgetNetProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me is { } m && m.IsOnFloor() && PlacedObjects.Instance != null, 150))
        {
            GD.Print($"[gadgetnet {_role}] RESULT: FAILED - no player on the ground");
            GetTree().Quit(1);
            return;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(1.5);
        try
        {
            if (_role == "A") await RunA(Me!, PlacedObjects.Instance!);
            else await RunB(PlacedObjects.Instance!);
        }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? $"[gadgetnet {_role}] RESULT: ok" : $"[gadgetnet {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA(FootPlayer me, PlacedObjects placed)
    {
        // a run that died half way left its gadgets: take them back first (only the owner may)
        foreach (var o in placed.All.Values.Where(o => Gadgets.IsGadget(o.Kind) && o.Owner == "GadgetA").ToList())
            placed.RequestRemove(o.Id);
        await Seconds(1.0);

        var low = me.GlobalPosition;
        var high = low + new Vector3(-30f, 8f, 0);
        var (e, n) = placed.Origin.ToLv95(high);
        long zip = await Place(placed, PlacedKind.Zipline, new Transform3D(Basis.Identity, low), Gadgets.ZipPayload(e, n, high.Y));
        long tramp = await Place(placed, PlacedKind.Trampoline, new Transform3D(Basis.Identity, low + new Vector3(4f, 0, 4f)), "");
        Expect(zip > 0 && tramp > 0, "a zipline and a trampoline set down");

        // chat is not replayed to a late joiner: say it until B answers
        for (int i = 0; i < 60 && !_heard.Any(l => l.Contains("GN B ready")); i++)
        {
            Say($"placed {zip} {tramp}");
            await Heard("B", "ready", 2.5);
        }
        if (!_heard.Any(l => l.Contains("GN B ready"))) { Expect(false, "B never answered"); return; }

        me.GlobalPosition = high + Vector3.Up * 0.2f;
        await Seconds(0.1);
        _items.GadgetTool.Board(me, PlacedKind.Zipline);
        Expect(_items.GadgetTool.Riding, "on the zipline");
        Say($"riding {Multiplayer.GetUniqueId()}");
        await Until(() => !_items.GadgetTool.Riding, 15);
        Expect(new Vector2(me.GlobalPosition.X - low.X, me.GlobalPosition.Z - low.Z).Length() < 4f, "rode to the bottom");
        await Seconds(1.5);
        Say("down");
        await Heard("B", "watched", 30);

        placed.RequestRemove(zip);
        placed.RequestRemove(tramp);
        Expect(await Until(() => !placed.All.ContainsKey(zip) && !placed.All.ContainsKey(tramp), 5), "both taken away");
        Say("clean");
        await Heard("B", "gone", 30);
    }

    private async Task RunB(PlacedObjects placed)
    {
        if (!await Heard("A", "placed", 150)) { Expect(false, "A never placed"); return; }
        var ids = _heard.Last(l => l.Contains("GN A placed")).Split(' ').TakeLast(2).Select(long.Parse).ToArray();
        Expect(await Until(() => ids.All(placed.All.ContainsKey), 10), "both in the join snapshot");
        Expect(GetTree().Root.FindChild($"P{ids[0]}", true, false) is Node3D, "the zipline is drawn");
        Say("ready");

        if (!await Heard("A", "riding", 60)) { Expect(false, "A never rode"); return; }
        string peer = _heard.Last(l => l.Contains("GN A riding")).Split(' ').Last();
        var body = GetTree().Root.FindChild(peer, true, false) as Node3D;
        Expect(body != null, $"A's body is here ({peer})");
        if (body != null)
        {
            var start = body.GlobalPosition;
            float travelled = 0, highest = float.MinValue;
            double end = Time.GetTicksMsec() / 1000.0 + 10;
            while (Time.GetTicksMsec() / 1000.0 < end && !_heard.Any(l => l.Contains("GN A down")))
            {
                travelled = Mathf.Max(travelled, new Vector2(body.GlobalPosition.X - start.X, body.GlobalPosition.Z - start.Z).Length());
                highest = Mathf.Max(highest, body.GlobalPosition.Y);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Expect(travelled > 20f, $"A's body travelled {travelled:F0} m along the cable here");
        }
        Say("watched");

        if (!await Heard("A", "clean", 60)) { Expect(false, "A never cleaned up"); return; }
        Expect(await Until(() => !ids.Any(placed.All.ContainsKey), 5), "both gone here too");
        Say("gone");
    }

    private async Task<long> Place(PlacedObjects placed, PlacedKind kind, Transform3D at, string payload)
    {
        PlacedResult? result = null;
        placed.RequestPlace(kind, at, payload, r => result = r);
        await Until(() => result != null, 5);
        if (result is { Ok: false } r) GD.Print($"[gadgetnet {_role}] refused: {r.Refused}");
        return result?.Object?.Id ?? 0;
    }

    private void Say(string what)
    {
        GD.Print($"[gadgetnet {_role}] say {what}");
        Chat?.Send($"GN {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"GN {role} {what}")), seconds);

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
        GD.Print($"[gadgetnet {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
