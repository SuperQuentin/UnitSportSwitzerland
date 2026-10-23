using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// One full-screen menu page. <see cref="GameShell"/> keeps them on a stack: Esc / B pops back,
/// each page gets focus on its first control when shown, so a pad can drive every one.
/// </summary>
public abstract partial class Screen : Control
{
    public GameShell Shell { get; internal set; } = null!;

    protected Screen()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        // the menu skin of the moment (#1135): day or night, Alpine, Night Arcade, Classic...
        Theme = Skins.MenuSkin.Theme();
    }

    /// <summary>Called each time the page comes to the front.</summary>
    public virtual void OnShown() => PlayerInput.FocusFirst(this);

    /// <summary>Called when the page leaves the front (covered or popped).</summary>
    public virtual void OnHidden() { }

    /// <summary>Esc / B: true lets the shell pop the page.</summary>
    public virtual bool OnBack() => true;

    /// <summary>
    /// The usual frame of a sub-page: a centred glass panel with a back arrow and a title. Returns
    /// the body to fill and the header row (for extra controls on the right).
    /// </summary>
    protected (VBoxContainer Body, HBoxContainer Header) Framed(string title, string? subtitle, Vector2 size)
    {
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var panel = new PanelContainer { CustomMinimumSize = size, ThemeTypeVariation = Skins.SkinTheme.Page };
        centre.AddChild(panel);

        var column = UiKit.VBox(14);
        panel.AddChild(column);

        var header = UiKit.HBox(12);
        var back = UiKit.IconButton(Icons.Back, InputHints.Format("Back ({menu})", InputDevice.KeyboardMouse), 38);
        back.Pressed += () => Shell.Back();
        header.AddChild(back);
        var titles = UiKit.VBox(0);
        titles.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var name = UiKit.Text(title, 30);
        UiKit.Display(name);
        titles.AddChild(name);
        if (subtitle != null) titles.AddChild(UiKit.Text(subtitle, UiTheme.FontSmall, UiTheme.TextDim));
        header.AddChild(titles);
        column.AddChild(header);
        column.AddChild(UiKit.Line());

        var body = UiKit.VBox(12);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(body);
        return (body, header);
    }
}
