using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --motioncheck --systems ui</c> (#1831): <see cref="UiMotion"/> on plain controls.
/// With ReduceMotion on, Open never leaves scale 1 and ends at alpha 1; Close runs its callback exactly once, also when
/// an Open (or another Close) cuts it short; after any sequence a control rests at scale 1, alpha 1 (or was freed by its
/// Close callback); Press, Pop and SlideTo end at rest. Every wait is for the state, capped in wall time, so it holds
/// under <c>--fixed-fps</c> and headless.
/// </summary>
public partial class MotionCheck : Node
{
    private int _failures;
    private ulong _start;

    public static bool Requested() => CmdArgs.Has("--motioncheck");

    public MotionCheck() => Name = "MotionCheck";

    public override async void _Ready()
    {
        foreach (bool reduced in new[] { true, false })
        {
            GameSettings.Current.ReduceMotion = reduced;
            string mode = reduced ? "reduced" : "full";

            // Open: ends at rest; reduced never scales
            var c = Make();
            UiMotion.Open(c);
            bool scaled = false;
            _start = Time.GetTicksMsec();
            while (UiMotion.Moving(c) && !TimedOut()) { scaled |= c.Scale != Vector2.One; await Frame(); }
            Expect(AtRest(c), $"{mode}: Open ends at scale 1, alpha 1 ({c.Scale}, {c.Modulate.A})");
            Expect(reduced ? !scaled : scaled, $"{mode}: Open {(reduced ? "never scales" : "does scale")}");

            // Close: the callback once, at the end
            int closed = 0;
            UiMotion.Close(c, () => closed++);
            Expect(closed == 0, $"{mode}: Close does not run its callback at once");
            await Until(() => closed > 0);
            await Frames(10);
            Expect(closed == 1, $"{mode}: Close ran its callback once ({closed})");

            // Close cut short by Open: the callback once (at the cut), the control ends at rest
            closed = 0;
            UiMotion.Close(c, () => closed++);
            await Frame();
            UiMotion.Open(c);
            Expect(closed == 1, $"{mode}: Open during Close runs the cut Close's callback once ({closed})");
            await Until(() => !UiMotion.Moving(c));
            await Frames(10);
            Expect(closed == 1 && AtRest(c), $"{mode}: Close then Open ends at rest, callback still once ({closed}, {c.Scale}, {c.Modulate.A})");

            // Open cut short by Close: the callback once, at the end
            closed = 0;
            UiMotion.Open(c);
            await Frame();
            UiMotion.Close(c, () => closed++);
            await Until(() => closed > 0);
            Expect(closed == 1, $"{mode}: Close during Open runs its callback once ({closed})");

            // Close twice: each callback once
            int a = 0, b = 0;
            UiMotion.Close(c, () => a++);
            UiMotion.Close(c, () => b++);
            await Until(() => b > 0);
            await Frames(10);
            Expect(a == 1 && b == 1, $"{mode}: two Closes run each callback once ({a}, {b})");

            // a hammered control still ends at rest
            UiMotion.Open(c);
            for (int i = 0; i < 3; i++)
            {
                UiMotion.Press(c); UiMotion.Pop(c); UiMotion.Open(c); await Frame();
                UiMotion.Close(c); UiMotion.Open(c);
            }
            await Until(() => !UiMotion.Moving(c));
            Expect(AtRest(c), $"{mode}: Open/Close/Press/Pop hammered ends at rest ({c.Scale}, {c.Modulate.A})");

            // Press and Pop on a resting control end at 1
            UiMotion.Press(c);
            UiMotion.Press(c);
            await Until(() => !UiMotion.Moving(c));
            UiMotion.Pop(c);
            if (reduced) Expect(c.Scale == Vector2.One, $"{mode}: Pop is instant");
            await Until(() => !UiMotion.Moving(c));
            Expect(AtRest(c), $"{mode}: Press and Pop end at scale 1 ({c.Scale})");

            // SlideTo ends on the target
            var target = new Rect2(10, 40, 120, 30);
            UiMotion.SlideTo(c, target);
            if (reduced) Expect(c.Position == target.Position && c.Size == target.Size, $"{mode}: SlideTo is instant");
            await Until(() => !UiMotion.Moving(c));
            Expect(c.Position == target.Position && c.Size == target.Size, $"{mode}: SlideTo ends on the target ({c.Position}, {c.Size})");

            // Stagger: every row ends at rest
            var rows = new List<Control> { Make(), Make(), Make() };
            UiMotion.Stagger(rows);
            await Until(() => rows.TrueForAll(r => !UiMotion.Moving(r)));
            Expect(rows.TrueForAll(AtRest), $"{mode}: Stagger rows end at rest");
            foreach (var r in rows) r.QueueFree();

            // freed by the Close callback: no error, gone
            var doomed = Make();
            UiMotion.Open(doomed);
            await Frame();
            UiMotion.Close(doomed, () => doomed.QueueFree());
            await Until(() => !IsInstanceValid(doomed) || doomed.IsQueuedForDeletion());
            Expect(!IsInstanceValid(doomed) || doomed.IsQueuedForDeletion(), $"{mode}: a Close callback may free the panel");
            c.QueueFree();
        }
        GameSettings.Current.ReduceMotion = false;
        GD.Print(_failures == 0 ? "[motioncheck] RESULT: ok" : $"[motioncheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private Control Make()
    {
        var c = new Control { Size = new Vector2(100, 50) };
        AddChild(c);
        return c;
    }

    private static bool AtRest(Control c) => c.Scale == Vector2.One && Mathf.IsEqualApprox(c.Modulate.A, 1f);

    // a wall-clock cap only: every wait is for a state, so --fixed-fps and real time both pass
    private bool TimedOut() => Time.GetTicksMsec() - _start > 10_000;

    private async Task Until(Func<bool> cond)
    {
        _start = Time.GetTicksMsec();
        while (!cond() && !TimedOut()) await Frame();
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await Frame();
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[motioncheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
