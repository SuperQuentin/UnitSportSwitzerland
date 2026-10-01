using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// <c>tools/radiocheck.sh</c>: a radio thrown, played, re-tuned, picked up and played in the hand,
/// over a real connection. Two clients of one dedicated server on loopback; the server burns two
/// fixture CDs at boot (<c>--cdfixture</c> twice: <c>radiofixture</c> and <c>radiofixture2</c>, of
/// different lengths, so the watcher can tell which file its speaker loaded).
///
/// <para>
/// <c>--radiocheck thrower</c> (headless) throws a radio, plays CD A, dances, changes to CD B,
/// picks the radio up (it keeps playing B in the hand), plays A in the hand, then burns a personal
/// CD from <c>--radiopersonal &lt;wav&gt;</c> and plays that. A headless client has no speaker, so
/// it only checks its own steps.
/// </para>
///
/// <para>
/// <c>--radiocheck watch</c> (windowed) listens to the other player's radio. It passes once it has
/// heard, each in sync with the clock (0.1 s) and from the right file: A on the world radio for ten
/// seconds (and seen the thrower dance), B on the world radio, B in the thrower's hand, A in the
/// thrower's hand (five seconds each), and silence for the thrower's personal CD.
/// </para>
/// </summary>
public partial class RadioSyncCheck : Node
{
    private const double SyncTolerance = 0.1, Timeout = 170;

    private readonly bool _thrower;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private readonly Inventory? _inventory;
    private double _t, _since = -1, _sinceLog;
    private int _step;
    private bool _sawDance;
    private int _seenDance = -1;
    private readonly HashSet<int> _personalBefore = new();
    private readonly Dictionary<string, double> _okSince = new();
    private readonly HashSet<string> _passed = new();

    private static readonly (string Key, double Hold)[] Conditions =
        { ("worldA", 10), ("worldB", 5), ("heldB", 5), ("heldA", 5), ("personalSilent", 3) };

    private RadioSyncCheck(bool thrower, Func<FootPlayer?> local, Func<Node?> players, Inventory? inventory)
    {
        Name = "RadioSyncCheck";
        _thrower = thrower;
        _local = local;
        _players = players;
        _inventory = inventory;
    }

    public static RadioSyncCheck? Create(Func<FootPlayer?> local, Func<Node?> players, Inventory? inventory)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--radiocheck");
        if (i < 0) return null;
        bool thrower = i + 1 < args.Length && args[i + 1] == "thrower";
        GD.Print($"[radiocheck] role {(thrower ? "thrower" : "watch")}");
        return new RadioSyncCheck(thrower, local, players, inventory);
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    private static RadioBody? AnyRadio(Vector3 near) => RadioManager.Instance?.Nearest(near, 80f);

    private static CdInfo? ByTitle(string title) => CdLibrary.Instance?.All.Values.FirstOrDefault(c => c.Title == title);

    public override void _Process(double delta)
    {
        // nothing to measure before the link is up (and LocalPlayer would log an error a frame)
        if (!Multiplayer.HasMultiplayerPeer()
            || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;
        _t += delta;
        _sinceLog += delta;
        if (_t > Timeout) Finish(false, _thrower ? $"timed out at step {_step}" : $"timed out; passed: {string.Join(", ", _passed)}");
        else if (_thrower) Throw();
        else Watch();
    }

    // ---- thrower -------------------------------------------------------------------------------

    private void Throw()
    {
        if (_local() is not { } me) return;
        if (_since < 0)
        {
            if (Other() == null || !me.IsOnFloor()) return;
            _since = _t;
            GD.Print("[radiocheck] thrower: the watcher is here");
        }
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
                if (ByTitle("radiofixture") is not { } a || ByTitle("radiofixture2") == null)
                {
                    if (t > 40) Finish(false, "the two fixture CDs are not in the library (were they burnt on the server?)");
                    return;
                }
                RadioManager.Instance!.Play(radio, a.Id, a.Duration);
                GD.Print($"[radiocheck] thrower: play CD {a.Id} on {radio.Name} (clock synced {ClockSync.Synced}, rtt {ClockSync.Rtt * 1000:F0} ms)");
                _step++;
                break;
            case 2 when t > 8:
                me.DanceId = 1;
                GD.Print("[radiocheck] thrower: dancing");
                _step++;
                break;
            case 3 when t > 30:
            {
                if (AnyRadio(me.GlobalPosition) is not { } r) { Finish(false, "the radio vanished"); return; }
                bool beat = r.BeatAt(ClockSync.ServerNow, out _, out _, out _, out _);
                if (!r.Playing || !beat) { Finish(false, $"CD A was not playing with a beat (playing {r.Playing}, beat {beat})"); return; }
                var b = ByTitle("radiofixture2")!;
                RadioManager.Instance!.Play(r, b.Id, b.Duration);
                GD.Print($"[radiocheck] thrower: change to CD {b.Id}");
                _step++;
                break;
            }
            case 4 when t > 50:
            {
                if (AnyRadio(me.GlobalPosition) is not { } r) { Finish(false, "the radio vanished"); return; }
                if (_inventory == null) { Finish(false, "no inventory"); return; }
                if (r.CdId != ByTitle("radiofixture2")!.Id) { Finish(false, $"the server did not change the CD (still {r.CdId})"); return; }
                string? playing = r.NowPlaying?.Encode();
                RadioManager.Instance!.PickUp(r, () =>
                {
                    _inventory.Add(new ItemStack(ItemId.Radio, 1, playing));
                    // this one into the hand: a radio saved by an earlier run may be selected, and a
                    // full hotbar sends the new one to the pack
                    for (int i = 0; i < Inventory.Size; i++)
                        if (_inventory[i].Id == ItemId.Radio && _inventory[i].Data == playing)
                        {
                            if (i >= Inventory.HotbarSize) _inventory.Move(i, _inventory.Selected);
                            else _inventory.Select(i);
                            break;
                        }
                    GD.Print($"[radiocheck] thrower: picked up, holding {_inventory.HeldId} data {_inventory.Held.Data}");
                });
                _step++;
                break;
            }
            case 5 when t > 70:
            {
                if (_inventory?.HeldId != ItemId.Radio) { Finish(false, "the radio is not in the hand"); return; }
                var cdA = ByTitle("radiofixture")!;
                _inventory.SetData(_inventory.Selected, new RadioPlay(cdA.Id, ClockSync.ServerNow, cdA.Duration).Encode());
                GD.Print($"[radiocheck] thrower: CD {cdA.Id} in the hand");
                _step++;
                break;
            }
            case 6 when t > 88:
            {
                string file = Arg("--radiopersonal") ?? "";
                if (CdLibrary.Instance is not { } library || !File.Exists(file)) { Finish(false, $"no personal fixture ({file})"); return; }
                foreach (int id in library.Personal.Keys) _personalBefore.Add(id);
                library.RequestBurn(file, personal: true);
                GD.Print("[radiocheck] thrower: burning a personal CD");
                _step++;
                break;
            }
            case 7:
            {
                if (CdLibrary.Instance?.Personal.Values.FirstOrDefault(c => !_personalBefore.Contains(c.Id)) is not { } mine)
                {
                    if (t > 120) Finish(false, "the personal CD was never burnt");
                    return;
                }
                _inventory!.SetData(_inventory.Selected, new RadioPlay(mine.Id, ClockSync.ServerNow, mine.Duration).Encode());
                GD.Print($"[radiocheck] thrower: personal CD {mine.Id} in the hand ({CdCache.LocalPath(mine.Id)})");
                _step++;
                break;
            }
            case 8 when t > 112:
                GD.Print($"[radiocheck] thrower: HeldRadio '{me.HeldRadio}'");
                // tidy: the fixture is not a CD anyone wants to keep
                if (RadioPlay.Decode(me.HeldRadio) is { CdId: < 0 } p) CdLibrary.Instance?.RemovePersonal(p.CdId);
                Finish(true, "threw, played, changed CD, picked up, played in the hand, played a personal CD");
                break;
        }
    }

