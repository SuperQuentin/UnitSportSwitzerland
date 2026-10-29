using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// <c>--swordclip combo|combomove|charged|plunge|dodge</c> (#1371, for <c>tools/record-clip.sh</c>): plays the sword for a camera, offline, through the
/// real input actions. Each scenario is filmed from the side and then from a three-quarter view, the camera fixed to the way the
/// body faced at the start (never turning with a spin). A dev tool: no pad or VR way.
/// </summary>
public partial class SwordClipProbe : Node
{
    public static string? Scenario => CmdArgs.Value("--swordclip");

    private readonly ItemController _items;
    private FootPlayer? _me;
    private float _yaw;
    private int _view;   // 0 free, 1 side, 2 three-quarter

    public SwordClipProbe(ItemController items)
    {
        _items = items;
        ProcessPriority = 1000;   // after the player's own camera, so the view below is the one drawn
    }

    public SwordClipProbe() : this(null!) { }

    public override async void _Ready()
    {
        double next = 0;
        while (GetViewport().GetCamera3D()?.GetParent() is not FootPlayer { } p || !p.IsOnFloor() || SwordService.Instance == null)
        {
            if (RealClock.Now > next && GetViewport().GetCamera3D()?.GetParent() is FootPlayer q)
            {
                next = RealClock.Now + 2;
                GD.Print($"[swordclip] waiting: y {q.GlobalPosition.Y:F0} ride {q.Ride} floor {q.IsOnFloor()}");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _me = (FootPlayer)GetViewport().GetCamera3D()!.GetParent();
        await Wait(1.5);
        _items.Inventory.Put(0, new ItemStack(ItemId.Sword, 1));
        _items.Inventory.Select(0);
        _me.LookYaw = _yaw = _me.Rotation.Y;
        await Wait(0.5);
        GD.Print($"[swordclip] {Scenario} go t={(long)(RealClock.Now * 1000)}");
        string what = Scenario ?? "combo";
        if (what is "combo" or "charged") GD.Print("[swordclip] " + SwordService.Instance!.Command(1, ""));
        for (int pass = 1; pass <= 2; pass++)
        {
            _view = pass;
            _me.LookYaw = _yaw;
            await Wait(0.8);
            switch (what)
            {
                case "combo": await Combo(false); break;
                case "combomove": await Combo(true); break;
                case "charged": await Charged(); break;
                case "plunge": await Plunge(); break;
                case "dodge": await Dodge(); break;
            }
            await Wait(1.2);
        }
        GD.Print("[swordclip] done");
        GetTree().Quit();
    }

    private async Task Combo(bool moving)
    {
        if (moving) Input.ActionPress(PlayerInput.MoveForward);
        for (int i = 0; i < 5; i++)
        {
            await Tap(PlayerInput.UseItem);
            await Wait(i == 4 ? 1.4 : 0.64);
        }
        Input.ActionRelease(PlayerInput.MoveForward);
    }

    private async Task Charged()
    {
        Input.ActionPress(PlayerInput.UseItem);
        await Wait(1.7);
        Input.ActionRelease(PlayerInput.UseItem);
        await Wait(1.8);
    }

    private async Task Plunge()
    {
        _me!.GlobalPosition += Vector3.Up * 6f;
        while (_me.Velocity.Y > -4f) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await Tap(PlayerInput.UseItem);
        await Wait(2.2);
    }

    private async Task Dodge()
    {
        foreach (var dir in new[] { PlayerInput.MoveForward, PlayerInput.MoveBack, PlayerInput.MoveLeft, PlayerInput.MoveRight })
        {
            Input.ActionPress(dir);
            await Wait(0.15);
            await Tap(PlayerInput.SwordDodge);
            Input.ActionRelease(dir);
            await Wait(1.0);
        }
    }

    private async Task Tap(string action)
    {
        Input.ActionPress(action);
        await Wait(0.07);
        Input.ActionRelease(action);
    }

    private async Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    public override void _Process(double delta)
    {
        if (_me == null || _view == 0 || _me.CameraOrNull is not { } cam) return;
        var anchor = _me.GlobalPosition;
        var fwd = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
        var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
        var at = anchor + Vector3.Up * 1.0f;
        var pos = _view == 1 ? anchor + right * 2.9f + Vector3.Up * 1.15f : anchor + fwd * 2.4f + right * 1.7f + Vector3.Up * 1.5f;
        var z = (pos - at).Normalized();
        var x = Vector3.Up.Cross(z).Normalized();
        cam.GlobalTransform = new Transform3D(new Basis(x, z.Cross(x), z), pos);
    }
}
