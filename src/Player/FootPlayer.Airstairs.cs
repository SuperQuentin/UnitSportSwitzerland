using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Driving airstairs (#417): brought slowly to an aircraft's door and let go, they line up with its
/// sill by themselves (slid and turned into place, as a driver would creep in) and raise the
/// platform to it; driven off, the platform comes down to its travel height. See
/// <c>docs/notes/vehicles/airstairs.md</c>.
/// </summary>
public partial class FootPlayer
{
    private readonly List<AirstairsDock.Sill> _stairsSills = new();
    private float _stairsLook;

    /// <summary>What this player rides, on any peer (a remote copy's from its replicated kind); null on foot.</summary>
    public Rideable? RideModel => VehicleOf(this);

    /// <summary>Lining up: how fast the truck creeps sideways and turns into the docked spot.</summary>
    private const float DockSlide = 0.9f, DockTurn = 0.5f;

    /// <summary>After the ride's step, before it moves: docking, the platform's height.</summary>
    private void DockStairs(Airstairs stairs, in RideInput input, float dt)
    {
        bool letGo = input.Throttle < 0.1f && input.Brake < 0.1f && Mathf.Abs(input.Steer) < 0.15f && SeatIndex == 0;
        if (!letGo || _motion.Speed > 1.5f)
        {
            if (stairs.Docked != null && _motion.Speed > 0.3f)
            {
                stairs.Docked = null;
                PassengerService.Say("Airstairs undocked.");
            }
            // on the move the platform rides low
            if (stairs.Docked == null && _motion.Speed > 0.5f) stairs.TargetHeight = AirstairsLayout.TravelHeight;
            return;
        }
        if ((_stairsLook -= dt) <= 0f)
        {
            _stairsLook = 0.25f;
            AirstairsDock.SillsNear(GetTree(), GlobalPosition, 15f, _stairsSills);
        }
        var frame = GlobalTransform.Orthonormalized();
        if (AirstairsDock.Approach(frame, _stairsSills) is not { } sill) return;

        var (origin, yaw, height) = AirstairsDock.Pose(sill, GlobalPosition.Y);
        stairs.TargetHeight = height;
        stairs.Hold(ref _motion);
        var to = (origin - GlobalPosition) with { Y = 0 };
        float turn = MathX.WrapAngle(yaw - _motion.Yaw);
        GlobalPosition += to.LimitLength(DockSlide * dt);
        _motion.Yaw += Mathf.Clamp(turn, -DockTurn * dt, DockTurn * dt);
        bool lined = to.Length() < 0.02f && Mathf.Abs(turn) < 0.01f;
        if (lined && stairs.Docked == null)
        {
            stairs.Docked = (sill.Host.Name, sill.Door);
            PassengerService.Say($"Airstairs docked at door {sill.Door + 1}.");
        }
    }
}
