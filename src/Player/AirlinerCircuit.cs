using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A scripted pilot for <c>--flycheck a320</c> (#414): flies one whole circuit on the real input
/// actions, as a player's keys and sticks would (analog strengths for the stick), so what is checked
/// is the game's own path from the keys to the model. Take-off at full thrust with flaps 1+F, gear
/// up, climb to 300 m, a 180° turn at 160 kt, an approach on a 3° path with the gear and the flaps
/// out, a flare, the brakes and the reversers, stopped; it lands within ~4 km of where it started
/// (the flat test world is 20 km across). Ends with what went wrong, if anything.
/// </summary>
public sealed class AirlinerCircuit
{
    private enum Phase { Start, Roll, Climb, Autopilot, Turn, Approach, Flare, Rollout }

    private Phase _phase = Phase.Start;
    private float _apFrom = float.NaN, _apWorst, _fuelAt;
    private float _startYaw = float.NaN, _turnedAt, _lastReport, _touchSink, _maxAgl;
    private bool _flapsSet, _gearUp, _gearDown;
    private float _healthAt, _lastHealth = float.MaxValue;

    public readonly record struct Outcome(bool Ok, string Text);

    /// <summary>Windowed with a picture path: called once a few seconds into each phase with its name, to save the view.</summary>
    public System.Action<string>? Snap;
    private Phase _snapped = (Phase)(-1);
    private float _phaseAt;

    private static void Axis(string negative, string positive, float value)
    {
        Press(negative, Mathf.Max(0f, -value));
        Press(positive, Mathf.Max(0f, value));
    }

    private static void Press(string action, float strength)
    {
        if (strength > 0.01f) Input.ActionPress(action, Mathf.Clamp(0.15f + strength * 0.85f, 0f, 1f));
        else Input.ActionRelease(action);
    }

    /// <summary>One physics step of the pilot. Non-null when the circuit is over.</summary>
    public Outcome? Step(Airliner jet, FootPlayer p, float agl, float t, System.Action<string, bool> hold)
    {
        var s = jet.State;
        var spec = jet.Spec;
        if (float.IsNaN(_startYaw)) { _startYaw = s.Yaw; _healthAt = _lastHealth = p.VehicleHealth; }
        _maxAgl = Mathf.Max(_maxAgl, agl);
        float vs = s.Velocity.Y;
        float pitch = AirlinerFlight.PitchOf(s.Attitude), bank = AirlinerFlight.BankOf(s.Attitude);
        float stickX = 0f, stickY = 0f, lever = 0f;
        bool brake = false;

        // fly a vertical speed with the stick (the law holds what it is given), a speed with the levers
        float FlyVs(float target) => Mathf.Clamp((target - vs) * 0.12f, -0.6f, 0.6f);
        // the levers move at a fixed rate while a key is held: a dead band stops them hunting
        float HoldSpeed(float target)
        {
            float err = target - s.Ias - s.Velocity.Y * 0.3f;
            return Mathf.Abs(err) < 2f ? 0f : Mathf.Sign(err);
        }
        float Heading(float target)
        {
            float err = Mathf.AngleDifference(s.Yaw, target);   // + : the target is to the left
            float wantBank = Mathf.Clamp(-err * 1.2f, -0.45f, 0.45f);
            return Mathf.Clamp((wantBank - bank) * 2.5f, -1f, 1f);
        }

        bool sim = Airliner.Handling == AirlinerHandling.Sim;
        switch (_phase)
        {
            case Phase.Start:
                // Light sim (#415): cold and dark, so the start first: battery, APU, each engine
                brake = true;
                if (!sim || s.Lit >= spec.Engines) { _phase = Phase.Roll; _fuelAt = s.Fuel; break; }
                if (!s.Starting) jet.Command(AirlinerCommand.Engines);
                if (t > 150f) return new Outcome(false, $"engines never started (APU {s.Apu:F2}, running {s.Lit:F1})");
                break;
            case Phase.Roll:
                if (!_flapsSet) { jet.Command(AirlinerCommand.FlapsDown); jet.Command(AirlinerCommand.FlapsDown); _flapsSet = true; }
                lever = 1f;
                stickX = Heading(_startYaw);
                if (s.Ias > spec.StallSpeed(s.Mass, s.FlapLever) * 1.08f && pitch < 0.2f) stickY = 0.8f;
                if (!s.OnGround && agl > 15f) _phase = Phase.Climb;
                if (t > 120f) return new Outcome(false, $"never lifted off ({s.Ias / 0.5144f:0} kt)");
                break;
            case Phase.Climb:
                if (!_gearUp && agl > 40f) { jet.Command(AirlinerCommand.Gear); _gearUp = true; }
                stickY = FlyVs(Mathf.Clamp((300f - agl) * 0.08f, -4f, 9f));
                lever = agl < 200f ? 1f : HoldSpeed(82f);
                stickX = Heading(_startYaw);
                if (agl > 280f) { _phase = sim ? Phase.Autopilot : Phase.Turn; _turnedAt = t; }
                break;
            case Phase.Autopilot:
                // Light sim: the autopilot holds the altitude and the heading for 25 s, hands off
                if (float.IsNaN(_apFrom))
                {
                    _apFrom = t;
                    jet.Command(AirlinerCommand.Autopilot);
                    break;
                }
                float altitude = p.Origin is { } o ? (float)o.ToGlobal(p.GlobalPosition).Alt : agl;
                if (t - _apFrom > 3f) _apWorst = Mathf.Max(_apWorst, Mathf.Abs(altitude - s.ApAltitude));
                if (!s.Autopilot && t - _apFrom > 0.5f) return new Outcome(false, "the autopilot did not engage");
                if (t - _apFrom > 25f)
                {
                    GD.Print($"[flycheck] a320 autopilot held its altitude within {_apWorst:F1} m for 22 s");
                    if (_apWorst > 30f) return new Outcome(false, $"the autopilot lost {_apWorst:F0} m");
                    jet.Command(AirlinerCommand.Autopilot);
                    _phase = Phase.Turn;
                    _turnedAt = t;
                }
                break;
            case Phase.Turn:
                stickY = FlyVs(Mathf.Clamp((300f - agl) * 0.08f, -4f, 4f));
                lever = HoldSpeed(82f);
                stickX = Heading(_startYaw + Mathf.Pi);
                if (Mathf.Abs(Mathf.AngleDifference(s.Yaw, _startYaw + Mathf.Pi)) < 0.05f && Mathf.Abs(bank) < 0.05f && t - _turnedAt > 20f)
                    _phase = Phase.Approach;
                break;
            case Phase.Approach:
            {
                // configure as the speed allows: each notch below its limit, the gear below VLO
                float vref = spec.StallSpeed(s.Mass, spec.FlapSettings - 1) * 1.23f;
                int want = Mathf.Min(spec.FlapSettings - 1, s.FlapLever + 1);
                if (s.FlapLever < want && s.Ias < spec.FlapLimit[want] - 8f && Mathf.Abs(s.Flaps - s.FlapLever) < 0.05f)
                    jet.Command(AirlinerCommand.FlapsDown);
                if (!_gearDown && s.Ias < spec.GearLimit - 10f) { jet.Command(AirlinerCommand.Gear); _gearDown = true; }
                float path = -0.0524f * s.Velocity.Length();   // 3°
                stickY = FlyVs(path);
                lever = HoldSpeed(s.FlapLever == spec.FlapSettings - 1 ? vref : spec.FlapLimit[Mathf.Min(s.FlapLever + 1, spec.FlapSettings - 1)] - 12f);
                stickX = Heading(_startYaw + Mathf.Pi);
                if (agl < 14f && s.Gear >= 1f) _phase = Phase.Flare;
                if (agl < 14f && s.Gear < 1f) return new Outcome(false, "approach without the gear down");
                break;
            }
            case Phase.Flare:
                stickY = FlyVs(-1.2f);
                lever = -1f;
                stickX = Heading(_startYaw + Mathf.Pi);
                if (s.OnGround) { _touchSink = s.LastSink; _phase = Phase.Rollout; }
                break;
            case Phase.Rollout:
                brake = true;
                lever = -1f;
                stickX = Heading(_startYaw + Mathf.Pi);
                if (s.Velocity.Length() < 0.5f)
                {
                    float damage = _healthAt - p.VehicleHealth;
                    string text = $"landed and stopped: touchdown sink {_touchSink:F2} m/s, peak {_maxAgl:F0} m agl, damage {damage:F0}"
                        + (sim ? $", fuel burnt {_fuelAt - s.Fuel:F0} kg" : "");
                    return new Outcome(_touchSink < spec.HardLanding && damage < 1f, text);
                }
                break;
        }

        Axis(PlayerInput.MoveLeft, PlayerInput.MoveRight, stickX);
        Axis(PlayerInput.MoveForward, PlayerInput.MoveBack, stickY);
        // the levers: Shift forward, Ctrl back (held at idle on the ground: reverse)
        Press(PlayerInput.Sprint, Mathf.Max(lever, 0f) > 0.05f ? 1f : 0f);
        Press(PlayerInput.CrouchSlide, lever < -0.05f ? 1f : 0f);
        if (lever is > -0.05f and < 0.05f) { Input.ActionRelease(PlayerInput.Sprint); Input.ActionRelease(PlayerInput.CrouchSlide); }
        hold(PlayerInput.Jump, brake);

        if (_phase != _snapped && _phaseAt < 0f) _phaseAt = t;
        if (_phase != _snapped && t - _phaseAt > (_phase == Phase.Roll ? 18f : 4f))
        {
            _snapped = _phase;
            Snap?.Invoke(_phase.ToString().ToLowerInvariant());
        }
        if (_phase == _snapped) _phaseAt = -1f;
        if (p.VehicleHealth < _lastHealth - 0.01f)
            GD.Print($"[flycheck] a320 damage {_lastHealth - p.VehicleHealth:F1} in {_phase} at {t:F1} s: ias {s.Ias / 0.5144f:0} kt, vs {vs:F1}, agl {agl:F1}, ground {s.OnGround}, gear {s.Gear:F2}");
        _lastHealth = p.VehicleHealth;
        if (t - _lastReport >= 5f)
        {
            _lastReport = t;
            GD.Print($"[flycheck] a320 {_phase,-8} ias {s.Ias / 0.5144f,4:0} kt  agl {agl,5:0} m  vs {vs,5:0.0}  pitch {Mathf.RadToDeg(pitch),5:0.0}  bank {Mathf.RadToDeg(bank),5:0.0}"
                + $"  N1 {s.Spool * 100f,3:0}%  flaps {spec.FlapNames[s.FlapLever]}  gear {s.Gear:0.0}");
        }
        if (!s.OnGround && agl < -0.5f) return new Outcome(false, "under the ground");
        return null;
    }
}
