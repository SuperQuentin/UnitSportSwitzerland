using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.BattleRoyale;

public enum BrPhase { Idle, Lobby, Countdown, Playing, Ended }

/// <summary>Why a player left the match. Sent as an int: append only.</summary>
public enum BrOut { Killed = 0, Zone = 1, Fell = 2, Disconnected = 3, Other = 4 }

/// <summary>One player in a match. <see cref="Team"/> is the squad (0 in solo: everyone alone).</summary>
public sealed class BrEntrant
{
    public long Peer { get; set; }
    public string Name { get; set; } = "";
    public int Team { get; set; }
    public bool Alive { get; set; } = true;
    public int Kills { get; set; }
    public float Damage { get; set; }
    /// <summary>Final placing, 1 = the winner; 0 while still in.</summary>
    public int Place { get; set; }
    /// <summary>Seconds survived since the start.</summary>
    public double Survived { get; set; }
    /// <summary>Out of the cargo plane (#207): jumped, or pushed out when the doors closed.</summary>
    public bool Jumped { get; set; }
    /// <summary>
    /// The group this player asked for in the lobby (<c>/br team &lt;name&gt;</c>, #469): entrants with the
    /// same one share a team at GO (<see cref="BrState.AssignTeams"/>). Empty = anyone.
    /// </summary>
    public string Party { get; set; } = "";
    /// <summary>Down, not out (#475): crawling and bleeding, until a team-mate revives them. Still <see cref="Alive"/>.</summary>
    public bool Downed { get; set; }
    /// <summary>Who downed them: the kill if they do not get up.</summary>
    public long DownedBy { get; set; }

    /// <summary>A group name as typed, made comparable: trimmed, lower case, letters and digits, at most 16.</summary>
    public static string PartyName(string typed) =>
        new string(typed.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).Take(16).ToArray());
}

/// <summary>
/// Everything a client knows of the match, sent whole whenever it changes (it is small) and to a
/// player joining. The zone is not in it: <see cref="Seed"/> and <see cref="Started"/> rebuild it
/// (<see cref="ZoneSchedule"/>), so it never has to be sent while it moves.
/// </summary>
public sealed class BrState
{
    public BrPhase Phase { get; set; }
    public double AreaE { get; set; }
    public double AreaN { get; set; }
    public float Side { get; set; }
    public string AreaName { get; set; } = "";
    public int Seed { get; set; }
    public float Pace { get; set; } = 1f;
    /// <summary>Server clock (<c>Net/ClockSync</c>) at which the countdown ends.</summary>
    public double CountdownEnds { get; set; }
    /// <summary>Server clock at which the zone's clock starts (t = 0): when the plane's doors close (#207).</summary>
    public double Started { get; set; }
    /// <summary>Server clock at GO, when the cargo plane sets off (<see cref="BrFlight"/>).</summary>
    public double FlightStart { get; set; }
    /// <summary>The plane's altitude, m above sea (<see cref="BrFlight.AltitudeOver"/>, from the server's terrain).</summary>
    public float FlightAlt { get; set; }
    public long Winner { get; set; }
    /// <summary>Players per team (#231): 1 is solo (every <see cref="BrEntrant.Team"/> 0), 2 duos, 4 squads.</summary>
    public int TeamSize { get; set; } = 1;
    /// <summary>Players at GO: the first circle is sized for them (<see cref="ZoneSchedule.FirstRadius"/>); 0 before GO.</summary>
    public int Field { get; set; }
    /// <summary>The winning team, 0 in solo or with no winner.</summary>
    public int WinnerTeam { get; set; }
    public List<BrEntrant> Entrants { get; set; } = new();

    [JsonIgnore] public int AliveCount => Entrants.Count(e => e.Alive);
    /// <summary>
    /// Sides still in it: each team with someone standing once, each living solo player once. A team
    /// whose living are all down is not (#475): nobody is left to pick them up.
    /// </summary>
    [JsonIgnore] public int TeamsAlive => Entrants.Where(e => e.Alive && !e.Downed).Select(SideOf).Distinct().Count();

    /// <summary>Whether <paramref name="peer"/> going down would leave a team-mate standing to revive them (#475).</summary>
    public bool MateStanding(long peer) => MatesOf(peer).Any(m => m.Alive && !m.Downed);

    /// <summary>A player's side: their team, or (solo) themselves.</summary>
    public static long SideOf(BrEntrant e) => e.Team != 0 ? e.Team : -e.Peer;

    /// <summary>Two entrants who may hurt each other: different sides.</summary>
    public static bool Hostile(BrEntrant a, BrEntrant b) => SideOf(a) != SideOf(b);

    /// <summary>The living team-mates of a player (none in solo).</summary>
    public IEnumerable<BrEntrant> MatesOf(long peer) =>
        Find(peer) is { Team: not 0 } me ? Entrants.Where(e => e.Team == me.Team && e.Peer != peer) : Enumerable.Empty<BrEntrant>();

    /// <summary>
    /// Fills teams of <see cref="TeamSize"/> at GO, in a seeded shuffle (#231). Entrants who asked for
    /// the same <see cref="BrEntrant.Party"/> share a team (#469), split into several when the group
    /// is bigger than a team; everyone else then fills the places left, short teams first, then new
    /// ones. With no groups it is the plain shuffle: team 1, 2, ... in order, the last one short if
    /// the field does not divide. Solo leaves every team 0.
    /// </summary>
    public void AssignTeams()
    {
        if (TeamSize <= 1)
        {
            foreach (var e in Entrants) e.Team = 0;
            return;
        }
        var rng = new Random(Seed ^ 0x6a09e667);
        var order = Entrants.OrderBy(e => e.Peer).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var teams = new List<List<BrEntrant>>();
        foreach (var group in order.Where(e => e.Party.Length > 0).GroupBy(e => e.Party))
            foreach (var chunk in group.Chunk(TeamSize)) teams.Add(chunk.ToList());
        foreach (var e in order.Where(e => e.Party.Length == 0))
        {
            var team = teams.FirstOrDefault(t => t.Count < TeamSize);
            if (team == null) teams.Add(team = new List<BrEntrant>());
            team.Add(e);
        }
        for (int i = 0; i < teams.Count; i++)
            foreach (var e in teams[i]) e.Team = i + 1;
    }
    [JsonIgnore] public bool Running => Phase is BrPhase.Playing or BrPhase.Ended;
    [JsonIgnore] public BrArea Area => new(AreaE, AreaN, Side, AreaName);
    /// <summary>This match's zone, the same on every peer; built once per (seed, side, pace, field).</summary>
    public ZoneSchedule Zone()
    {
        var key = (Seed, Side, Pace, Field);
        if (_zone == null || key != _zoneKey) (_zone, _zoneKey) = (new ZoneSchedule(Seed, Side, Pace, Field), key);
        return _zone;
    }

    private ZoneSchedule? _zone;
    private (int, float, float, int) _zoneKey;

    /// <summary>The cargo plane's line, once the match has started.</summary>
    [JsonIgnore] public BrFlight? Flight => Running && FlightStart > 0 ? new BrFlight(this) : null;

    public BrEntrant? Find(long peer) => Entrants.FirstOrDefault(e => e.Peer == peer);

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static BrState? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<BrState>(json, Options); }
        catch (JsonException) { return null; }
    }
}
