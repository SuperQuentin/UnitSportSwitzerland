using Godot;

namespace UnitSport.Player;

/// <summary>
/// A machine with tines that lift pallets (#583, #615): the forklift, the telehandler, the wheel
/// loader with its fork carriage. What <c>Items/PalletService.Tend</c> asks of one, so the fork rule,
/// the take and the set-down are the same for all of them: where its tines are, how high, and what
/// is on them. See <c>docs/notes/vehicles/pallets.md</c>.
/// </summary>
public interface IForks
{
    /// <summary>What is on the tines: 0 nothing, else <c>Pallets.Carried</c> (1 + a load byte, and the across bit).</summary>
    int Carrying { get; set; }

    /// <summary>Whether this one has tines at all (a wheel loader may carry a bucket instead).</summary>
    bool HasTines { get; }

    /// <summary>The tines' top face over the ground the machine stands on, m: what the take and the set-down heights are measured against.</summary>
    float ForkHeight { get; }

    /// <summary>
    /// The tines' frame in the vehicle's node space (forward −Z, +X its right): origin between the
    /// tines on their top face, at their heel (the carriage's face), turned as they point.
    /// </summary>
    Transform3D TinesFrame { get; }

    /// <summary>How far the tines reach ahead of their heel, m, and half their span outside edge to outside edge.</summary>
    float TineLength { get; }
    float TineHalfSpan { get; }
}
