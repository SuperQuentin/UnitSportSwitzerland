using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// <c>tools/radiocheck.sh</c>: a radio thrown, played and danced to over a real connection. Two
/// clients of one dedicated server on loopback; the server burns a fixture CD at boot
/// (<c>--cdfixture</c>).
///
/// <para>
/// <c>--radiocheck thrower</c> (headless) throws a radio once the watcher is there, starts the
/// first CD on it and dances. It passes when the radio plays on the shared clock for ten
/// seconds with a valid beat. A headless client has no speaker, so it cannot judge the sound.
/// </para>
///
/// <para>
/// <c>--radiocheck watch</c> (windowed) looks at the other player's radio and figure. It passes
/// once the speaker has followed the clock within 0.1 s for ten seconds and the other player
/// has been seen dancing; non-zero exit after two minutes otherwise.
/// </para>
/// </summary>
public partial class RadioSyncCheck : Node
{
    private const double SyncTolerance = 0.1, MustHold = 10;

    private readonly bool _thrower;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private double _t, _since = -1, _sinceLog, _okSince = -1;
    private int _step;
    private bool _sawDance;
    private int _seenDance = -1;

    private RadioSyncCheck(bool thrower, Func<FootPlayer?> local, Func<Node?> players)
    {
        Name = "RadioSyncCheck";
        _thrower = thrower;
        _local = local;
        _players = players;
    }

    public static RadioSyncCheck? Create(Func<FootPlayer?> local, Func<Node?> players)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--radiocheck");
        if (i < 0) return null;
        bool thrower = i + 1 < args.Length && args[i + 1] == "thrower";
        GD.Print($"[radiocheck] role {(thrower ? "thrower" : "watch")}");
        return new RadioSyncCheck(thrower, local, players);
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    private static RadioBody? AnyRadio(Vector3 near) => RadioManager.Instance?.Nearest(near, 80f);

    public override void _Process(double delta)
    {
        // nothing to measure before the link is up (and LocalPlayer would log an error a frame)
        if (!Multiplayer.HasMultiplayerPeer()
            || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;
        _t += delta;
        _sinceLog += delta;
        if (_t > 120) Finish(false, "timed out");
        else if (_thrower) Throw();
        else Watch();
    }

    private void Throw()
    {
        if (_local() is not { } me || Other() == null || !me.IsOnFloor()) return;
        if (_since < 0) { _since = _t; GD.Print("[radiocheck] thrower: the watcher is here"); }
        double t = _t - _since;

        switch (_step)
        {
            case 0 when t > 3:
                var forward = -me.GlobalTransform.Basis.Z with { Y = 0 };
                forward = forward.LengthSquared() > 1e-6f ? forward.Normalized() : Vector3.Forward;
                RadioManager.Instance?.Throw(new RadioState("", 0,
                    me.GlobalPosition + Vector3.Up * 1.5f + forward * 0.6f, me.Rotation.Y, forward * 4f + Vector3.Up * 2f));
                GD.Print("[radiocheck] thrower: thrown");
                _step++;
                break;
            case 1 when t > 6:
                if (AnyRadio(me.GlobalPosition) is not { } radio) { if (t > 20) Finish(false, "the thrown radio never appeared"); return; }
                if (CdLibrary.Instance?.All.Keys.OrderBy(k => k).FirstOrDefault() is not (> 0 and var cdId))
                {
                    if (t > 40) Finish(false, "no CD in the library (was the fixture burnt on the server?)");
                    return;
                }
                RadioManager.Instance!.Play(radio, cdId);
                GD.Print($"[radiocheck] thrower: play CD {cdId} on {radio.Name} (clock synced {ClockSync.Synced}, rtt {ClockSync.Rtt * 1000:F0} ms)");
                _step++;
                break;
            case 2 when t > 8:
                me.DanceId = 1;
                GD.Print("[radiocheck] thrower: dancing");
                _step++;
                break;
            case 3:
                if (_sinceLog < 2) return;
                _sinceLog = 0;
                if (AnyRadio(me.GlobalPosition) is not { } r) { Finish(false, "the radio vanished"); return; }
                bool beat = r.BeatAt(ClockSync.ServerNow, out float phase, out int index, out int bar, out var style);
                GD.Print($"[radiocheck] thrower: playing {r.Playing} want={r.WantedPosition:F2} beat={beat} phase={phase:F2} index={index} bar={bar} style={style} dance={me.DanceId}");
                bool ok = r.Playing && beat && r.WantedPosition > 0;
                _okSince = ok ? (_okSince < 0 ? _t : _okSince) : -1;
                if (ok && _t - _okSince >= MustHold) Finish(true, "the radio ran on the shared clock with a valid beat for ten seconds");
                break;
        }
    }

    private void Watch()
    {
        if (Other() is { } other && other.DanceId != _seenDance)
        {
            _seenDance = other.DanceId;
            GD.Print($"[radiocheck] watch: the other player's DanceId is now {other.DanceId}");
            _sawDance |= other.DanceId == 1;
        }
        if (_sinceLog < 2 || _local() is not { } me) return;
        _sinceLog = 0;
        if (AnyRadio(me.GlobalPosition) is not { } r) return;
        double heard = r.HeardPosition, want = r.WantedPosition;
        bool beat = r.BeatAt(ClockSync.ServerNow, out float phase, out _, out int bar, out var style);
        GD.Print($"[radiocheck] watch: {r.Name} at {r.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m, playing {r.Playing} heard={heard:F2} want={want:F2} beat={beat} phase={phase:F2} bar={bar} style={style} settled={r.Settled} dance={_sawDance}");
        bool ok = r.Playing && !double.IsNaN(heard) && Math.Abs(heard - want) < SyncTolerance;
        _okSince = ok ? (_okSince < 0 ? _t : _okSince) : -1;
        if (ok && _sawDance && _t - _okSince >= MustHold)
            Finish(true, "the speaker followed the clock within 0.1 s for ten seconds and the thrower danced");
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[radiocheck] RESULT: {(ok ? "ok" : "FAILED")} ({(_thrower ? "thrower" : "watch")}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
