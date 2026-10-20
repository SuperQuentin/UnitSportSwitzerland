using Godot;
using UnitSport.Core;
using UnitSport.Playtest.TopView;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// <c>--playtest-recordcheck</c> (#844, quick tier): a recorded session on the flat course, with
/// <c>--playtest --playtest-record test_output/playtest/recordcheck</c>. It sets up a scenario that
/// places a car, drops a scripted note, ends the session after 20 s and reads the bundle back: the
/// trace has the player and the car where the scenario put it, the note has its top view (and its
/// screenshot, with a screen), report.md lists both, and <c>--topview</c>'s drawing works on it.
/// </summary>
public partial class RecorderCheck : Node
{
    public static bool Requested => CmdArgs.Has("--playtest-recordcheck");
    private const string Scenario = "collide-car-parked";

    private readonly PlaytestRecorder _recorder;
    private int _failed;

    public RecorderCheck(PlaytestRecorder recorder)
    {
        _recorder = recorder;
        Name = "RecorderCheck";
    }

    private void Expect(bool ok, string what)
    {
        if (!ok) _failed++;
        GD.Print($"[recordcheck] {(ok ? "ok  " : "FAIL")} {what}");
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds)
    {
        double until = _recorder.Now + seconds;
        while (_recorder.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Run()
    {
        MouseCapture.Disabled = true;
        await Wait(2);
        var director = PlaytestDirector.Instance;
        Expect(director != null, "the playtest director runs (--playtest)");
        var entry = director?.Entries.FirstOrDefault(e => e.Id == Scenario);
        Expect(entry != null, $"scenario {Scenario} exists");
        if (director == null || entry == null) { Done(); return; }
        string started = await director.Start(entry);
        Expect(director.SetupError == null, $"{Scenario} set up ({started})");
        await Wait(3);
        // where the scenario put the parked car, by the name it shares with its trace entity
        var cars = director.Placed().Where(IsInstanceValid).Select(v => (Name: v.Name.ToString(), At: v.Global)).ToList();
        int note = _recorder.Note("scripted note: the car looks fine", "check", NoteTaker.Grab(GetViewport()));
        Expect(note > 0, "a note is taken");
        // with a screen: the live top view over the game, kept as a picture to look at
        if (DisplayServer.GetName() != "headless" && _recorder.GetNodeOrNull<TopViewOverlay>("TopViewOverlay") is { } overlay)
        {
            overlay.Shown = true;
            await Wait(2);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string shot = Path.Combine(PlaytestDirector.Root, "test_output", "playtest", "recordcheck_overlay.png");
            Expect(GetViewport().GetTexture().GetImage().SavePng(shot) == Error.Ok, $"the live top view is on screen ({shot})");
            overlay.Shown = false;
        }
        await Wait(14);   // the note's top view waits for the 10 s after it
        bool notePicture = File.Exists(Path.Combine(_recorder.Dir, $"notes/{note:000}_top.png"));
        Expect(notePicture, "the note's top view is drawn 10 s after it");
        await _recorder.Stop();

        string dir = _recorder.Dir;
        var reader = new TraceReader(dir);
        var frames = reader.Window(0, 1e9);
        Expect(frames.Count >= 100, $"{frames.Count} frames in 20 s (10 Hz)");
        var me = reader.Entities.Values.FirstOrDefault(e => e.IsPlayer && e.Local);
        Expect(me != null && frames.Count(f => f.TryGet(me.Id, out _)) >= 100, "the local player is traced in every frame");
        Expect(cars.Count > 0, $"the scenario placed {cars.Count} vehicle(s)");
        foreach (var car in cars)
        {
            var traced = reader.Entities.Values.FirstOrDefault(e => !e.IsPlayer && e.Name == car.Name);
            bool there = traced != null && frames.Where(f => f.T > 3).Any(f => f.TryGet(traced.Id, out var s)
                && Math.Abs(s.E - car.At.E) < 2 && Math.Abs(s.N - car.At.N) < 2);
            Expect(there, FormattableString.Invariant($"{car.Name} ({traced?.Label}) is traced where the scenario put it ({car.At.E:F1}, {car.At.N:F1}), size {(traced == null ? "-" : string.Join(" x ", traced.Size.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))))}"));
        }
        string report = Path.Combine(dir, SessionReport.ReportFile);
        string text = File.Exists(report) ? File.ReadAllText(report) : "";
        Expect(text.Contains("### Note 1", StringComparison.Ordinal) && text.Contains(Scenario, StringComparison.Ordinal),
            "report.md lists the scenario and the note");
        bool screen = DisplayServer.GetName() != "headless";
        Expect(!screen || File.Exists(Path.Combine(dir, $"notes/{note:000}.png")), screen ? "the note has its screenshot" : "no screen, no screenshot (headless)");
        var events = SessionReport.ReadLines(Path.Combine(dir, SessionReport.EventsFile));
        Expect(events.Any(e => e.Type == "scenario" && e.Str("id") == Scenario) && events.Any(e => e.Type == "log"),
            $"events.jsonl has the scenario and the log ({events.Count} lines)");
        Expect(File.Exists(Path.Combine(dir, "perf", "summary.txt")), "the perf recording is in the bundle (perf/summary.txt)");

        string png = Path.Combine(PlaytestDirector.Root, "test_output", "playtest", "recordcheck_top.png");
        try
        {
            // the ground found again from the session's start event (#851), as --topview finds it
            var ground = TopViewImage.SourceOf(dir, null);
            Expect(ground != null, "the session says what ground it ran on");
            string drawn = TopViewCli.Draw(dir, 0, 20, 8, null, null, null, png, 900, html: false, ground);
            GD.Print("[recordcheck] " + drawn);
            Expect(File.Exists(png) && drawn.Contains("ground from", StringComparison.Ordinal), $"--topview draws the session over its ground ({png})");
            TopViewCli.Draw(dir, 0, 20, null, null, null, null, png, 600, html: true, ground);
            Expect(File.Exists(Path.ChangeExtension(png, ".html")), "--topview --html writes the slider page");
        }
        catch (Exception e) { Expect(false, $"--topview: {e.Message}"); }
        Done();
    }

    private void Done()
    {
        GD.Print(_failed == 0 ? "[recordcheck] RESULT: ok" : $"[recordcheck] RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