    private static string? Arg(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---- watcher -------------------------------------------------------------------------------

    private void Watch()
    {
        if (Other() is { } other && other.DanceId != _seenDance)
        {
            _seenDance = other.DanceId;
            GD.Print($"[radiocheck] watch: the other player's DanceId is now {other.DanceId}");
            _sawDance |= other.DanceId == 1;
        }
        if (_sinceLog < 1 || _local() is not { } me) return;
        _sinceLog = 0;
        var them = Other();
        int a = ByTitle("radiofixture")?.Id ?? 0, b = ByTitle("radiofixture2")?.Id ?? 0;

        var r = AnyRadio(me.GlobalPosition);
        var held = them?.GetNodeOrNull<RadioSpeaker>(RadioManager.HeldSpeakerName);
        bool worldSync = r?.Speaker is { } ws && Synced(ws);
        bool heldSync = held != null && held.On && Synced(held);
        var heldPlay = them == null ? null : RadioPlay.Decode(them.HeldRadio);

        Check("worldA", r != null && r.CdId == a && worldSync && _sawDance);
        Check("worldB", r != null && r.CdId == b && worldSync);
        Check("heldB", r == null && heldPlay?.CdId == b && heldSync);
        Check("heldA", r == null && heldPlay?.CdId == a && heldSync);
        Check("personalSilent", _passed.Contains("heldA") && heldPlay is { CdId: < 0 } && held != null && !held.Playing && double.IsNaN(held.HeardPosition));

        var spk = r?.Speaker ?? held;
        GD.Print($"[radiocheck] watch: {(r != null ? $"world {r.Name} cd {r.CdId}" : held != null ? $"held cd {held.CdId} ({them!.HeldRadio})" : "no radio")}"
                 + $" heard={spk?.HeardPosition ?? double.NaN:F2} want={spk?.WantedPosition ?? double.NaN:F2} loaded={spk?.LoadedCd ?? 0} ({spk?.LoadedLength ?? 0:F1} s)"
                 + $" dance={_sawDance} passed=[{string.Join(",", _passed)}]");
        if (_passed.Count == Conditions.Length) Finish(true, "heard A, then B on the world radio, B then A in the hand, all in sync, and silence for a personal CD");
    }

    /// <summary>On the clock, and playing the file of the CD it is told to (not the previous one).</summary>
    private static bool Synced(RadioSpeaker s)
    {
        if (double.IsNaN(s.HeardPosition) || Math.Abs(s.HeardPosition - s.WantedPosition) >= SyncTolerance) return false;
        return s.LoadedCd == s.CdId && CdLibrary.Instance?.Find(s.CdId) is { } cd && Math.Abs(s.LoadedLength - cd.Duration) < 0.5;
    }

    private void Check(string key, bool ok)
    {
        if (_passed.Contains(key)) return;
        if (!ok) { _okSince.Remove(key); return; }
        if (!_okSince.TryGetValue(key, out double since)) _okSince[key] = since = _t;
        double hold = Conditions.First(c => c.Key == key).Hold;
        if (_t - since >= hold)
        {
            _passed.Add(key);
            GD.Print($"[radiocheck] watch: {key} ok");
        }
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[radiocheck] RESULT: {(ok ? "ok" : "FAILED")} ({(_thrower ? "thrower" : "watch")}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
