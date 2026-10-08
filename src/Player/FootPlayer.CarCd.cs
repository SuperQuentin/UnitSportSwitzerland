using Godot;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>A CD in the car's stereo (#211), beside the live station of <see cref="CarRadio"/>.</summary>
public partial class FootPlayer
{
    /// <summary>
    /// The CD the car, truck or bus this player drives plays (<see cref="RadioPlay"/>), empty when
    /// none; set by the driver from the radio panel. Replicated like <see cref="CarRadio"/> and never
    /// both at once: picking one clears the other. Kept in the parked vehicle (<c>VehicleState.Cd</c>).
    /// </summary>
    [Export] public string CarCd { get; set; } = "";

    /// <summary>
    /// The volume of the radio this player carries and of their car stereo, 0..1 (#734), everyone's:
    /// how loud and how far it plays (<see cref="Items.RadioLoudness"/>). Written by the owner,
    /// replicated on change; a thrown radio takes it along, a picked-up one brings its own.
    /// </summary>
    [Export] public float RadioVolume { get; set; } = Items.RadioLoudness.Default;

    /// <summary>The CD this body's vehicle plays, or null when it drives none or plays no CD.</summary>
    public RadioPlay? PlayingCarCd => RidingWith == 0 && CarCd.Length > 0 && HasCarRadio((RideKind)RideKindId) ? RadioPlay.Decode(CarCd) : null;

    /// <summary>
    /// The player whose stereo this one can reach: itself at the wheel of a car, truck or bus; the
    /// driver when riding along in one; otherwise null.
    /// </summary>
    public FootPlayer? StereoOwner
    {
        get
        {
            if (RidingWith != 0) return Host is { } host && HasCarRadio((RideKind)host.RideKindId) ? host : null;
            return SeatIndex == 0 && HasCarRadio((RideKind)RideKindId) ? this : null;
        }
    }
}
