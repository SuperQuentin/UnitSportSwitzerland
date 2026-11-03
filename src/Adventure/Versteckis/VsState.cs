using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>A game's phases (#1268): <c>Lobby → Gather → Hide → Seek → RoundEnd → (Hide | Results) → Closed</c>.</summary>
public enum VsPhase : byte { Lobby, Gather, Hide, Seek, RoundEnd, Results, Closed }

/// <summary>An entrant's part in the round now: hiding, seeking, or sitting this round out (joined late).</summary>
public enum VsRole : byte { Hider, Seeker, Out }

/// <summary>The three objectives (#1268, plan 4.3): each breaks its doer's disguise, each once a round.</summary>
public enum VsSite : byte { Bell = 0, Fire = 1, Can = 2 }

/// <summary>One player or bot in a game: who, the scores, the role and prop this round, the tools' charges, the tallies the rewards count.</summary>
public sealed class VsEntrant
{
    public long Peer { get; set; }
    public string Name { get; set; } = "";
    public bool Bot { get; set; }
    /// <summary>Join order: each human seeks once, in this order, before anyone seeks twice.</summary>
    public int Order { get; set; }
    public int Score { get; set; }
    /// <summary>Points this round (the round-end table).</summary>
    public int RoundScore { get; set; }
    public int Seeks { get; set; }
    public VsRole Role { get; set; }
    public VsProp Prop { get; set; }
    public bool Caught { get; set; }
    /// <summary>Game time it was caught (<c>ClockSync.SimNow</c>), the hidden seconds' end.</summary>
    public double CaughtAt { get; set; }
    /// <summary>Out of bounds or failing the server's checks for 3 s: the seeker sees a marker on it.</summary>
    public bool Revealed { get; set; }
    /// <summary>Gone (left or disconnected): its rounds are kept for the reward, it plays no more.</summary>
    public bool Left { get; set; }
    /// <summary>Rounds it took part in (the payout: 6 CHF a round).</summary>
    public int Played { get; set; }

    // ---- the tools (server-kept; the HUD shows them) ----
    public int Sniffs { get; set; } = VsRules.SniffCharges;
    public double SniffBackAt { get; set; }
    public bool DecoyUsed { get; set; }
    public double SprintReadyAt { get; set; }
    public double MooReadyAt { get; set; }
    public int Moos { get; set; }
    public double ChangeReadyAt { get; set; }
    /// <summary>An objective held or carried now (its disguise is down): the site, or -1.</summary>
    public int Holding { get; set; } = -1;
    /// <summary>When its disguise comes back after a hold (game time), 0 when it is up.</summary>
    public double DisguiseBackAt { get; set; }

    // ---- tallies for the progress section (#1268, VersteckisProgress) ----
    public int Catches { get; set; }
    public int Survived { get; set; }
    public int[] SurvivedAs { get; set; } = new int[VsProps.Count];
    public int Bells { get; set; }
    public int Fires { get; set; }
    public int Cans { get; set; }
    public int MooTotal { get; set; }
    public int BestHide { get; set; }

    /// <summary>Hiding now: a hider not caught, not gone.</summary>
    [JsonIgnore] public bool Hiding => Role == VsRole.Hider && !Caught && !Left;

    /// <summary>Its disguise is drawn now (a hider whose objective hold has not dropped it).</summary>
    [JsonIgnore] public bool Disguised => Hiding && Prop != VsProp.None && Holding < 0 && DisguiseBackAt <= 0;
}

