using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>
/// How a cell is seen and worked (#494), on top of <see cref="FarmTables"/>: a cell's shown stage
/// is its stored <see cref="CellState"/> if it has one, else its field's crop at the month's
/// <see cref="FarmTables.NaturalStage"/>. Working a natural cell first turns it into the stored
/// state it stands for (<see cref="NaturalState"/>), so a field growing in June keeps growing
/// from where it was once a player touches it. The server and every predicting client run this.
/// </summary>
public static class FarmRules
{
    /// <summary>Share of the growing time a natural <see cref="FieldStage.Growing"/> cell has already grown.</summary>
    public const float NaturalGrowingShare = 0.5f;
    /// <summary>Share a natural <see cref="FieldStage.Sown"/> cell has grown (just sprouting).</summary>
    public const float NaturalSownShare = 0.1f;

    /// <summary>
    /// What a cell shows at <paramref name="now"/> (server Unix seconds): its stage, growth 0..1 and
    /// the crop on it (the sown crop, else the field's).
    /// </summary>
    public static FieldStage StageOf(CropKind fieldCrop, CellState? stored, int month, long now, out float growth, out CropKind crop)
    {
        if (stored is { } s)
        {
            crop = s.Crop == CropKind.None ? fieldCrop : s.Crop;
            return FarmTables.Now(s, now, out growth);
        }
        crop = fieldCrop;
        var stage = FarmTables.NaturalStage(fieldCrop, month);
        growth = stage switch
        {
            FieldStage.Ripe => 1f,
            FieldStage.Growing => NaturalGrowingShare,
            FieldStage.Sown => NaturalSownShare,
            _ => 0f,
        };
        return stage;
    }

    /// <summary>
    /// The stored state an untouched cell stands for at <paramref name="now"/>: a natural sown,
    /// growing or ripe crop becomes <see cref="FieldStage.Sown"/> with a <see cref="CellState.Since"/>
    /// that puts it as far along as it was drawn; stubble, soil and grass stay what they are.
    /// </summary>
    public static CellState NaturalState(CropKind fieldCrop, int month, uint now)
    {
        var stage = FarmTables.NaturalStage(fieldCrop, month);
        float share = stage switch
        {
            FieldStage.Ripe => 1f,
            FieldStage.Growing => NaturalGrowingShare,
            FieldStage.Sown => NaturalSownShare,
            _ => -1f,
        };
        if (share < 0) return new CellState(stage, fieldCrop, now);
        uint back = (uint)Math.Ceiling(FarmTables.GrowSeconds(fieldCrop) * share);
        return new CellState(FieldStage.Sown, fieldCrop, now > back ? now - back : 0);
    }

    /// <summary>
    /// Works one cell: the state it is left in, or null when <paramref name="tool"/> cannot work it
    /// now (<see cref="FarmTables.CanWork"/>, a grass seed, fertiliser twice).
    /// </summary>
    public static CellState? Work(FarmTool tool, CropKind fieldCrop, CellState? stored, CropKind seed, int month, uint now)
    {
        if (fieldCrop == CropKind.None) return null;
        var stage = StageOf(fieldCrop, stored, month, now, out _, out _);
        var current = stored ?? NaturalState(fieldCrop, month, now);
        // grass is mown, never harvested by a combine; a grain field is never mown
        if (tool == FarmTool.Mow && !FarmTables.IsGrass(current.Crop == CropKind.None ? fieldCrop : current.Crop)) return null;
        return FarmTables.Apply(tool, stage, current, seed, now);
    }

    /// <summary>
    /// What one worked cell is worth to the stroke: items gained for Harvest/Mow
    /// (<see cref="FarmTables.YieldPerCell"/> of the crop that was on it), seed items used for Sow.
    /// </summary>
    public static float UnitsPerCell(FarmTool tool, CropKind cropBefore) => tool switch
    {
        FarmTool.Harvest or FarmTool.Mow => FarmTables.YieldPerCell(cropBefore),
        FarmTool.Sow => 1f / FarmTables.CellsPerSeed,
        _ => 0f,
    };

