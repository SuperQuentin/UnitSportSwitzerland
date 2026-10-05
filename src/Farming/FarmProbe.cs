using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// <c>--farmcheck [shots]</c> (offline, flat fixture, <c>--systems ui,physics,loot,farming --farmmonth 7
/// --farmdir test_output/farmcheck_store</c>), #494: the fixture's wheat field is ripe in July; a
/// ripe cell is offered to Gathering and harvested by hand, the hoe tills it, a seed sows it,
/// fertiliser feeds it, a fast-forward ripens it and it is harvested again; then machine strokes
/// through <see cref="FarmWork.Sweep"/> (harvest, plough, sow, mow) with their counts and units,
/// the readout line, the save file, and the drawing (chunks built). <c>shots</c> (not headless):
/// <c>test_output/farmcheck_&lt;style&gt;.png</c> over the worked field.
/// </summary>
public partial class FarmProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--farmcheck") >= 0;
    private static bool Shots => Array.IndexOf(OS.GetCmdlineUserArgs(), "shots") > Array.IndexOf(OS.GetCmdlineUserArgs(), "--farmcheck");

    private readonly ItemController _items;
    private readonly WorldOrigin _origin;
    private int _failures;

    public FarmProbe(ItemController items, WorldOrigin origin) { _items = items; _origin = origin; }
    public FarmProbe() : this(null!, null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        var (se, sn) = SpawnPoint.ParseTarget();
        var wheatTile = TileId.FromLv95(se + 60, sn);
        if (!await Until(() => Me is { } m && m.IsOnFloor() && FarmField.Instance?.HasFields(wheatTile) == true && HandFarming.Instance != null, 120))
        {
            Fail("no player on the ground, or no field tile");
            return;
        }
        await Seconds(0.5);
        try { await Run(Me!, FarmField.Instance!, HandFarming.Instance!, se, sn); }
        catch (Exception e) { Expect(false, $"threw: {e}"); }
        GD.Print(_failures == 0 ? "[farmcheck] RESULT: ok" : $"[farmcheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, FarmField farm, HandFarming hand, double se, double sn)
    {
        var tile = TileId.FromLv95(se + 60, sn);
        Expect(farm.Month == 7, $"the month is July ({farm.Month})");
        var census = farm.Census(tile, 0xF1E1D001);
        // 72 x 72 m: 18 or 19 cells a side, as the LV95 grid falls on the start
        Expect(census.Count == 1 && census.GetValueOrDefault(FieldStage.Ripe) is >= 18 * 18 and <= 19 * 19, $"the wheat field: every cell ripe ({Show(census)})");
        var potato = farm.Census(tile, 0xF1E1D002);
        Expect(potato.Count == 1 && potato.GetValueOrDefault(FieldStage.Growing) is >= 12 * 12 and <= 13 * 13, $"the potato field grows in July ({Show(potato)})");

        // 1. stand in the wheat, facing east
        await Stand(me, se + 60, sn, -Mathf.Pi * 0.5f);
        var (ae, an) = hand.Ahead(me);
        Expect(ae > se + 60.5, $"facing east ({ae - se:F1}, {an - sn:F1})");
        await Seconds(0.4);
        Expect(hand.Readout.StartsWith("Wheat — ripe"), $"the readout: \"{hand.Readout}\"");
        var gathering = GetParent().GetNodeOrNull<Loot.Gathering>("Gathering");
        if (gathering != null)
        {
            await Seconds(0.4);
            Expect(gathering.Target == Loot.Gathering.Resource.Crop, $"Gathering offers the ripe crop ({gathering.Target})");
        }

        // 2. by hand: harvest (the gather hold's end), till, sow, fertilise
        var got = hand.HarvestAt(ae, an);
        Expect(got.Id == ItemId.Wheat && got.Count >= FarmTables.HandYieldMin, $"harvested by hand: {got.Count} {got.Id}");
        Expect(farm.CellAtLv95(ae, an)?.Stage == FieldStage.Stubble, $"the cell is stubble ({farm.CellAtLv95(ae, an)?.Stage})");
        Expect(await HandStroke(me, hand, ItemId.Hoe) && farm.CellAtLv95(ae, an)?.Stage == FieldStage.Ploughed, $"the hoe tills it ({farm.CellAtLv95(ae, an)?.Stage})");
        Expect(await HandStroke(me, hand, ItemId.WheatSeed) && farm.CellAtLv95(ae, an)?.Stage == FieldStage.Sown, $"a seed sows it ({farm.CellAtLv95(ae, an)?.Stage})");
        Expect(await HandStroke(me, hand, ItemId.Fertiliser) && hand.LastStroke.Cells == 1, $"fertiliser on the 3x3: the one sown cell takes it ({hand.LastStroke.Cells})");
        Expect(farm.CellAtLv95(ae, an)?.Stage == FieldStage.Sown && hand.Readout.StartsWith("Wheat — sown"), $"still sprouting: \"{hand.Readout}\"");

        // 3. fast-forward: fertilised wheat ripens in 0.6 of 40 min
        FarmField.ClockSkew += FarmTables.GrowSeconds(CropKind.Wheat) * FarmTables.FertilisedGrowth * 0.5;
        Expect(farm.CellAtLv95(ae, an) is { Stage: FieldStage.Growing } half && half.Growth is > 0.4f and < 0.6f, $"half way: growing ({farm.CellAtLv95(ae, an)?.Stage} {farm.CellAtLv95(ae, an)?.Growth:F2})");
        FarmField.ClockSkew += FarmTables.GrowSeconds(CropKind.Wheat);
        Expect(farm.CellAtLv95(ae, an)?.Stage == FieldStage.Ripe, $"fast-forward: ripe ({farm.CellAtLv95(ae, an)?.Stage})");
        got = hand.HarvestAt(ae, an);
        Expect(got.Id == ItemId.Wheat && got.Count >= 1, $"harvested again: {got.Count} {got.Id}");

        // 4. machines: a 6 m combine pass north along the field, then plough, sow the same strip
        double x = se + 40;
        Vector3 W(double e, double n) => _origin.ToWorld(e, n, 0);
        var stroke = FarmWork.Sweep(FarmTool.Harvest, W(x, sn - 30), W(x, sn + 30), 6f);
        Expect(stroke.Cells is >= 14 and <= 32 && stroke.Crop == CropKind.Wheat && Math.Abs(stroke.Units - stroke.Cells * FarmTables.YieldPerCell(CropKind.Wheat)) < 1e-3,
            $"combine pass: {stroke.Cells} cells of {stroke.Crop}, {stroke.Units:F2} items");
        var again = FarmWork.Sweep(FarmTool.Harvest, W(x, sn - 30), W(x, sn + 30), 6f);
        Expect(again.Cells == 0, $"the same pass again harvests nothing ({again.Cells})");
        var plough = FarmWork.Sweep(FarmTool.Plough, W(x, sn - 30), W(x, sn + 30), 6f);
        Expect(plough.Cells == stroke.Cells, $"plough the stubble: {plough.Cells} cells");
        var sow = FarmWork.Sweep(FarmTool.Sow, W(x, sn - 30), W(x, sn + 30), 6f, CropKind.Barley);
        Expect(sow.Cells == plough.Cells && sow.Crop == CropKind.Barley && Math.Abs(sow.Units - sow.Cells / (float)FarmTables.CellsPerSeed) < 1e-3,
            $"seed drill: {sow.Cells} cells of {sow.Crop}, {sow.Units:F2} seed items");
        // a ploughed strip beside it, left bare (for the eye), and a harvested one
        FarmWork.Sweep(FarmTool.Harvest, W(x + 8, sn - 30), W(x + 8, sn + 30), 6f);
        FarmWork.Sweep(FarmTool.Plough, W(x + 16, sn - 30), W(x + 16, sn + 30), 6f);
        var mow = FarmWork.Sweep(FarmTool.Mow, W(se - 60, sn - 30), W(se - 60, sn + 30), 3f);
        Expect(mow.Cells > 0 && mow.Crop == CropKind.Meadow && mow.Units > 0, $"mower on the meadow: {mow.Cells} cells, {mow.Units:F2} bales");
        Expect(FarmWork.Sweep(FarmTool.Harvest, W(se - 50, sn - 30), W(se - 50, sn + 30), 3f).Cells == 0, "a combine on grass does nothing");
        Expect(FarmWork.Sweep(FarmTool.Mow, W(x + 30, sn - 30), W(x + 30, sn + 30), 3f).Cells == 0, "a mower on wheat does nothing");
        Expect(FarmWork.CellAt(W(se - 60, sn)) is { Stage: FieldStage.Mown }, $"the meadow is mown ({FarmWork.CellAt(W(se - 60, sn))?.Stage})");

        // 5. saved: the file holds every stored cell
        string path = farm.FlushedPath(tile);
        int saved = -1;
        try { saved = System.Text.Json.JsonSerializer.Deserialize<FieldCells.File>(File.ReadAllText(path))?.Cells.Length ?? -1; }
        catch (Exception e) { GD.Print($"[farmcheck] reading {path}: {e.Message}"); }
        Expect(saved > 0 && saved == farm.StoredCount(tile), $"saved: {saved} cells in {Path.GetFileName(path)} (stored {farm.StoredCount(tile)})");

        // 6. drawn: chunks near the camera are built
        await Stand(me, se + 14, sn - 46, -Mathf.Pi * 0.25f);   // looking north-east over the worked wheat
        me.LookPitch = -0.35f;
        double wait = Time.GetTicksMsec() / 1000.0 + 15;
        while (farm.DrawnChunks < 2 && Time.GetTicksMsec() / 1000.0 < wait) await Seconds(0.2);
        await Seconds(1.0);
        Expect(farm.DrawnChunks >= 2 && farm.DrawnVertices > 0, $"drawn: {farm.DrawnChunks} chunks, {farm.DrawnVertices} vertices, {farm.Rebuilds} builds, main {farm.MainMsTotal:F1} ms (max frame {farm.MainMsMax:F2})");
        if (Shots && DisplayServer.GetName() != "headless")
        {
            // a camera of our own, above the field's south-west corner, over the worked strips
            float ground = me.GlobalPosition.Y;
            var cam = new Camera3D { Fov = 60, Far = 4000 };
            GetParent().AddChild(cam);
            cam.GlobalPosition = _origin.ToWorld(se + 18, sn - 52, ground + 14);
            cam.LookAt(_origin.ToWorld(se + 52, sn - 4, ground), Vector3.Up);
            cam.MakeCurrent();
            await Seconds(1.5);
            string style = Styles.StyleKit.Applied.ToString().ToLowerInvariant();
            string file = ProjectSettings.GlobalizePath("res://test_output/farmcheck_" + style + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var image = GetViewport().GetTexture().GetImage();
            GD.Print($"[farmcheck] shot {file}: {image.SavePng(file)}");
        }
    }

    /// <summary>A hand stroke through the real path (the bar), with nothing taken from the pack.</summary>
    private async Task<bool> HandStroke(FootPlayer me, HandFarming hand, ItemId item)
    {
        hand.Use(me, -1, item);
        if (!hand.Busy) return false;
        return await Until(() => !hand.Busy, 5);
    }

    private async Task Stand(FootPlayer me, double e, double n, float yaw)
    {
        me.DebugLaunch(_origin.ToWorld(e, n, me.GlobalPosition.Y + 1.5f), Vector3.Zero);
        me.LookYaw = yaw;
        me.LookPitch = -0.3f;
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.3);
    }

    private static string Show(Dictionary<FieldStage, int> c) => string.Join(", ", c.Select(kv => $"{kv.Key} {kv.Value}"));

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[farmcheck]   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[farmcheck] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
