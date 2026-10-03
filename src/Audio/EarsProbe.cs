using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Audio;

/// <summary>
/// <c>--earscheck</c> (#375), offline, best with <c>--world fixture --view third</c>: the listener
/// is the body's head, not the camera. Checks that the ears sit at the head while a third-person
/// camera is metres away, that the bus layout is there (Master limiter, the world's cabin filter,
/// the Player bus), then gets into a car (the ears are in its cabin, the world filter closes, the
/// reverb is the cabin) and out again (all open). Read the <c>[earscheck]</c> lines.
/// </summary>
public partial class EarsProbe : Node
{
    public static bool Requested => CmdArgs.Has("--earscheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public EarsProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "EarsProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[earscheck] {what}");

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
            // offline the world starts on the free camera: the player comes with the mode key
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Log("RESULT: FAIL no local player"); GetTree().Quit(1); return; }
        await Wait(1.0);

        // on foot: the ears at the head, wherever the camera is
        var head = me.GlobalPosition + Vector3.Up * 1.68f;
        var cam = GetViewport().GetCamera3D();
        Check(Ears.Ready && Ears.Body == me, $"the ears belong to the local body (ready {Ears.Ready})");
        Check(Ears.Position.DistanceTo(head) < 0.35f, $"the ears are at the head ({Ears.Position.DistanceTo(head):0.00} m off)");
        if (cam != null)
            Log($"camera {cam.GlobalPosition.DistanceTo(Ears.Position):0.0} m from the ears");
        Check(Ears.Cabin == null && Ears.Shut < 0.01f, "on foot, no cabin");

        Check(Has<AudioEffectHardLimiter>(0), "a limiter on Master");
        int sfx = AudioServer.GetBusIndex(SfxBus.Name);
        Check(sfx >= 0 && Has<AudioEffectLowPassFilter>(sfx), "the world bus has its cabin filter");
        Check(AudioServer.GetBusIndex(SfxBus.Player) >= 0 && SfxBus.PlayerReverb != null, "the Player bus, with its reverb");
        Check(sfx >= 0 && !AudioServer.IsBusEffectEnabled(sfx, 0), "the cabin filter is off in the open");

        // into a car: the ears in its cabin, the world behind glass
        if (!me.SetRide(CarCatalog.All[0].Kind)) Check(false, "SetRide took the car");
        else
        {
            await Wait(1.5);
            Check(Ears.Cabin == me, "in the car, the ears are in its cabin");
            Check(Ears.Shut > 0.95f, $"the cabin is shut ({Ears.Shut:0.00})");
            Check(sfx >= 0 && AudioServer.IsBusEffectEnabled(sfx, 0) && SfxBus.Cabin is { CutoffHz: < 2000f },
                $"the world is low-passed ({SfxBus.Cabin?.CutoffHz:0} Hz)");
            var zones = GetTree().Root.FindChild("ReverbZones", true, false) as ReverbZones;
            Check(zones?.Environment == "cabin", $"the reverb is the cabin ({zones?.Environment ?? "no zones"})");
            Check(Ears.Position.DistanceTo(me.GlobalPosition) < 2.5f, "the ears ride in the seat");

            me.ExitVehicle();
            await Wait(1.5);
            Check(Ears.Cabin == null && Ears.Shut < 0.05f, "out of the car, open air again");
            Check(sfx >= 0 && !AudioServer.IsBusEffectEnabled(sfx, 0), "the cabin filter is off again");
        }

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private static bool Has<T>(int bus) where T : AudioEffect
    {
        for (int e = 0; e < AudioServer.GetBusEffectCount(bus); e++)
            if (AudioServer.GetBusEffect(bus, e) is T) return true;
        return false;
    }
}
