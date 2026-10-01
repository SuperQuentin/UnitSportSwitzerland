using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>tools/dropcheck.sh</c> (#206): items dropped, thrown and picked up over a real connection, two
/// clients of one dedicated server on loopback.
///
/// <para>
/// <c>--dropcheck thrower</c> drops a stone at its feet (the Q path), winds up a full-strength throw
/// of a second one through the real <see cref="ThrowAim"/> (Aim and Use held by the probe), with a
/// screenshot of the arc and the shoulder camera at full charge, then drops a stack of three energy
/// bars. It passes once all three lie settled on its side and then the bars are gone (picked up by
/// the watcher).
/// </para>
///
/// <para>
/// <c>--dropcheck watch</c> must see the thrower's wind-up arm pose, the three items settled — the
/// thrown stone far out, the other two at the thrower's feet — then steps next to the bars, must
/// find an item at hand pointed at (the outline, screenshotted) and pick it up into its pack.
/// The two first trade a ping (held items) so the wind-up is not sent while the second client's
/// link is still buried under its first terrain.
/// </para>
/// </summary>
public partial class DropCheck : Node
{
    private const double Timeout = 160;
    private readonly bool _thrower, _solo;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private readonly ItemController? _items;
    private double _t, _since = -1, _stepAt;
    private int _step;
    private bool _sawWindup, _ponged;
    private int _barsBefore;
    private ItemStack _picked;
    private Vector3 _throwerAt;

    private DropCheck(bool thrower, bool solo, Func<FootPlayer?> local, Func<Node?> players, ItemController? items)
    {
        Name = "DropCheck";
        _thrower = thrower;
        _solo = solo;
        _local = local;
        _players = players;
        _items = items;
    }

    /// <summary>"--dropcheck" on the command line: the client must use a scratch inventory, never the player's save.</summary>
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--dropcheck") >= 0;

    public static DropCheck? Create(Func<FootPlayer?> local, Func<Node?> players, ItemController? items)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--dropcheck");
        if (i < 0) return null;
        string role = i + 1 < args.Length ? args[i + 1] : "watch";
        GD.Print($"[dropcheck] role {role}");
        return new DropCheck(role == "thrower", role == "solo", local, players, items);
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    private static List<DroppedItem> Items => DroppedItems.Instance?.Items.ToList() ?? new List<DroppedItem>();

    public override void _Process(double delta)
    {
        if (delta > 0.4) GD.Print($"[dropcheck] {(_thrower ? "thrower" : "watch")}: a {delta:F1} s frame at t={_t:F0}, {Items.Count} items, pointed {Highlight.Pointed?.Name ?? "-"}");
        if (_solo)
        {
            _t += delta;
            if (_t > 90) Finish(false, $"timed out at step {_step}");
            else if ((_local() ?? GetViewport().GetCamera3D()?.GetParent() as FootPlayer) is { } self && _items != null) Solo(self, _items);
            return;
        }
        if (!Multiplayer.HasMultiplayerPeer()
            || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;
        _t += delta;
        if ((int)(_t / 3) != (int)((_t - delta) / 3))
            GD.Print($"[dropcheck] {(_thrower ? "thrower" : "watch")} t={_t:F0} step {_step}: "
                + string.Join(", ", Items.Select(i => $"{i.Label}{(i.Settled ? "" : " (falling)")} y={i.GlobalPosition.Y:F1}")));
        if (_t > Timeout) Finish(false, $"timed out at step {_step}");
        else if (_local() is { } me && _items != null)
        {
            if (_since < 0)
            {
                if (Other() == null || !me.IsOnFloor()) return;
                _since = _t;
                GD.Print("[dropcheck] the other player is here");
            }
            if (_thrower) Throw(me, _items);
            else Watch(me, _items);
        }
    }

    private void Next(string what)
    {
        GD.Print($"[dropcheck] {(_solo ? "solo" : _thrower ? "thrower" : "watch")}: {what}");
        _step++;
        _stepAt = _t;
    }

    private double InStep => _t - _stepAt;

    private void Shot(string name)
    {
        var image = GetViewport().GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://test_output"));
        image.SavePng(ProjectSettings.GlobalizePath($"res://test_output/{name}.png"));
    }

    // ---- thrower -------------------------------------------------------------------------------

    private void Throw(FootPlayer me, ItemController items)
    {
        var inv = items.Inventory;
        switch (_step)
        {
            // The first seconds of a join stream terrain to both clients, and everything reliable to the
            // second one can arrive ten seconds late and all at once (a wind-up and its release in the
            // same frame). So a ping first: the bars in hand, until the watcher answers holding a stone.
            case 0 when _t - _since > 3:
                inv.Put(0, new ItemStack(ItemId.Stone, 2));
                inv.Put(1, new ItemStack(ItemId.EnergyBar, 3));
                inv.Select(1);
                Next("ping: bars in hand, waiting for the watcher's stone");
                break;
            case 1 when Other()?.HeldItemId == (int)ItemId.Stone && InStep > 1:
                inv.Select(0);
                Next($"pong after {InStep:F1} s: the watcher has caught up");
                break;
            case 2 when InStep > 1:
                items.DropSlot(me, 0, all: false);
                Next("one stone dropped");
                break;
            case 3 when InStep > 1:
                // no waiting on the server: the dropper sees it fall at once
                if (Items.Count == 0) { Finish(false, "the dropped stone was not in the world at once"); return; }
                items.ForceAim = true;
                me.LookPitch = 0.22f;   // a little up: the arc reads better against the sky than into the ground
                Next("aiming");
                break;
            case 4 when InStep > 0.8 && items.Throw.Active:
                items.ForceUse = true;
                items.Throw.BeginCharge();
                Next($"winding up (shoulder cam {me.ThrowAim:F1}, item action {me.ItemAction})");
                break;
            // held at full strength until the watcher shows (a rope in its hand) that it saw the wind-up
            case 5 when InStep > ThrowAim.ChargeTime + 0.5 && (Other()?.HeldItemId == (int)ItemId.Rope || InStep > 30):
                Shot("dropcheck_aim");
                Next($"full charge: power {items.Throw.Power:F2}, shake {items.Throw.Shake:F4}, fov {me.Camera.Fov:F1}");
                break;
            case 6 when InStep > 0.3:
                items.ForceUse = false;
                Next("let go");
                break;
            case 7 when InStep > 0.4:
                items.ForceAim = false;
                if (inv[0].Count != 0) Finish(false, $"stone slot holds {inv[0].Count} after drop and throw");
                Next($"thrown (release power {items.Throw.ReleasePower:F2})");
                break;
            case 8 when InStep > 1.5:
                inv.Select(1);
                items.DropSlot(me, 1, all: true);
                Next("three bars dropped as a stack");
                break;
            case 9 when Items.Count == 3 && Items.All(i => i.Settled && !i.Proxy):
            {
                foreach (var i in Items)
                {
                    float ground = DroppedItems.GroundHeight?.Invoke(i.GlobalPosition) ?? i.GlobalPosition.Y;
                    GD.Print($"[dropcheck] thrower: {i.Label} at {i.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m, {i.GlobalPosition.Y - ground:F2} m above the ground");
                    if (Mathf.Abs(i.GlobalPosition.Y - ground) > 1.5f) { Finish(false, $"{i.Label} is not on the ground"); return; }
                }
                var far = Items.Where(i => i.GlobalPosition.DistanceTo(me.GlobalPosition) > 7f).ToList();
                if (far.Count != 1 || far[0].GlobalPosition.DistanceTo(me.GlobalPosition) > 80f) { Finish(false, "the thrown stone did not land 7-80 m out"); return; }
                Next("all settled here, the thrown one on the ground");
                break;
            }
            case 10 when Other() != null && Items.Count == 2:
                Finish(true, "the watcher picked one up");
                break;
        }
    }

    // ---- solo (offline: no link to wait on) ------------------------------------------------------

    /// <summary><c>--dropcheck solo</c>: drop the bars, look down at them, the outline and the prompt in a picture, pick them up.</summary>
    private void Solo(FootPlayer me, ItemController items)
    {
        var inv = items.Inventory;
        switch (_step)
        {
            case 0 when _t > 8:
                inv.Put(1, new ItemStack(ItemId.EnergyBar, 3));
                inv.Select(1);
                _barsBefore = Count(inv, ItemId.EnergyBar) - 3;
                items.DropSlot(me, 1, all: true);
                Next($"bars dropped from {me.GlobalPosition}");
                break;
            case 1 when Items.Count > 0 && InStep > 0.5:   // the --ride harness walks on: catch them as it passes
            {
                // they land a step ahead of the walking body; look down at them
                me.LookPitch = -0.75f;
                Next($"looking down at them: me {me.GlobalPosition}, bars {Items[0].GlobalPosition}");
                break;
            }
            case 2 when InStep > 0.35:
                if (Highlight.Pointed is not DroppedItem { } pointed)
                {
                    Finish(false, $"nothing pointed at; the bars are {Items[0].GlobalPosition.DistanceTo(me.GlobalPosition):F1} m away");
                    return;
                }
                Shot("dropcheck_solo");
                items.PickUp(pointed);
                Next($"pointed at {pointed.Label}, picking up");
                break;
            case 3 when Count(inv, ItemId.EnergyBar) == _barsBefore + 3 && Items.Count == 0:
                Finish(true, "dropped, pointed at and picked up offline");
                break;
        }
    }

    // ---- watcher -------------------------------------------------------------------------------

    private void Watch(FootPlayer me, ItemController items)
    {
        if (Other() is not { } other) return;
        if (other.DrawnArmPose == Avatar.ItemArmPose.ThrowWindup && !_sawWindup)
        {
            _sawWindup = true;
            items.Inventory.Put(Inventory.HotbarSize - 2, new ItemStack(ItemId.Rope, 1));
            items.Inventory.Select(Inventory.HotbarSize - 2);
            GD.Print("[dropcheck] watch: saw the wind-up arm pose");
        }
        // pong: the thrower's bars seen in its hand, a stone in ours
        if (!_ponged && other.HeldItemId == (int)ItemId.EnergyBar)
        {
            _ponged = true;
            items.Inventory.Put(Inventory.HotbarSize - 1, new ItemStack(ItemId.Stone, 1));
            items.Inventory.Select(Inventory.HotbarSize - 1);
            GD.Print("[dropcheck] watch: pong");
        }
        switch (_step)
        {
            case 0 when Items.Count == 3 && Items.All(i => i.Settled):
            {
                _throwerAt = other.GlobalPosition;
                var far = Items.Where(i => i.GlobalPosition.DistanceTo(_throwerAt) is > 7f and < 80f).ToList();
                var near = Items.Where(i => i.GlobalPosition.DistanceTo(_throwerAt) < 4f).ToList();
                foreach (var i in Items) GD.Print($"[dropcheck] watch: {i.Label} at {i.GlobalPosition.DistanceTo(_throwerAt):F1} m from the thrower");
                if (!_sawWindup) { Finish(false, "never saw the wind-up pose"); return; }
                if (far.Count != 1 || far[0].Stack.Id != ItemId.Stone) { Finish(false, "the thrown stone is not far out"); return; }
                if (near.Count != 2) { Finish(false, "the dropped ones are not at the thrower's feet"); return; }
                Next("three items settled, the thrown one far out");
                break;
            }
            case 1:
            {
                var bars = Items.FirstOrDefault(i => i.Stack.Id == ItemId.EnergyBar);
                if (bars == null) { Finish(false, "the bars are gone"); return; }
                _barsBefore = Count(items.Inventory, ItemId.EnergyBar);
                // a step behind them along the view, looking down at them: the outline in the picture
                var view = me.Camera.GlobalTransform.Basis;
                var ahead = new Vector3(-view.Z.X, 0, -view.Z.Z).Normalized();
                me.GlobalPosition = bars.GlobalPosition - ahead * 1.2f + Vector3.Up * 0.5f;
                me.Velocity = Vector3.Zero;
                me.LookPitch = -0.55f;
                Next("stepped next to the bars");
                break;
            }
            case 2 when InStep > 2:
                // the dropped stone lies right beside the bars: whichever of the two the view finds
                if (Highlight.Pointed is not DroppedItem { } pointed || pointed.GlobalPosition.DistanceTo(me.GlobalPosition) > DroppedItems.Reach)
                {
                    Finish(false, $"pointed at {Highlight.Pointed?.Name ?? "nothing"}, not an item at hand");
                    return;
                }
                _picked = pointed.Stack;
                _barsBefore = Count(items.Inventory, _picked.Id);
                Shot("dropcheck_point");
                items.PickUp(pointed);
                Next($"pointed at {pointed.Label}, picking up");
                break;
            case 3 when Count(items.Inventory, _picked.Id) == _barsBefore + _picked.Count && Items.Count == 2:
                Finish(true, $"picked up {_picked.Id} x{_picked.Count}");
                break;
        }
    }

    private static int Count(Inventory inv, ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++)
            if (inv[i].Id == id) n += inv[i].Count;
        return n;
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[dropcheck] RESULT: {(ok ? "ok" : "FAILED")} ({(_solo ? "solo" : _thrower ? "thrower" : "watch")}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
