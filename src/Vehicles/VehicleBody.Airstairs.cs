using Godot;
using UnitSport.Avatar;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// Parked airstairs (#417): docked, the platform follows the sill it stands at (worked out on every
/// peer from the aircraft it sees, so nothing more is replicated); an aircraft taxiing away with
/// them docked shoves them clear sideways (their authority moves them). See
/// <c>docs/notes/vehicles/airstairs.md</c>.
/// </summary>
public partial class VehicleBody
{
    private float _stairsLook;
    private readonly List<AirstairsDock.Sill> _sills = new();
    private bool _stairsPushed;
    private float _stairsFlagsHeight = float.NaN;

    /// <summary>The sill these stairs stand docked at, as last looked (every half second); null when none.</summary>
    public AirstairsDock.Sill? StairsDockedAt { get; private set; }

    /// <summary>Every frame, every peer: the frame is where the stairs stand, the platform eases to the sill.</summary>
    private void StandAirstairs(Airstairs stairs, float dt)
    {
        // nothing poses it: its frame is the body's own (headless) or its visual at the body's origin
        Posed = true;
        if ((_stairsLook -= dt) <= 0f)
        {
            _stairsLook = 0.5f;
            AirstairsDock.SillsNear(GetTree(), GlobalPosition, 12f, _sills);
            StairsDockedAt = AirstairsDock.DockedSill(GlobalTransform, _sills);
            // docked: the height follows the sill (an aircraft kneeling, loaded, refuelled)
            if (StairsDockedAt is { } sill && !Wrecked)
                stairs.TargetHeight = sill.Edge.Y - GlobalPosition.Y + AirstairsLayout.DockAbove;
        }
        if (!Mathf.IsEqualApprox(stairs.Height, AirstairsLayout.Clamp(stairs.TargetHeight))) stairs.Lift(dt);
        if (_visual is AirstairsRig rig) rig.Height = stairs.Height;
        // kept in its state, so whoever takes it gets it at this height
        if (float.IsNaN(_stairsFlagsHeight) || Mathf.Abs(_stairsFlagsHeight - stairs.Height) > 0.01f)
        {
            _stairsFlagsHeight = stairs.Height;
            _initial = _initial with { Flags = stairs.PackFlags() };
        }
    }

    /// <summary>Each kind's level footprint, node space: x half width, z from and to (wings and all).</summary>
    private static readonly Dictionary<RideKind, (float HalfX, float Z0, float Z1)> Envelopes = new();

    private static (float HalfX, float Z0, float Z1) EnvelopeOf(Rideable ride)
    {
        if (Envelopes.TryGetValue(ride.Kind, out var known)) return known;
        var (c, s) = ride.ParkedBox;
        float hx = Mathf.Abs(c.X) + s.X * 0.5f, z0 = c.Z - s.Z * 0.5f, z1 = c.Z + s.Z * 0.5f;
        foreach (var (pose, centre, size) in ride.ExtraBoxes())
        {
            var at = pose * centre;
            hx = Mathf.Max(hx, Mathf.Abs(at.X) + size.X * 0.5f);
            z0 = Mathf.Min(z0, at.Z - size.Z * 0.5f);
            z1 = Mathf.Max(z1, at.Z + size.Z * 0.5f);
        }
        return Envelopes[ride.Kind] = (hx, z0, z1);
    }

    /// <summary>
    /// The stairs' authority, every physics step, asleep or not: an aircraft moving with these stairs
    /// inside its footprint (docked at it, or in the way of its wing) shoves them out sideways, away
    /// from its centreline, faster than it goes, until they are clear of its span. No aircraft is
    /// stuck on its stairs; the stairs keep their height (they are no longer docked).
    /// </summary>
    private void PushAirstairs(float dt)
    {
        bool pushing = false;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            if (s.Ride == RideKind.OnFoot || s.Player.RideModel is not Flyer { Walkable: true } jet) continue;
            float speed = new Vector2(s.Vel.X, s.Vel.Z).Length();
            if (speed < 0.3f || s.Pos.DistanceTo(GlobalPosition) > 60f) continue;
            var (hx, z0, z1) = EnvelopeOf(jet);
            var plane = s.Player.GlobalTransform.Orthonormalized();
            var inverse = plane.AffineInverse();
            // the stairs' foot, middle and lip, in the aircraft's frame
            float deepest = 0f, side = 0f;
            foreach (float z in StairsPoints)
            {
                var local = inverse * (GlobalTransform * new Vector3(0, 0, z));
                if (local.Z < z0 - 1f || local.Z > z1 + 1f) continue;
                float depth = hx + 1.2f - Mathf.Abs(local.X);
                if (depth > deepest) { deepest = depth; side = local.X >= 0f ? 1f : -1f; }
            }
            if (deepest <= 0f) continue;
            var lateral = (plane.Basis.X with { Y = 0 }).Normalized() * side;
            float step = Mathf.Min(deepest, Mathf.Max(2.5f, speed * 2f) * dt);
            GlobalPosition += lateral * step;
            pushing = true;
        }
        if (pushing)
        {
            if (!_stairsPushed && _sync != null) _sync.ReplicationInterval = 0.05f;
            _stairsPushed = true;
            Velocity = Vector3.Zero;
            _place.Publish(GlobalPosition);
            return;
        }
        if (!_stairsPushed) return;
        _stairsPushed = false;
        // shoved clear: a client lets it settle on the ground by physics (then it sleeps again); the
        // dedicated server has no ground to settle it on, so it stays put, asleep
        if (Net.NetworkManager.DedicatedServer) { if (_sync != null) _sync.ReplicationInterval = 2f; }
        else { _asleep = false; _restTime = 0f; SetAnchored(true); }
    }

    /// <summary>The stairs' foot, middle and lip along their own axis (node z).</summary>
    private static readonly float[] StairsPoints = { -AirstairsLayout.FootZ, 0f, -AirstairsLayout.LipZ };
}
