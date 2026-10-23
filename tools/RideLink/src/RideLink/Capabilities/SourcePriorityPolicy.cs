namespace RideLink.Capabilities;

/// <summary>
/// Decides which of several sources for the same quantity wins.
/// <para>
/// Higher is better. The defaults encode the usual truth about indoor cycling hardware:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Power</b> — a strain-gauge power meter beats a trainer's estimate. Trainers infer watts
/// from a resistance curve and drift with temperature; pedals and cranks measure force.
/// </item>
/// <item>
/// <b>Cadence</b> — a dedicated magnet or accelerometer sensor beats a power meter's crank
/// counter, which beats a trainer's estimate. Trainers infer cadence from flywheel ripple and
/// are visibly wrong at low cadence or when soft-pedalling.
/// </item>
/// <item>
/// <b>Heart rate</b> — a worn strap beats a machine relaying whatever it happened to pair with.
/// </item>
/// <item>
/// <b>Speed</b> — indoors, the trainer's own speed is the meaningful one because it reflects the
/// simulated road; a wheel sensor on a wheel-off trainer reads zero, and on a wheel-on trainer
/// reads roller speed. A footpod wins for running.
/// </item>
/// </list>
/// <para>Subclass and override to change any of it; the router takes this as a dependency.</para>
/// </summary>
public class SourcePriorityPolicy
{
    /// <summary>Default policy.</summary>
    public static SourcePriorityPolicy Default { get; } = new();

    /// <summary>
    /// How long a source may go silent before it stops being eligible to win.
    /// <para>
    /// A strap that drops off mid-ride must hand over to the machine's relayed HR rather than
    /// freezing the last value forever. Five seconds is long enough to ride through a missed
    /// notification at a 1 Hz update rate and short enough that a real dropout is noticed.
    /// </para>
    /// </summary>
    public virtual TimeSpan StaleAfter => TimeSpan.FromSeconds(5);

    public virtual int RankPower(IPowerSource source) => source.Channel() switch
    {
        Channels.CyclingPower or Channels.AntBicyclePower => source.IsDirectForceMeasurement ? 100 : 70,
        Channels.FtmsIndoorBike or Channels.AntFitnessEquipment => 60,
        Channels.FtmsRower or Channels.FtmsCrossTrainer or Channels.FtmsTreadmill => 55,
        Channels.Simulated => 10,
        _ => 40,
    };

    public virtual int RankCadence(ICadenceSource source) => source.Channel() switch
    {
        Channels.SpeedCadence or Channels.AntSpeedCadence => 100,
        Channels.CyclingPower or Channels.AntBicyclePower => 80,
        Channels.RunningSpeedCadence => 75,
        Channels.FtmsIndoorBike or Channels.AntFitnessEquipment => 60,
        Channels.FtmsRower or Channels.FtmsCrossTrainer => 55,
        Channels.Simulated => 10,
        _ => 40,
    };

    public virtual int RankHeartRate(IHeartRateSource source) =>
        source.IsWornSensor ? 100 : 50;

    public virtual int RankSpeed(ISpeedSource source) => source.Channel() switch
    {
        Channels.RunningSpeedCadence => 95,
        Channels.FtmsIndoorBike or Channels.AntFitnessEquipment => 90,
        Channels.FtmsTreadmill or Channels.FtmsRower or Channels.FtmsCrossTrainer => 85,
        Channels.SpeedCadence or Channels.AntSpeedCadence => 70,
        Channels.CyclingPower or Channels.AntBicyclePower => 60,
        Channels.Simulated => 10,
        _ => 40,
    };

    public virtual int RankDistance(IDistanceSource source) => source.Channel() switch
    {
        Channels.FtmsIndoorBike or Channels.AntFitnessEquipment => 90,
        Channels.FtmsTreadmill or Channels.FtmsRower or Channels.FtmsCrossTrainer => 85,
        Channels.RunningSpeedCadence => 80,
        Channels.SpeedCadence or Channels.AntSpeedCadence => 70,
        Channels.Simulated => 10,
        _ => 40,
    };
}

internal static class CapabilityChannelExtensions
{
    /// <summary>Shorthand for the channel name a capability speaks on.</summary>
    public static string Channel(this ICapability capability) => capability.Source.Channel;
}
