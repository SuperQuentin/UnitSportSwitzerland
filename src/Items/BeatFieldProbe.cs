using System.Threading.Tasks;
using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--beatfieldcheck</c> (#734), offline and windowed (nothing reacts headless), <c>--world fixture</c>:
/// a radio before the camera plays the chess type beat at full volume. Things near it must move with
/// it, drawn only: a dropped item's float pose hops while its body stays exactly where it lies; a
/// registered mesh squashes; the shaders get the music (<c>world_music</c>). A mesh 20 m off moves at
/// full volume and stands still once the radio is turned down to nothing (the radius is the
/// volume's); everything is back at rest once it stops. Writes <c>test_output/beatfield.png</c>.
/// </summary>
public partial class BeatFieldProbe : Node
{
    public static bool Requested => CmdArgs.Has("--beatfieldcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public BeatFieldProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "BeatFieldProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[beatfield] {what}");

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
        if (me == null || RadioManager.Instance is not { } radios || DroppedItems.Instance is not { } dropped
            || CdLibrary.Instance?.Find(CdLibrary.Instance.RatBeatId) is not { } cd)
        {
            Finish("no player, radio manager, dropped items or CD");
            return;
        }
        await Wait(1.0);
        // only this check's radio plays: the shared save may hold a carried one still on from another check
        if (ItemController.Instance?.Inventory is { } inv)
            for (int i = 0; i < inv.Capacity; i++)
                if (!inv[i].IsEmpty && inv[i].Id == ItemId.Radio) inv.SetData(i, null);

        var cam = GetViewport().GetCamera3D();
        var fwd = (-cam.GlobalBasis.Z with { Y = 0 }).Normalized();
        var side = fwd.Cross(Vector3.Up);
        var ground = me.GlobalPosition;
        radios.Throw(new RadioState("", 0, radios.Origin.ToGlobal(ground + fwd * 3f), Mathf.Atan2(fwd.X, fwd.Z), Vector3.Zero, Settled: true));
        dropped.Drop(new ItemStack(ItemId.Hammer, 1), ground + fwd * 3f + side * 1.2f + Vector3.Up * 0.5f, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        // a registered mesh near the radio, and one 20 m off
        var near = Box(ground + fwd * 3f - side * 1.4f + Vector3.Up * 0.25f);
        var far = Box(ground + fwd * 3f + side * 20f + Vector3.Up * 0.25f);
        BeatField.Add(near, 1f, 0.25f);
        BeatField.Add(far, 1f, 0.25f);
        await Wait(3.0);   // the drop lands and settles

        RadioBody? radio = null;
        foreach (var n in radios.GetChildren()) if (n is RadioBody r) radio = r;
        DroppedItem? item = null;
        foreach (var n in dropped.GetChildren()) if (n is DroppedItem d) item = d;
        if (radio == null || item?.Visual is not { } visual) { Finish("no radio or no dropped item"); return; }

        radios.SetVolume(radio, 1f);
        radios.Play(radio, cd.Id, cd.Duration);
        for (int i = 0; i < 100 && !(radio.Speaker?.Playing ?? false); i++) await Wait(0.1);
        await Wait(0.5);
        Check(BeatField.Count >= 1, $"the playing radio is a source ({BeatField.Count})");

        var body = item.GlobalPosition;
        var (itemHop, nearMove, farMove) = await Watch(visual, near, far, 2.5);
        Check(item.GlobalPosition.IsEqualApprox(body), "the dropped item's body never moves (drawing only)");
        Check(nearMove > 0.01f, $"a registered mesh near it squashes and hops ({nearMove:F3})");
        Check(farMove > 0.003f, $"20 m off, at full volume (60 m reach), it moves too ({farMove:F3})");
        var music = BeatField.ShaderMusic;
        Check(music.W > 50f, $"the shaders hear the music (reach {music.W:F0} m)");
        Shot();

        radios.SetVolume(radio, 0f);   // 12 m reach
        await Wait(0.5);
        (_, nearMove, farMove) = await Watch(visual, near, far, 1.5);
        Check(farMove < 0.001f && far.Transform.IsEqualApprox(new Transform3D(Basis.Identity, far.Position)), $"turned down to nothing, 20 m is out of reach ({farMove:F4})");
        Check(nearMove > 0.005f, $"but the near one still moves ({nearMove:F3})");

        radios.Stop(radio);
        await Wait(1.0);
        Check(near.Transform.Basis.IsEqualApprox(Basis.Identity), "stopped: back at rest");
        // the float pose bobs and spins by itself: the music's hop is what it adds on top
        var (restHop, _, _) = await Watch(visual, near, far, 2.5);
        Check(itemHop > restHop + 0.04f, $"the dropped item hops on the beat ({itemHop:F3} m up and down, {restHop:F3} without music)");

        Finish(null);
    }

    private MeshInstance3D Box(Vector3 at)
    {
        var m = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) } };
        GetTree().Root.AddChild(m);
        m.GlobalPosition = at;
        return m;
    }

    /// <summary>
    /// Over <paramref name="seconds"/>: how far the item's drawing went up and down (its float pose
    /// spins anyway), and the most each mesh moved from where it started.
    /// </summary>
    private async Task<(float ItemHop, float Near, float Far)> Watch(Node3D visual, Node3D near, Node3D far, double seconds)
    {
        var n0 = near.Transform; var f0 = far.Transform;
        float lo = float.MaxValue, hi = float.MinValue, n = 0, f = 0;
        for (double t = 0; t < seconds; t += 0.05)
        {
            await Wait(0.05);
            float y = visual.Transform.Origin.Y;
            lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y);
            n = Mathf.Max(n, Moved(n0, near.Transform));
            f = Mathf.Max(f, Moved(f0, far.Transform));
        }
        return (hi - lo, n, f);
    }

    private static float Moved(Transform3D a, Transform3D b) =>
        a.Origin.DistanceTo(b.Origin) + (a.Basis.Y - b.Basis.Y).Length() + (a.Basis.X - b.Basis.X).Length();

    private void Shot()
    {
        if (DisplayServer.GetName() == "headless") return;
        var image = GetViewport().GetTexture().GetImage();
        string dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        image.SavePng(System.IO.Path.Combine(dir, "beatfield.png"));
        Log("ok: wrote test_output/beatfield.png");
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { Log($"RESULT: FAIL {fatal}"); GetTree().Quit(1); return; }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED {_failed} check(s)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
