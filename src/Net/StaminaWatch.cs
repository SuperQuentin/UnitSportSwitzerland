using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// The server's look at climbs (#1225, <c>docs/notes/player/stamina.md</c>): foot movement and the
/// stamina meter are the owner's, so the server only watches what is replicated, ten times a second.
/// A player in the wall-climbing pose who gains more height since standing than the rules allow with
/// a full ring, the best food and climb-jumps (<see cref="Stamina.PlausibleClimbM"/> of its max, read
/// from its progress) is logged once per climb: <c>[stamina] implausible climb &lt;name&gt; &lt;m&gt;</c>.
/// No kick, no correction: there are no movement checks yet, and this is the first data. Off with
/// <c>--stamina off</c>, and for a peer the Adventure layer is off for (a Battle Royale round).
/// </summary>
public partial class StaminaWatch : Node
{
    private const double Every = 0.1;

    private readonly Node3D _players;
    private readonly Func<long, string>? _nameOf;
    private readonly Dictionary<long, (float From, bool Told)> _climbs = new();
    private readonly List<long> _gone = new();
    private readonly HashSet<long> _seen = new();
    private double _next;

    /// <summary>Climbs logged as implausible since the server started.</summary>
    public int Implausible { get; private set; }

    public StaminaWatch(Node3D players, Func<long, string>? nameOf)
    {
        _players = players;
        _nameOf = nameOf;
        Name = "StaminaWatch";
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now < _next) return;
        _next = now + Every;
        if (CmdArgs.Value("--stamina") == "off") return;
        long start = Stopwatch.GetTimestamp();
        var adventure = Adventure.AdventureService.Instance;
        _seen.Clear();
        foreach (var node in _players.GetChildren())
        {
            if (node is not FootPlayer p || p.Bot || p.Npc || !long.TryParse(p.Name, out long peer)) continue;
            if (adventure == null || !adventure.Active(peer)) continue;
            _seen.Add(peer);
            float y = (float)p.NetGlobal.Alt;
            bool climbing = p.PoseKind == FootPlayer.PoseWallClimb;
            if (!climbing)
            {
                // standing or walking: the next climb counts from here (a fall or a jump keeps the count)
                if (p.PoseKind == FootPlayer.PoseStride) _climbs[peer] = (y, false);
                else if (!_climbs.ContainsKey(peer)) _climbs[peer] = (y, false);
                continue;
            }
            if (!_climbs.TryGetValue(peer, out var climb)) { _climbs[peer] = (y, false); continue; }
            if (climb.Told) continue;
            int steps = adventure.Progress(peer)?.Get(Adventure.StaminaUpgrades.Section).Steps ?? 0;
            float max = Stamina.Base + Stamina.StepPoints * steps + Stamina.FedBonus;
            float gained = y - climb.From;
            if (gained <= Stamina.PlausibleClimbM(max)) continue;
            _climbs[peer] = (climb.From, true);
            Implausible++;
            GD.Print($"[stamina] implausible climb {_nameOf?.Invoke(peer) ?? peer.ToString()} {gained:F0} m (at most {Stamina.PlausibleClimbM(max):F0} with {steps} steps)");
        }
        foreach (long peer in _climbs.Keys)
            if (!_seen.Contains(peer)) _gone.Add(peer);
        foreach (long peer in _gone) _climbs.Remove(peer);
        _gone.Clear();
        ServerStats.Ran("stamina", start);
    }
}
