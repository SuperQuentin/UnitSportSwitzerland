using System.Threading.Tasks;
using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--sparklecheck</c> (#387), offline and windowed (the glints are not built headless),
/// <c>--world fixture</c>: a radio stood in front of the camera plays the chess type beat; its
/// sparkles must fade in, kick on the beat (screenshots on and off it, <c>test_output/sparkles_*.png</c>),
/// and be gone once it stops. Read the <c>[sparkles]</c> lines.
/// </summary>
public partial class SparkleProbe : Node
{
    public static bool Requested => CmdArgs.Has("--sparklecheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public SparkleProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "SparkleProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[sparkles] {what}");

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
        for (int i = 0; i < 600 && CdLibrary.Instance is not { RatBeatId: >= 0 }; i++) await Wait(0.1);
        if (me == null || RadioManager.Instance is not { } radios || CdLibrary.Instance?.Find(CdLibrary.Instance.RatBeatId) is not { } cd)
        {
            Log("RESULT: FAIL no player, radio manager or CD");
            GetTree().Quit(1);
            return;
        }
        await Wait(1.0);

        // stood 1.4 m in front of the camera, a little below its eye, facing it
        var cam = GetViewport().GetCamera3D();
        var fwd = (-cam.GlobalBasis.Z with { Y = 0 }).Normalized();
        var at = cam.GlobalPosition + fwd * 1.4f + Vector3.Down * 0.35f;
        float yaw = Mathf.Atan2(fwd.X, fwd.Z);
        radios.Throw(new RadioState("", 0, radios.Origin.ToGlobal(at), yaw, Vector3.Zero, Settled: true));
        await Wait(0.2);
        RadioBody? radio = null;
        foreach (var n in radios.GetChildren()) if (n is RadioBody r) radio = r;
        if (radio?.Sparkles is not { } sparkles) { Log("RESULT: FAIL no radio or no sparkles on it"); GetTree().Quit(1); return; }
        Check(!sparkles.Visible, "silent radio: no sparkles");

        radios.Play(radio, cd.Id, cd.Duration);
        for (int i = 0; i < 100 && !(radio.Speaker?.Playing ?? false); i++) await Wait(0.1);
        await Wait(1.0);
        Check(sparkles.Visible && sparkles.Shown > 0.99f, $"playing: sparkles at full ({sparkles.Shown:0.00})");

        // one shot just on a beat, one half a beat later
        await ShootAt(radio, 0.02f, "sparkles_beat.png");
        await ShootAt(radio, 0.5f, "sparkles_offbeat.png");

        radios.Stop(radio);
        await Wait(1.5);
        Check(!sparkles.Visible, $"stopped: sparkles gone ({sparkles.Shown:0.00})");

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED {_failed} check(s)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private async Task ShootAt(RadioBody radio, float wantPhase, string file)
    {
        for (int i = 0; i < 600; i++)
        {
            if (radio.BeatAt(Net.ClockSync.ServerNow, out float phase, out _, out _, out _) && Mathf.Abs(phase - wantPhase) < 0.04f) break;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, file);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        Check(err == Error.Ok, $"wrote {path}");
    }
}
