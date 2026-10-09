using Godot;

namespace UnitSport.Player;

/// <summary>
/// A machine with a bucket that carries a loose pallet (#615: the wheel loader's): what
/// <c>Items/PalletService.TendBucket</c> asks of it. Driven in with the bucket down and level, a
/// pallet in it is scooped up when the bucket curls back, and tipped out when it is dumped
/// (<c>Pallets.Curled</c>, <c>Pallets.Dumped</c>). See <c>docs/notes/vehicles/pallets.md</c>.
/// </summary>
public interface IBucket
{
    /// <summary>What is in the bucket: 0 nothing, else <c>Pallets.Carried</c> (1 + a load byte, and the across bit).</summary>
    int Carrying { get; set; }

    /// <summary>Whether this one has a bucket (a wheel loader may carry forks instead).</summary>
    bool HasBucket { get; }

    /// <summary>The bucket's frame in the vehicle's node space (forward −Z): origin at the pin it hangs from, turned and pitched with it.</summary>
    Transform3D BucketFrame { get; }

    /// <summary>
    /// The same pin, level: turned with the machine's front, not pitched with the bucket. What
    /// "in the bucket" is measured in — the bucket swings its floor through a wide arc about a pin
    /// above it as it curls, and a pallet scooped up must not slip out of the test on the way.
    /// </summary>
    Transform3D BucketPinLevel { get; }

    /// <summary>The bucket's pitch from level, rad (+ rolled back): what curls a pallet in and dumps it out.</summary>
    float BucketPitch { get; }

    /// <summary>The middle of the bucket's floor, in its own frame.</summary>
    Vector3 BucketFloor { get; }

    /// <summary>Half the bucket's width and how far its lip stands ahead of the pin, m.</summary>
    float BucketHalfWidth { get; }
    float BucketReach { get; }
}
