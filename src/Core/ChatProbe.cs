using Godot;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// The scaffolding the multiplayer probes share (<c>--plantcheck</c>, <c>--gunshot</c>, <c>--placedcheck</c>,
/// <c>--useanim</c>, <c>--lootsynccheck</c>, <c>--locksynccheck</c>, <c>--econcheck</c>, <c>--photocheck</c>,
/// <c>--bankcheck</c>, <c>--brprobe</c>, <c>--pvpcheck</c>, <c>--birdnetcheck</c>):
/// clients coordinate through chat lines (<c>"&lt;prefix&gt; &lt;role&gt; &lt;what&gt;"</c>), wait on conditions,
/// count failed expectations and log as <c>[tag role]</c>. The scripts in <c>tools/</c> grep those lines.
/// </summary>
public abstract partial class ChatProbe : Node
{
    private readonly string _tag, _prefix, _shots;
    protected readonly ItemController _items;
    protected readonly List<string> _heard = new();
    /// <summary>A, B, C… from the command line; empty for a probe that runs alone.</summary>
    protected string _role = "";
    protected int _failures;

    /// <param name="tag">The log tag: <c>[tag role]</c>.</param>
    /// <param name="prefix">The chat prefix of <see cref="Say"/> and <see cref="Heard"/>.</param>
    /// <param name="shots">The file prefix of <see cref="Shot"/> in <c>test_output/</c>.</param>
    protected ChatProbe(ItemController items, string tag, string prefix = "", string shots = "")
    {
        _items = items;
        _tag = tag;
        _prefix = prefix;
        _shots = shots;
    }

    /// <summary>The upper-cased word after <paramref name="flag"/>, or null when the flag is absent.</summary>
    public static string? RoleArg(string flag) => CmdArgs.Value(flag)?.ToUpperInvariant();

    protected string Log => _role.Length > 0 ? $"[{_tag} {_role}]" : $"[{_tag}]";
    /// <summary>Echo each <see cref="Say"/> to the log (some scripts wait for the echo).</summary>
    protected virtual bool EchoSay => true;
    /// <summary>The dash of the failure line, as each probe always wrote it.</summary>
    protected virtual string Dash => "—";

    protected ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    /// <summary>Our player: by default the one the camera rides.</summary>
    protected virtual FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    /// <summary>Waits for chat, a connection and our player on the ground (plus <paramref name="also"/>), then listens to chat. False after a <see cref="Fail"/>.</summary>
    protected async Task<bool> Joined(double seconds, Func<bool>? also = null)
    {
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor() && (also?.Invoke() ?? true), seconds))
        {
            Fail("no player on the ground");
            return false;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        return true;
    }

    /// <summary>The RESULT line, then quit after <paramref name="linger"/> seconds.</summary>
    protected async Task Finish(double linger)
    {
        GD.Print(_failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({_failures})");
        if (linger > 0) await Seconds(linger);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    protected void Say(string what)
    {
        if (EchoSay) GD.Print($"{Log} say {what}");
        Chat?.Send($"{_prefix} {_role} {what}");
    }

    protected Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"{_prefix} {role} {what}")), seconds);

    protected async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    protected async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    protected void Expect(bool ok, string what)
    {
        GD.Print($"{Log} {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    // ---- walking into a building and standing at a piece (the shop and house-prop probes) ---------

    /// <summary>Stands outside <paramref name="door"/>, opens it with E and walks in. False (and failed) when it cannot.</summary>

    protected async Task<bool> WalkIn(FootPlayer me, DoorIndex.Entry door)
    {
        var interiors = InteriorManager.Instance!;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        if (me.Indoors) interiors.Leave(me);
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 6 && !open; attempt++)
        {
            if (!interiors.IsOpen(doorKey)) me.TryInteract();
            open = await Until(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }

    /// <summary>
    /// Stands in front of piece <paramref name="index"/> on its floor, a little to one side (A left,
    /// B right), until <paramref name="facing"/> says the player is at it. False (and failed) when never.
    /// </summary>
    protected async Task<bool> StandAt(FootPlayer me, InteriorLayout layout, InteriorNode node, int index, Func<int, bool> facing)
    {
        var f = layout.Furniture[index];
        var turn = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2);
        var front = turn * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        float sign = _role == "B" ? 1f : -1f;
        foreach (float offset in new[] { 0.3f, 0.15f, 0f, 0.45f })
        {
            var side = turn * new Vector3(sign * offset, 0, 0);
            me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, layout.FloorY(f.Floor) + 0.1f, f.Z) + front + side), Mathf.Atan2(-face.X, -face.Z));
            me.Velocity = Vector3.Zero;
            await Seconds(0.8);
            if (InteriorManager.Instance?.Current?.Key == layout.Key && facing(index)) return true;
        }
        Fail($"could not stand in front of #{index} ({f.Type})");
        return false;
    }

    /// <summary>Logs the failure and quits with 1.</summary>
    protected virtual void Fail(string why)
    {
        GD.Print($"{Log} RESULT: FAILED {Dash} {why}");
        GetTree().Quit(1);
    }

    /// <summary>Saves the view to <c>test_output/&lt;shots&gt;&lt;name&gt;.png</c>; the absolute path.</summary>
    protected virtual string Shot(string name)
    {
        System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://test_output"));
        string path = ProjectSettings.GlobalizePath($"res://test_output/{_shots}{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        return path;
    }

    protected static float Float(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first slot holding <paramref name="id"/>, or -1.</summary>
    protected int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    protected int CountOf(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    /// <summary>Items in the pack plus cash.</summary>
    protected int PackTotal()
    {
        var inv = _items.Inventory;
        int n = inv.Cash;
        for (int i = 0; i < Inventory.Size; i++) if (!inv[i].IsEmpty) n += inv[i].Count;
        return n;
    }
}
