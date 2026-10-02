using Godot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Vehicles;
using UnitSport.Core;

namespace UnitSport.Audio.Live;

/// <summary>
/// <c>tools/webradiocheck.sh</c>: a car radio over a real connection (#179). Two clients of one
/// dedicated server on loopback (the server needs ffmpeg and the internet).
///
/// <para>
/// <c>--webradiocheck driver</c> (headless) logs in as admin, takes a car, tunes SRF 3, retunes
/// to Radio Swiss Pop, gets out (the parked car keeps playing) and passes once it has received
/// real audio of both stations.
/// </para>
///
/// <para>
/// <c>--webradiocheck watch</c> (windowed, so it has a real speaker) passes once it has heard each
/// of SRF 3 in the driver's car, Swiss Pop in the driver's car and Swiss Pop in the parked car,
/// for eight seconds, with audio in the buffer and the speaker within 50 ms of the clock.
/// </para>
///
/// <para>
/// Both print a fingerprint of every station they hold at each 5 s mark of the server clock (the
/// sum of the samples due then): the script checks the two clients agree on every common mark,
/// which is "the same audio at the same moment" on both machines.
/// </para>
/// </summary>
public partial class WebRadioCheck : Node
{
    private const double Hold = 8, Timeout = 170, Tolerance = 0.05;
    private const int SrfThree = 2, SwissPop = 8;

    private readonly bool _driver, _offline;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private double _t, _since = -1, _sinceLog;
    private long _mark = -1;
    private int _step;
    private FootPlayer? _localOverride;
    private readonly Dictionary<string, double> _okSince = new();
    private readonly HashSet<string> _passed = new();
    private readonly HashSet<int> _gotAudio = new();

    private WebRadioCheck(string role, Func<FootPlayer?> local, Func<Node?> players)
    {
        Name = "WebRadioCheck";
        _offline = role == "offline";
        _driver = role == "driver" || _offline;
        _local = local;
        _players = players;
    }

    /// <summary>The check for this run, if asked for: <paramref name="networked"/> for driver/watch, not for offline.</summary>
    public static WebRadioCheck? Create(Func<FootPlayer?> local, Func<Node?> players, bool networked)
    {
        if (!CmdArgs.Has("--webradiocheck")) return null;
        string role = CmdArgs.Value("--webradiocheck") ?? "watch";
        if (networked == (role == "offline")) return null;
        GD.Print($"[webradiocheck] role {role}");
        return new WebRadioCheck(role, local, players);
    }

    private static string? Password() => CmdArgs.Value("--webradiopw");

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    public override void _Process(double delta)
    {
        if (!_offline && (!Multiplayer.HasMultiplayerPeer()
            || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)) return;
        _t += delta;
        _sinceLog += delta;
        Fingerprints();
        if (_t > Timeout) Finish(false, $"timed out at step {_step}; passed: {string.Join(", ", _passed)}");
        else if (_driver) Drive();
        else Watch(delta);
    }

    private void Fingerprints()
    {
        long mark = (long)Math.Floor(ClockSync.ServerNow / 5);
        if (mark == _mark || WebRadio.Instance is not { } radio) return;
        _mark = mark;
        foreach (int id in Stations.All.Select(s => s.Id))
        {
            if (radio.Buffer(id) is not { } b) continue;
            long at = (long)((mark * 5.0 - b.T0 - WebRadio.Delay) * WebRadio.Rate);
            if (!b.Has(at) || !b.Has(at + WebRadio.ChunkSamples - 1)) continue;
            double sum = 0, energy = 0;
            for (long i = at; i < at + WebRadio.ChunkSamples; i++)
            {
                float v = b.Sample(i);
                sum += Math.Abs(v);
                energy += v * v;
            }
            if (Math.Sqrt(energy / WebRadio.ChunkSamples) > 0.003) _gotAudio.Add(id);
            GD.Print($"[webradiocheck] fp mark {mark} station {id} sum {sum:F4}");
        }
    }

    // ---- driver ---------------------------------------------------------------------------

