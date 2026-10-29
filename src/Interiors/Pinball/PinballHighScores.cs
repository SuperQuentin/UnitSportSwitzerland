using System.Text.Json;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// One machine's high-score table (#1325): the best five games, a name and a score each, best first. A new
/// score enters below any equal one already there (the older keeps its place). Pure: the server keeps one per
/// machine in a <see cref="PinballScoreBook"/>, the peers inside get it packed and the backbox shows it in attract.
/// </summary>
public sealed class PinballHighScores
{
    public const int Kept = 5;
    /// <summary>Names are cut to this many characters (the displays' room).</summary>
    public const int NameLength = 12;

    public sealed record Entry(string Name, long Score);

    public List<Entry> Entries { get; } = new();

    /// <summary>A game's score offered: its place on the table (0 the best), or -1 when it does not get on.</summary>
    public int Offer(string name, long score)
    {
        if (score <= 0) return -1;
        int at = Entries.FindIndex(e => score > e.Score);
        if (at < 0) at = Entries.Count;
        if (at >= Kept) return -1;
        string clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "PLAYER";
        if (clean.Length > NameLength) clean = clean[..NameLength];
        Entries.Insert(at, new Entry(clean, score));
        if (Entries.Count > Kept) Entries.RemoveRange(Kept, Entries.Count - Kept);
        return at;
    }

    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Entries.Count);
        foreach (var e in Entries)
        {
            w.Write(e.Name);
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
        for (int i = 0; i < n && i < Kept; i++) t.Entries.Add(new Entry(r.ReadString(), r.ReadInt64()));
        return t;
    }
}

/// <summary>
/// Every machine's high scores on a server (#1325), keyed like the machine's table choice: the venue's plan key and the
/// piece's furniture index, with the table it was played on (a plan that changed and put another table there starts
/// a fresh table). Saved as JSON with the server's other state (<c>user://pinball_scores.json</c>).
/// </summary>
public sealed class PinballScoreBook
{
    public sealed class Row
    {
        public string Key { get; set; } = "";
        public int Index { get; set; }
        public int Table { get; set; }
        public List<string> Names { get; set; } = new();
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

    public string ToJson() => JsonSerializer.Serialize(_machines
        .Where(m => m.Value.Scores.Entries.Count > 0)
        .Select(m => new Row
        {
            Key = m.Key.Item1, Index = m.Key.Item2, Table = m.Value.Table,
            Names = m.Value.Scores.Entries.Select(e => e.Name).ToList(), Scores = m.Value.Scores.Entries.Select(e => e.Score).ToList(),
        }).ToList(), new JsonSerializerOptions { WriteIndented = true });

    public static PinballScoreBook FromJson(string json)
    {
        var book = new PinballScoreBook();
        var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? new List<Row>();
        foreach (var row in rows)
        {
            var t = book.Of(row.Key, row.Index, row.Table);
            for (int i = 0; i < row.Names.Count && i < row.Scores.Count; i++) t.Offer(row.Names[i], row.Scores[i]);
        }
        return book;
    }
}
