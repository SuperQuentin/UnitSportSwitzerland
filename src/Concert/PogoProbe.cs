using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Concert;

/// <summary>
/// <c>--pogocheck --world fixture</c> (#1150), offline: the concert pogo QTE on a stand-in show
/// (<see cref="ConcertRig.ProbeMusic"/>: the bundled rat beat, around the player). Dancing, the ring
/// shows and the jump key is the QTE's; six presses on the beat grade Perfect or Good, put the dancer
/// into the Pogo emote and never lift the body; two beats let go by drop back to the dance; a press
/// on the off-beat is a Miss; the show over, the ring goes and the jump is the body's again.
/// Windowed, it saves <c>test_output/pogo.png</c>. Read the <c>[pogo]</c> lines.
/// </summary>
public partial class PogoProbe : Node
{
    public static bool Requested => CmdArgs.Has("--pogocheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public PogoProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "PogoProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await Frame();
    }

    private static void Log(string what) => GD.Print($"[pogo] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private void Finish()
    {
        ConcertRig.ProbeMusic = null;
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private async Task Run()
    {
        // the body on the floor and the bundled beat burnt, on the wall clock (threaded work)
        ulong until = Time.GetTicksMsec() + 60_000;
        FootPlayer? me = null;
        int cd = -1;
        while (Time.GetTicksMsec() < until)
        {
            me = _local();
            cd = Audio.Cd.CdLibrary.Instance is { RatBeatId: >= 0 } lib && lib.Find(lib.RatBeatId) != null ? lib.RatBeatId : -1;
            if (me != null && me.IsOnFloor() && cd >= 0) break;
            if (me == null)
            {
                // the run starts off foot: T steps out, as the emote wheel's probe does
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Frames(6);
        }
        var qte = GetTree().Root.FindChild("PogoQte", true, false) as PogoQte;
        if (me == null || cd < 0 || qte == null)
        {
            Log($"RESULT: FAIL no local player ({me != null}), no bundled beat ({cd}) or no QTE ({qte != null})");
            GetTree().Quit(1);
            return;
        }

        // settled: free to dance (a fresh spawn lands, or slides off the step, first)
        for (int i = 0; i < 600 && !me.CanEmote; i++) await Frame();
        Check(me.CanEmote, "the body is free to dance");
        double start = Net.ClockSync.ServerNow - 1.0;
        var body = me;
        ConcertRig.ProbeMusic = _ => new Items.RadioManager.Music(body, cd, start);
        await Frames(10);
        Check(!qte.Showing, "no ring before the dance");
        me.DanceId = 1;
        await Frames(10);
        Check(me.DanceId == 1 && qte.Showing && me.JumpTaken, $"dancing at the show: the ring shows and the jump is the QTE's (DanceId {me.DanceId}, showing {qte.Showing})");

        // six presses on the beat as heard
        float maxRise = 0f;
        int pressed = 0, lastBeat = int.MinValue, frames = 0;
        while (pressed < 6 && frames++ < 1200)
        {
            await Frame();
            maxRise = Mathf.Max(maxRise, me.Velocity.Y);
            if (!PogoQte.Heard(ConcertRig.MusicNear(me.GlobalPosition)!.Value, out double beats, out _)) continue;
            int n = (int)System.Math.Floor(beats);
            if (beats - n > 0.06 || n == lastBeat) continue;
            lastBeat = n;
            Input.ActionPress(PlayerInput.Jump);
            await Frame();
            Input.ActionRelease(PlayerInput.Jump);
            pressed++;
        }
        var judge = qte.Judge;
        Check(pressed == 6 && judge.Combo >= 6 && judge.Last is PogoGrade.Perfect or PogoGrade.Good,
            $"six presses on the beat: combo {judge.Combo}, last {judge.Last}");
        Check(me.DanceId == PogoQte.PogoDanceId, $"and the dancer pogos (DanceId {me.DanceId}, Pogo {PogoQte.PogoDanceId})");
        Check(maxRise < 0.5f, $"the press never jumped the body (rise {maxRise:F2} m/s)");
        await Frames(20);
        Check(me.DrawnDance is { } d && d.Move == Avatar.HumanMeshBuilder.EmoteMoveAt(PogoQte.PogoDanceId - FootPlayer.EmoteDanceBase, d.Bar),
            $"the figure draws the Pogo ({me.DrawnDance?.Move})");
        if (DisplayServer.GetName() != "headless")
        {
            System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://test_output"));
            GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://test_output/pogo.png"));
        }

        // let beats go by: back to the dance
        for (int i = 0; i < 600 && me.DanceId != 1; i++) await Frame();
        Check(me.DanceId == 1 && !judge.Pogoing && judge.Combo == 0, $"two beats let go by: back to the dance (DanceId {me.DanceId}, combo {judge.Combo})");

        // the off-beat
        for (int i = 0; i < 600; i++)
        {
            await Frame();
            if (PogoQte.Heard(ConcertRig.MusicNear(me.GlobalPosition)!.Value, out double beats, out _) && System.Math.Abs(beats - System.Math.Floor(beats) - 0.5) < 0.05) break;
        }
        Input.ActionPress(PlayerInput.Jump);
        await Frame();
        Input.ActionRelease(PlayerInput.Jump);
        await Frames(2);
        Check(judge.Last == PogoGrade.Miss && me.DanceId == 1, $"a press on the off-beat is a Miss ({judge.Last}) and no pogo (DanceId {me.DanceId})");

        // the show over
        ConcertRig.ProbeMusic = null;
        await Frames(30);
        Check(!qte.Showing && !me.JumpTaken && me.DanceId == 0, $"the show over: no ring, the jump is the body's, the dance stops (DanceId {me.DanceId})");
        Finish();
    }
}
