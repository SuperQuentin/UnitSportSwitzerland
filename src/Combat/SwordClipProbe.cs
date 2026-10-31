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
        GameSettings.Current.AutoHolster = false;   // the probes idle for seconds with the sword out (#1369 would put it away)
        _items = items;
        ProcessPriority = 1000;   // after the player's own camera, so the view below is the one drawn
    }

    public SwordClipProbe() : this(null!) { }

    public override async void _Ready()
    {
        GameSettings.Current.AutoHolster = false;   // the probes idle for seconds with the sword out (#1369 would put it away)
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
        if (what is "combo" or "charged" or "hit" or "miss") GD.Print("[swordclip] " + SwordService.Instance!.Command(1, ""));
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
                case "grip": await Grip(); pass = 3; break;
                case "hit": await Hit(false); break;
                case "miss": await Hit(true); break;
            }
            await Wait(1.2);
        }
        if (_me != null) GD.Print($"[swordclip] hand audit: the old hand frame was up to {_me.HandBoneMaxDeg:F1} degrees off the skinned hand bone, and its guessed socket up to {_me.HandSocketMaxCm:F1} cm off the fist's ring centre");
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

    /// <summary>Two swings at the dummy, from where the lunge brings the blade onto it, or from 4 m back where nothing can touch it (the debug draw shows the shapes).</summary>
    private async Task Hit(bool far)
    {
        if (far)
        {
            // 3.5 m back from where it stands: out of what a lunge and the blade can cover
            _me!.GlobalPosition += new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw)) * 3.5f;
            await Wait(0.8);
        }
        for (int i = 0; i < 2; i++)
        {
            await Tap(PlayerInput.UseItem);
            await Wait(0.9);
        }
        await Wait(0.8);
    }

    private int _gripView;
    private static readonly string[] GripNames = { "", "palm", "back", "thumb", "below" };

    /// <summary>
    /// Close-ups of the hand holding the sword, from the posed in-game figure: four angles (palm, back, thumb side, below), each at rest, mid-strike of
    /// swing 1, mid-strike of swing 4 and in the charged cut's two-handed hold. Stills in <c>test_output/grip_game_*.png</c>.
    /// </summary>
    private async Task Grip()
    {
        string style = CmdArgs.Value("--style") ?? "cartoon";
        for (int v = 1; v <= 4; v++)
        {
            _gripView = v;
            await Wait(0.5);
            await Still($"{style}_{GripNames[v]}_rest");
            await Tap(PlayerInput.UseItem);
            await Wait(0.33);
            await Still($"{style}_{GripNames[v]}_swing1");
            await Wait(0.9);
            for (int i = 0; i < 3; i++)
            {
                await Tap(PlayerInput.UseItem);
                await Wait(i == 2 ? 0.33 : 0.64);
            }
            await Still($"{style}_{GripNames[v]}_swing4");
            await Wait(1.4);
            Input.ActionPress(PlayerInput.UseItem);
            await Wait(1.55);
            await Still($"{style}_{GripNames[v]}_twohand");
            Input.ActionRelease(PlayerInput.UseItem);
            await Wait(1.6);
        }
        _gripView = 0;
    }

    private async Task Still(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        string path = ProjectSettings.GlobalizePath("res://test_output/grip_game_" + name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[swordclip] still {path}");
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
        string zoom = CmdArgs.Value("--swordclipview") ?? "";
        if (_gripView > 0 && _me.GripLocal is { } g)
        {
            var target = _me.GlobalTransform * g.Origin;
            var bodyFwd = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
            var bodyRight = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
            var from = _gripView switch
            {
                1 => target - bodyRight * 0.42f + bodyFwd * 0.05f,   // the palm side: from the body's inside
                2 => target + bodyRight * 0.42f + bodyFwd * 0.05f,   // the back of the hand
                3 => target + bodyFwd * 0.42f + Vector3.Up * 0.05f,  // the thumb side, from in front
                _ => target + Vector3.Down * 0.42f + bodyFwd * 0.12f, // from below
            };
            var zg = (from - target).Normalized();
            var upRef = Mathf.Abs(zg.Y) > 0.95f ? bodyFwd : Vector3.Up;
            var xg = upRef.Cross(zg).Normalized();
            cam.GlobalTransform = new Transform3D(new Basis(xg, zg.Cross(xg), zg), from);
            return;
        }
        var fwd = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
        var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
        var at = anchor + Vector3.Up * 1.0f;
        if (zoom == "hand" && _me.HandLocal is { } hand)
        {
            // close on the sword hand: the grip never slides or pops in the fist
            at = _me.GlobalTransform * hand.Origin;
            var posH = _view == 1 ? at + right * 0.85f + Vector3.Up * 0.15f : at + fwd * 0.7f + right * 0.55f + Vector3.Up * 0.35f;
            var zh = (posH - at).Normalized();
            var xh = Vector3.Up.Cross(zh).Normalized();
            cam.GlobalTransform = new Transform3D(new Basis(xh, zh.Cross(xh), zh), posH);
            return;
        }
        if (zoom == "off")
        {
            // the off arm: the upper body from its own side, then three-quarter from behind
            at = anchor + Vector3.Up * 1.15f;
            var posO = _view == 1 ? at - right * 2.0f + Vector3.Up * 0.1f : at - right * 1.4f - fwd * 1.3f + Vector3.Up * 0.3f;   // the off (left) side, then from behind it: the dummy stands in front
            var zo = (posO - at).Normalized();
            var xo = Vector3.Up.Cross(zo).Normalized();
            cam.GlobalTransform = new Transform3D(new Basis(xo, zo.Cross(xo), zo), posO);
            return;
        }
        var pos = _view == 1 ? anchor + right * 2.9f + Vector3.Up * 1.15f : anchor + fwd * 2.4f + right * 1.7f + Vector3.Up * 1.5f;
        var z = (pos - at).Normalized();
        var x = Vector3.Up.Cross(z).Normalized();
        cam.GlobalTransform = new Transform3D(new Basis(x, z.Cross(x), z), pos);
    }
}
