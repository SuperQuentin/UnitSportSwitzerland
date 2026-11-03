using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--beamcheck [shot.png] --world flat</c> (#861): a saucer hovers 18 m up and switches its
/// tractor beam on over a person standing still, a parked car and a dropped item. All three rise and
/// are held under the hatch; the person walks off and is let down gently; the beam goes off and the
/// car and the item are let down onto the ground, released. Prints <c>[beamcheck] RESULT: ok</c>.
/// </summary>
public partial class BeamCheck : Node3D
{
    public static bool Requested => CmdArgs.Has("--beamcheck");
    private static string? Shot => CmdArgs.Value("--beamcheck") is { } v && v.EndsWith(".png") ? v : null;

    private readonly WorldOrigin _origin;
    private FootPlayer? _pilot, _walker;
    private Vehicles.VehicleBody? _car;
    private Items.DroppedItem? _item;
    private double _t;
    private bool _done, _walking, _beamed, _checked, _freed;
    private int _failed;
    private float _walkerTop, _carTop, _itemTop;

    public BeamCheck(WorldOrigin origin) => _origin = origin;

    public override void _Ready()
    {
        _pilot = new FootPlayer { Name = "Pilot" };
        AddChild(_pilot);
        _pilot.GlobalPosition = new Vector3(0, 1f, 0);
        _pilot.DebugLaunch(_pilot.GlobalPosition, Vector3.Zero);
        // hover where it is put: no keys, no heading of its own
        _pilot.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);

        _walker = new FootPlayer { Name = "Walker", Npc = true };
        _walker.WalkControls = () => (_walking ? Vector3.Right : Vector3.Zero, false);
        AddChild(_walker);
        _walker.DebugLaunch(new Vector3(0.6f, 0.1f, 0.4f), Vector3.Zero);

        var car = new Vehicles.VehicleState((RideKind)CarCatalog.First, _origin.ToGlobal(new Vector3(-3f, 0.2f, 2f)), 0.4f,
            Vector3.Zero, 100f, false, false, 0f, Vehicles.VehicleState.Now);
        _car = Vehicles.VehicleBody.Create(car, null, _origin);
        AddChild(_car);

        var drop = new Items.DropState("drop_beam", 0, new Items.ItemStack(Items.ItemId.FonduePot, 1),
            _origin.ToGlobal(new Vector3(2.5f, 0.1f, -1.5f)), Vector3.Zero, Vector3.Zero, Vector3.Zero, Settled: true);
        _item = Items.DroppedItem.Create(drop, _origin);
        AddChild(_item);
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[beamcheck] {(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done || _pilot == null || _walker == null || _car == null || _item == null) return;
        _t += delta;
        if (_t > 60) { Expect(false, "timed out"); End(); return; }
        if (_t > 1.0 && _t < 1.1 && _pilot.Ride != RideKind.Ufo)
        {
            Expect(_pilot.SetRide(RideKind.Ufo), "mounted the saucer");
            _pilot.DebugLaunch(new Vector3(0, 18f, 0), Vector3.Zero);
        }
        if (_pilot.Saucer is not { } ufo) return;
        // the legs down near the ground would land it: keep it up, legs folded, for the picture
        ufo.SetLegs(false);
        if (_t > 3.0 && !_beamed) { _beamed = true; ufo.SetBeam(true); GD.Print("[beamcheck] beam on"); }

        _walkerTop = Mathf.Max(_walkerTop, _walker.GlobalPosition.Y);
        _carTop = Mathf.Max(_carTop, _car.GlobalPosition.Y);
        _itemTop = Mathf.Max(_itemTop, _item.GlobalPosition.Y);
        if (_t > 3.0 && (int)(_t * 2) != (int)((_t - delta) * 2))
            GD.Print($"[beamcheck] t={_t:F1} pilot {_pilot.GlobalPosition.Y:F1}  walker {_walker.GlobalPosition.Y:F1}{(_walker.InBeam ? " (beam)" : "")}"
                + $"  car {_car.GlobalPosition.Y:F1}  item {_item.GlobalPosition.Y:F1}  held {TractorBeams.Instance?.Held}");

        if (_t > 12.0 && !_checked)
        {
            _checked = true;
            float hold = _pilot.GlobalPosition.Y + Ufo.HatchOffset.Y - TractorBeamMath.HoldDepth;
            Expect(_walker.InBeam && _walker.GlobalPosition.Y > 8f, $"the person rose ({_walker.GlobalPosition.Y:F1} m)");
            Expect(_car.GlobalPosition.Y > hold - 3f, $"the car is held under the hatch ({_car.GlobalPosition.Y:F1} m, hold {hold:F1})");
            Expect(_item.GlobalPosition.Y > hold - 3f, $"the item is held under the hatch ({_item.GlobalPosition.Y:F1} m)");
            if (Shot is { } shot && GetViewport().GetTexture().GetImage().SavePng(shot.Replace(".png", "_held.png")) == Error.Ok)
                GD.Print($"[beamcheck] wrote {shot.Replace(".png", "_held.png")}");
            _walking = true;
            GD.Print("[beamcheck] the person walks");
        }
        if (_t > 14.0 && !_freed)
        {
            _freed = true;
            Expect(!_walker.InBeam || _walker.GlobalPosition.Y < _walkerTop - 1f, "walking frees the person: let down");
            ufo.SetBeam(false);
            _walking = false;
            GD.Print("[beamcheck] beam off");
        }
        if (_t > 16.0 && _walker.IsOnFloor() && !_walker.InBeam && TractorBeams.Instance?.Held == 0
            && _car.GlobalPosition.Y < 1.5f && _item.GlobalPosition.Y < 1f)
        {
            Expect(_walker.Health >= FootPlayer.MaxHealth, $"the person landed unhurt (health {_walker.Health:F0})");
            Expect(_car.Health >= 99f && !_car.Wrecked, $"the car landed unharmed (health {_car.Health:F0})");
            GD.Print($"[beamcheck] peaks: person {_walkerTop:F1} m, car {_carTop:F1} m, item {_itemTop:F1} m");
            End();
        }
    }

    private void End()
    {
        _done = true;
        if (Shot is { } shot && GetViewport().GetTexture().GetImage().SavePng(shot) == Error.Ok) GD.Print($"[beamcheck] wrote {shot}");
        GD.Print(_failed == 0 ? "[beamcheck] RESULT: ok" : $"[beamcheck] RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
