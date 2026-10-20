using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The world dropdown of Play solo and the Host dialog (#999): the open world, then the test worlds
/// under their own heading, with a line saying what the picked one is. Items carry their index in
/// <see cref="WorldChoice.All"/> as their id, so the heading never shifts what is picked.
/// </summary>
public static class WorldPicker
{
    public static (OptionButton Picker, Label Blurb) Create(string? selectedId)
    {
        var picker = new OptionButton { CustomMinimumSize = new Vector2(0, 36), FocusMode = Control.FocusModeEnum.All, FitToLongestItem = false };
        picker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        picker.ClipText = true;
        var blurb = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
        for (int i = 0; i < WorldChoice.All.Length; i++)
        {
            if (i == 1) picker.AddSeparator("Test worlds");
            picker.AddItem(WorldChoice.All[i].Title, i);
        }
        int at = Math.Max(0, Array.FindIndex(WorldChoice.All, w => w.Id == selectedId));
        picker.Select(picker.GetItemIndex(at));
        void Show() => blurb.Text = Picked(picker).Blurb;
        picker.ItemSelected += _ => Show();
        Show();
        return (picker, blurb);
    }

    public static WorldChoice Picked(OptionButton picker) =>
        picker.GetSelectedId() is var id and >= 0 && id < WorldChoice.All.Length ? WorldChoice.All[id] : WorldChoice.Open;
}
