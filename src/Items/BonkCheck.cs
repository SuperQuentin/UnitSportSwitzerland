using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// <c>tools/bonkcheck.sh</c> (#261): thrown things hitting a player, and a radio carried on the back,
/// over a real connection: a headless dedicated server on a generated world and two headless clients.
///
/// <para>
/// <c>--bonkcheck thrower</c> carries a playing radio in its pack (on its back), stands four metres
/// from the other player and throws at its chest: a stone, then (once the victim is down to a sliver
/// of health) a radio. It passes once it has sent two <see cref="ItemEventKind.Bonk"/>s.
/// </para>
///
/// <para>
/// <c>--bonkcheck victim</c> drops its own health to 9 after the first hit, so the second would kill
/// it if hits could: it must receive both bonks from the server, lose health on the first, never go
/// below <see cref="ThrowHits.Floor"/>, never be knocked out, and see the thrower's replicated
/// <c>BackItemId</c> (a radio) and <c>HeldRadio</c> (playing).
/// </para>
/// </summary>
public partial class BonkCheck : Node
{
    private const double Timeout = 150;
    private readonly bool _thrower;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private readonly Inventory _inventory;
    private double _t, _stepAt;
    private int _step, _bonks;
    private float _lowest = float.MaxValue;
    private bool _sawBack, _knockedOut, _lowered;

    private BonkCheck(bool thrower, Func<FootPlayer?> local, Func<Node?> players, Inventory inventory)
    {
        Name = "BonkCheck";
        _thrower = thrower;
        _local = local;
        _players = players;
        _inventory = inventory;
        ItemEvents.Received += OnEvent;
    }

    /// <summary>"--bonkcheck" on the command line: the client must use a scratch inventory.</summary>
    public static bool Requested => CmdArgs.Has("--bonkcheck");

    public static BonkCheck? Create(Func<FootPlayer?> local, Func<Node?> players, Inventory inventory)
    {
        if (!CmdArgs.Has("--bonkcheck")) return null;
        bool thrower = CmdArgs.Value("--bonkcheck") == "thrower";
        GD.Print($"[bonkcheck] role {(thrower ? "thrower" : "victim")}");
        return new BonkCheck(thrower, local, players, inventory);
    }

    public override void _ExitTree() => ItemEvents.Received -= OnEvent;

