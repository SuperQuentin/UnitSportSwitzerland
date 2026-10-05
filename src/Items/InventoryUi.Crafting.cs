using Godot;
using UnitSport.Crafting;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The panel's third column (#271): every recipe, the ones you cannot make greyed out with what is
/// missing in red. Make crafts one; shift-click crafts as many as the ingredients allow. Each one
/// takes the recipe's seconds, shown on the bar under the header; closing the panel stops it.
/// Station: hands always, a workbench or a fire within reach (<see cref="CraftStations"/>), named in the header.
/// </summary>
public partial class InventoryUi
{
    private sealed class RecipeRow
    {
        public required Recipe Recipe;
        public required Control Root;
        public required RichTextLabel Needs;
        public required Button Make;
    }

    private readonly List<RecipeRow> _recipeRows = new();
    private Label _stationLine = null!;
    private Label _craftStatus = null!;
    private ProgressBar _craftBar = null!;
    private Station _station = Station.Hands;
    private InventoryStore? _store;
    private float _stationPoll;

    // what is being made: the recipe, how many batches are left, time into the current one
    private Recipe? _making;
    private int _makeLeft;
    private float _makeTime;

    private InventoryStore Store => _store ??= new InventoryStore(_items);

    private void BuildCrafting(HBoxContainer columns)
    {
        var column = UiKit.VBox(8);
        column.CustomMinimumSize = new Vector2(250, 0);
        columns.AddChild(column);

        var header = UiKit.HBox(12);
        header.AddChild(UiKit.Text("Crafting", UiTheme.FontHeading, UiTheme.Text, bold: true));
        header.AddChild(UiKit.Spacer(expand: true));
        _stationLine = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _stationLine.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        header.AddChild(_stationLine);
        column.AddChild(header);

        _craftBar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), MaxValue = 1 };
        column.AddChild(_craftBar);
        _craftStatus = UiKit.Text("", UiTheme.FontTiny, UiTheme.Amber);
        column.AddChild(_craftStatus);

        var (scroll, rows) = UiKit.ScrollPage(6);
        column.AddChild(scroll);
        string? section = null;
        foreach (var r in Recipes.All)
        {
            string s = r.Salvage ? "Salvage" : CraftStations.Name(r.Station);
            if (s != section)
            {
                section = s;
                rows.AddChild(UiKit.Section(s));
            }
            rows.AddChild(BuildRecipeRow(r));
        }

        column.AddChild(UiKit.Text($"{Core.InputHints.Keyboard(Key.Shift)}-click Make to craft as many as you can.",
            UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));
    }

    private Control BuildRecipeRow(Recipe r)
    {
        var def = ItemDefs.Get(r.Out);
        var row = UiKit.HBox(8);
        var icon = new TextureRect
        {
            Texture = r.Salvage ? ItemIcons.Get(r.In[0].Id) : ItemIcons.Get(r.Out),
            CustomMinimumSize = new Vector2(32, 32),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(icon);

        var text = UiKit.VBox(0);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        string title = r.Salvage
            ? $"Strip {ItemDefs.Get(r.In[0].Id)?.Name}"
            : r.Count > 1 ? $"{def?.Name} ×{r.Count}" : def?.Name ?? r.Out.ToString();
        text.AddChild(UiKit.Text(title, UiTheme.FontSmall, UiTheme.Text, bold: true));
        var needs = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        needs.AddThemeFontSizeOverride("normal_font_size", UiTheme.FontTiny);
        needs.AddThemeFontSizeOverride("bold_font_size", UiTheme.FontTiny);
        text.AddChild(needs);
        row.AddChild(text);

        var make = UiKit.Button("Make");
        make.CustomMinimumSize = new Vector2(56, 30);
        make.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        make.Pressed += () => StartMaking(r, Input.IsKeyPressed(Key.Shift) ? int.MaxValue : 1);
        row.AddChild(make);

        _recipeRows.Add(new RecipeRow { Recipe = r, Root = row, Needs = needs, Make = make });
        return row;
    }

    /// <summary>Repaints every row from the pack: called with the rest of the panel on any change.</summary>
    private void RefreshCrafting()
    {
        if (_recipeRows.Count == 0) return;
        var spot = CraftStations.Where(_items.UsablePlayer);
        _station = spot.Here;
        _stationLine.Text = spot.Label;

        foreach (var row in _recipeRows)
        {
            var r = row.Recipe;
            bool here = (r.Station & _station) != 0;
            int max = Recipes.MaxTimes(Store, r);
            row.Root.Modulate = here && max > 0 ? Colors.White : new Color(1, 1, 1, 0.55f);
            row.Make.Disabled = !here || max == 0 || _making != null;
            row.Make.TooltipText = !here ? CraftStations.WhereToFind(r.Station)
                : max == 0 ? "Missing ingredients."
                : $"Takes {r.Seconds:0.#} s. {Core.InputHints.Keyboard(Key.Shift)}-click: make all {max}.";
            row.Needs.Text = Describe(r, here);
        }
    }

    private string Describe(Recipe r, bool here)
    {
        var parts = new List<string>();
        foreach (var need in r.In)
        {
            int have = Store.Count(need.Id);
            string colour = (have >= need.Count ? UiTheme.TextDim : UiTheme.Bad).ToHtml(false);
            parts.Add($"[color=#{colour}]{need.Count} {ItemDefs.Get(need.Id)?.Name} ({have})[/color]");
        }
        string line = string.Join(", ", parts);
        if (r.Salvage)
            line += $"\n[color=#{UiTheme.Good.ToHtml(false)}]→ " + string.Join(", ",
                Recipes.Outputs(r).Select(o => $"{o.Count} {ItemDefs.Get(o.Id)?.Name}")) + "[/color]";
        if (!here)
            line += $"\n[color=#{UiTheme.TextFaint.ToHtml(false)}]{CraftStations.Name(r.Station)}[/color]";
        return line;
    }

    private void StartMaking(Recipe r, int times)
    {
        if (_making != null) return;
        int max = Recipes.MaxTimes(Store, r);
        if ((r.Station & _station) == 0 || max == 0) return;
        _making = r;
        _makeLeft = Math.Min(times, max);
        _makeTime = 0;
        RefreshCrafting();
    }

    private void StopMaking()
    {
        _making = null;
        _makeLeft = 0;
        _makeTime = 0;
        if (_craftBar != null) _craftBar.Value = 0;
        if (_craftStatus != null) _craftStatus.Text = "";
    }

    /// <summary>Runs from <see cref="_Process"/>: advances the batch being made, crafts it when its time is up.</summary>
    private void ProcessCrafting(float dt)
    {
        // a campfire lit or burnt out, a bench walked up to: the rows follow what is in reach
        _stationPoll -= dt;
        if (IsOpen && _stationPoll <= 0f && _recipeRows.Count > 0)
        {
            _stationPoll = 1f;
            if (CraftStations.Where(_items.UsablePlayer).Label != _stationLine.Text) RefreshCrafting();
        }
        if (_making is not { } r) return;
        if (!IsOpen) { StopMaking(); return; }

        _makeTime += dt;
        _craftBar.Value = Mathf.Clamp(_makeTime / r.Seconds, 0, 1);
        string name = r.Salvage ? $"stripping {ItemDefs.Get(r.In[0].Id)?.Name}" : $"making {ItemDefs.Get(r.Out)?.Name}";
        _craftStatus.Text = _makeLeft > 1 ? $"{char.ToUpperInvariant(name[0])}{name[1..]}… {_makeLeft} to go" : $"{char.ToUpperInvariant(name[0])}{name[1..]}…";
        if (_makeTime < r.Seconds) return;

        _station = CraftStations.At(_items.UsablePlayer);
        int made;
        using (Inv.Batch())
            made = Recipes.Craft(Store, r, 1, _station);
        _makeLeft = made > 0 ? _makeLeft - 1 : 0;
        _makeTime = 0;
        if (made > 0) Toast(r.Salvage ? $"Stripped: {ItemDefs.Get(r.In[0].Id)?.Name}" : $"Made: {ItemDefs.Get(r.Out)?.Name}");
        if (_makeLeft <= 0) StopMaking();
        RefreshCrafting();
    }
}
