using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertdeskcheck</c> (#885), offline on the real tiles at Stade de Genève with a forced show
/// (<c>--chunks &lt;main&gt;/terrain_chunks --at 2498751,1114890 --concert 11</c>): the player is put at
/// the front-of-house console, E opens the light desk's screen (the key's own path), its buttons host
/// the set, hold the peak, fire the confetti and pull the master down, each checked on the service
/// and the rig; Esc closes it. Windowed, it saves <c>test_output/concert/desk_*.png</c>.
/// </summary>
public partial class ConcertDeskProbe : Node
{
    public static bool Requested => CmdArgs.Has("--concertdeskcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public ConcertDeskProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "ConcertDeskProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Check(bool ok, FormattableString what)
    {
        GD.Print($"[concertdesk] {(ok ? "ok" : "FAIL")}: {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private async Task Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static Button? ButtonNamed(Node root, string text)
    {
        foreach (var n in root.FindChildren("*", "Button", true, false))
            if (n is Button b && b.Text == text) return b;
        return null;
    }

    private async Task Click(string text, double wait = 0.6)
    {
        var b = ConcertDeskUi.Instance is { } ui ? ButtonNamed(ui, text) : null;
        Check(b != null, $"the desk has a '{text}' button");
        b?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(wait);
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200 && (me == null || !me.IsOnFloor()); i++)
        {
            me = _local();
            if (me == null && i % 50 == 25) await Press(PlayerInput.ToggleMode);
            await Wait(0.1);
        }
        ConcertRig? rig = null;
        for (int i = 0; i < 600 && rig == null; i++)
        {
            rig = ConcertRig.Live.Values.FirstOrDefault();
            await Wait(0.1);
        }
        if (me == null || rig?.Plan is not { } plan) { Finish("no player or no concert"); return; }

        // beside the console, on the floor in front of the riser
        var desk = rig.ToGlobal(plan.Desk);
        me.GlobalPosition = desk + rig.GlobalBasis * new Vector3(0, 0.5f, 0) + (rig.ToGlobal(new Vector3(plan.Origin.X, 0, plan.Origin.Y)) - desk).Normalized() * -2.2f;
        me.Velocity = Vector3.Zero;
        await Wait(2.0);
        Check(ConcertDeskUi.At(me) == rig, $"at the console ({me.GlobalPosition.DistanceTo(desk):F1} m from it)");

        await Press(PlayerInput.InteractMount);
        await Wait(0.5);
        Check(ConcertDeskUi.Instance?.IsOpen == true, $"E opens the light desk");
        Shot("desk_open.png");

        await Click("Host this set");
        var svc = ConcertService.Instance;
        Check(svc?.SeedFor(0) == rig.Event.Seed, $"'Host this set' hosts set {rig.Event.Seed} ({svc?.SeedFor(0)})");
        // hosting builds the rig again: the screen follows the new one
        await Wait(1.0);
        rig = ConcertRig.Live.Values.First();
        Check(ConcertDeskUi.Instance?.IsOpen == true, $"the screen stays open on the set's new rig");
        await Click("Peak");
        Check(svc?.CuesOf(0) is { Held: (int)ShowSection.Peak } && rig.Desk.Section == ShowSection.Peak, $"'Peak' holds the peak ({rig.Desk.Section})");
        await Click("Confetti", 0.1);
        Check(rig.Patch.Fixtures.Where(f => f.Type.Kind == FixtureKind.Confetti).Any(f => rig.Patch.Get01(f, Attr.Fire) > 0.5f), $"'Confetti' fires the cannons");
        Shot("desk_peak.png");
        var slider = ConcertDeskUi.Instance!.FindChildren("*", "HSlider", true, false).OfType<HSlider>().FirstOrDefault();
        if (slider != null)
        {
            slider.Value = 0.4;
            slider.EmitSignal(Slider.SignalName.DragEnded, true);
            await Wait(0.6);
        }
        Check(MathF.Abs(rig.Desk.Master - 0.4f) < 0.01f, $"the master fader pulls the rig down ({rig.Desk.Master:F2})");

        await Press("ui_cancel");
        await Wait(0.4);
        Check(ConcertDeskUi.Instance?.IsOpen == false, $"Esc closes it");
        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { GD.Print($"[concertdesk] RESULT: FAIL {error}"); GetTree().Quit(1); return; }
        GD.Print(_failed == 0 ? "[concertdesk] RESULT: ok" : $"[concertdesk] RESULT: FAIL {_failed} checks");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/concert/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[concertdesk] screenshot {path}");
    }
}
