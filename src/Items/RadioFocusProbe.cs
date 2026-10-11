using System.Threading.Tasks;
using Godot;
using UnitSport.Audio;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--radiofocuscheck</c> (#813), offline and windowed (radios get no speaker headless),
/// <c>--world fixture</c>: two radios play, one 4 m from the ears, one 25 m off. The near one must
/// have the focus at its own level, the far one duck by <see cref="RadioFocus.DuckDb"/>; once the
/// near one stops, the far one takes the focus and comes back up. Read the <c>[radiofocus]</c> lines.
/// </summary>
public partial class RadioFocusProbe : Node
{
    public static bool Requested => CmdArgs.Has("--radiofocuscheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public RadioFocusProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "RadioFocusProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[radiofocus] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private void Done()
    {
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED {_failed} check(s)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
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

        var ears = Ears.Of(this) ?? me.GlobalPosition;
        var fwd = (-GetViewport().GetCamera3D().GlobalBasis.Z with { Y = 0 }).Normalized();
        var side = fwd.Cross(Vector3.Up);
        var near = Place(radios, me.GlobalPosition + fwd * 4f);
        var far = Place(radios, me.GlobalPosition + fwd * 12f + side * 22f);
        await Wait(0.3);
        if (Find(radios, near) is not { Speaker: { } a } nearRadio || Find(radios, far) is not { Speaker: { } b } farRadio)
        {
            Log("RESULT: FAIL the radios or their speakers are missing");
            GetTree().Quit(1);
            return;
        }

        radios.Play(nearRadio, cd.Id, cd.Duration);
        radios.Play(farRadio, cd.Id, cd.Duration);
        for (int i = 0; i < 100 && !(a.Playing && b.Playing); i++) await Wait(0.1);
        Check(a.Playing && b.Playing, "both radios play");
        Log($"ears at {ears}, near {a.GlobalPosition.DistanceTo(ears):0.0} m, far {b.GlobalPosition.DistanceTo(ears):0.0} m");

        await Wait(1.6);
        Check(MusicFocus.Focus == a, "the near radio has the focus");
        Check(a.DuckDb == 0f, $"the near radio plays at its own level (duck {a.DuckDb:0.0} dB)");
        Check(Mathf.IsEqualApprox(b.DuckDb, RadioFocus.DuckDb), $"the far radio ducks under it ({b.DuckDb:0.0} dB)");
        Check(Mathf.IsEqualApprox(b.VolumeDb, b.SourceDb + RadioFocus.DuckDb), $"the duck is in its volume ({b.VolumeDb:0.0} dB from {b.SourceDb:0.0})");

        radios.Stop(nearRadio);
        await Wait(0.5);
        Check(MusicFocus.Focus == b, "the near one stopped: the far one has the focus");
        Check(b.DuckDb > RadioFocus.DuckDb && b.DuckDb < 0f, $"it fades back up, not at once ({b.DuckDb:0.0} dB after 0.5 s)");
        await Wait(1.0);
        Check(b.DuckDb == 0f, $"and is at its own level a second later ({b.DuckDb:0.0} dB)");

        radios.Stop(farRadio);
        await Wait(0.5);
        Check(MusicFocus.Focus == null, "both stopped: no focus");
        Done();
    }

    /// <summary>A radio set down settled at <paramref name="at"/>, by the name it will get.</summary>
    private static Vector3 Place(RadioManager radios, Vector3 at)
    {
        radios.Throw(new RadioState("", 0, radios.Origin.ToGlobal(at), 0f, Vector3.Zero, Settled: true));
        return at;
    }

    private static RadioBody? Find(RadioManager radios, Vector3 at)
    {
        RadioBody? best = null;
        float d = 2f;
        foreach (var n in radios.GetChildren())
            if (n is RadioBody r && r.GlobalPosition.DistanceTo(at) is var dist && dist < d) { d = dist; best = r; }
        return best;
    }
}
