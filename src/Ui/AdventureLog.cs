using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The Journal (#1223, J): one page with tabs, on the shell's stack like the Activities screen. This
/// plan builds <b>Quests</b>; the bird journal is its <b>Birds</b> and <b>Fish</b> tabs; the other
/// Adventure plans add <b>Atlas</b>, <b>Collection</b> and <b>Travel</b> through
/// <see cref="RegisterTab"/> without editing this file. The adventure tabs show only while the
/// adventure is on for this player (not in a Battle Royale round); Birds and Fish always. Tabs switch with
/// LB / RB and Q / E; J, Esc or B closes. Docs: <c>docs/notes/ui/adventure-log.md</c>.
/// </summary>
public partial class AdventureLog : Screen
{
    public const string QuestsTab = "quests", BirdsTab = "birds", FishTab = "fish";

    private sealed record TabDef(string Id, string Title, int Order, Func<Control> Build, Func<bool>? Shown);

    private static readonly List<TabDef> Registered = new();
    /// <summary>The tab shown last: the Journal opens on it again (ux-guidelines 7).</summary>
    private static string? _lastTab;

    /// <summary>
    /// Adds a tab: <paramref name="order"/> places it (Quests 10, Atlas 20, Collection 30, Travel 40,
    /// Birds 50, Fish 60), <paramref name="build"/> makes its page each time the Journal opens,
    /// <paramref name="shown"/> hides it when false (null: always shown). Registering an id again replaces it.
    /// </summary>
    public static void RegisterTab(string id, string title, int order, Func<Control> build, Func<bool>? shown = null)
    {
        Registered.RemoveAll(t => t.Id == id);
        Registered.Add(new TabDef(id, title, order, build, shown));
        Registered.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    /// <summary>The adventure is on for the local player: the adventure tabs show.</summary>
    public static bool Adventuring => Adventure.AdventureService.Instance != null && !Permissions.InMatch;

    static AdventureLog()
    {
        RegisterTab(QuestsTab, "Quests", 10, () => new QuestsPage(), () => Adventuring);
        RegisterTab(BirdsTab, "Birds", 50, () => Birds.BirdLife.Instance?.Journal is { } j ? j.BuildBirdsPage()
            : Empty("No birds in this world."));
        RegisterTab(FishTab, "Fish", 60, Birds.BirdJournal.BuildFishPage);
    }

    private readonly List<(TabDef Def, Button Button, Control Page)> _tabs = new();
    private string? _want;
    private int _current = -1;

    public static AdventureLog Create(string? tab) => new() { Name = "Journal", _want = tab };

    /// <summary>Opens the Journal over the world, on <paramref name="tab"/> (or the last one); shows that tab when it is open already.</summary>
    public static void Open(string? tab = null)
    {
        if (GameShell.Instance is not { InWorld: true } shell) return;
        if (shell.Top is AdventureLog open) { if (tab != null) open.ShowTab(tab); return; }
        shell.Push(Create(tab));
    }

    /// <summary>J: opens the Journal, or closes it when it is in front.</summary>
    public static void Toggle()
    {
        if (GameShell.Instance is not { InWorld: true } shell) return;
        if (shell.Top is AdventureLog) shell.Back();
        // the rod in hand opens it on the fish (#493)
        else if (shell.Top == null) Open(Items.ItemController.Instance?.Inventory.HeldId == Items.ItemId.FishingRod ? FishTab : null);
    }

    public override void _Ready()
    {
        // the world dimmed behind the page, as under the pause menu
        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var view = GetViewportRect().Size;
        var (body, _) = Framed("Journal", InputHints.Format("Quests and what you found · {bird_journal} closes"),
            new Vector2(Mathf.Min(view.X - 48, 1040), Mathf.Min(view.Y - 48, 640)));

        var tabRow = UiKit.HBox(4);
        body.AddChild(tabRow);
        var group = new ButtonGroup();
        var pages = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        body.AddChild(pages);
        foreach (var def in Registered)
        {
            if (def.Shown?.Invoke() == false) continue;
            var b = TabButton(def.Title, group);
            int index = _tabs.Count;
            b.Toggled += pressed => { if (pressed) Show(index); };
            tabRow.AddChild(b);
            Control page;
            try { page = def.Build(); }
            catch (Exception e)
            {
                GD.PushWarning($"[journal] tab {def.Id}: {e.Message}");
                page = Empty("This page could not be shown.");
            }
            page.SetAnchorsPreset(LayoutPreset.FullRect);
            page.Visible = false;
            pages.AddChild(page);
            _tabs.Add((def, b, page));
        }
        ShowTab(_want ?? _lastTab ?? (Adventuring ? QuestsTab : BirdsTab));
    }

    private static Button TabButton(string name, ButtonGroup group)
    {
        var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(96, 36) };
        var normal = UiTheme.Flat(new Color(0, 0, 0, 0), 6, 14, 6);
        var on = UiTheme.Flat(new Color(UiTheme.Amber, 0.12f), 6, 14, 6);
        on.BorderColor = UiTheme.Amber;
        on.BorderWidthBottom = 2;
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("pressed", on);
        b.AddThemeStyleboxOverride("hover_pressed", on);
        UiKit.DimFont(b);
        b.AddThemeColorOverride("font_pressed_color", UiTheme.Amber);
        b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Amber);
        return b;
    }

    /// <summary>A page that only says something (an empty state).</summary>
    public static Control Empty(string text)
    {
        var c = new CenterContainer();
        c.AddChild(UiKit.Text(text, UiTheme.FontBody, UiTheme.TextDim));
        return c;
    }

    /// <summary>The id of the tab in front (checks).</summary>
    public string? Current => _current >= 0 ? _tabs[_current].Def.Id : null;

    public void ShowTab(string id)
    {
        int i = _tabs.FindIndex(t => t.Def.Id == id);
        Show(i >= 0 ? i : 0);
    }

    private void Show(int index)
    {
        if (_tabs.Count == 0) return;
        _current = Mathf.Clamp(index, 0, _tabs.Count - 1);
        for (int i = 0; i < _tabs.Count; i++) _tabs[i].Page.Visible = i == _current;
        if (!_tabs[_current].Button.ButtonPressed) _tabs[_current].Button.SetPressedNoSignal(true);
        _lastTab = _tabs[_current].Def.Id;
    }

    public override void OnShown()
    {
        if (_current >= 0) _tabs[_current].Button.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>LB / RB and Q / E step through the tabs; J closes.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree() || !e.IsPressed() || e.IsEcho() || Modal.Current != null || _tabs.Count == 0) return;
        if (e.IsActionPressed(PlayerInput.BirdJournal) && !UiFocus.TextEntryActive)
        {
            GetViewport().SetInputAsHandled();
            Shell.Back();
            return;
        }
        int step = e switch
        {
            InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventKey { PhysicalKeycode: Key.Q } when GetViewport().GuiGetFocusOwner() is not LineEdit => -1,
            InputEventKey { PhysicalKeycode: Key.E } when GetViewport().GuiGetFocusOwner() is not LineEdit => 1,
            _ => 0,
        };
        if (step == 0) return;
        int next = (_current + step + _tabs.Count) % _tabs.Count;
        Show(next);
        _tabs[next].Button.GrabFocus();
        GetViewport().SetInputAsHandled();
    }
}