/// <summary>An objective's site this round: where it is, done or not and by whom. The milk can has a pick-up and a bench.</summary>
public sealed class VsObjective
{
    public VsSite Site { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public float Alt { get; set; }
    /// <summary>The milk can's pick-up (an Agricultural door); the bench is <see cref="E"/>, <see cref="N"/>.</summary>
    public double FromE { get; set; }
    public double FromN { get; set; }
    public bool Done { get; set; }
    public long By { get; set; }
    /// <summary>Who carries the can now, 0 for nobody.</summary>
    public long Carrier { get; set; }
}

/// <summary>A decoy: a still copy of its owner's prop for 60 s; tagging it is a miss.</summary>
public sealed class VsDecoy
{
    public int Id { get; set; }
    public long Owner { get; set; }
    public VsProp Prop { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public float Alt { get; set; }
    public float Yaw { get; set; }
    public double Until { get; set; }
}

/// <summary>Where a hider was when the round ended (the round-end map's pins, the best moment of a prop hunt).</summary>
public sealed record VsPin(long Peer, string Name, VsProp Prop, double E, double N, bool Caught);

/// <summary>
/// One game (#1268), pure, sent whole as JSON on every change (as <c>BrState</c>). Times are game seconds
/// (<c>ClockSync.SimNow</c>), so <c>/speed</c> paces a game as it does a Battle Royale. No hider's position is in it
/// before the round ends: the pins are written at <see cref="VsPhase.RoundEnd"/>.
/// </summary>
public sealed class VsState
{
    public int Id { get; set; }
    public long Opener { get; set; }
    /// <summary>The village: "Ernen", or the POI's own name.</summary>
    public string Place { get; set; } = "";
    public string Canton { get; set; } = "";
    /// <summary>The area's centre (the church or chapel), LV95, its ground and radius.</summary>
    public double E { get; set; }
    public double N { get; set; }
    public float Ground { get; set; }
    public float Radius { get; set; } = VsRules.MinRadius;
    /// <summary>The church door, where the seeker stands blind in Hide.</summary>
    public double DoorE { get; set; }
    public double DoorN { get; set; }
    /// <summary>The hide plan's seed (<c>HidePlan</c>): every peer draws the same decor from it.</summary>
    public int Seed { get; set; }
    public int Rounds { get; set; }
    /// <summary>1-based once the first round starts, 0 in the lobby.</summary>
    public int Round { get; set; }
    public VsPhase Phase { get; set; }
    public double PhaseEnds { get; set; }
    public double PhaseStarted { get; set; }
    /// <summary>Bots top the game up to this many players (0 = off); their skill.</summary>
    public int Bots { get; set; }
    public int BotLevel { get; set; } = 1;
    /// <summary>The kinds the area's hide plan holds (a cow only with a herd): the Disguise radial's choices.</summary>
    public List<VsProp> Kinds { get; set; } = new();
    public long Seeker { get; set; }
    public List<VsEntrant> Entrants { get; set; } = new();
    public List<VsObjective> Objectives { get; set; } = new();
    public List<VsDecoy> Decoys { get; set; } = new();
    public List<VsPin> Pins { get; set; } = new();
    /// <summary>All three objectives done this round: "The village is awake".</summary>
    public bool Awake { get; set; }
    /// <summary>The game's top scorers once it is over (ties share).</summary>
    public List<long> Winners { get; set; } = new();

    public VsEntrant? Find(long peer)
    {
        foreach (var e in Entrants)
            if (e.Peer == peer) return e;
        return null;
    }

    public IEnumerable<VsEntrant> Playing => Entrants.Where(e => !e.Left);
    public int HidersLeft => Entrants.Count(e => e.Hiding);
    public int Humans => Entrants.Count(e => !e.Bot && !e.Left);
    public bool Running => Phase is VsPhase.Gather or VsPhase.Hide or VsPhase.Seek or VsPhase.RoundEnd;

    /// <summary>"Ernen (VS)", for the lists.</summary>
    public string Label => Canton.Length > 0 ? $"{Place} ({Canton})" : Place;

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static VsState? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<VsState>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Every game a server runs, as one message (the Activities list; each client's own game).</summary>
    public static string ListJson(IEnumerable<VsState> games) => JsonSerializer.Serialize(games.ToList(), Options);

    public static List<VsState> FromListJson(string json)
    {
        try { return JsonSerializer.Deserialize<List<VsState>>(json, Options) ?? new(); }
        catch (JsonException) { return new(); }
    }
}
