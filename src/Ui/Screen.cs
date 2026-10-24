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
    /// <summary>The least room left around a framed page's panel, px of the menus' canvas.</summary>
    private const float FrameMargin = 20;

    protected (VBoxContainer Body, HBoxContainer Header) Framed(string title, string? subtitle, Vector2 size)
    {
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var panel = new PanelContainer { CustomMinimumSize = size, ThemeTypeVariation = Skins.SkinTheme.Page };
        centre.AddChild(panel);
        // never taller or wider than the menus' canvas (1152 x 648 before stretching), with a margin: a page asks for its
        // size and gets what fits; its body scrolls (#1209: the Account page ran off the top and bottom of the screen)
        void Fit()
        {
            if (!IsInsideTree()) return;
            var room = GetViewportRect().Size - new Vector2(2 * FrameMargin, 2 * FrameMargin);
            panel.CustomMinimumSize = new Vector2(Mathf.Min(size.X, room.X), Mathf.Min(size.Y, room.Y));
        }
        Ready += Fit;
        Resized += Fit;

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
