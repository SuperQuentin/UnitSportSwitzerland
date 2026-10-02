using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Items;

/// <summary>
/// <c>--interactcheck</c> (offline, windowed; needs terrain): the precise interactions of #261,
/// worked like a player and photographed into <c>test_output/interact_*.png</c>.
///
/// <list type="number">
/// <item>A car parked beside the player: nothing to act on from a step behind its boot; at the
/// passenger door, aimed at it, the door (with its border); E opens it, E again gets in.</item>
/// <item>A radio playing in the hand, then put away: on the back (replicated <c>BackItemId</c>), still
/// playing (<c>HeldRadio</c>), bouncing.</item>
/// <item>A playing radio on the ground: pointed at, Use takes it into the hand with its CD.</item>
/// <item>Dancing to the carried radio: the crowd moves forced (Pogo, JumpTogether) to see the feet leave the ground.</item>
/// </list>
/// Without a CD in the library the music steps are reported and skipped.
/// </summary>
public partial class InteractCheck : Node
{
    private const double Timeout = 240;
    private readonly Func<FootPlayer?> _local;
    private readonly Inventory _inventory;
    private double _t, _since = -1, _stepAt;
    private int _step;
    private bool _steppedDown, _failed;
    private VehicleBody? _car;
    private readonly List<string> _notes = new();

    private InteractCheck(Func<FootPlayer?> local, Inventory inventory)
    {
        Name = "InteractCheck";
        _local = local;
        _inventory = inventory;
    }

    /// <summary>Asked for on the command line: the world then gives it a scratch inventory (the real one is shared by every worktree).</summary>
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--interactcheck") >= 0;

    public static InteractCheck? Create(Func<FootPlayer?> local, Inventory inventory) =>
        Requested ? new InteractCheck(local, inventory) : null;

    private void Check(bool ok, string what)
    {
        GD.Print($"[interactcheck] {(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) { _failed = true; _notes.Add(what); }
    }

    private void Next() { _step++; _stepAt = _t; }
    private double InStep => _t - _stepAt;

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > Timeout) { Finish($"timed out at step {_step}"); return; }
        if (_local() is not { } me)
        {
            if (_t > 10 && !_steppedDown && GetParent() is ClientWorld world) { world.ToggleMode(); _steppedDown = true; }
            return;
        }
        if (_since < 0)
        {
            if (!me.IsOnFloor() || _t < 8) return;
            _since = _t;
            _stepAt = _t;
        }
        var library = CdLibrary.Instance;
        int cd = RadioQueue.Order(library, withPersonal: true).FirstOrDefault();
        float length = library?.Find(cd)?.Duration ?? 0;
        var playing = cd != 0 ? new RadioPlay(cd, ClockSync.ServerNow - 1, length).Encode() : null;

