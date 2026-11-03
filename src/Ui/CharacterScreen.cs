using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The Character menu (#1127): the figure editor (<see cref="CharacterPage"/>) on a page of its own, out of Settings.
/// Ways in: the title screen and the pause menu (Character), P on foot (<see cref="PlayerInput.CharacterMenu"/>; in a
/// vehicle P is the previous radio station), and the quick menu's Character wedge on a pad or in VR.
/// </summary>
public partial class CharacterScreen : Screen
{
    public static CharacterScreen Create() => new() { Name = "Character" };

    /// <summary>Opens it over the world (the key, the quick menu); does nothing when it is already in front.</summary>
    public static void Open()
    {
        if (GameShell.Instance is not { InWorld: true } shell || shell.Top is CharacterScreen) return;
        shell.Push(Create());
    }

    public override void _Ready()
    {
        if (Shell.InWorld)
        {
            // the world dimmed behind the page, as under the pause menu
            var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(dim);
        }
        var (body, _) = Framed("Character", "Who your figure is: everyone online sees it", new Vector2(980, 600));
        var (scroll, rows) = UiKit.ScrollPage(4);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(scroll);
        CharacterPage.Fill(rows);
    }
}
