using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--emotewheelcheck</c> (#404), offline, <c>--world fixture</c>: the emote wheel driven with real
/// input events. Hold B, push the captured mouse up, let go: the first emote plays with no music
/// anywhere. Again on the next page, aimed at its third slot. A tap stops it, a second tap plays it
/// again, Esc closes the wheel without a change, E stops the emote. Windowed, it saves
/// <c>test_output/emotewheel_*.png</c>. Read the <c>[emotewheel]</c> lines.
/// </summary>
public partial class EmoteWheelProbe : Node
{
    public static bool Requested => CmdArgs.Has("--emotewheelcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public EmoteWheelProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "EmoteWheelProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[emotewheel] {what}");

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
        var wheel = GetTree().Root.FindChild("EmoteWheel", true, false) as EmoteWheel;
        if (me == null || wheel == null) { Log("RESULT: FAIL no local player or no wheel"); GetTree().Quit(1); return; }
        await Wait(0.5);

        // hold, aim up (the first slot), let go: Wave, with no music anywhere
        await Hold(true);
        Check(wheel.IsOpen, "holding B opens the wheel");
        await Aim(0f);
        Shot("emotewheel_open.png");
        await Hold(false);
        Check(!wheel.IsOpen, "letting go closes it");
        Check(me.Emote == 0, $"aimed up on the first page: the first emote ({Avatar.HumanMeshBuilder.EmoteName(0)}), got {me.Emote}");
        await Wait(1.6);
        Check(me.DanceId == FootPlayer.EmoteDanceBase, "it goes on with no music near");
        Shot("emotewheel_wave.png");

        // the next page by the mouse wheel, its third slot
        await Hold(true);
        Check(wheel.IsOpen && me.DanceId != 0, "the wheel opens while emoting");
        await WheelDown();
        await Aim(2f * Mathf.Tau / Avatar.HumanMeshBuilder.EmotesPerPage);
        Shot("emotewheel_page2.png");
        await Hold(false);
        int expect = Avatar.HumanMeshBuilder.EmotesPerPage + 2;
        Check(me.Emote == expect, $"page 2, third slot: {Avatar.HumanMeshBuilder.EmoteName(expect)}, got {me.Emote}");
        await Wait(1.6);
        Shot("emotewheel_dance.png");

        // a tap stops it, another plays it again
        await Hold(true, 0.05);
        await Hold(false);
        Check(me.DanceId == 0, "a tap stops the emote");
        await Hold(true, 0.05);
        await Hold(false);
        Check(me.Emote == expect, "a second tap plays it again");

        // Esc closes without a change
        await Hold(true);
        await Aim(0f);
        await Key(Godot.Key.Escape);
        Check(!wheel.IsOpen, "Esc closes the wheel");
        await Hold(false);
        Check(me.Emote == expect, "and changes nothing");

        // E stops it, as it stops a dance to the radio
        foreach (bool down in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = down });
            await Wait(0.15);
        }
        await Wait(0.3);
        Check(me.DanceId == 0, "E stops the emote");

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private async Task Hold(bool down, double after = 0.3)
    {
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.EmoteWheel, Pressed = down });
        await Wait(after);
    }

    /// <summary>The captured mouse pushed 120 px toward <paramref name="angle"/> (0 up, clockwise), as a player aims.</summary>
    private async Task Aim(float angle)
    {
        var d = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * 120f;
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = d });
        await Wait(0.3);
    }

    private async Task WheelDown()
    {
        var at = GetViewport().GetVisibleRect().Size / 2f;
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = down, Position = at, Factor = 1f });
        await Wait(0.3);
    }

    private async Task Key(Key key)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down });
        await Wait(0.3);
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
}
