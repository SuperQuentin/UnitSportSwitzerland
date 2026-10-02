using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Audio.Live;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Items;

/// <summary>
/// <c>tools/carcdcheck.sh</c>: a CD in a car stereo over a real connection (#211). Two clients of
/// one dedicated server on loopback; the server burns two fixture CDs at boot (<c>--cdfixture</c>
/// twice: <c>carcdA</c>, short, then <c>carcdB</c>).
///
/// <para>
/// <c>--carcdcheck driver</c> (headless) logs in as admin, takes a car, opens the radio panel and
/// works it like a player: tunes a live station row, sets the mode to "Play the list" and plays
/// CD A from its row. When A ends its own CD changer puts on B; then it gets out (the parked car
/// keeps playing B).
/// </para>
///
/// <para>
/// <c>--carcdcheck watch</c> (windowed, so it has a real speaker) passes once it has heard, each
/// for five seconds in sync with the clock (0.1 s) and from the right file: A from the driver's
/// car, B from the driver's car (the driver's changer, replicated), B from the parked car.
/// </para>
///
/// <para>
/// <c>--carcdcheck shots</c> (offline, windowed) opens the panel on a car stereo, in the hand and
/// on a radio on the ground, and writes a screenshot of each to <c>test_output/</c>.
/// </para>
/// </summary>
public partial class CarCdCheck : Node
{
    private const double Hold = 5, Timeout = 300, Tolerance = 0.1;
    private const string TitleA = "carcdA", TitleB = "carcdB";

    private readonly string _role;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private readonly Inventory _inventory;
    private double _t, _since = -1, _sinceLog, _stepAt;
    private int _step;
    private bool _steppedDown;
    private readonly Dictionary<string, double> _okSince = new();
    private readonly HashSet<string> _passed = new();

    private CarCdCheck(string role, Func<FootPlayer?> local, Func<Node?> players, Inventory inventory)
    {
        Name = "CarCdCheck";
        _role = role;
        _local = local;
        _players = players;
        _inventory = inventory;
    }

