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

    /// <summary>What it plays when the relay is silent (<see cref="Tracker.TrackerSpeaker"/>).</summary>
    public Tracker.VenueMood Mood { get; set; }

    /// <summary>The venue's own shift of its playlist on the clock.</summary>
    public uint Salt { get; set; }

    /// <summary>
    /// How far from it the music keeps its full level, m (its speakers' <c>UnitSize</c>): a car stereo's 3 m, a cinema
    /// hall's half-diagonal (#1279), so the whole room hears it and not only its middle.
    /// </summary>
    public float Spread { get; set; } = 3f;

    public override void _Ready()
    {
        // the fallback beside the radio; a headless peer hears nothing, so mixes nothing
        if (Mood != Tracker.VenueMood.None && DisplayServer.GetName() != "headless")
            AddChild(new Tracker.TrackerSpeaker { Name = Tracker.TrackerSpeaker.NodeName, Mood = Mood, Salt = Salt, Position = new Vector3(0, 1f, 0) });
    }

    public override void _EnterTree() => All.Add(this);

    public override void _ExitTree() => All.Remove(this);
}
