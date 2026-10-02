using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Birds;

/// <summary>
/// <c>godot --path . -- --birdstrikecheck[,out.png] [--at E,N]</c>
///
/// <para>
/// Flies a plane 400 m up at 180 km/h into a house sparrow hanging on its nose line, then into a
/// greylag goose, then fires the guns at a (protected) common buzzard on the boresight. Passes when
/// the two struck birds die, the sparrow is a dent, the goose costs far more and stops the engine,
/// the buzzard falls to the rounds (not to a strike) with the protected-species penalty, and the
/// plane is still flying. Birds do not dodge here (they get one chance
/// in play). Works with no terrain: after 20 s it flies over a stand-in pad, like
/// <c>--combatcheck</c>.
/// </para>
///
/// <para>
/// <c>--survey &lt;minutes&gt;</c> measures the rate instead: a plane held 60 m above the real
/// terrain at 160 km/h on a 1.5 km circle (re-placed every half second, so it cannot fly into a
/// hill) with birds
/// spawning and dodging as in play, time sped up 5×. Prints strikes per 10 minutes; needs terrain.
/// </para>
/// </summary>
public partial class BirdStrikeProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly BirdLife _birds;
    private readonly string? _shot;
    private FootPlayer? _player;
    private double _wait, _t;
    private bool _started, _done, _noGround;
    private int _stage;
    private Bird? _bird;
    private float _before, _smallCost, _bigCost;
    private bool _engineAfterSmall;
    private int _scoreBefore, _strikesBefore;
    private bool _shotDown;
    private readonly float _survey = SurveyMinutes();
    private double _hold;

    private static float SurveyMinutes() => CmdArgs.Float("--survey") ?? 0f;

    public BirdStrikeProbe(ChunkManager chunks, WorldOrigin origin, BirdLife birds, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _birds = birds;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--birdstrikecheck");

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 120 + _survey * 60) { GD.Print("[birdstrike] TIMEOUT"); Finish(2); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (_player == null)
        {
            if (!_chunks.TryGetHeight(at, out float g))
            {
                if (_wait < 20) return;
                GD.Print("[birdstrike] no terrain under the spawn; flying over the void");
                g = at.Y;
                _noGround = true;
                var pad = new StaticBody3D { Name = "Pad" };
                pad.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) } });
                AddChild(pad);
                pad.GlobalPosition = at + Vector3.Down * 0.5f;
            }
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
            if (_noGround) _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
            _player.Announced += (text, _) => GD.Print($"[birdstrike] announce: {text}");
            _birds.AutoSpawn = _survey > 0;
            _birds.EvadeAircraft = _survey > 0;
            _birds.PlayerOverride = () => _player;
            if (Combat.CombatManager.Instance is { } combat)
            {
                combat.LocalPlayer = () => _player;
                combat.DronesEnabled = false;
            }
            return;
        }
        if (!_started)
        {
            if ((!_noGround && !_chunks.HasCollisionAt(at)) || !_player.IsOnFloor()) return;
            _player.SetRide(RideKind.Plane);
            _player.DebugLaunch(_player.GlobalPosition + Vector3.Up * 400f, new Vector3(0, 0, -50));
            _started = true;
            return;
        }

        _t += delta;
        if (_survey > 0) { Survey(delta); return; }
        switch (_stage)
        {
            case 0 when _t > 1.0: Place("House Sparrow"); break;
            case 1 when _bird!.State is Bird.Mode.Falling or Bird.Mode.Dead:
                _smallCost = _before - _player.VehicleHealth;
                _engineAfterSmall = _player.EngineOn;
                Report();
                _stage = 2;
                break;
            case 2 when _t > 4.0:
                PlaceOnGuns();
                Input.ActionPress(PlayerInput.Fire);
                _stage = 3;
                break;
            case 3 when _bird!.State is Bird.Mode.Falling or Bird.Mode.Dead || _t > 7.0:
                Input.ActionRelease(PlayerInput.Fire);
                _shotDown = _bird.State is Bird.Mode.Falling or Bird.Mode.Dead && _birds.Strikes == _strikesBefore;
                GD.Print($"[birdstrike] buzzard {(_shotDown ? "shot down" : "NOT shot down")} by the guns ({Combat.CombatManager.Instance!.Shots} rounds, {Combat.CombatManager.Instance!.Hits} hits, armed {Combat.CombatManager.Instance!.Armed}), score {_scoreBefore} to {_birds.Journal.Score}");
                _stage = 4;
                _t = 7.0;
                break;
            case 4 when _t > 8.0: Place("Greylag Goose"); break;
            case 5 when _bird!.State is Bird.Mode.Falling or Bird.Mode.Dead:
                _bigCost = _before - _player.VehicleHealth;
                Report();
                End();
                break;
        }
        if (_t > 16 && !_done) End();
    }

    private void Survey(double delta)
    {
        Engine.TimeScale = 5.0;
        var p = _player!;
        _hold -= delta;
        if (_hold <= 0 && p.Ride == RideKind.Plane)
        {
            _hold = 0.5;
            // a 1.5 km circle, so ten minutes stays over the few km of terrain there is
            float a = (float)_t * 0.03f;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var at = p.GlobalPosition;
            if (_chunks.TryGetHeight(at + dir * 40f, out float g)) at.Y = g + 60f;
            p.DebugLaunch(at, dir * 45f);
        }
        if (_t < _survey * 60 && p.Ride == RideKind.Plane) return;
        double minutes = _t / 60.0;
        GD.Print($"[birdstrike] SURVEY {minutes:F1} min low flying: {_birds.Strikes} strikes "
            + $"({_birds.Strikes / minutes * 10:F1} per 10 min), birds now {_birds.Birds.Count}, ride {p.Ride}");
        Engine.TimeScale = 1.0;
        Finish(0);
    }

    /// <summary>
    /// A protected common buzzard where one wing gun's rounds will be 200 m out — inside
    /// <see cref="BirdLife.DespawnDistance"/>, which removes anything further. The two guns fire
    /// parallel to the nose 1.7 m either side of it (they do not converge), so the bird sits on the
    /// right-hand gun's line, carried by the plane's own velocity and dropped by gravity over the
    /// 0.29 s of flight.
    /// </summary>
    private void PlaceOnGuns()
    {
        var p = _player!;
        var combat = Combat.CombatManager.Instance!;
        var att = p.Flight.Attitude.Orthonormalized();
        var nose = -att.Z;
        var centre = combat.AimPoint - nose * 400f + att.X * 1.7f;
        const float t = 200f / 700f;
        var at = centre + (nose * 700f + p.Flight.Velocity) * t + Vector3.Down * (0.5f * Rideable.Gravity * t * t);
        var s = BirdCatalog.ByName("Common Buzzard")!;
        _bird = _birds.Spawn(s, at, Bird.Mode.Hovering, lift: 0f);
        _scoreBefore = _birds.Journal.Score;
        _strikesBefore = _birds.Strikes;
        GD.Print($"[birdstrike] Common Buzzard (protected) on the gun line {_bird.Centre.DistanceTo(p.GlobalPosition):F0} m out; firing");
    }

    /// <summary>A bird hanging 90 m down the plane's path, at the height of its strike volume.</summary>
    private void Place(string name)
    {
        var s = BirdCatalog.ByName(name)!;
        var p = _player!;
        var fwd = p.Flight.Velocity.Normalized();
        _bird = _birds.Spawn(s, p.GlobalPosition + Vector3.Up * 1.4f + fwd * 90f, Bird.Mode.Hovering, lift: 0f);
        _before = p.VehicleHealth;
        _stage++;
        GD.Print($"[birdstrike] {name} ({BirdLife.Mass(s):F2} kg) placed 90 m ahead, plane at {p.Flight.Velocity.Length() * 3.6f:F0} km/h, health {_before:F1}");
    }

    private void Report()
    {
        var (sp, j, dmg, eo) = _birds.LastStrike;
        GD.Print($"[birdstrike] struck {sp}: {j:F0} J -> {dmg:F1} damage, engine out {eo}, plane health {_player!.VehicleHealth:F1}, engine {(_player.EngineOn ? "on" : "off")}");
    }

    private void End()
    {
        var p = _player!;
        bool ok = _birds.Strikes == 2 && _smallCost > 0f && _smallCost < 5f && _bigCost > _smallCost * 10f
            && _engineAfterSmall && !p.EngineOn && p.Ride == RideKind.Plane
            && _shotDown && _birds.Journal.Score < _scoreBefore;
        GD.Print($"[birdstrike] END strikes {_birds.Strikes}, sparrow cost {_smallCost:F2}, goose cost {_bigCost:F1}, "
            + $"engine after sparrow {(_engineAfterSmall ? "on" : "off")}, now {(p.EngineOn ? "on" : "off")}, ride {p.Ride}");
        GD.Print(ok ? "[birdstrike] RESULT: ok" : "[birdstrike] RESULT: FAILED");
        if (_shot != null && GetViewport().GetTexture().GetImage().SavePng(_shot) == Error.Ok)
            GD.Print($"[birdstrike] wrote {_shot}");
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        Input.ActionRelease(PlayerInput.Fire);
        _done = true;
        GetTree().Quit(code);
    }
}