    /// <summary>The check for this run, if asked for: <paramref name="networked"/> for driver/watch, not for shots.</summary>
    public static CarCdCheck? Create(Func<FootPlayer?> local, Func<Node?> players, Inventory inventory, bool networked)
    {
        if (!CmdArgs.Has("--carcdcheck")) return null;
        string role = CmdArgs.Value("--carcdcheck") ?? "watch";
        if (networked == (role == "shots")) return null;
        GD.Print($"[carcdcheck] role {role}");
        return new CarCdCheck(role, local, players, inventory);
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    /// <summary>The newest CD of that title: earlier runs left theirs in the library.</summary>
    private static CdInfo? Fixture(string title) =>
        CdLibrary.Instance?.All.Values.Where(c => c.Title == title).OrderByDescending(c => c.Id).FirstOrDefault();

    public override void _Process(double delta)
    {
        bool offline = _role == "shots";
        if (!offline && (!Multiplayer.HasMultiplayerPeer()
            || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)) return;
        _t += delta;
        _sinceLog += delta;
        if (_t > Timeout) Finish(false, $"timed out at step {_step}; passed: {string.Join(", ", _passed)}");
        else if (_role == "driver") Drive();
        else if (offline) Shots();
        else Watch(delta);
    }

    // ---- the panel, worked like a player would -------------------------------------------------

    private static Button? PanelButton(string text) => RadioUi.Instance?.FindChildren("*", nameof(Button), true, false)
        .OfType<Button>().FirstOrDefault(b => b.IsVisibleInTree() && b.Text == text);

    private static bool Press(string text)
    {
        if (PanelButton(text) is not { } b) { GD.Print($"[carcdcheck] no button \"{text}\" on the panel"); return false; }
        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    // ---- driver -----------------------------------------------------------------------------

    private void Drive()
    {
        if (_local() is not { } me)
        {
            if (_sinceLog > 5) { _sinceLog = 0; GD.Print("[carcdcheck] driver: no player yet"); }
            return;
        }
        if (_since < 0)
        {
            if (Other() == null || !me.IsOnFloor())
            {
                if (_sinceLog > 5) { _sinceLog = 0; GD.Print($"[carcdcheck] driver: waiting (watcher {Other()?.Name}, on floor {me.IsOnFloor()})"); }
                return;
            }
            _since = _t;
            GD.Print("[carcdcheck] driver: the watcher is here");
        }
        double t = _t - _since;
        switch (_step)
        {
            case 0 when t > 1:
                if (CmdArgs.Value("--carcdpw") is { } pw && GetTree().Root.FindChild(ChatManager.NodeName, true, false) is ChatManager chat)
                    chat.Send($"/login {pw}");
                _step++;
                break;
            case 1 when t > 3:
                var car = CarCatalog.All[0].Kind;
                GD.Print($"[carcdcheck] driver: into {car}: {me.SetRide(car)}");
                _step++;
                break;
            case 2 when t > 6:
                if (Fixture(TitleA) is not { } a || Fixture(TitleB) is not { } b)
                {
                    if (t > 60) Finish(false, "the fixture CDs are not in the library (burnt on the server?)");
                    return;
                }
                // the real key: R at the wheel opens the radio, and the travel picker (also R) stays shut
                foreach (bool down in new[] { true, false })
                    GetViewport().PushInput(new InputEventKey { Keycode = Key.R, PhysicalKeycode = Key.R, Pressed = down });
                if (RadioUi.Instance is not { OnCar: true }) { Finish(false, $"R did not open the car radio panel (stereo {me.StereoOwner?.Name})"); return; }
                if (GetTree().Root.FindChild("RideUi", true, false) is Player.RideUi { IsOpen: true }) { Finish(false, "R opened the travel picker too"); return; }
                GD.Print("[carcdcheck] driver: R opened the radio, not the travel picker");
                // a station first: picking a CD after it must turn the station off
                bool ok = Press(Stations.Name(8));
                GD.Print($"[carcdcheck] driver: station row {ok}, radio {me.PlayingCarRadio}");
                ok &= Press(RadioQueue.Label(RadioMode.Once)) && Press(RadioQueue.Label(RadioMode.Repeat));
                ok &= Press(a.Title);
                var play = me.PlayingCarCd;
                GD.Print($"[carcdcheck] driver: CD A ({a.Id}, {a.Duration:F1} s) then B ({b.Id}): playing {play}, station {me.PlayingCarRadio}");
                if (!ok || play is not { } p || p.CdId != a.Id || p.Mode != RadioMode.All || me.PlayingCarRadio != 0)
                {
                    Finish(false, "the panel did not put CD A on in 'play the list' mode with the station off");
                    return;
                }
                RadioUi.Instance!.Close();
                _stepAt = t;
                _step++;
                break;
            case 3:
                // the changer (RadioUi, open or not) must have moved on to B by itself
                if (me.PlayingCarCd is { } now && Fixture(TitleB) is { } cdB && now.CdId == cdB.Id && t - _stepAt > 30)
                {
                    GD.Print($"[carcdcheck] driver: on B by itself, started {now.StartedAt:F2}");
                    me.ExitVehicle();
                    var parked = ParkedCd();
                    GD.Print($"[carcdcheck] driver: got out, parked car plays {parked?.Cd}");
                    _stepAt = t;
                    _step++;
                }
                else if (t - _stepAt > 90) Finish(false, $"the stereo did not go on to B: {me.CarCd}");
                break;
            case 4 when t - _stepAt > 25:
                Finish(ParkedCd() != null, "played A, went on to B by itself, parked with B");
                break;
        }
        if (_sinceLog > 5)
        {
            _sinceLog = 0;
            GD.Print($"[carcdcheck] driver: step {_step}, car cd {me.CarCd}, station {me.CarRadio}");
        }
    }

    private static VehicleBody? ParkedCd() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => RadioPlay.Decode(v.Cd) != null);

    // ---- watcher ------------------------------------------------------------------------------

    private void Watch(double delta)
    {
        var them = Other();
        var a = Fixture(TitleA);
        var b = Fixture(TitleB);
        var driving = them?.GetNodeOrNull<RadioSpeaker>(WebRadio.CdSpeakerName);
        var parkedCar = ParkedCd();
        var parked = parkedCar?.GetNodeOrNull<RadioSpeaker>(WebRadio.CdSpeakerName);
        Check("carA", a != null && them?.PlayingCarCd is { Mode: RadioMode.All } pa && pa.CdId == a.Id && Synced(driving, a), delta);
        Check("carB", b != null && them?.PlayingCarCd is { } pb && pb.CdId == b.Id && Synced(driving, b), delta);
        Check("parkedB", b != null && RadioPlay.Decode(parkedCar?.Cd) is { } pp && pp.CdId == b.Id && Synced(parked, b), delta);
        if (_sinceLog > 4)
        {
            _sinceLog = 0;
            var s = parked ?? driving;
            GD.Print($"[carcdcheck] watch: driver cd {them?.CarCd}, parked {parkedCar?.Cd}, speaker {s?.GetParent()?.Name} "
                + $"cd {s?.LoadedCd} ({s?.LoadedLength:F1} s), heard {s?.HeardPosition:F2}, wanted {s?.WantedPosition:F2}");
        }
        if (_passed.Count == 3) Finish(true, "heard A and then B from the driver's car, and B from the parked car, in sync");
    }

    private static bool Synced(RadioSpeaker? s, CdInfo cd) =>
        s != null && s.On && s.LoadedCd == cd.Id && Math.Abs(s.LoadedLength - cd.Duration) < 0.6
        && !double.IsNaN(s.HeardPosition) && Math.Abs(s.HeardPosition - s.WantedPosition) < Tolerance;

    private void Check(string key, bool ok, double delta)
    {
        if (_passed.Contains(key)) return;
        if (!ok) { _okSince.Remove(key); return; }
        _okSince[key] = _okSince.GetValueOrDefault(key) + delta;
        if (_okSince[key] < Hold) return;
        _passed.Add(key);
        GD.Print($"[carcdcheck] watch: passed {key}");
    }

    // ---- screenshots, offline -------------------------------------------------------------------

    private void Shots()
    {
        if (_local() is not { } me)
        {
            // offline the game starts in the fly camera: step down onto foot, once the world is in
            if (_t > 10 && !_steppedDown && GetParent() is ClientWorld world) { world.ToggleMode(); _steppedDown = true; }
            return;
        }
        if (_since < 0)
        {
            if (!me.IsOnFloor() || _t < 8) return;
            _since = _t;
        }
        double t = _t - _since;
        var library = CdLibrary.Instance;
        int first = RadioQueue.Order(library, withPersonal: true).FirstOrDefault();
        switch (_step)
        {
            case 0:
                GD.Print($"[carcdcheck] shots: into a car: {me.SetRide(CarCatalog.All[0].Kind)}");
                _step++;
                break;
            case 1 when t > 3:
                RadioUi.Instance?.OpenCar();
                if (first != 0 && library?.Find(first) is { } cd) Press(cd.Title);
                _step++;
                break;
            case 2 when t > 7:
                Shoot("radio_car.png");
                RadioUi.Instance?.Close();
                me.ExitVehicle();
                _step++;
                break;
            case 3 when t > 9:
                _inventory.Add(new ItemStack(ItemId.Radio, 1));
                for (int i = 0; i < 6; i++)
                    if (_inventory[i].Id == ItemId.Radio) { _inventory.Select(i); break; }
                _step++;
                break;
            case 4 when t > 10:
                RadioUi.Instance?.OpenHeld(_inventory.Selected);
                Press(RadioQueue.Label(RadioMode.Once));
                if (RadioQueue.Order(library, true).Skip(1).FirstOrDefault() is var second and not 0 && library?.Find(second) is { } cd2) Press(cd2.Title);
                _step++;
                break;
            case 5 when t > 13:
                Shoot("radio_held.png");
                RadioUi.Instance?.Close();
                var forward = -me.GlobalTransform.Basis.Z with { Y = 0 };
                if (RadioManager.Instance is { } radios)
                    radios.Throw(new RadioState("", 0, radios.Origin.ToGlobal(me.GlobalPosition + Vector3.Up * 1.2f + forward.Normalized() * 1.2f),
                        me.Rotation.Y, Vector3.Zero));
                _step++;
                break;
            case 6 when t > 17:
                if (RadioManager.Instance?.Nearest(me.GlobalPosition, RadioManager.Reach + 2) is { } radio)
                {
                    RadioUi.Instance?.Open(radio);
                    var search = RadioUi.Instance?.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().FirstOrDefault();
                    if (search != null) { search.Text = "a"; search.EmitSignal(LineEdit.SignalName.TextChanged, "a"); }
                }
                _step++;
                break;
            case 7 when t > 20:
                Shoot("radio_world.png");
                Finish(true, "three screenshots");
                break;
        }
    }

    private void Shoot(string file)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, file);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[carcdcheck] shots: wrote {path}: {err}");
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[carcdcheck] RESULT: {(ok ? "ok" : "FAILED")} ({_role}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
