using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>
/// The worked cells of one tile (#494), sparse: only what players changed is kept, everything else
/// is natural (<see cref="FarmRules"/>). The server's copy is the truth, saved to
/// <c>user://farm/E_N.json</c>; a client holds what the server sent (snapshot, then changes) plus
/// its own predictions. Main thread only.
///
/// <para>
/// <b>Packed form</b> (the save file and the wire): one long a cell, <see cref="Pack"/>: cell index
/// (16 bits), stage (4), crop (8), fertilised (1), <see cref="CellState.Since"/> (32, server Unix
/// seconds). Stage <see cref="FieldStage.Natural"/> means "no stored state": the cell went back to
/// the calendar (a correction from the server).
/// </para>
/// </summary>
public sealed class FieldCells
{
    private readonly Dictionary<int, CellState> _cells = new();

    /// <summary>Bumped on every change: savers and drawers compare it.</summary>
    public int Version { get; private set; }

    /// <summary>Per drawing chunk (<see cref="FieldTile.ChunkOf"/>), bumped when one of its cells changes.</summary>
    public int[] ChunkVersions { get; } = new int[FieldTile.ChunkCount];

    public int Count => _cells.Count;

    public IReadOnlyDictionary<int, CellState> All => _cells;

    public CellState? Get(int cell) => _cells.TryGetValue(cell, out var s) ? s : null;

    /// <summary>Stores (or, with null or a <see cref="FieldStage.Natural"/> state, forgets) a cell. True when it changed.</summary>
    public bool Set(int cell, CellState? state)
    {
        if ((uint)cell >= FieldFormat.CellCount) return false;
        bool changed;
        if (state is not { Stage: not FieldStage.Natural } s) changed = _cells.Remove(cell);
        else if (_cells.TryGetValue(cell, out var old) && old == s) changed = false;
        else { _cells[cell] = s; changed = true; }
        if (changed)
        {
            Version++;
            ChunkVersions[FieldTile.ChunkOf(cell)]++;
        }
        return changed;
    }

    /// <summary>Forgets every cell (a snapshot replaces them).</summary>
    public void Clear()
    {
        if (_cells.Count == 0) return;
        foreach (int cell in _cells.Keys) ChunkVersions[FieldTile.ChunkOf(cell)]++;
        _cells.Clear();
        Version++;
    }

    public static long Pack(int cell, CellState s) =>
        (uint)cell & 0xFFFF
        | ((long)s.Stage & 0xF) << 16
        | ((long)s.Crop & 0xFF) << 20
        | (s.Fertilised ? 1L << 28 : 0)
        | (long)s.Since << 32;

    public static (int Cell, CellState? State) Unpack(long v)
    {
        int cell = (int)(v & 0xFFFF);
        var stage = (FieldStage)((v >> 16) & 0xF);
        if (stage == FieldStage.Natural) return (cell, null);
        return (cell, new CellState(stage, (CropKind)((v >> 20) & 0xFF), (uint)(v >>> 32), (v & (1L << 28)) != 0));
    }

    /// <summary>A cell's packed "back to natural" record.</summary>
    public static long PackNatural(int cell) => (uint)cell & 0xFFFF;

    /// <summary>The packed record of a cell as stored now (natural when none).</summary>
    public long PackCell(int cell) => Get(cell) is { } s ? Pack(cell, s) : PackNatural(cell);

    /// <summary>Every stored cell, packed (the save file, a snapshot), in cell order so the file is stable.</summary>
    public long[] PackAll()
    {
        var keys = new int[_cells.Count];
        _cells.Keys.CopyTo(keys, 0);
        Array.Sort(keys);
        var packed = new long[keys.Length];
        for (int i = 0; i < keys.Length; i++) packed[i] = Pack(keys[i], _cells[keys[i]]);
        return packed;
    }

    /// <summary>Applies packed records (a snapshot after <see cref="Clear"/>, or a batch of changes). Returns how many cells changed.</summary>
    public int Apply(ReadOnlySpan<long> packed)
    {
        int changed = 0;
        foreach (long v in packed)
        {
            var (cell, state) = Unpack(v);
            if (Set(cell, state)) changed++;
        }
        return changed;
    }

    /// <summary>
    /// The save file's shape: <c>{"Version":2,"Cells":[packed...]}</c>. 2: <see cref="CellState.Since"/>
    /// in environment seconds (#579); a version-1 file held Unix stamps and is not read (the fields go back to natural).
    /// </summary>
    public sealed class File
    {
        public const int CurrentVersion = 2;
        public int Version { get; set; } = CurrentVersion;
        public long[] Cells { get; set; } = Array.Empty<long>();
    }
}