        switch (_step)
        {
            case 0:
                // a car, left right here: in, and out on the driver's side
                Check(me.SetRide(CarCatalog.All[0].Kind), "into a car");
                Next();
                break;
            case 1 when InStep > 1.5:
                me.ExitVehicle();
                Next();
                break;
            case 2 when InStep > 2.5:
                // the car just left here (other runs may have left bikes about)
                _car = VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Where(v => v.Ride is Car)
                    .OrderBy(v => v.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
                Check(_car != null, "the car is parked in the world");
                if (_car == null) { Finish("no car"); return; }
                // a step behind its boot: nothing to get into from there
                var behind = _car.GlobalTransform * new Vector3(0, 0.3f, _car.Ride.ParkedBox.Size.Z * 0.5f + _car.Ride.ParkedBox.Centre.Z + 1.2f);
                me.GlobalPosition = behind;
                me.Velocity = Vector3.Zero;
                Look(me, _car.GlobalPosition + Vector3.Up * 0.5f);
                Next();
                break;
            case 3 when InStep > 0.6:
                Check(VehicleReach.Find(me) == null, $"nothing to act on a step behind the boot (got {VehicleReach.Find(me)?.Door.ToString() ?? "none"})");
                // at the passenger door (the left: these cars are right-hand drive), facing it
                if (_car!.Rig is { } rig)
                {
                    var door = rig.DoorCentre(Avatar.CarRig.DoorLeft);
                    var outward = (door - _car.GlobalPosition) with { Y = 0 };
                    var stand = door + outward.Normalized() * 0.9f;
                    stand.Y = _car.GlobalPosition.Y + 0.3f;
                    me.GlobalPosition = stand;
                    me.Velocity = Vector3.Zero;
                    Look(me, door);
                }
                Next();
                break;
            case 4 when InStep > 0.8:
                var aim = VehicleReach.Current;
                Check(aim is { HasDoor: true, Door: Avatar.CarRig.DoorLeft, DoorOpen: false }, $"at the passenger door, aimed at it (got {aim?.Door.ToString() ?? "none"})");
                Shoot("interact_door.png");
                Check(me.TryInteract() && _car!.DoorsOpen == Avatar.CarRig.DoorLeft && me.Ride == RideKind.OnFoot, $"E opens that door only (doors {_car!.DoorsOpen})");
                Next();
                break;
            case 5 when InStep > 0.8:
                Shoot("interact_door_open.png");
                Check(me.TryInteract(), "E at the open door");
                Next();
                break;
            case 6 when InStep > 0.8:
                Check(me.Ride != RideKind.OnFoot, $"and gets in ({me.Ride})");
                me.ExitVehicle();
                Next();
                break;
            case 7 when InStep > 2:
                if (playing == null) { Check(true, "no CD in the library: music steps skipped"); _step = 13; break; }
                _inventory.Put(0, new ItemStack(ItemId.Radio, 1, playing));
                _inventory.Select(0);
                Next();
                break;
            case 8 when InStep > 1:
                Check(me.HeldItemId == (int)ItemId.Radio && RadioPlay.Decode(me.HeldRadio)?.CdId == cd, $"a playing radio in the hand (held {(ItemId)me.HeldItemId}, inv {_inventory.HeldId}, radio '{me.HeldRadio}', cd {cd})");
                // put away: another hotbar slot
                _inventory.Select(1);
                SideOn(me);
                Next();
                break;
            case 9 when InStep > 1.5:
                Check(me.BackItemId == (int)ItemId.Radio && RadioPlay.Decode(me.HeldRadio)?.CdId == cd, $"put away, on the back and still playing (back {me.BackItemId})");
                Check(me.GetNodeOrNull<RadioSpeaker>(RadioManager.HeldSpeakerName) is { On: true }, "its speaker on");
                Shoot("interact_back.png");
                // the walker turns to face its travel: walk away so the camera sees the back
                me.DanceId = 1;
                Next();
                break;
            case 10 when InStep > 2:
                Check(me.DanceId == 1, "dancing to the radio on its back");
                SideOn(me);
                FootPlayer.DanceMoveOverride = Avatar.HumanMeshBuilder.GroupPogo;
                Next();
                break;
            case 11 when InStep > 2.0:
                ShootAtBeat("interact_pogo.png", 0.55f);
                if (_shotTaken) { FootPlayer.DanceMoveOverride = Avatar.HumanMeshBuilder.GroupJump; Next(); _shotTaken = false; }
                break;
            case 12 when InStep > 1.0:
                // beat four of the bar is the big jump: the shot at its top
                if (BarBeat(me) is (3, var b) && b > 0.5f && b < 0.6f)
                {
                    Shoot("interact_jump.png");
                    FootPlayer.DanceMoveOverride = -1;
                    me.DanceId = 0;
                    // a radio of its own on the ground, playing, to take with a click
                    var ahead = -me.Camera.GlobalTransform.Basis.Z with { Y = 0 };
                    RadioManager.Instance?.Throw(new RadioState("", 0, me.GlobalPosition + Vector3.Up * 0.5f + ahead.Normalized() * 1.4f,
                        0, Vector3.Zero, cd, ClockSync.ServerNow - 1, true, false, length));
                    Next();
                }
                break;
            case 13 when InStep > 2.5:
                var radio = RadioManager.Instance?.Nearest(me.GlobalPosition, 4f);
                if (radio != null) Look(me, radio.GlobalPosition);
                Next();
                break;
            case 14 when InStep > 0.7:
                if (playing != null)
                {
                    Check(Highlight.Pointed is RadioBody, "the radio on the ground is pointed at");
                    Shoot("interact_radio_bounce.png");
                    int before = CountRadios();
                    if (Highlight.Pointed is RadioBody r) ItemController.Instance?.TakeRadio(me, r);
                    Check(CountRadios() == before + 1 || _inventory.HeldId == ItemId.Radio, "Use takes it");
                }
                Next();
                break;
            case 15 when InStep > 1:
                if (playing != null)
                    Check(_inventory.HeldId == ItemId.Radio && RadioPlay.Decode(_inventory.Held.Data) is { CdId: var c } && c == cd,
                        "in the hand, with its CD still playing");
                Finish(_failed ? string.Join("; ", _notes) : "all steps");
                break;
        }
    }

    private bool _shotTaken;

    /// <summary>A shot when the dance's beat phase is near <paramref name="phase"/> (the top of a jump).</summary>
    private void ShootAtBeat(string file, float phase)
    {
        if (_local() is not { } me || BarBeat(me) is not (_, var b) || Mathf.Abs(b - phase) > 0.06f) return;
        Shoot(file);
        _shotTaken = true;
    }

    /// <summary>Which beat of the bar the carried radio is on, and the phase inside it.</summary>
    private static (int Beat, float Phase)? BarBeat(FootPlayer me) =>
        RadioPlay.Decode(me.HeldRadio) is { } p && RadioBody.BeatOf(p.CdId, p.StartedAt, ClockSync.ServerNow, out float phase, out int beat, out int bar, out _)
            ? (beat - bar * 4, phase) : null;

    private int CountRadios()
    {
        int n = 0;
        for (int i = 0; i < _inventory.Capacity; i++) if (_inventory[i].Id == ItemId.Radio) n += _inventory[i].Count;
        return n;
    }

    /// <summary>The camera level with the figure and side on to it, to see feet leave the ground and what is on its back.</summary>
    private static void SideOn(FootPlayer me)
    {
        if (me.GetNodeOrNull<Node3D>("Body") is not { } body) return;
        var facing = -body.GlobalTransform.Basis.Z with { Y = 0 };
        if (facing.LengthSquared() < 1e-4f) return;
        // looking along the figure's left-to-right, a little from behind: the back and one side
        var look = facing.Normalized().Cross(Vector3.Up).Rotated(Vector3.Up, -0.5f);
        me.LookYaw = Mathf.Atan2(-look.X, -look.Z);
        me.LookPitch = -0.08f;
    }

    /// <summary>Turns the view to look at a point.</summary>
    private static void Look(FootPlayer me, Vector3 at)
    {
        var d = at - (me.GlobalPosition + Vector3.Up * 1.6f);
        me.LookYaw = Mathf.Atan2(-d.X, -d.Z);
        me.LookPitch = Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
    }

    private void Shoot(string file)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, file);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[interactcheck] wrote {path}: {err}");
    }

    private void Finish(string why)
    {
        GD.Print($"[interactcheck] RESULT: {(_failed ? "FAILED" : "ok")} ({why})");
        FootPlayer.DanceMoveOverride = -1;
        SetProcess(false);
        GetTree().Quit(_failed ? 1 : 0);
    }
}
