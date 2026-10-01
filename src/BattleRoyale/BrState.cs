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
    public List<BrEntrant> Entrants { get; set; } = new();

    [JsonIgnore] public int AliveCount => Entrants.Count(e => e.Alive);
    [JsonIgnore] public bool Running => Phase is BrPhase.Playing or BrPhase.Ended;
    [JsonIgnore] public BrArea Area => new(AreaE, AreaN, Side, AreaName);

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
