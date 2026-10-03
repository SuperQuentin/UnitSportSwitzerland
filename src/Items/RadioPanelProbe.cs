using System.Threading.Tasks;
using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--radiopanelcheck</c> (#375, #392), offline, <c>--world fixture</c>: a radio in the hand, Use
/// (a left click) opens its panel on the player view (no CD row shown, the screen's middle free), a
/// second click there closes it, then the mouse wheel goes through the hotbar; then again, closed
/// with Esc. Nothing was chosen, so nothing may play: the second click used to land on the CD row
/// under the centred cursor and start it (the chess type beat, first in the list). Last, the
/// library: its button shows the rows, a row plays, Play / Stop stops, the button goes back.
/// Read the <c>[radiopanel]</c> lines.
/// </summary>
public partial class RadioPanelProbe : Node
{
    public static bool Requested => CmdArgs.Has("--radiopanelcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public RadioPanelProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "RadioPanelProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[radiopanel] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null || ItemController.Instance is not { } items) { Log("RESULT: FAIL no local player"); GetTree().Quit(1); return; }
        // the list needs a CD in it: the chess type beat is burnt into the library at start
        for (int i = 0; i < 600 && CdLibrary.Instance is not { RatBeatId: >= 0 }; i++) await Wait(0.1);
        Log($"library ready: rat beat {CdLibrary.Instance?.RatBeatId}");

        var inv = items.Inventory;
        // a fresh radio in the first hotbar slot, nothing loaded
        inv.Put(0, new ItemStack(ItemId.Radio, 1));
        inv.Select(0);
        await Wait(0.5);

        // twice: closed by a second Use, then by Esc
        foreach (bool esc in new[] { false, true })
        {
            await Use();
            Check(RadioUi.Instance?.IsOpen == true, "Use opens the radio's panel");
            Check(Visible(RadioUi.LibraryLabel) && !RowsShown(), "on the player view: the library button, no CD row");
            await Wait(0.2);
            var cursor = Input.MouseMode == Input.MouseModeEnum.Visible && DisplayServer.GetName() != "headless"
                ? GetViewport().GetMousePosition() : GetViewport().GetVisibleRect().Size / 2f;
            Check(PanelRect() is { } r && !r.HasPoint(cursor), $"the cursor is not on the player ({cursor} vs {PanelRect()})");
            State("open");
            if (esc)
            {
                await Key(Godot.Key.Escape);
                Check(RadioUi.Instance?.IsOpen != true, "Esc closes it");
            }
            else
            {
                await Click();
                State("after a second Use");
                Check(RadioUi.Instance?.IsOpen != true, "a click off the player (where the cursor comes back) closes it");
                if (RadioUi.Instance?.IsOpen == true) await Key(Godot.Key.Escape);
            }
            State("closed");
            Check(Silent(inv), "closing without a choice plays nothing");

            await Key(Godot.Key.Space);   // a jump
            for (int i = 0; i < 3; i++) { await Wheel(MouseButton.WheelDown); State("wheel down"); }
            for (int i = 0; i < 3; i++) { await Wheel(MouseButton.WheelUp); State("wheel up"); }
            await Wait(1.0);
            Check(Silent(inv), "a jump and the wheel through the hotbar play nothing");
            inv.Select(0);
            await Wait(0.5);
        }

        await Library(inv);

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>The library (#392): its button shows the rows, a row plays, Stop stops, the button goes back.</summary>
    private async Task Library(Inventory inv)
    {
        await Use();
        await Wait(0.3);
        Shot("radiopanel_player.png");
        Check(Press(RadioUi.LibraryLabel) && RowsShown(), "the library button shows the CDs");
        await Wait(0.3);
        var row = Buttons().FirstOrDefault(b => b.IsVisibleInTree() && b.TooltipText == "Play");
        Check(row != null, "a CD row to play");
        row?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.5);
        Check(!Silent(inv), $"pressing \"{row?.Text}\" plays it");
        Check(RadioUi.Instance?.IsOpen == true && RowsShown(), "and the library stays open");
        Shot("radiopanel_library.png");
        Check(Press("■  Stop"), "Stop");
        await Wait(0.5);
        Check(Silent(inv), "Stop silences it");
        Check(Press(RadioUi.PlayerLabel) && !RowsShown(), "back to the player");
        await Key(Godot.Key.Escape);
        Check(RadioUi.Instance?.IsOpen != true, "Esc closes it");
    }

    /// <summary>A screenshot into test_output/ when windowed (headless has no image).</summary>
    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }

    private static IEnumerable<Button> Buttons() =>
        RadioUi.Instance?.FindChildren("*", nameof(Button), true, false).OfType<Button>() ?? Enumerable.Empty<Button>();

    private static bool Visible(string text) => Buttons().Any(b => b.IsVisibleInTree() && b.Text == text);

    private static bool Press(string text)
    {
        if (Buttons().FirstOrDefault(b => b.IsVisibleInTree() && b.Text == text) is not { } b) { Log($"no button \"{text}\""); return false; }
        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    /// <summary>Any CD or station row on screen (their tooltip says what a press does).</summary>
    private static bool RowsShown() => Buttons().Any(b => b.IsVisibleInTree() && b.TooltipText is "Play" or "Only the driver changes the music");

    private static Rect2? PanelRect() =>
        RadioUi.Instance?.GetChildren().OfType<PanelContainer>().FirstOrDefault() is { } p ? p.GetGlobalRect() : null;

    /// <summary>No radio stack holds a CD and no radio speaker sounds.</summary>
    private bool Silent(Inventory inv)
    {
        bool silent = true;
        for (int i = 0; i < inv.Capacity; i++)
            if (!inv[i].IsEmpty && inv[i].Id == ItemId.Radio && !string.IsNullOrEmpty(inv[i].Data))
            {
                Log($"slot {i} holds '{inv[i].Data}'");
                silent = false;
            }
        foreach (var speaker in GetTree().Root.FindChildren("*", nameof(RadioSpeaker), true, false).OfType<RadioSpeaker>())
            if (speaker.On && speaker.CdId != 0)
            {
                Log($"{speaker.GetPath()} plays CD {speaker.CdId} on bus {speaker.Bus}");
                silent = false;
            }
        return silent;
    }

    /// <summary>The Use key, as an action: a headless run has no captured mouse for the click to open with.</summary>
    private async Task Use()
    {
        foreach (bool down in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.UseItem, Pressed = down });
            await Wait(0.15);
        }
        await Wait(0.4);
    }

    /// <summary>A left click where a player's cursor is: the centre while captured, else wherever it was put.</summary>
    private async Task Click()
    {
        var at = Input.MouseMode == Input.MouseModeEnum.Visible ? GetViewport().GetMousePosition() : GetViewport().GetVisibleRect().Size / 2f;
        foreach (bool down in new[] { true, false })
        {
            // pushed into the viewport like a real click: the GUI sees it first, then the game
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at,
                ButtonMask = down ? MouseButtonMask.Left : 0,
            });
            await Wait(0.15);
        }
        await Wait(0.4);
    }

    private void State(string when)
    {
        var focus = GetViewport().GuiGetFocusOwner();
        var inv = ItemController.Instance!.Inventory;
        Log($"{when}: selected {inv.Selected} ({inv.HeldId}), panel {(RadioUi.Instance?.IsOpen == true ? "open" : "shut")}, focus {(focus == null ? "none" : $"{focus.GetType().Name} '{(focus as Button)?.Text}' visible {focus.IsVisibleInTree()}")}");
    }

    private async Task Key(Key key)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down });
        await Wait(0.4);
    }

    private async Task Wheel(MouseButton button)
    {
        var at = GetViewport().GetVisibleRect().Size / 2f;
        foreach (bool down in new[] { true, false })
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button, Pressed = down, Position = at, GlobalPosition = at, Factor = 1f });
        await Wait(0.3);
    }
}
