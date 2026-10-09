using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A machine whose tipping body carries one pallet set down in it (#615): the tipper's body, the
/// mini dumper's skip. The pallet is part of the machine, as one on forks is: <see cref="BedLoad"/>
/// rides its pose and its parked flags, and nothing is on the wire per frame. What
/// <c>Items.PalletService</c> asks of it to set a pallet in and to tip it out.
/// See <c>docs/notes/vehicles/pallets.md</c>.
/// </summary>
public interface IBed
{
    /// <summary>It has a tipping body: a tipper (a mixer or a bus has none).</summary>
    bool HasBed { get; }

    /// <summary>What is in it: 0, else <c>Pallets.Carried(load, across)</c>, across meaning the runners across the machine.</summary>
    int BedLoad { get; set; }

    /// <summary>The body tipped up (the driver's state, in the pose).</summary>
    bool BedUp { get; }

    /// <summary>Where it carries a pallet and where one tipped out lands.</summary>
    BedShape Bed { get; }

    /// <summary>The parked flags <paramref name="flags"/> with <paramref name="bedLoad"/> in the body instead.</summary>
    int FlagsWithBed(int flags, int bedLoad);
}
