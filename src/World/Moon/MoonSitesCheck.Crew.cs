using System;
using System.Globalization;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

public partial class MoonSitesCheck
{
    /// <summary>
    /// <c>--colonistcheck</c> (#1945), after the last named staffed colony (<c>--moonsites artemis:close --colonistcheck</c>):
    /// the crew's clock is set 3 s into a window (walks under way), then for 25 s every shown figure inside stands in the
    /// colony's air (<see cref="ColonyAirlocks.Pressurised"/>: in a module or a tube, never in a wall or outside) and every
    /// one out on an EVA stands outside it; it counts those shown, walking and outside. Fails with no crew, nobody shown, or
    /// a figure where it should not be. Windowed: <c>crew_hub.png</c> (in the hub) and <c>crew_eva.png</c>.
    /// </summary>
    public static bool ColonistCheck => CmdArgs.Has("--colonistcheck");

    private int _crewStage;
    private double _crewT;
    private int _crewSamples, _crewWalking, _crewOutside, _crewShown;

    private void CrewStep(double delta)
    {
        _crewT += delta;
        var s = _sites[_index];
        var colony = MoonColonies.Find(PlacedId(s));
        if (colony == null || MoonSurface.Active?.Sites.TryGetValue(colony.Id, out var placed) != true) { Finish("no colony in place"); return; }
        var crew = ColonyColonists.In(placed!.Node).FirstOrDefault();
        if (crew == null) { Finish($"{colony.Id}: no colonists"); return; }
        switch (_crewStage)
        {
            case 1:
            {
                double t = SimClock.SimAt(ClockSync.ServerNow);
                ColonyColonists.ClockOffset = ColonyCrew.Window - (t % ColonyCrew.Window) + 3;
                CrewLog($"{crew.Crew.Crew.Count} colonists, {crew.Crew.Spots.Count} spots; the crew's clock 3 s into a window");
                _crewStage = 2;
                _crewT = 0;
                break;
            }
            case 2:
                if (_crewT < 1) break;
                if (_crewSamples == 0 && crew.BotsOn) CrewLog("the crew are bots (#1976)");
                foreach (var (at, shown, walking, outside) in crew.Figures())
                {
                    if (!shown) continue;
                    _crewShown++;
                    if (walking) _crewWalking++;
                    if (outside) _crewOutside++;
                    // a bot on its way is anywhere along it (a chamber pumped down, the outer stairs): judged where it stands
                    if (crew.BotsOn && walking) continue;
                    bool air = ColonyAirlocks.Pressurised(at + Vector3.Up * 1.0f);
                    if (air == outside)
                    {
                        Finish(string.Create(CultureInfo.InvariantCulture,
                            $"a colonist {(outside ? "on an EVA stands in the air" : "inside stands outside the air")} at {at}"));
                        return;
                    }
                }
                _crewSamples++;
                if (_crewT > 25)
                {
                    CrewLog(string.Create(CultureInfo.InvariantCulture,
                        $"{_crewSamples} looks: on average {_crewShown / (double)_crewSamples:F1} shown, {_crewWalking / (double)_crewSamples:F1} walking, {_crewOutside / (double)_crewSamples:F1} out on an EVA"));
                    if (_crewShown == 0) { Finish("no colonist shown"); return; }
                    if (ColonyColonists.ForceEva)
                    {
                        _crewStage = 10;
                        _crewT = 0;
                        break;
                    }
                    _crewStage = 3;
                    _crewT = 0;
                    if (!_headless)
                    {
                        var hub = placed.Node.GlobalTransform * new Vector3(0, ColonyKit.FloorY + 1.7f, 0);
                        var plan = ColonyPlan.For(colony);
                        var m = plan.Modules[0];
                        var (ax, az) = ColonyPlan.Along(m.Yaw + 0.4);
                        Shot("crew_hub", hub + placed.Node.GlobalBasis * new Vector3((float)ax * 3.6f, 0.4f, (float)az * 3.6f), hub + Vector3.Down * 0.6f);
                    }
                }
                break;
            case 3:
            {
                if (_headless) { Finish(null); return; }
                // an EVA colonist if one is out, from 6 m
                var eva = crew.Figures().FirstOrDefault(f => f.Shown && f.Outside);
                if (eva.Shown)
                    Shot("crew_eva", eva.At + Vector3.Up * 2.2f + placed.Node.GlobalBasis * new Vector3(4, 0, 4), eva.At + Vector3.Up * 1.0f);
                _crewStage = 4;
                break;
            }
            case 4:
                ColonyColonists.ClockOffset = 0;
                Finish(null);
                break;
            default:
                EvaStep(crew, placed!.Node, colony);
                break;
        }
    }

    // ---- --colonisteva (#1976): a colonist bot out through an airlock, by rover to its EVA spot, and back --------------

    private int _evaMember = -1;
    private bool _evaDrove, _evaCycled, _shotRover, _shotLock, _shotEva;
    private string? _evaRover;

