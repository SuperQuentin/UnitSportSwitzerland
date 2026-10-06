using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Flats' front doors (#557, <see cref="InnerDoorPlan"/>): a real leaf in the doorway off the
/// landing, open or shut as the server says, like a street door but with no portal behind it.
/// Some are locked (<see cref="InnerDoorPlan.Locked"/>): E opens the lock dial
/// (<see cref="Loot.LootService.PickOther"/>), the server checks the numbers
/// (<see cref="InnerDoorLock"/>) and the door stays unlocked for everyone until the server restarts.
///
/// <para>
/// Controls: E / Y at the door opens or shuts it, or starts the dial on a locked one; in VR, grip
/// the door (<see cref="TryInsideByHand"/>). The dial is the lockers' (A/D, the stick, the mouse).
/// </para>
/// </summary>
public partial class InteriorManager
{
    /// <summary>How near a flat's doorway a player must stand to work its door, m.</summary>
    private const float InnerDoorReach = 1.4f;
    /// <summary>A VR hand this near the doorway grips the door, m.</summary>
    private const float HandInnerReach = 0.6f;

    /// <summary>Open doors, (plan key, door index), as the server says.</summary>
    private readonly HashSet<(string Key, int Door)> _innerOpen = new();
    /// <summary>Locked doors somebody cracked.</summary>
    private readonly HashSet<(string Key, int Door)> _innerUnlocked = new();
    /// <summary>Client: how far each leaf has swung, for the ones moving.</summary>
    private readonly Dictionary<(string Key, int Door), float> _innerSwing = new();
    /// <summary>Client: the building whose doors were last asked for.</summary>
    private string? _innerAsked;
    private (string Key, int Door)? _innerPicking;

    private static float InnerSwingSeconds => DoorLink.SwingSecondsFor(InnerDoorWidth);
    private const float InnerDoorWidth = 0.9f;

    /// <summary>Where a flat's doorway is in its plan's frame, and which way is out of the flat.</summary>
    private static (Vector3 At, Vector3 Out) InnerDoorway(InteriorLayout l, InnerDoorPlan d)
    {
        var r = l.Floors[d.Floor].Rooms[d.Room];
        float y = l.FloorY(d.Floor);
        return d.Side switch
        {
            Side.Front => (new Vector3(d.Center, y, r.Z0), new Vector3(0, 0, -1)),
            Side.Back => (new Vector3(d.Center, y, r.Z1), new Vector3(0, 0, 1)),
            Side.Left => (new Vector3(r.X0, y, d.Center), new Vector3(-1, 0, 0)),
            _ => (new Vector3(r.X1, y, d.Center), new Vector3(1, 0, 0)),
        };
    }

