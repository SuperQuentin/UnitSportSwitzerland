using Godot;

namespace UnitSport.Core;

/// <summary>
/// The network the phone is on (#63), re-read every few seconds so a switch to cellular or the
/// data saver turning on is caught mid-session: <see cref="Metered"/> for the warning before
/// joining, and <see cref="GameSettings.DataSaverOn"/> for automatic Low data. Godot has no API for
/// either; <see cref="AndroidBridge.NetworkState"/> asks Android. Off Android it reads nothing and
/// both stay false. Lives under the <see cref="GameShell"/>.
/// </summary>
public partial class DataWatch : Node
{
    private const double Interval = 5;
    private double _timer;

    /// <summary>On a metered connection (cellular, a phone hotspot) as last read.</summary>
    public static bool Metered { get; private set; }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(AndroidBridge.Available);
        Read();
    }

    public override void _Process(double delta)
    {
        _timer -= delta;
        if (_timer > 0) return;
        _timer = Interval;
        Read();
    }

    private static void Read()
    {
        if (AndroidBridge.NetworkState() is not { } state) return;
        var (metered, saver) = state;
        if (metered != Metered) GD.Print($"[data] metered connection: {metered}");
        Metered = metered;
        if (saver == GameSettings.DataSaverOn) return;
        GD.Print($"[data] data saver {(saver ? "on" : "off")}");
        GameSettings.DataSaverOn = saver;
        // Low data follows it when auto is on: the terrain re-reads its ring table
        if (GameSettings.Current.AutoLowData) GameSettings.NotifyChanged();
    }
}
