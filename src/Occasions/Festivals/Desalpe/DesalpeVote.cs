// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>A herd that arrived at the fair and stands in its ring: who drove it, how many cows came, how it was dressed, the votes it has.</summary>
public sealed class VoteEntry
{
    public required int Id { get; init; }
    public required string Label { get; init; }
    public required int Score { get; init; }
    public required int Arrived { get; init; }
    public required int Dressed { get; init; }
    public required byte[] Dress { get; init; }
    /// <summary>Progress keys of the humans who drove this herd: they may not vote for it.</summary>
    public required HashSet<string> Drivers { get; init; }
    public int Votes { get; set; }
}

/// <summary>
/// The decoration vote of the fair (#1269 part 7): every herd that arrived stands in the ring from the vote's day; each human gives one
/// vote per festival instance to a herd they did not drive. The top three are the "Schönste Herde"; ties go to the herd that came in
/// first (the lower id). With fewer than three herds in the ring every herd that arrived shares the middle prize (open question 7 of the plan).
/// </summary>
public sealed class DesalpeVote
{
    private readonly List<VoteEntry> _entries = new();
    private readonly HashSet<string> _voted = new();

    public IReadOnlyList<VoteEntry> Entries => _entries;

    private int _nextId = 1;

    public VoteEntry Add(string label, DesalpeResult result, byte[] dress, IEnumerable<string> drivers)
    {
        var e = new VoteEntry
        {
            Id = _nextId++, Label = label, Score = result.Score, Arrived = result.Arrived, Dressed = result.ArrivedDressed,
            Dress = (byte[])dress.Clone(), Drivers = new HashSet<string>(drivers),
        };
        _entries.Add(e);
        return e;
    }

    public bool HasVoted(string voter) => _voted.Contains(voter);

    /// <summary>Gives <paramref name="voter"/>'s one vote to an entry. Null when it counted, else why not.</summary>
    public string? TryVote(string voter, int entryId)
    {
        if (voter.Length == 0) return "Nobody votes.";
        if (_voted.Contains(voter)) return "You have voted already.";
        var e = _entries.Find(x => x.Id == entryId);
        if (e == null) return "That herd is not in the ring.";
        if (e.Drivers.Contains(voter)) return "Not for your own herd.";
        e.Votes++;
        _voted.Add(voter);
        return null;
    }

    /// <summary>The entries best first: votes, then the herd that came in first.</summary>
    public List<VoteEntry> Standings() => _entries.OrderByDescending(e => e.Votes).ThenBy(e => e.Id).ToList();

    /// <summary>The "Schönste Herde" winners: the top three, or every herd when fewer than three are in the ring.</summary>
    public List<VoteEntry> Winners() => _entries.Count < 3 ? _entries.ToList() : Standings().Take(3).ToList();
}
