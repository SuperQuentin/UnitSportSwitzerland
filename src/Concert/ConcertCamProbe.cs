using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertcamcheck</c> (#885), offline on the real tiles (<c>--chunks &lt;main&gt;/terrain_chunks --at
/// 2498751,1114890 --concert 13 --time 22</c>): the camera crew's controls through the input map, as a
/// keyboard, a pad or VR's pad would send them. Takes the pit dolly (camera 2): pushing right runs it
/// along its rails, zooming tightens its lens; takes the wire cam (camera 4, the dolly is freed): up
/// climbs it under its cables; Esc leaves it. Windowed, saves the viewfinder and the kit
/// (<c>test_output/concert/crew_*.png</c>).
/// </summary>
public partial class ConcertCamProbe : Node
{
    public static bool Requested => CmdArgs.Has("--concertcamcheck");

    private readonly Func<FootPlayer?> _local;
    private readonly Func<ChatManager?> _chat;
    private int _failed;

    public ConcertCamProbe(Func<FootPlayer?> local, Func<ChatManager?> chat)
    {
        _local = local;
        _chat = chat;
        Name = "ConcertCamProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Check(bool ok, FormattableString what)
    {
        GD.Print($"[concertcam] {(ok ? "ok" : "FAIL")}: {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private async Task Hold(string action, double seconds)
    {
        Input.ActionPress(action);
        await Wait(seconds);
        Input.ActionRelease(action);
        await Wait(0.1);
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200 && (me == null || !me.IsOnFloor()); i++)
        {
            me = _local();
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        ConcertRig? rig = null;
        for (int i = 0; i < 600 && rig == null; i++)
        {
            rig = ConcertRig.Live.Values.FirstOrDefault();
            await Wait(0.1);
        }
        if (rig == null) { Finish("no concert"); return; }
        await Wait(3.0);
        Shot("crew_kit.png");

        _chat()?.Send("/concert camera take 2");
        await Wait(0.8);
        var cams = rig.Cameras;
        var dolly = cams.Rigs[1];
        Check(cams.Operating == 1 && dolly.Operator != 0, $"camera 2 ({dolly.Name}) taken");
        // from the middle of the track, both ends free
        dolly.S = 0.5f;
        float s0 = dolly.S, f0 = dolly.Fov;
        await Hold(PlayerInput.MoveRight, 1.5);
        await Hold(PlayerInput.MoveLeft, 0.1);
        Check(MathF.Abs(dolly.S - s0) > 0.05f, $"the stick runs it along its rails ({s0:F2} -> {dolly.S:F2})");
        await Hold(PlayerInput.CamZoomIn, 1.0);
        Check(dolly.Fov < f0 - 5f, $"zoom tightens its lens ({f0:F0}° -> {dolly.Fov:F0}°)");
        Shot("crew_dolly_viewfinder.png");

        _chat()?.Send("/concert camera take 4");
        await Wait(0.8);
        var wire = cams.Rigs[3];
        Check(cams.Operating == 3 && wire.Operator != 0 && dolly.Operator == 0, $"camera 4 ({wire.Name}) taken, the dolly freed");
        float y0 = wire.P.Y;
        await Hold(PlayerInput.FlyUp, 1.2);
        Check(wire.P.Y > y0 + 1f && wire.P.Y <= 26.01f, $"up climbs it under its cables ({y0:F1} -> {wire.P.Y:F1} m)");
        await Hold(PlayerInput.MoveForward, 1.5);
        Shot("crew_wire_viewfinder.png");

        Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = true });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = false });
        await Wait(0.8);
        Check(cams.Operating == -1 && wire.Operator == 0, $"Esc leaves the camera");
        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { GD.Print($"[concertcam] RESULT: FAIL {error}"); GetTree().Quit(1); return; }
        GD.Print(_failed == 0 ? "[concertcam] RESULT: ok" : $"[concertcam] RESULT: FAIL {_failed} checks");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/concert/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[concertcam] screenshot {path}");
    }
}
