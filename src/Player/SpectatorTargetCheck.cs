using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--targetcheck</c> (#1850), headless, under <c>--frame earth</c>: the spectator's target cue on its keyboard and pad
/// ways (VR's right stick down reaches the same D-pad right through <c>XrPad</c>). A tap of ] aims at the Moon, a tap of
/// the pad's D-pad right steps to Mars, a hold turns the view onto Mars within a degree, the list wraps through none.
/// </summary>
public partial class SpectatorTargetCheck : Node
{
    public static bool Requested => CmdArgs.Has("--targetcheck");

    private int _failed;

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[targetcheck] {what}"));

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[targetcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Key(bool down) => Input.ParseInputEvent(new InputEventKey
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.Bracketright, Keycode = Godot.Key.Bracketright,
        KeyLabel = Godot.Key.Bracketright, Pressed = down,
    });

    private static void Pad(bool down) => Input.ParseInputEvent(new InputEventJoypadButton
    {
        Device = 0, ButtonIndex = JoyButton.DpadRight, Pressed = down,
    });

    private async Task Tap(System.Action<bool> press)
    {
        press(true);
        await Wait(0.1);
        press(false);
        await Wait(0.1);
    }

    private async Task Run()
    {
        await Wait(3);
        var cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as SpectatorCamera;
        var targets = SpectatorTargets.Instance;
        if (cam == null || targets == null) { Finish("no spectator camera or no target cue"); return; }
        cam.MakeCurrent();
        await Wait(0.5);

        Expect(targets.Target == null, "no target to begin with");
        await Tap(Key);
        Expect(targets.Target == BodyId.Moon, $"] tapped: the Moon (got {targets.Target})");
        await Wait(0.6);
        Shot("target_moon");
        await Tap(Pad);
        Expect(targets.Target == BodyId.Mars, $"the pad's D-pad right tapped: Mars (got {targets.Target})");
        Expect(targets.Toward is { Distance: > 5e10 }, $"Mars' distance read ({targets.Toward?.Distance / 1e9:F1} million km)");

        // hold: the view turns onto Mars (a quarter turn a second, so three seconds turn it anywhere)
        Key(true);
        await Wait(4);
        bool aligning = targets.Aligning;
        Key(false);
        await Wait(0.1);
        Expect(aligning, "a hold turns the view");
        Expect(targets.Target == BodyId.Mars, "a hold does not step on");
        await Wait(0.6);
        Shot("target_mars");
        if (targets.Toward is { } toward)
        {
            float off = Mathf.RadToDeg((-cam.GlobalBasis.Z).AngleTo(toward.Direction));
            Expect(off < 1f, $"the view is on Mars after the hold ({off:F2}° off)");
        }

        // through the whole list and back to none
        int steps = 0;
        while (targets.Target != null && steps < 30)
        {
            await Tap(Key);
            steps++;
        }
        Expect(targets.Target == null && steps == TargetList.Order.Length - 2, $"the list wraps through none ({steps} taps from Mars)");
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    /// <summary>A windowed run's picture (<c>test_output/target/NAME.png</c>).</summary>
    private void Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        var path = ProjectSettings.GlobalizePath($"res://test_output/target/{name}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"saved {path}");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[targetcheck] RESULT: ok");
        else GD.PrintErr($"[targetcheck] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