    private void OnEvent(ItemEvent e)
    {
        if (e.Kind != ItemEventKind.Bonk || ThrowHits.Bonk.Parse(e.Extra) is not { } b) return;
        if (_thrower && e.Local) { _bonks++; GD.Print($"[bonkcheck] thrower: bonk {_bonks} sent ({b.Item}, {b.Damage:F1})"); }
        if (!_thrower && !e.Local && _local() is { } me)
        {
            _bonks++;
            // the handler has run: health before it is last frame's (it regenerates, so not the first frame's)
            _lastDrop = _lastHealth - me.Health;
            GD.Print($"[bonkcheck] victim: bonk {_bonks} received ({b.Item}, {b.Damage:F1}): health {_lastHealth:F1} -> {me.Health:F1}");
        }
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer { Npc: false } p && p.Name != own) return p;
        return null;
    }

    public override void _Process(double delta)
    {
        if (!Multiplayer.HasMultiplayerPeer() || Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;
        _t += delta;
        if (_t > Timeout) { Finish(false, $"timed out at step {_step}, {_bonks} bonks"); return; }
        if (!_thrower && _doneAt >= 0 && _t - _doneAt > 1.5 && _local() is { } done) { Verdict(done); return; }
        if (_local() is not { } me || Other() is not { } other || !me.IsOnFloor()) return;
        if (_thrower) Throw(me, other);
        else Watch(me, other);
    }

    private void Next() { _step++; _stepAt = _t; }

    private void Throw(FootPlayer me, FootPlayer other)
    {
        double at = _t - _stepAt;
        switch (_step)
        {
            case 0:
                // a radio playing in the pack: on the back, heard by the other
                int cd = RadioQueue.Order(CdLibrary.Instance, withPersonal: false).FirstOrDefault();
                _inventory.Put(Inventory.HotbarSize, new ItemStack(ItemId.Radio, 1, new RadioPlay(cd != 0 ? cd : 1, ClockSync.ServerNow, 600).Encode()));
                _inventory.Select(Inventory.HotbarSize - 1);
                Next();
                break;
            case 1 when at > 6:
            case 3 when at > 6:
                // four metres off, square on, then a hard throw at the chest
                var away = (me.GlobalPosition - other.GlobalPosition) with { Y = 0 };
                away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
                me.GlobalPosition = other.GlobalPosition + away * 4f + Vector3.Up * 0.2f;
                me.Velocity = Vector3.Zero;
                Next();
                break;
            case 2 when at > 1.5:
            case 4 when at > 1.5:
                var chest = other.GlobalPosition + Vector3.Up * 1.2f;
                var from = me.GlobalPosition + Vector3.Up * 1.4f + (chest - me.GlobalPosition with { Y = chest.Y }).Normalized() * 0.5f;
                var velocity = (chest - from).Normalized() * 14f + Vector3.Up * 1.2f;
                if (_step == 2) DroppedItems.Instance?.Drop(new ItemStack(ItemId.Stone, 1), from, velocity, Vector3.Zero, Vector3.Right * 8f);
                else if (RadioManager.Instance is { } radios) radios.Throw(new RadioState("", 0, radios.Origin.ToGlobal(from), 0, velocity));
                GD.Print($"[bonkcheck] thrower: threw a {(_step == 2 ? "stone" : "radio")} from {me.GlobalPosition.DistanceTo(other.GlobalPosition):F1} m");
                Next();
                break;
            case 5 when _bonks >= 2:
                Finish(true, "two bonks sent");
                break;
            case 5 when at > 20:
                Finish(false, $"only {_bonks} bonks sent");
                break;
        }
    }

    private void Watch(FootPlayer me, FootPlayer other)
    {
        _lowest = Mathf.Min(_lowest, me.Health);
        _knockedOut |= me.KnockedOut;
        if (other.BackItemId == (int)ItemId.Radio && RadioPlay.Decode(other.HeldRadio) != null && !_sawBack)
        {
            _sawBack = true;
            GD.Print("[bonkcheck] victim: the thrower's radio is on its back, playing");
        }
        if (_bonks >= 1 && !_lowered)
        {
            // hurt by the first: now a sliver left, which a lethal second would take
            _lowered = true;
            if (!(_lastDrop > 1f)) { Finish(false, $"the first hit took no health ({_lastDrop:F1})"); return; }
            me.TakeDamage(me.Health - 9f);
            GD.Print($"[bonkcheck] victim: down to {me.Health:F1} for the second");
        }
        if (_bonks >= 2 && _doneAt < 0) _doneAt = _t;
    }

    private void Verdict(FootPlayer me)
    {
        _lowest = Mathf.Min(_lowest, me.Health);
        _knockedOut |= me.KnockedOut;
        bool ok = _sawBack && !_knockedOut && _lowest >= ThrowHits.Floor - 0.01f;
        Finish(ok, $"two bonks, lowest health {_lowest:F1} (floor {ThrowHits.Floor}), knocked out {_knockedOut}, back radio seen {_sawBack}");
    }

    private double _doneAt = -1;
    private float _lastHealth = FootPlayer.MaxHealth, _lastDrop;

    public override void _PhysicsProcess(double delta)
    {
        if (_local() is not { } me) return;
        // between the hits, held at 9: health regenerates, and the radio must find a sliver to take
        if (_lowered && _bonks == 1 && me.Health > 9.05f) me.TakeDamage(me.Health - 9f);
        _lastHealth = me.Health;
    }

    private void Finish(bool ok, string why)
    {
        GD.Print($"[bonkcheck] RESULT: {(ok ? "ok" : "FAILED")} ({(_thrower ? "thrower" : "victim")}: {why})");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
