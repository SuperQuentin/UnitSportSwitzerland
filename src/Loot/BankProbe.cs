using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--bankcheck A|B</c> with <c>--connect</c> (#213): two real clients at the nearest bank,
/// coordinated through chat lines like <see cref="LockSyncProbe"/>. A finds the bank, sees its
/// sign, is refused a deposit in the street, deposits and withdraws at the teller desk. In the
/// vault, B works a safe's dial and then submits a wrong Simon sequence: the server must refuse
/// it. A cracks the same safe through the real dial and the real Simon panel (a wrong pad first,
/// which must start it over), the server opens it; B must see the door swing on its own and the
/// same stacks. A then visits a house cellar (a shelter, a music room) for the screenshots, and
/// checks the front door cannot be used from down there. Run all three with the same
/// <c>--lootepoch N</c> and <c>--at E,N</c>; <c>tools/bankcheck.sh</c> does.
/// </summary>
public partial class BankProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--bankcheck");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly WorldOrigin _origin;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public BankProbe(ItemController items, WorldOrigin origin)
    {
        _items = items;
        _origin = origin;
    }

    public BankProbe() : this(null!, null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        string other = _role == "A" ? "B" : "A";

        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor()
            && GetParent() is ClientWorld { Stage: LoadStage.Ready }, 180)) { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        var loot = LootService.Instance!;
        var bank = Bank.Instance!;

        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut && DoorIndex.Nearest(me.GlobalPosition, 400f) != null, 60)) { Fail("no door near the spot"); return; }
        await Seconds(1.0);

        // the nearest bank to the agreed spot, so both pick the same one
        DoorIndex.Entry? found = null;
        InteriorLayout? plan = null;
        var doors = DoorIndex.All()
            .Where(d => d.Kind == BuildingKind.Commercial && new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length() < 900f)
            .OrderBy(d => new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length()).ToList();
        foreach (var d in doors)
        {
            InteriorLayout? l = null;
            try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
            if (l is not { IsBank: true } || l.Key != d.Key.ToString()) continue;
            (found, plan) = (d, l);
            break;
        }
        if (found is not { } door || plan == null) { Fail($"no bank among {doors.Count} commercial doors"); return; }
        int counter = plan.Furniture.FindIndex(f => f.Type == FurnitureType.TellerDesk);
        int safe = plan.Furniture.FindIndex(f => f.Type == FurnitureType.VaultSafe);
        GD.Print($"[bank {_role}] bank {door.Key} at {door.World}, counter #{counter}, vault safe #{safe}");
        if (counter < 0 || safe < 0) { Fail("the bank has no counter or no vault safe"); return; }

        // in the street, in front of the door: the sign is up, and no deposit goes through out here
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 4.5f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(2.0);
        await Until(() => me.IsOnFloor(), 10);
        int signs = GetTree().Root.FindChildren("BankSigns", "Node3D", true, false).Sum(s => s.GetChildCount());
        Expect(signs > 0, $"bank signs are up ({signs})");
        Shot("street");
        long start = 0;
        if (_role == "A")
        {
            await Until(() => !bank.Pending, 5);
            start = bank.Balance;
            _items.Inventory.Add(ItemId.Francs, 300);
            bank.ClaimAll();
            await Until(() => !bank.Pending, 8);
            Expect(_items.Inventory.Cash == 300 && bank.Balance == start, $"no deposit in the street (cash {_items.Inventory.Cash}, account {bank.Balance})");
        }

        // one at a time through the door: two clients toggling it at once shut it on each other
        if (_role == "B" && !await Heard("A", "inside", 240)) { Fail("A never got inside"); return; }
        if (!await WalkIn(me, interiors, door)) return;
        if (_role == "A") Say("inside");
        var layout = interiors.Current!;
        var node = interiors.CurrentNode!;
        if (layout.Key != plan.Key) { Fail($"walked into {layout.Key}, not {plan.Key}"); return; }

        if (_role == "A")
        {
            if (!await StandAt(me, layout, node, counter, f => LootService.NearestCounter(me, layout, node) == f)) return;
            Expect(loot.PromptFor(me)?.Contains("Bank counter") == true, $"the prompt offers the counter ({loot.PromptFor(me)})");
            Expect(me.TryInteract(), "E opens the counter");
            await Seconds(0.8);
            Shot("counter");
            bank.ClaimAll();
            Expect(await Until(() => !bank.Pending, 8) && _items.Inventory.Cash == 0 && bank.Balance == start + 300,
                $"300 CHF deposited at the counter (cash {_items.Inventory.Cash}, account {start} -> {bank.Balance})");
            bank.Withdraw(120);
            Expect(await Until(() => !bank.Pending, 8) && _items.Inventory.Cash == 120 && bank.Balance == start + 180,
                $"120 CHF withdrawn (cash {_items.Inventory.Cash}, account {bank.Balance})");
            bank.Withdraw(1_000_000_000);
            await Seconds(1.0);
            Expect(_items.Inventory.Cash == 120, "no overdraft");
            me.TryInteract();   // closes the counter
            await Seconds(0.5);
        }

        if (!await StandAt(me, layout, node, safe, f => LootService.NearestContainer(me, layout, node) == f)) return;
        bool both = false;
        for (int tries = 0; tries < 40 && !both; tries++) { Say("vault"); both = await Heard(other, "vault", 2); }
        if (!both) { Fail($"{other} never came to the vault"); return; }
        Say("vault");
        await Seconds(1.0);

        long epoch = LootTables.Epoch(layout.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var combo = LootTables.Combination(layout.Key, safe, epoch, FurnitureType.VaultSafe);
        var seq = LootTables.SimonSequence(layout, safe, epoch);
        var dial = loot.LockUi!;
        var simon = loot.Simon!;
        GD.Print($"[bank {_role}] vault safe: dial {string.Join("-", combo)}, Simon {seq.Length} long");

        if (_role == "B")
        {
            Expect(me.TryInteract() && dial.IsOpen, "E on the vault safe opens the dial");
            loot.SubmitCombination(combo);
            Expect(simon.IsOpen && !dial.IsOpen && simon.Length == seq.Length, "the right dial leads to the Simon panel, as long as the safe is rich");
            Shot("simon");
            loot.SubmitSimon(seq.Select(p => (p + 1) % 4).ToArray());
            await Seconds(2.0);
            Expect(!loot.IsUnlocked(layout.Key, safe) && !node.IsLockOpen(safe) && !simon.IsOpen,
                "the server refuses a wrong Simon sequence: still locked");
            Say("refused");

            if (!await Heard("A", "cracked", 240)) { Fail("A never cracked it"); return; }
            Expect(await Until(() => node.IsLockOpen(safe) && loot.IsUnlocked(layout.Key, safe), 5), "B saw the vault safe swing open");
            await Seconds(1.2);
            Shot("vault_open");
            Expect(me.TryInteract() && loot.IsOpen, "B searches it straight away");
            await Until(() => !loot.Waiting, 10);
            string seen = string.Join(",", loot.OpenContents().Select(c => $"{c.Stack.Id}x{c.Stack.Count}"));
            string theirs = _heard.Last(l => l.Contains("BK A cracked"));
            int lb = theirs.IndexOf('['), rb = theirs.LastIndexOf(']');
            theirs = lb >= 0 && rb > lb ? theirs[(lb + 1)..rb] : "?";
            Expect(seen == theirs, $"B sees what A saw: {seen} / {theirs}");
            loot.Close();
            Say("done");
        }
        else
        {
            if (!await Heard("B", "refused", 90)) { Fail("B never tried a wrong sequence"); return; }
            Expect(me.TryInteract() && dial.IsOpen, "A opens the dial");
            for (int stage = 0; stage < combo.Length; stage++)
            {
                int dir = LockPickUi.Direction(stage);
                bool clicked = false;
                for (int f = 0; f < 2400 && !clicked; f++)
                {
                    dial.Turn(dir * 0.3f);
                    clicked = dial.Clicking;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                bool dropped = await Until(() => dial.Stage > stage || simon.IsOpen, 3);
                if (!(clicked && dropped)) { Fail($"tumbler {stage + 1} would not drop"); return; }
            }
            Expect(await Until(() => simon.IsOpen, 3), "the dial done, the Simon panel opens");
            // a wrong pad in the first round starts it over
            await Until(() => simon.AwaitingInput, 10);
            Shot("simon");
            simon.Press((seq[0] + 1) % 4);
            Expect(!simon.AwaitingInput, "a wrong pad stops the round");
            Expect(await Until(() => simon.AwaitingInput && simon.Round == 1, 6), "and starts again from round 1");
            for (int round = 1; round <= seq.Length; round++)
            {
                if (!await Until(() => simon.AwaitingInput && simon.Round == round, 15)) { Fail($"round {round} never came"); return; }
                for (int i = 0; i < round; i++)
                {
                    simon.Press(seq[i]);
                    await Seconds(0.12);
                }
            }
            bool opened = await Until(() => loot.IsOpen && !loot.Waiting, 10);
            Expect(opened && !simon.IsOpen, "the server opened it, and the panel opened on its contents");
            var contents = loot.OpenContents().ToList();
            GD.Print($"[bank A] it holds {string.Join(", ", contents.Select(c => $"{c.Stack.Id} x{c.Stack.Count}"))} (worth {LootTables.Value(contents.Select(c => c.Stack))} CHF)");
            Say($"cracked [{string.Join(",", contents.Select(c => $"{c.Stack.Id}x{c.Stack.Count}"))}]");
            await Seconds(1.0);
            Shot("vault_open");
            if (!await Heard("B", "done", 90)) { Fail("B never finished"); return; }
            loot.TakeAll();
            await Until(() => !loot.Waiting && !loot.OpenContents().Any(), 10);
            Expect(_items.Inventory.Cash > 120 || contents.All(c => c.Stack.Id != ItemId.Francs), $"the vault's cash is in the pocket ({_items.Inventory.Cash})");
            loot.Close();

            await Cellar(me, interiors, spot);
        }

        GD.Print(_failures == 0 ? $"[bank {_role}] RESULT: ok" : $"[bank {_role}] RESULT: FAILED ({_failures})");
        await Seconds(2);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>A house with a cellar: stand in its shelter and its music room, if it has them, and look around.</summary>
    private async Task Cellar(FootPlayer me, InteriorManager interiors, Vector3 spot)
    {
        var doors = DoorIndex.All()
            .Where(d => d.Kind == BuildingKind.House && new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length() < 600f)
            .OrderBy(d => new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length()).Take(120).ToList();
        foreach (var d in doors)
        {
            InteriorLayout? l = null;
            try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
            if (l is not { Below: > 0 } || l.Key != d.Key.ToString()) continue;
            var rooms = l.Floors[0].Rooms;
            if (!rooms.Any(r => r.Type == RoomType.Shelter)) continue;
            GD.Print($"[bank A] cellar of {l.Key}: {string.Join(", ", rooms.Skip(1).Select(r => r.Type))}");
            if (!await WalkIn(me, interiors, d)) return;
            var node = interiors.CurrentNode!;
            foreach (var r in rooms.Where(r => r.Type is RoomType.Shelter or RoomType.MusicRoom or RoomType.Carnotzet or RoomType.HomeCinema))
            {
                // from the middle of the room (first person: --view first), toward each of its four walls
                var at = new Vector3((r.X0 + r.X1) / 2, l.FloorY(0) + 0.1f, (r.Z0 + r.Z1) / 2);
                foreach (var (dir, name) in new[] { (Vector3.Back, "back"), (Vector3.Forward, "front"), (Vector3.Left, "left"), (Vector3.Right, "right") })
                {
                    var face = node.GlobalTransform.Basis * dir;
                    me.EnterInterior(l.Key, node.GlobalTransform * at, Mathf.Atan2(-face.X, -face.Z));
                    me.Velocity = Vector3.Zero;
                    await Seconds(1.2);
                    Shot($"cellar_{r.Type}_{name}");
                }
                var local = node.ToLocal(me.GlobalPosition);
                Expect(me.IsOnFloor() && Mathf.Abs(local.Y - l.FloorY(0)) < 0.4f, $"standing on the cellar floor of the {r.Type} ({local.Y:F2} m, want {l.FloorY(0):F2})");
                Expect(!interiors.AtExit(me), "the front door cannot be used from the cellar");
            }
            return;
        }
        Expect(false, "a house with a shelter near the spot");
    }

    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
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

    private async Task<bool> StandAt(FootPlayer me, InteriorLayout layout, InteriorNode node, int index, Func<int, bool> facing)
    {
        var f = layout.Furniture[index];
        var turn = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2);
        var front = turn * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        float sign = _role == "A" ? -1f : 1f;
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

    private void Shot(string name)
    {
        string path = ProjectSettings.GlobalizePath($"res://test_output/bank_{_role}_{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[bank {_role}] screenshot {path}");
    }

    private void Say(string what) => Chat?.Send($"BK {_role} {what}");

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"BK {role} {what}")), seconds);

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
        GD.Print($"[bank {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[bank {_role}] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
