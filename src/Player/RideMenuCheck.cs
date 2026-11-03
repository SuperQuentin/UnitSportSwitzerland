using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--ridemenucheck --world flat --systems physics,ui</c> (#1167): the travel picker's Bicycles tab
/// driven the way each device drives it, through the real input path and nothing else:
/// <list type="bullet">
/// <item><b>pad</b>: joypad events (device 0): RB to the Bicycles tab, D-pad → to the e-bike, A rides it;</item>
/// <item><b>VR controllers</b>: the same buttons from <see cref="XrPad"/>'s device (7), which is what the
/// Touch controllers replay as;</item>
/// <item><b>VR hand pointer</b>: what <c>XrUi</c> sends, a mouse move and a left click at window
/// coordinates: on the tab, on the low frame's card, then on Ride.</item>
/// </list>
/// Each time the player must end up on the bike picked, and the picker closed.
/// </summary>
public partial class RideMenuCheck : Node
{
    public static bool Requested => CmdArgs.Has("--ridemenucheck");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private RideUi? _ui;
    private int _failures;
    private bool _started;

    public RideMenuCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[ridemenu] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_started) return;
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        if (!TestWorld.TryGround(null, at, out float ground)) return;
        _started = true;
        _player = new FootPlayer { Name = "Probe", Origin = _origin };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + 1.0f, at.Z);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ui = RideUi.Create();
        _ui.ActivePlayer = () => _player;
        AddChild(_ui);
        _ = Guarded();
    }

    private async Task Guarded()
    {
        try { await Run(); }
        catch (System.Exception e) { Expect(false, $"threw {e.GetType().Name}: {e.Message}"); }
        Finish();
    }

    // ---- the pad ---------------------------------------------------------------------------------

    private async Task Button(int device, JoyButton button)
    {
        Input.ParseInputEvent(new InputEventJoypadButton { Device = device, ButtonIndex = button, Pressed = true, Pressure = 1f });
        await Frames(3);
        Input.ParseInputEvent(new InputEventJoypadButton { Device = device, ButtonIndex = button, Pressed = false });
        await Frames(3);
    }

    /// <summary>On foot, opens the picker, then RB, D-pad → and A on <paramref name="device"/>.</summary>
    private async Task ByPad(int device, string who)
    {
        var me = _player!;
        me.SetRide(RideKind.OnFoot);
        await Frames(5);
        // a button first, as a player picks the pad up: the picker then focuses a card for the D-pad
        await Button(device, JoyButton.DpadDown);
        Expect(PlayerInput.LastDevice == InputDevice.Gamepad, $"{who}: the picker sees a pad");
        _ui!.Open();
        await Frames(10);
        Expect(_ui.IsOpen && FocusedLabel() == "On foot", $"{who}: opens on Mounts, \"On foot\" focused (\"{FocusedLabel()}\")");
        await Button(device, JoyButton.RightShoulder);
        Expect(FocusedLabel() == "Road bike", $"{who}: RB, the Bicycles tab, its first card focused (\"{FocusedLabel()}\")");
        await Button(device, JoyButton.DpadRight);
        Expect(FocusedLabel() == "E-bike", $"{who}: D-pad →, the e-bike (\"{FocusedLabel()}\")");
        await Button(device, JoyButton.A);
        await Frames(10);
        Expect(me.Ride == RideKind.EBike && !_ui.IsOpen, $"{who}: A rides it ({me.Ride}, picker {(_ui.IsOpen ? "open" : "closed")})");
    }

    // ---- the VR pointer --------------------------------------------------------------------------

    /// <summary>What <c>XrUi.Click</c> sends where its ray meets the panel: a move, a press and a release.</summary>
    private async Task Click(Control c)
    {
        var at = GetViewport().GetFinalTransform() * (c.GetGlobalTransformWithCanvas() * (c.Size / 2f));
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
        await Frames(4);
    }

    private async Task ByPointer()
    {
        var me = _player!;
        me.SetRide(RideKind.OnFoot);
        await Frames(5);
        _ui!.Open();
        await Frames(10);
        var tab = Buttons().FirstOrDefault(b => b.Text.StartsWith("Bicycles"));
        Expect(tab != null, "VR pointer: the Bicycles tab is there");
        if (tab == null) return;
        await Click(tab);
        var card = Buttons().FirstOrDefault(b => b.IsVisibleInTree() && LabelOf(b) == "E-bike (low frame)");
        Expect(card != null, "VR pointer: the tab shows the low frame's card");
        if (card == null) return;
        await Click(card);
        var ride = Buttons().FirstOrDefault(b => b.IsVisibleInTree() && b.Text == "Ride");
        Expect(ride != null, "VR pointer: a Ride button");
        if (ride == null) return;
        await Click(ride);
        await Frames(10);
        Expect(me.Ride == RideKind.EBikeOpen && !_ui.IsOpen, $"VR pointer: tab, card, Ride: on the low frame ({me.Ride}, picker {(_ui.IsOpen ? "open" : "closed")})");
    }

    // ---- reading the picker as it is drawn -------------------------------------------------------

    private System.Collections.Generic.IEnumerable<Button> Buttons() => All(_ui!).OfType<Button>();

    private static System.Collections.Generic.IEnumerable<Node> All(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    /// <summary>The card name a button shows: its first label's text.</summary>
    private static string? LabelOf(Button b) => All(b).OfType<Label>().FirstOrDefault()?.Text;

    private string? FocusedLabel() => GetViewport().GuiGetFocusOwner() is Button b ? LabelOf(b) : null;

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ByPad(0, "pad");
        _ui!.Close();
        await ByPad(XrPad.DeviceId, "VR controllers");
        _ui.Close();
        await ByPointer();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[ridemenu] RESULT: ok — the Bicycles tab picks the e-bikes by pad, VR controllers and VR pointer"
            : $"[ridemenu] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
