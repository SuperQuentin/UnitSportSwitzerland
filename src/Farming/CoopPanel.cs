using Godot;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Farming;

/// <summary>
/// The farm co-op's part of the shop panel (#494, <c>Loot.ShopUi</c>): this week's wish list and
/// the season, the co-op's orders with "Accept", and the player's contracts with what is left to
/// deliver and the time left. Built by the panel when it rebuilds (on a change, never per frame).
/// </summary>
public static class CoopPanel
{
    /// <summary>One line under "Your pack": what the co-op pays more for this week, and the season.</summary>
    public static string MarketLine(string coop)
    {
        var wishes = FarmPrices.Wishes(coop, FarmSales.Week);
        string wanted = string.Join(", ", wishes.Select(w => $"{FarmSales.NameOfItem(w.Item)} +{Math.Round((w.Bonus - 1) * 100):0} %"));
        int month = FarmSales.Month;
        var glut = FarmPrices.Harvests.Where(h => FarmPrices.HarvestMonth(h, month)).Select(FarmSales.NameOfItem).ToList();
        string season = glut.Count > 0 ? $" Harvest time for {string.Join(", ", glut).ToLowerInvariant()}: {Math.Round((1 - FarmPrices.GlutFactor) * 100):0} % less."
            : month is >= 3 and <= 5 ? $" Spring: stored crops fetch {Math.Round((FarmPrices.SpringFactor - 1) * 100):0} % more." : "";
        return $"Wanted this week: {wanted}.{season} A load delivered in the yard pays the full price.";
    }

    /// <summary>The orders and the player's contracts, into <paramref name="box"/> (emptied first).</summary>
    public static void Fill(VBoxContainer box, string coop)
    {
        foreach (var c in box.GetChildren()) c.QueueFree();
        if (FarmSales.Instance is not { } sales) return;
        box.AddChild(UiKit.Section($"Orders this week ({FarmCalendar.Left(FarmCalendar.WeekEnds(FarmSales.Week) - FarmSales.Now)} left)"));
        var mine = sales.Mine;
        foreach (var o in FarmSales.OrdersOf(coop))
        {
            float unit = ItemDefs.Get(o.Item)?.Value ?? 0;
            var row = UiKit.HBox(8);
            row.AddChild(Icon(o.Item));
            var text = UiKit.Text($"{o.Count} × {FarmSales.NameOfItem(o.Item)} within {o.Days} days, {o.Multiplier:0.0}×  (bonus +{FarmContracts.Bonus(o, unit)} CHF)",
                UiTheme.FontSmall, UiTheme.Text, wrap: true);
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(text);
            bool held = FarmContracts.Holds(mine, coop, FarmSales.Week, o.Index);
            var accept = UiKit.Button(held ? "Taken" : "Accept", minWidth: 80);
            accept.Disabled = held || mine.Count >= FarmContracts.MaxActive;
            accept.TooltipText = "Deliver them here, at the counter or by the load, before the deadline: the bonus is paid on top";
            int index = o.Index;
            accept.Pressed += () => sales.Accept(coop, index);
            row.AddChild(accept);
            box.AddChild(row);
        }
        if (mine.Count == 0) return;
        box.AddChild(UiKit.Section($"Your contracts ({mine.Count}/{FarmContracts.MaxActive})"));
        double now = FarmSales.Now;
        foreach (var c in mine)
        {
            string where = c.Coop == coop ? "here" : "another co-op";
            box.AddChild(UiKit.Text($"{c.Delivered}/{c.Count} {FarmSales.NameOfItem(c.Item)} for {where}, {c.Multiplier:0.0}× (+{FarmContracts.Bonus(c)} CHF), {FarmCalendar.Left(c.Deadline - now)} left",
                UiTheme.FontTiny, c.Coop == coop ? UiTheme.Good : UiTheme.TextDim, wrap: true));
        }
    }

    private static TextureRect Icon(ItemId id) => new()
    {
        Texture = ItemIcons.Get(id),
        CustomMinimumSize = new Vector2(24, 24),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