    /// <summary>Items a ripe cell gives by hand: the machine's yield doubled, at least <see cref="FarmTables.HandYieldMin"/> (roots a few).</summary>
    public static int HandYield(CropKind crop) =>
        Math.Max(FarmTables.HandYieldMin, (int)MathF.Round(FarmTables.YieldPerCell(crop) * 2f));

    /// <summary>The calendar month in a game run: <c>--farmmonth N</c>, else <c>--birdmonth N</c> (the hunting season's), else today's.</summary>
    public static int MonthFromArgs(string[] args, DateTime today)
    {
        foreach (string flag in new[] { "--farmmonth", "--birdmonth" })
        {
            int at = Array.IndexOf(args, flag);
            if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int m) && m is >= 1 and <= 12) return m;
        }
        return today.Month;
    }

    public static string CropName(CropKind crop) => crop switch
    {
        CropKind.Wheat => "Wheat",
        CropKind.Barley => "Barley",
        CropKind.Maize => "Maize",
        CropKind.Potato => "Potatoes",
        CropKind.SugarBeet => "Sugar beet",
        CropKind.Rapeseed => "Rapeseed",
        CropKind.Sunflower => "Sunflowers",
        CropKind.Vegetables => "Vegetables",
        CropKind.Legumes => "Peas and beans",
        CropKind.Meadow => "Meadow",
        CropKind.Pasture => "Pasture",
        CropKind.Fallow => "Fallow",
        CropKind.OtherArable => "Crop",
        _ => "Field",
    };

    public static string StageName(FieldStage stage) => stage switch
    {
        FieldStage.Stubble => "stubble",
        FieldStage.Ploughed => "ploughed",
        FieldStage.Sown => "sown",
        FieldStage.Ripe => "ripe",
        FieldStage.Grass => "grass",
        FieldStage.Mown => "mown",
        FieldStage.Growing => "growing",
        _ => "untouched",
    };

    /// <summary>
    /// The cells whose centres lie in the strip from (ae, an) to (be, bn), LV95, <paramref name="width"/>
    /// metres wide and centred on the line (a rectangle; a strip of no length holds none), appended
    /// to <paramref name="into"/> as packed <see cref="CellKey"/>s. No allocation beyond the list's growth.
    /// </summary>
    public static void CellsInStrip(double ae, double an, double be, double bn, float width, List<long> into)
    {
        double dx = be - ae, dy = bn - an, len2 = dx * dx + dy * dy;
        if (len2 < 1e-6 || width <= 0) return;
        double half = width / 2.0, cs = FieldFormat.CellSize;
        int c0 = (int)Math.Floor((Math.Min(ae, be) - half) / cs), c1 = (int)Math.Floor((Math.Max(ae, be) + half) / cs);
        int r0 = (int)Math.Floor((Math.Min(an, bn) - half) / cs), r1 = (int)Math.Floor((Math.Max(an, bn) + half) / cs);
        double len = Math.Sqrt(len2);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                double px = (c + 0.5) * cs - ae, py = (r + 0.5) * cs - an;
                double t = (px * dx + py * dy) / len2;
                if (t < 0 || t > 1) continue;
                if (Math.Abs(px * dy - py * dx) / len > half) continue;
                into.Add(CellKey(c, r));
            }
    }

    /// <summary>A cell of the LV95 grid (column = floor(E / 4), row = floor(N / 4)) packed in one long.</summary>
    public static long CellKey(int col, int row) => ((long)col << 32) | (uint)row;

    /// <summary>The tile and cell index of a packed <see cref="CellKey"/>.</summary>
    public static (TileId Tile, int Cell) Unkey(long key)
    {
        int col = (int)(key >> 32), row = (int)key;
        int tileE = FloorDiv(col, FieldFormat.CellsPerSide), tileN = FloorDiv(row, FieldFormat.CellsPerSide);
        return (new TileId(tileE, tileN), FieldFormat.CellIndex(col - tileE * FieldFormat.CellsPerSide, row - tileN * FieldFormat.CellsPerSide));
    }

    /// <summary>The cell of the LV95 grid holding a point.</summary>
    public static long KeyAt(double e, double n) =>
        CellKey((int)Math.Floor(e / FieldFormat.CellSize), (int)Math.Floor(n / FieldFormat.CellSize));

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
