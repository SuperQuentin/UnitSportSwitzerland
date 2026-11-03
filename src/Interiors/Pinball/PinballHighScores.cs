using System.Text.Json;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// One machine's high-score table (#1325): the best five games, a player and a score each, best first. A new
/// score enters below any equal one already there (the older keeps its place). A score belongs to a player's
/// <b>id</b> (#1359) and shows the name that id has now; an entry with no id (a score from before ids, or an offline
/// one) shows the name it was saved with. Pure: the server keeps one per machine in a <see cref="PinballScoreBook"/>,
/// the peers inside get it packed with the names resolved and the backbox shows it in attract.
/// </summary>
public sealed class PinballHighScores
{
    public const int Kept = 5;
    /// <summary>Names are cut to this many characters (the displays' room).</summary>
    public const int NameLength = 12;

    /// <summary><paramref name="Id"/> is the player's id (empty for an entry without one); <paramref name="Name"/> is
    /// what to show when there is no id, and otherwise only a cache of the last name seen.</summary>
    public sealed record Entry(string Id, string Name, long Score);

    public List<Entry> Entries { get; } = new();

    /// <summary>A game's score offered by a player without an id (offline, or a legacy name): its place on the table (0 the best), or -1.</summary>
    public int Offer(string name, long score) => OfferId("", name, score);

    /// <summary>A game's score offered by the player <paramref name="id"/>: its place on the table (0 the best), or -1 when it does not get on.</summary>
    public int OfferId(string id, string name, long score)
    {
        if (score <= 0) return -1;
        int at = Entries.FindIndex(e => score > e.Score);
        if (at < 0) at = Entries.Count;
        if (at >= Kept) return -1;
        string clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "PLAYER";
        if (clean.Length > NameLength) clean = clean[..NameLength];
        Entries.Insert(at, new Entry(id, clean, score));
        if (Entries.Count > Kept) Entries.RemoveRange(Kept, Entries.Count - Kept);
        return at;
    }

    /// <summary>The name shown for an entry: the id's name as <paramref name="nameOf"/> gives it now (the stored one is its backup); cut to the displays.</summary>
    public string NameShown(Entry e, Func<string, string?, string>? nameOf)
    {
        // the stored name is the backup for an id nothing knows any more
        string name = e.Id.Length > 0 && nameOf != null ? nameOf(e.Id, e.Name) : e.Name;
        return name.Length > NameLength ? name[..NameLength] : name;
    }

    /// <summary>
    /// Moves every entry still under the name <paramref name="name"/> (an old save has no ids) to <paramref name="id"/>,
    /// unless the id already has an entry here. Only names that fit the displays whole are claimed: a longer one was cut
    /// and could be another player's. Returns how many were moved.
    /// </summary>
    public int ClaimLegacy(string name, string id)
    {
        if (name.Length == 0 || name.Length > NameLength || Entries.Any(e => e.Id == id)) return 0;
        int claimed = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (e.Id.Length > 0 || !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            Entries[i] = e with { Id = id, Name = name };
            claimed++;
        }
        return claimed;
    }

    /// <summary>The player <paramref name="id"/> is known by <paramref name="name"/> now: refreshes the backup name of its entries.</summary>
    public void Touch(string id, string name)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Id == id && Entries[i].Name != name) Entries[i] = Entries[i] with { Name = name.Length > NameLength ? name[..NameLength] : name };
    }

    /// <summary>The table for the wire: the names as <paramref name="nameOf"/> gives them now (the stored ones without it).</summary>
    public byte[] Pack(Func<string, string?, string>? nameOf = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Entries.Count);
        foreach (var e in Entries)
        {
            w.Write(NameShown(e, nameOf));
            w.Write(e.Score);
        }
        return ms.ToArray();
    }

    public static PinballHighScores Unpack(byte[] data)
    {
        var t = new PinballHighScores();
        if (data.Length == 0) return t;
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadByte();
        for (int i = 0; i < n && i < Kept; i++) t.Entries.Add(new Entry("", r.ReadString(), r.ReadInt64()));
        return t;
    }
}

/// <summary>
/// Every machine's high scores on a server (#1325), keyed like the machine's table choice: the venue's plan key and the
/// piece's furniture index, with the table it was played on (a plan that changed and put another table there starts
/// a fresh table). Saved as JSON with the server's other state (<c>user://pinball_scores.json</c>): per entry the
/// player's id (#1359; a file from before has none) and the name seen with it.
/// </summary>
public sealed class PinballScoreBook
{
    public sealed class Row
    {
        public string Key { get; set; } = "";
        public int Index { get; set; }
        public int Table { get; set; }
        public List<string> Names { get; set; } = new();
        /// <summary>Parallel to <see cref="Names"/>; missing in a file from before #1359.</summary>
        public List<string> Ids { get; set; } = new();
        public List<long> Scores { get; set; } = new();
    }

    private readonly Dictionary<(string, int), (int Table, PinballHighScores Scores)> _machines = new();

    /// <summary>The machine's table of scores (empty when none, or when the machine now holds another table).</summary>
    public PinballHighScores Of(string key, int index, int table)
    {
        if (_machines.TryGetValue((key, index), out var m) && m.Table == table) return m.Scores;
        var fresh = new PinballHighScores();
        _machines[(key, index)] = (table, fresh);
        return fresh;
    }

    /// <summary>The machines of a venue with any score.</summary>
    public IEnumerable<(int Index, int Table, PinballHighScores Scores)> In(string key)
    {
        foreach (var ((k, index), m) in _machines)
            if (k == key && m.Scores.Entries.Count > 0) yield return (index, m.Table, m.Scores);
    }

    /// <summary>A player got its id: every machine moves the scores still under the name to it (see <see cref="PinballHighScores.ClaimLegacy"/>). Returns how many.</summary>
    public int ClaimLegacy(string name, string id)
    {
        foreach (var m in _machines.Values) m.Scores.Touch(id, name);
        return Claim(name, id);
    }

    private int Claim(string name, string id) => _machines.Values.Sum(m => m.Scores.ClaimLegacy(name, id));

    public string ToJson() => JsonSerializer.Serialize(_machines
        .Where(m => m.Value.Scores.Entries.Count > 0)
        .Select(m => new Row
        {
            Key = m.Key.Item1, Index = m.Key.Item2, Table = m.Value.Table,
            Names = m.Value.Scores.Entries.Select(e => e.Name).ToList(), Ids = m.Value.Scores.Entries.Select(e => e.Id).ToList(), Scores = m.Value.Scores.Entries.Select(e => e.Score).ToList(),
        }).ToList(), new JsonSerializerOptions { WriteIndented = true });

    public static PinballScoreBook FromJson(string json)
    {
        var book = new PinballScoreBook();
        var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? new List<Row>();
        foreach (var row in rows)
        {
            var t = book.Of(row.Key, row.Index, row.Table);
            for (int i = 0; i < row.Names.Count && i < row.Scores.Count; i++) t.OfferId(i < row.Ids.Count ? row.Ids[i] ?? "" : "", row.Names[i], row.Scores[i]);
        }
        return book;
    }
}