    private void EvaStep(ColonyColonists crew, Node3D colonyNode, MoonColony colony)
    {
        if (!crew.BotsOn) { Finish("--colonisteva: the crew are not bots (no bot director?)"); return; }
        if (_evaMember < 0)
            for (int i = 0; i < crew.Crew.Crew.Count; i++)
                if (crew.Crew.Crew[i].Eva >= 0) { _evaMember = i; break; }
        if (_evaMember < 0) { Finish("no colonist with an EVA spot"); return; }
        var who = crew.Crew.Crew[_evaMember];
        var bot = crew.BotOf(_evaMember);
        var brain = crew.BrainOf(_evaMember);
        if (bot == null || brain == null)
        {
            if (_crewT > 10) Finish("the EVA colonist has no bot");
            return;
        }
        var body = bot.Body;
        if (brain.Driving) _evaDrove = true;
        if (brain.Status.StartsWith("Cycle")) _evaCycled = true;
        var eva = crew.Crew.Spots[who.Eva];
        // the camera follows the colonist (the body's ground square follows the camera): 9 m behind, 4 m up
        if (_crewStage >= 11 && _pending == null)
        {
            _airCam ??= new Camera3D { Name = "AirlockShot", Fov = 70 };
            if (_airCam.GetParent() == null) AddChild(_airCam);
            var at = body.GlobalPosition;
            _airCam.GlobalPosition = at + body.GlobalBasis.Z * 9f + Vector3.Up * 4f;
            _airCam.LookAt(at + Vector3.Up * 1.0f, Vector3.Up);
            _airCam.MakeCurrent();
        }
        bool helmet = body.HeadwearId == (int)Avatar.Headwear.SpaceHelmet;
        bool air = ColonyAirlocks.Pressurised(body.GlobalPosition + Vector3.Up * 1.6f);
        switch (_crewStage)
        {
            case 10:
            {
                var spot = colonyNode.GlobalTransform * new Vector3((float)eva.X, (float)eva.Y, (float)eva.Z);
                CrewLog(string.Create(CultureInfo.InvariantCulture,
                    $"EVA colonist {who.Index} {bot.Profile.Name} ({body.Name}): spot {who.Eva} {spot.DistanceTo(colonyNode.GlobalPosition):F0} m from the hub, airlock {crew.Crew.LockFor(who.Eva)}; {brain.Status}"));
                _crewStage = 11;
                _crewT = 0;
                break;
            }
            case 11:
                // windowed: a picture in the airlock, at the wheel, at the spot
                if (!_headless && !_shotLock && brain.Status.StartsWith("Cycle") && !brain.Outside)
                {
                    _shotLock = true;
                    var head = body.GlobalPosition + Vector3.Up * 1.4f;
                    Shot("colonist_airlock", head + body.GlobalBasis.Z * 2.2f + Vector3.Up * 0.5f, head);
                    break;
                }
                if (!_headless && !_shotRover && brain.Driving && body.RideSpeed > 2)
                {
                    _shotRover = true;
                    var at = body.GlobalPosition;
                    Shot("colonist_rover", at + body.GlobalBasis.Z * 7f + body.GlobalBasis.X * 3f + Vector3.Up * 3f, at + Vector3.Up * 0.8f);
                    break;
                }
                if (brain.Spot == who.Eva && !brain.Moving)
                {
                    CrewLog(string.Create(CultureInfo.InvariantCulture,
                        $"the EVA colonist is at its spot after {_crewT:F0} s: cycled out {_evaCycled}, drove a rover {_evaDrove}, air at the head {air}, helmet {helmet}"));
                    if (!_evaCycled) { Finish("the EVA colonist never cycled an airlock"); return; }
                    if (!_evaDrove) { Finish("the EVA colonist never drove a rover"); return; }
                    if (air) { Finish("the EVA colonist stands in the air"); return; }
                    if (!helmet) { Finish("the EVA colonist has no helmet on outside"); return; }
                    foreach (var v in Vehicles.VehicleManager.Instance!.GetChildren())
                        if (v is Vehicles.VehicleBody vb && vb.Name.ToString().StartsWith(MoonRoverDepot.Prefix + colony.Id)
                            && vb.GlobalPosition.DistanceTo(body.GlobalPosition) < 20)
                            _evaRover = vb.Name;
                    if (!_headless && !_shotEva)
                    {
                        _shotEva = true;
                        var at = body.GlobalPosition;
                        Shot("colonist_eva", at + body.GlobalBasis.Z * 5f + body.GlobalBasis.X * 2.5f + Vector3.Up * 2.4f, at + Vector3.Up * 1.0f);
                    }
                    brain.GoHome();
                    _crewStage = 12;
                    _crewT = 0;
                    break;
                }
                if (_crewT > 300) Finish($"the EVA colonist did not reach its spot in 300 s ({brain.Status})");
                break;
            case 12:
                if (!brain.Moving && !brain.Outside && brain.Spot == who.Work)
                {
                    var slots = MoonRoverDepot.Slots(colony, ColonyPlan.For(colony));
                    float parked = float.MaxValue;
                    if (_evaRover != null && Vehicles.VehicleManager.Instance!.GetNodeOrNull(_evaRover) is Vehicles.VehicleBody rv)
                        foreach (var sl in slots)
                            parked = Mathf.Min(parked, rv.GlobalPosition.DistanceTo(colonyNode.GlobalTransform * sl.At));
                    CrewLog(string.Create(CultureInfo.InvariantCulture,
                        $"back inside after {_crewT:F0} s: air at the head {air}, helmet {helmet}, rover {_evaRover ?? "?"} {parked:F1} m from its slot"));
                    if (!air) { Finish("the colonist is back but not in the air"); return; }
                    if (helmet) { Finish("the colonist is back inside with the helmet on"); return; }
                    if (_evaRover == null || parked > 12) { Finish($"the rover was not parked back by its slot ({parked:F1} m)"); return; }
                    if (!_headless)
                    {
                        var head = body.GlobalPosition + Vector3.Up * 1.5f;
                        Shot("colonist_home", head + crew.Direction(crew.Crew.Spots[who.Work].Facing) * 2.6f + Vector3.Up * 0.3f, head);
                    }
                    _crewStage = 4;
                    break;
                }
                if (_crewT > 300) Finish($"the EVA colonist did not come back in in 300 s ({brain.Status})");
                break;
        }
    }


    private static void CrewLog(string what) => GD.Print($"[moonsites]   crew: {what}");
}