    private void Drive()
    {
        if (_offline && GetTree().Root.FindChild("Probe", true, false) is FootPlayer probe && WebRadio.Instance is { } radio)
        {
            // offline the car is RideProbe's own body (--ride), which no player list holds
            radio.Players = () => new[] { probe };
            if (_local() == null) _localOverride = probe;
        }
        if ((_local() ?? _localOverride) is not { } me)
        {
            if (_sinceLog > 4) { _sinceLog = 0; GD.Print("[webradiocheck] driver: no player yet"); }
            return;
        }
        if (_since < 0 && _sinceLog > 4) { _sinceLog = 0; GD.Print($"[webradiocheck] driver: {me.Name} rides {me.Ride}, on floor {me.IsOnFloor()}"); }
        if (_since < 0)
        {
            // offline the car comes from --ride (RideProbe): the menu never puts anyone on foot
            if (_offline ? !FootPlayer.HasCarRadio(me.Ride) : Other() == null || !me.IsOnFloor()) return;
            _since = _t;
            GD.Print("[webradiocheck] driver: the watcher is here");
        }
        double t = _t - _since;
        switch (_step)
        {
            case 0 when _offline:
                _step = 2;
                break;
            case 0 when t > 1:
                if (Password() is { } pw && GetTree().Root.FindChild(ChatManager.NodeName, true, false) is ChatManager chat)
                    chat.Send($"/login {pw}");
                _step++;
                break;
            case 1 when t > 3:
                var car = CarCatalog.All[0].Kind;
                GD.Print($"[webradiocheck] driver: into {car}: {me.SetRide(car)}");
                _step++;
                break;
            case 2 when t > 5:
                me.CarRadio = SrfThree;
                GD.Print($"[webradiocheck] driver: tuned {Stations.Name(SrfThree)}, playing {me.PlayingCarRadio}");
                _step++;
                break;
            case 3 when _offline && t > 25:
                Finish(_gotAudio.Contains(SrfThree) && me.GetNodeOrNull<WebRadioSpeaker>(WebRadio.SpeakerName)?.Playing == true,
                    $"offline: audio from {string.Join(", ", _gotAudio.Select(Stations.Name))}");
                break;
            case 3 when t > 45:
                me.CarRadio = SwissPop;
                GD.Print($"[webradiocheck] driver: tuned {Stations.Name(SwissPop)}");
                _step++;
                break;
            case 4 when t > 80:
                me.ExitVehicle();
                GD.Print($"[webradiocheck] driver: got out, radio now {me.CarRadio}, parked radio {ParkedRadio()?.Radio}");
                _step++;
                break;
            case 5 when t > 115:
                bool ok = _gotAudio.Contains(SrfThree) && _gotAudio.Contains(SwissPop);
                Finish(ok, $"audio received from {string.Join(", ", _gotAudio.Select(Stations.Name))}");
                break;
        }
        if (_sinceLog > 4)
        {
            _sinceLog = 0;
            Log("driver", me.GetNodeOrNull<WebRadioSpeaker>(WebRadio.SpeakerName) ?? ParkedRadio()?.GetNodeOrNull<WebRadioSpeaker>(WebRadio.SpeakerName));
        }
    }

    private static VehicleBody? ParkedRadio() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Radio > 0);

    // ---- watcher ----------------------------------------------------------------------------

    private void Watch(double delta)
    {
        var them = Other();
        var driving = them?.GetNodeOrNull<WebRadioSpeaker>(WebRadio.SpeakerName);
        var parkedCar = ParkedRadio();
        var parked = parkedCar?.GetNodeOrNull<WebRadioSpeaker>(WebRadio.SpeakerName);
        Check("srf3", them?.PlayingCarRadio == SrfThree && Synced(driving, SrfThree), delta);
        Check("pop", them?.PlayingCarRadio == SwissPop && Synced(driving, SwissPop), delta);
        Check("parked", parkedCar?.Radio == SwissPop && Synced(parked, SwissPop), delta);
        if (_sinceLog > 4)
        {
            _sinceLog = 0;
            Log("watch", driving ?? parked);
        }
        if (_passed.Count == 3) Finish(true, "heard SRF 3, Swiss Pop and the parked car in sync");
    }

    private bool Synced(WebRadioSpeaker? s, int station)
    {
        if (s == null || s.Station != station || !s.Playing || s.HeardIndex < 0) return false;
        if (WebRadio.Instance?.Buffer(station) is not { } b) return false;
        double off = (s.HeardIndex - (ClockSync.ServerNow - b.T0 - WebRadio.Delay) * WebRadio.Rate) / WebRadio.Rate;
        return Math.Abs(off) < Tolerance && _gotAudio.Contains(station);
    }

    private void Check(string key, bool ok, double delta)
    {
        if (_passed.Contains(key)) return;
        if (!ok) { _okSince.Remove(key); return; }
        _okSince[key] = _okSince.GetValueOrDefault(key) + delta;
        if (_okSince[key] < Hold) return;
        _passed.Add(key);
        GD.Print($"[webradiocheck] watch: passed {key}");
    }

    private static void Log(string role, WebRadioSpeaker? s)
    {
        if (s == null) { GD.Print($"[webradiocheck] {role}: no speaker"); return; }
        var b = WebRadio.Instance?.Buffer(s.Station);
        string off = b == null || s.HeardIndex < 0 ? "-"
            : $"{(s.HeardIndex - (ClockSync.ServerNow - b.T0 - WebRadio.Delay) * WebRadio.Rate) / WebRadio.Rate * 1000:F0} ms";
        double ahead = b == null ? 0 : b.End / (double)WebRadio.Rate - (ClockSync.ServerNow - b.T0 - WebRadio.Delay);
        GD.Print($"[webradiocheck] {role}: speaker on {Stations.Name(s.Station)}, playing {s.Playing}, offset {off}, "
            + $"buffered {ahead:F2} s ahead, drift fixes {s.Resyncs}");
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[webradiocheck] RESULT: {(ok ? "ok" : "FAILED")} ({(_driver ? "driver" : "watch")}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
