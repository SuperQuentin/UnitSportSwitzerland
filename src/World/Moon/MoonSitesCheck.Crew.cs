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
                foreach (var (at, shown, walking, outside) in crew.Figures())
                {
                    if (!shown) continue;
                    _crewShown++;
                    if (walking) _crewWalking++;
                    if (outside) _crewOutside++;
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
        }
    }

    private static void CrewLog(string what) => GD.Print($"[moonsites]   crew: {what}");
}
