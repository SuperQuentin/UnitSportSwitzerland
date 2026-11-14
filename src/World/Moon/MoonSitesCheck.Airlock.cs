using System;
using System.Globalization;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.World.Moon;

public partial class MoonSitesCheck
{
    /// <summary>
    /// <c>--airlockcheck</c> (#1941), after the last named colony (<c>--moonsites artemis:close --airlockcheck</c>): the
    /// walker steps in outside an airlock (the suit and its helmet on), cycles it from the top step, waits for the outer
    /// door, steps into the chamber, cycles to the inside (the helmet comes off once the outer door is shut), then stands
    /// in the hub (helmet off) and back out on the plateau (helmet on). The two doors are never open at once. Windowed:
    /// pictures outside the airlock, in the chamber and in the hub.
    /// </summary>
    public static bool AirlockCheck => CmdArgs.Has("--airlockcheck");

    private int _air;
    private double _airT;
    private Camera3D? _airCam;

    private void AirStep(double delta)
    {
        _airT += delta;
        var set = ColonyAirlocks.Sets.Count > 0 ? ColonyAirlocks.Sets[0] : null;
        if (set == null || set.Count == 0) { Finish("no airlock in place"); return; }
        var me = _local();
        if (set.Opening(0, true) > 0.01f && set.Opening(0, false) > 0.01f) { Finish("both doors of the airlock open at once"); return; }
        bool Helmet() => me != null && me.HeadwearId == (int)Headwear.SpaceHelmet;
        var outer = set.DoorAt(0, true);
        var outDir = set.DoorOut(0, true);
        switch (_air)
        {
            case 1:
            {
                var cam = (Camera3D)GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false)!;
                var stand = outer + outDir * 3.5f;
                stand.Y = outer.Y - ColonyKit.FloorY + 1.7f;
                cam.GlobalPosition = stand;
                cam.LookAt(outer + Vector3.Up * 1.2f, Vector3.Up);
                Key(Godot.Key.T, true);
                AirNext(2);
                break;
            }
            case 2:
                Key(Godot.Key.T, false);
                AirNext(3);
                break;
            case 3:
                if (me is { } m && m.Camera.Current && m.IsOnFloor() && _airT > 1.5)
                {
                    if (!Helmet()) { Finish($"outside the colony without the helmet (headwear {m.HeadwearId})"); return; }
                    AirLog("outside: the suit and its helmet on");
                    Picture("airlock_outside", m, back: 3.2f);
                    // on the top step, facing the door
                    m.PlaceAt(outer + outDir * 0.25f + Vector3.Up * 0.05f, Mathf.Atan2(outDir.X, outDir.Z));
                    AirNext(4);
                }
                else if (_airT > 20) Finish("the walker did not stand within 20 s of T");
                break;
            case 4:
                if (_airT < 0.4) break;
                if (!ColonyAirlocks.TryUse(me!)) { Finish("E at the outer door did not start a cycle"); return; }
                AirLog("E at the outer door: cycling");
                AirNext(5);
                break;
            case 5:
                if (set.Opening(0, true) >= 0.999f)
                {
                    AirLog(string.Create(CultureInfo.InvariantCulture, $"the outer door open after {_airT:F1} s"));
                    var chamber = (outer + set.DoorAt(0, false)) / 2;
                    me!.PlaceAt(chamber + Vector3.Up * 0.05f, 0);
                    AirNext(6);
                }
                else if (_airT > 10) Finish("the outer door did not open within 10 s");
                break;
            case 6:
                if (_airT < 0.4) break;
                if (!Helmet()) { Finish("the helmet came off in a chamber open to the vacuum"); return; }
                if (!ColonyAirlocks.TryUse(me!)) { Finish("E in the chamber did not start a cycle to the inside"); return; }
                AirLog("E in the chamber: cycling in");
                AirNext(7);
                break;
            case 7:
                if (set.Opening(0, false) >= 0.999f && _airT > 0.6)
                {
                    if (Helmet()) { Finish("the helmet still on in the shut, filled chamber"); return; }
                    AirLog(string.Create(CultureInfo.InvariantCulture, $"the inner door open after {_airT:F1} s, the helmet off"));
                    Picture("airlock_inside", me!, back: 1.6f);
                    AirNext(8);
                }
                else if (_airT > 12) Finish(string.Create(CultureInfo.InvariantCulture,
                    $"the inner door did not open within 12 s (outer {set.Opening(0, true):F2}, inner {set.Opening(0, false):F2}, {set.State(0)})"));
                break;
            case 8:
            {
                var root = set.GetParent<Node3D>();
                me!.PlaceAt(root.GlobalTransform * new Vector3(1.5f, ColonyKit.FloorY + 0.05f, 1.5f), 0);
                AirNext(9);
                break;
            }
            case 9:
                if (_airT < 1.0) break;
                if (Helmet()) { Finish("the helmet on in the hub"); return; }
                AirLog("in the hub: the helmet off");
                Picture("colony_hub", me!, back: 1.8f);
                me!.PlaceAt(set.GetParent<Node3D>().GlobalTransform * new Vector3(60, 0.3f, 60), 0);
                AirNext(10);
                break;
            case 10:
                if (_airT < 1.2) break;
                if (!Helmet()) { Finish("back outside without the helmet"); return; }
                AirLog("back outside on the plateau: the helmet on");
                Finish(null);
                break;
        }
    }

    private void AirNext(int stage)
    {
        _air = stage;
        _airT = 0;
    }

    private static void AirLog(string what) => GD.Print($"[airlock] {what}");

    /// <summary>A picture (windowed) from <paramref name="back"/> metres behind and over the walker.</summary>
    private void Picture(string name, Player.FootPlayer me, float back)
    {
        if (_headless) return;
        _airCam ??= new Camera3D { Name = "AirlockShot", Fov = 75 };
        if (_airCam.GetParent() == null) AddChild(_airCam);
        var fwd = -me.GlobalBasis.Z;
        fwd.Y = 0;
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        _airCam.GlobalPosition = me.GlobalPosition - fwd * back + Vector3.Up * 1.8f;
        _airCam.LookAt(me.GlobalPosition + Vector3.Up * 1.1f + fwd * 2, Vector3.Up);
        _airCam.MakeCurrent();
        _pending = name;
        _pendingFrames = 3;
    }

    private string? _pending;
    private int _pendingFrames;

    /// <summary>The picture a few frames after its camera moved (the frame drawn is the last one), then back to the walker.</summary>
    private void SavePending()
    {
        if (_pending == null || --_pendingFrames > 0) return;
        var path = ProjectSettings.GlobalizePath($"res://test_output/moon/{_pending}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        AirLog($"saved {path}");
        _pending = null;
        _local()?.Camera.MakeCurrent();
    }
}