    /// <summary>The flat door nearest a point of the plan on its floor, within <paramref name="reach"/>.</summary>
    private static int InnerDoorAt(InteriorLayout l, Vector3 local, float reach)
    {
        int floor = FloorAt(l, local.Y), best = -1;
        float bestD = reach;
        for (int i = 0; i < l.InnerDoors.Count; i++)
        {
            if (l.InnerDoors[i].Floor != floor) continue;
            var (at, _) = InnerDoorway(l, l.InnerDoors[i]);
            float d = new Vector2(at.X - local.X, at.Z - local.Z).Length();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public bool InnerDoorOpen(string key, int door) => _innerOpen.Contains((key, door));
    public bool InnerDoorLocked(InteriorLayout l, int door) => l.InnerDoors[door].Locked && !_innerUnlocked.Contains((l.Key, door));

    private bool TryInnerDoor(FootPlayer p, Vector3 local) => WorkInnerDoor(p, InnerDoorAt(_current!, local, InnerDoorReach));

    private bool TryInnerDoorByHand(FootPlayer p, Vector3 hand) => WorkInnerDoor(p, InnerDoorAt(_current!, hand, HandInnerReach));

    private bool WorkInnerDoor(FootPlayer p, int door)
    {
        if (door < 0) return false;
        var l = _current!;
        if (InnerDoorLocked(l, door))
        {
            if (Loot.LootService.Instance is not { } loot) return true;
            string key = l.Key;
            _innerPicking = (key, door);
            loot.PickOther(p, "flat door", InnerDoorLock.Combination(key, door), 1.6f, combo =>
            {
                if (Online) RpcId(1, MethodName.RequestInnerPick, key, door, combo);
                else ServeInnerPick(MyId, key, door, combo);
            });
            return true;
        }
        AskInnerDoor(l.Key, door, !InnerDoorOpen(l.Key, door));
        return true;
    }

    private string? InnerDoorPrompt(Vector3 local)
    {
        int door = InnerDoorAt(_current!, local, InnerDoorReach);
        if (door < 0) return null;
        if (InnerDoorLocked(_current!, door)) return InputHints.Prompt(PlayerInput.InteractMount, "Pick the lock");
        return InputHints.Prompt(PlayerInput.InteractMount, InnerDoorOpen(_current!.Key, door) ? "Close the door" : "Open the door");
    }

    private void AskInnerDoor(string key, int door, bool open)
    {
        if (Online) RpcId(1, MethodName.RequestInnerDoor, key, door, open);
        else ServeInnerDoor(MyId, key, door, open);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestInnerDoor(string key, int door, bool open)
    {
        if (!Multiplayer.IsServer()) return;
        ServeInnerDoor(Multiplayer.GetRemoteSenderId(), key, door, open);
    }

    /// <summary>Server (or offline): opens or shuts a flat's door for someone at it, if it is not locked.</summary>
    private void ServeInnerDoor(long sender, string key, int door, bool open)
    {
        if (!_cache.TryGetValue(key, out var l) || door < 0 || door >= l.InnerDoors.Count) return;
        if (Online && SpaceOf(sender) != key) return;
        if (LocalOf(sender, l) is { } at && InnerDoorAt(l, at, InnerDoorReach + ServerLiftSlack) != door)
        {
            Refuse(sender, "Too far from the door.");
            return;
        }
        if (InnerDoorLocked(l, door)) { Refuse(sender, "This door is locked."); return; }
        if (InnerDoorOpen(key, door) != open) BroadcastInner(key, door, open, false);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestInnerPick(string key, int door, int[] combo)
    {
        if (!Multiplayer.IsServer()) return;
        ServeInnerPick(Multiplayer.GetRemoteSenderId(), key, door, combo);
    }

    /// <summary>Server (or offline): the dial's numbers for a locked flat door. Right: unlocked and swung open for everyone.</summary>
    private void ServeInnerPick(long sender, string key, int door, int[] combo)
    {
        if (!_cache.TryGetValue(key, out var l) || door < 0 || door >= l.InnerDoors.Count) return;
        if (Online && SpaceOf(sender) != key) return;
        if (!InnerDoorLock.Opens(key, door, combo))
        {
            GD.Print($"[flatdoor] peer {sender} gave a wrong combination for {key} door {door}");
            if (sender == MyId) InnerPickRefused(); else RpcId(sender, MethodName.InnerPickRefused);
            return;
        }
        GD.Print($"[flatdoor] {key} door {door} cracked by peer {sender}");
        BroadcastInner(key, door, true, true);
    }

    private void BroadcastInner(string key, int door, bool open, bool unlocked)
    {
        SetInnerDoor(key, door, open, unlocked);
        if (!Online) return;
        foreach (var (peer, space) in _spaces)
            if (space == key && peer != MyId) RpcId(peer, MethodName.SetInnerDoor, key, door, open, unlocked);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetInnerDoor(string key, int door, bool open, bool unlocked)
    {
        bool was = _innerOpen.Contains((key, door));
        if (open) _innerOpen.Add((key, door)); else _innerOpen.Remove((key, door));
        if (unlocked) _innerUnlocked.Add((key, door));
        if (_innerPicking == (key, door) && unlocked)
        {
            _innerPicking = null;
            Loot.LootService.Instance?.OtherPicked();
        }
        if (was != open && _presenting && _built.TryGetValue(key, out var node) && door < node.Layout.InnerDoors.Count)
        {
            var (at, _) = InnerDoorway(node.Layout, node.Layout.InnerDoors[door]);
            _sounds?.Door(node.GlobalTransform * (at + Vector3.Up * 1.0f), open);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void InnerPickRefused() => Loot.LootService.Instance?.OtherRefused();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestInnerDoors(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var (k, door) in _innerOpen.Union(_innerUnlocked).Distinct())
            if (k == key)
                RpcId(sender, MethodName.SetInnerDoor, k, door, _innerOpen.Contains((k, door)), _innerUnlocked.Contains((k, door)));
    }

    /// <summary>Client, every frame: leaves swing toward what the server says; a building just entered is asked about its doors.</summary>
    private void PresentInnerDoors(double delta)
    {
        if (_current is { InnerDoors.Count: > 0 } here && _innerAsked != here.Key)
        {
            _innerAsked = here.Key;
            if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestInnerDoors, here.Key);
        }
        foreach (var (plan, node) in _built)
        {
            var l = node.Layout;
            for (int i = 0; i < l.InnerDoors.Count; i++)
            {
                float target = _innerOpen.Contains((plan, i)) ? 1f : 0f;
                float now = _innerSwing.GetValueOrDefault((plan, i));
                if (now == target && node.InnerSwing(i) == target) continue;
                now = Mathf.MoveToward(now, target, (float)delta / InnerSwingSeconds);
                _innerSwing[(plan, i)] = now;
                node.SetInnerDoor(i, now);
            }
        }
    }
}
