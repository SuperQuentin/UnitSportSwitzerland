using Godot;

namespace UnitSport.Audio.Live;

/// <summary>
/// A venue's music (#1244): a point that plays a live station on the relay, as a car radio does
/// (<see cref="WebRadio"/> hangs its speaker on it and asks the server for its station when it is
/// near). Placed by <see cref="Interiors.VenueMusic"/>: loud in a venue's room, quiet just inside its
/// door for the street. Registers itself while in the tree; read on the main thread only.
/// </summary>
public partial class VenueMusicSource : Node3D
{
    /// <summary>Every source in the tree, for <see cref="WebRadio"/>'s scan.</summary>
    public static readonly HashSet<VenueMusicSource> All = new();

    /// <summary>The station it plays (<see cref="Stations"/>).</summary>
    public int Station { get; set; }

    /// <summary>Its speaker's volume, 0..1 (<see cref="Items.RadioLoudness"/>).</summary>
    public float Volume { get; set; } = Items.RadioLoudness.Default;

    public override void _EnterTree() => All.Add(this);

    public override void _ExitTree() => All.Remove(this);
}
