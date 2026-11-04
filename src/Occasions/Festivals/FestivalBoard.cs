using System.Text.Json;

// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>One crew's standing on a festival's board: the party's score, and who put what into it.</summary>
public sealed class CrewEntry
{
    public string Key { get; set; } = "";
    public int Points { get; set; }

    /// <summary>The milestones (by index) the crew has reached.</summary>
    public List<int> Taken { get; set; } = new();

    /// <summary>Points per member, by progress key: who contributed (and so is paid a milestone).</summary>
    public Dictionary<string, int> Contributed { get; set; } = new();

    /// <summary>The members' names as they were when they last scored: a cache, for a key whose name is unknown (#1359).</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    /// <summary>
    /// The name to show for a member: <paramref name="nameOf"/> gives it now from the progress key (a PlayerKey or
    /// <c>acct:</c> id; null for a key that is no id), else the stored one (also the backup when the id no longer resolves).
    /// </summary>
    public string NameOf(string memberKey, Func<string, string?, string>? nameOf) =>
        nameOf != null && Net.PlayerIds.IsId(memberKey) ? nameOf(memberKey, Names.GetValueOrDefault(memberKey)) : Names.GetValueOrDefault(memberKey, "?");
}

/// <summary>A milestone a play has just reached, and the members it pays.</summary>
public sealed record MilestoneHit(int Index, FestivalMilestone Milestone, string[] Payees);

/// <summary>
/// The party score board of one festival instance (#1269 section 1.3), kept by the server in
/// <c>user://festivals/&lt;instance&gt;.json</c>. A party's id lasts a session, so a crew is its members: the entry is keyed by the
/// <b>sorted progress keys</b> of the members present when the points were earned, and a party that re-forms the next day
/// with the same people finds its score again. A solo player is a crew of one. A milestone pays every member who
/// contributed, guests included; one who is offline is owed it (<see cref="Owed"/>) and gets it on their next load. Pure.
/// </summary>
public sealed class FestivalBoard
{
    public string Instance { get; set; } = "";
    public string Festival { get; set; } = "";
    public Dictionary<string, CrewEntry> Crews { get; set; } = new();

    /// <summary>Batzen of milestones owed to members who were not online when it was reached, by progress key.</summary>
    public Dictionary<string, int> Owed { get; set; } = new();

    public static string CrewKey(IEnumerable<string> memberKeys) =>
        string.Join('+', memberKeys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>The standing of the crew made of exactly these members, or null when it has none yet.</summary>
    public CrewEntry? Find(IEnumerable<string> memberKeys) => Crews.GetValueOrDefault(CrewKey(memberKeys));

    /// <summary>
    /// <paramref name="contributor"/> (one of <paramref name="memberKeys"/>) earned <paramref name="points"/> for the crew.
    /// Returns the milestones that this crossed, each with the members who had contributed by now (this play included).
    /// </summary>
    public List<MilestoneHit> Add(FestivalDef def, IEnumerable<string> memberKeys, string contributor, string name, int points)
    {
        var hits = new List<MilestoneHit>();
        if (points <= 0 || contributor.Length == 0) return hits;
        var members = memberKeys.Append(contributor);
        string key = CrewKey(members);
        if (!Crews.TryGetValue(key, out var crew)) Crews[key] = crew = new CrewEntry { Key = key };
        crew.Points += points;
        crew.Contributed[contributor] = crew.Contributed.GetValueOrDefault(contributor) + points;
        if (name.Length > 0) crew.Names[contributor] = name;
        for (int i = 0; i < def.Milestones.Length; i++)
        {
            if (crew.Points < def.Milestones[i].Points || crew.Taken.Contains(i)) continue;
            crew.Taken.Add(i);
            hits.Add(new MilestoneHit(i, def.Milestones[i],
                crew.Contributed.Where(c => c.Value > 0).Select(c => c.Key).Order(StringComparer.Ordinal).ToArray()));
        }
        return hits;
    }

    /// <summary>A member offline when a milestone paid: it is owed until they next load.</summary>
    public void Owe(string key, int batzen)
    {
        if (batzen > 0) Owed[key] = Owed.GetValueOrDefault(key) + batzen;
    }

    /// <summary>What <paramref name="key"/> is owed; zeroed, so it is paid once.</summary>
    public int TakeOwed(string key) => Owed.Remove(key, out int n) ? n : 0;

    // ---- the file ---------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FestivalBoard FromJson(string json) => JsonSerializer.Deserialize<FestivalBoard>(json, Json) ?? new FestivalBoard();
}
