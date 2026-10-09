using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// <c>--farmnet A|B</c> with <c>--connect</c> (driven by <c>tools/farmnetcheck.sh</c>, #494), on the
/// flat fixture in July (<c>--farmmonth 7</c> on the server):
/// <list type="bullet">
/// <item>A ploughs a strip of the ripe wheat with a machine stroke and tills one more cell by hand;
/// its predictions are answered (nothing left pending) and say "worked".</item>
/// <item>B joins after: the strip comes in the subscribe snapshot (late joiner).</item>
/// <item>A ploughs a second strip while B is there: B sees it live.</item>
/// <item>A tills and sows a cell by hand with a hoe and a seed bag from its pack (hotbar slot,
/// <c>use_item</c>): B sees the cell sown with wheat.</item>
/// <item>B, 200 m from the potato field, tries to plough it: the server refuses (too far) and B's
/// prediction goes back to the growing crop.</item>
/// </list>
/// </summary>
public partial class FarmNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--farmnet");

    private readonly WorldOrigin _origin;

    public FarmNetProbe(ItemController items, WorldOrigin origin) : base(items, "farmnet", "FM") => _origin = origin;
    public FarmNetProbe() : this(null!, null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        var (se, sn) = SpawnPoint.ParseTarget();
        var tile = TileId.FromLv95(se + 60, sn);
        if (!await Joined(150, () => FarmField.Instance?.HasFields(tile) == true)) { await Finish(0); return; }
        // the subscribe snapshot (and the server's month) arrive a moment after the fields load
        Expect(await Until(() => FarmField.Instance!.Month == 7, 15), $"the server's month: July ({FarmField.Instance!.Month})");
        if (_role == "A") await RunA(FarmField.Instance!, se, sn); else await RunB(FarmField.Instance!, se, sn);
        await Finish(2.0);
    }

    private Vector3 W(double e, double n) => _origin.ToWorld(e, n, 0);

    private int Count(FarmField farm, double x, double sn, FieldStage stage)
    {
        int k = 0;
        for (double n = sn - 26; n <= sn + 26; n += 4) if (farm.CellAtLv95(x, n)?.Stage == stage) k++;
        return k;
    }

    private async Task RunA(FarmField farm, double se, double sn)
    {
        await Stand(se + 44, sn);
        var s1 = FarmWork.Sweep(FarmTool.Plough, W(se + 40, sn - 28), W(se + 40, sn + 28), 3f);
        Expect(s1.Cells > 10, $"A ploughs a strip: {s1.Cells} cells");
        var hand = HandFarming.Instance;
        if (hand != null)
        {
            Me!.LookYaw = -Mathf.Pi * 0.5f;
            await Seconds(0.3);
            hand.Use(Me!, -1, ItemId.Hoe);
            Expect(await Until(() => !hand.Busy, 5) && hand.LastStroke.Cells == 1, $"A tills a cell by hand ({hand.LastStroke.Cells})");
        }
        Expect(await Until(() => farm.PendingCount == 0, 10), $"the server answered every cell ({farm.PendingCount} pending)");
        Expect(Count(farm, se + 40, sn, FieldStage.Ploughed) >= 10, $"and they stay ploughed ({Count(farm, se + 40, sn, FieldStage.Ploughed)})");
        Say($"worked {Count(farm, se + 40, sn, FieldStage.Ploughed)}");

        if (!await Heard("B", "joined", 120)) { Fail("B never joined"); return; }
        var s2 = FarmWork.Sweep(FarmTool.Plough, W(se + 60, sn - 28), W(se + 60, sn + 28), 3f);
        Expect(s2.Cells > 10, $"A ploughs a second strip: {s2.Cells} cells");
        Expect(await Until(() => farm.PendingCount == 0, 10), "answered");
        Say($"more {Count(farm, se + 60, sn, FieldStage.Ploughed)}");

        // by hand from the pack, the player's way (hotbar slot, use_item): till and sow one cell
        if (hand != null)
        {
            var inv = _items.Inventory;
            inv.BeginMatch();   // an empty pack lent for this, the saved one untouched
            try
            {
                _items.Give(new ItemStack(ItemId.Hoe, 1));
                _items.Give(new ItemStack(ItemId.WheatSeed, 1));
                const float cs = FieldFormat.CellSize;
                double ce = (Math.Floor((se + 80) / cs) + 0.5) * cs, cn = (Math.Floor((sn + 10) / cs) + 0.5) * cs;
                await Stand(ce - HandFarming.Reach, cn);
                Me!.LookYaw = -Mathf.Pi * 0.5f;
                await Seconds(0.3);
                await Press(PlayerInput.Slots[SlotOf(ItemId.Hoe)]);
                await Press(PlayerInput.UseItem);
                Expect(hand.Busy && await Until(() => !hand.Busy, 5) && farm.CellAtLv95(ce, cn)?.Stage == FieldStage.Ploughed,
                    $"A tills a cell with the hoe from its pack ({farm.CellAtLv95(ce, cn)?.Stage})");
                await Press(PlayerInput.Slots[SlotOf(ItemId.WheatSeed)]);
                await Press(PlayerInput.UseItem);
                Expect(hand.Busy && await Until(() => !hand.Busy, 5) && farm.CellAtLv95(ce, cn) is { Stage: FieldStage.Sown, Crop: CropKind.Wheat },
                    $"and sows it with a seed bag from its pack ({farm.CellAtLv95(ce, cn)?.Stage} {farm.CellAtLv95(ce, cn)?.Crop})");
                Expect(CountOf(ItemId.WheatSeed) == 0, "the bag was opened");
                Expect(await Until(() => farm.PendingCount == 0, 10) && farm.CellAtLv95(ce, cn)?.Stage == FieldStage.Sown, "the server kept it");
                Say(FormattableString.Invariant($"sown {ce:F1} {cn:F1}"));
            }
            finally { inv.EndMatch(); }
        }
        Expect(await Heard("B", "done", 60), "B is done");
    }

    /// <summary>Presses and lets go of an action, as a key, a pad button or a VR control would.</summary>
    private async Task Press(StringName action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true, Strength = 1f });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task RunB(FarmField farm, double se, double sn)
    {
        Expect(await Until(() => Count(farm, se + 40, sn, FieldStage.Ploughed) >= 10, 15),
            $"B (late) gets A's strip in the snapshot ({Count(farm, se + 40, sn, FieldStage.Ploughed)} ploughed)");
        Say("joined");
        Expect(await Heard("A", "more", 60), "A ploughed more");
        Expect(await Until(() => Count(farm, se + 60, sn, FieldStage.Ploughed) >= 10, 10),
            $"B sees the second strip live ({Count(farm, se + 60, sn, FieldStage.Ploughed)} ploughed)");

        // A tills and sows a cell by hand from its pack: B sees it sown with wheat
        if (await Heard("A", "sown", 60) && _heard.LastOrDefault(l => l.Contains("FM A sown ")) is { } line)
        {
            var w = line[(line.IndexOf("FM A sown ", StringComparison.Ordinal) + 10)..].Split(' ');
            double ce = double.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture);
            double cn = double.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture);
            Expect(await Until(() => farm.CellAtLv95(ce, cn) is { Stage: FieldStage.Sown, Crop: CropKind.Wheat }, 10),
                $"B sees A's hand-sown cell: {farm.CellAtLv95(ce, cn)?.Stage} {farm.CellAtLv95(ce, cn)?.Crop}");
        }
        else Expect(false, "A never sowed by hand");

        // too far from the potato field: the server refuses, the prediction goes back
        await Stand(se - 150, sn);
        await Seconds(1.0);   // the server has our new position
        double px = se + 48, pn = sn + 68;
        var refused = FarmWork.Sweep(FarmTool.Plough, W(px - 10, pn), W(px + 10, pn), 3f);
        Expect(refused.Cells > 0 && farm.CellAtLv95(px, pn)?.Stage == FieldStage.Ploughed, $"B predicts ploughing the potatoes ({refused.Cells})");
        Expect(await Until(() => farm.PendingCount == 0 && farm.CellAtLv95(px, pn)?.Stage == FieldStage.Growing, 10),
            $"the server refused it from 200 m: growing again ({farm.CellAtLv95(px, pn)?.Stage})");
        Say("done");
    }

    private async Task Stand(double e, double n)
    {
        if (Me is not { } me) return;
        me.DebugLaunch(_origin.ToWorld(e, n, me.GlobalPosition.Y + 1.5f), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.5);
    }
}
