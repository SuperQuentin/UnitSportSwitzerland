using System.IO.Compression;
using System.Text;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Reads an Esri File Geodatabase (<c>.gdb</c>) without GDAL (#537).
///
/// <para>
/// Two swisstopo datasets are published only as FileGDB — swissBUILDINGS3D's solids and the
/// ASTRA Veloland / Mountainbikeland route networks — and they were the last reason this project
/// asked anyone to install GDAL and its Python bindings. Bundling GDAL is not an option (150-250 MB
/// per platform, three platforms, plus macOS codesigning), and the format is documented, so this
/// reads it directly. It is the same choice <see cref="GeoPackageReader"/> already makes for
/// swissTLM3D.
/// </para>
///
/// <para>
/// <b>What it does and does not do.</b> This is a reader for the two things those datasets need:
/// the table catalogue, and rows of attributes and geometry. It is not a general FileGDB
/// implementation — no writing, no indexes (<c>.spx</c>, <c>.atx</c>), no domains, no subtypes.
/// Anything it does not understand makes it throw, loudly and with the offset, rather than return
/// a plausible-looking wrong answer.
/// </para>
///
/// <para>
/// <b>Layout.</b> A <c>.gdb</c> is a directory of numbered tables. <c>aNNNNNNNN.gdbtable</c> holds a
/// header, a field descriptor block and the rows; <c>aNNNNNNNN.gdbtablx</c> holds one offset per row
/// id, so a row can be reached without scanning. Table 1 is the catalogue naming the rest.
/// </para>
/// </summary>
public sealed class FileGdb : IDisposable
{
    /// <summary>A field's storage type, as the format numbers them.</summary>
    public enum FieldType
    {
        Int16 = 0,
        Int32 = 1,
        Float32 = 2,
        Float64 = 3,
        String = 4,
        DateTime = 5,
        ObjectId = 6,
        Geometry = 7,
        Binary = 8,
        Raster = 9,
        Uuid = 10,
        GlobalId = 11,
        Xml = 12,
    }

    /// <summary>One column: what it is called, what it holds, and whether a row may leave it out.</summary>
    public sealed record Field(string Name, FieldType Type, bool Nullable, int Width)
    {
        /// <summary>ObjectId is stored in the row id, not in the row, so it occupies no bytes.</summary>
        public bool InRow => Type != FieldType.ObjectId;

        /// <summary>Only fields that can be null take a bit in a row's null bitmap.</summary>
        public bool InNullBitmap => Nullable && InRow;
    }

    /// <summary>
    /// How a geometry column's coordinates are packed: integers, scaled and offset, so the format
    /// stores no floating point. A reader that ignores this produces coordinates near the South
    /// Pole rather than in Switzerland, which is why it is carried with the field.
    /// </summary>
    public sealed record GeometryGrid(
        double XOrigin, double YOrigin, double XyScale,
        double ZOrigin, double ZScale,
        double MOrigin, double MScale,
        bool HasZ, bool HasM);

    private readonly string _dir;
    private readonly Dictionary<string, string> _tables = new(StringComparer.OrdinalIgnoreCase);

    private FileGdb(string dir) => _dir = dir;

    /// <summary>Every table name in the catalogue, in the order the catalogue lists them.</summary>
    public IReadOnlyCollection<string> TableNames => _tables.Keys;

    /// <summary>
    /// Opens the <c>.gdb</c> directory and reads its catalogue. The caller is expected to have
    /// unpacked the zip swisstopo publishes — see <see cref="OpenZip"/>, which does that part.
    /// </summary>
    public static FileGdb Open(string gdbDir)
    {
        if (!Directory.Exists(gdbDir)) throw new DirectoryNotFoundException($"no FileGDB at {gdbDir}");
        var gdb = new FileGdb(gdbDir);
        gdb.ReadCatalogue();
        return gdb;
    }

    /// <summary>
    /// The catalogue (table 1, <c>GDB_SystemCatalog</c>): one row per table, giving its name and the
    /// number its files are called by. Read first, because every other table is found through it.
    /// </summary>
    private void ReadCatalogue()
    {
        using var catalogue = OpenFile(Path.Combine(_dir, "a00000001.gdbtable"));
        int name = catalogue.FieldIndex("Name");
        if (name < 0) throw new InvalidDataException("a00000001 is not a FileGDB catalogue (no Name column)");

        foreach (var row in catalogue.Rows())
        {
            // The row id is the table number: row 17 is a00000011.gdbtable, in hex.
            if (row[name] is string tableName && tableName.Length > 0)
                _tables[tableName] = $"a{row.ObjectId:x8}.gdbtable";
        }
    }

    /// <summary>Opens one named table. The name is the one the catalogue holds, case-insensitively.</summary>
    public Table OpenTable(string tableName)
    {
        if (!_tables.TryGetValue(tableName, out var file))
            throw new KeyNotFoundException(
                $"no table {tableName} in {_dir}; it has: {string.Join(", ", _tables.Keys)}");
        return OpenFile(Path.Combine(_dir, file));
    }

    /// <summary>True when the catalogue has that table, so a caller can choose between layer names.</summary>
    public bool Has(string tableName) => _tables.ContainsKey(tableName);

    private static Table OpenFile(string path) => new(path);

    public void Dispose() { }

    // ---- one table ---------------------------------------------------------------------------

    /// <summary>
    /// One table: its columns, and its rows read through the <c>.gdbtablx</c> offset list. Rows are
    /// yielded one at a time into a reused buffer — a buildings sheet is tens of thousands of rows
    /// and a nationwide route network is a quarter of a million, so holding them all would be
    /// gigabytes for no reason.
    /// </summary>
    public sealed class Table : IDisposable
    {
        private readonly FileStream _table;
        private readonly string _path;
        private readonly long[] _rowOffsets;

        public IReadOnlyList<Field> Fields { get; }

        /// <summary>The geometry column's packing, or null when the table has no geometry.</summary>
        public GeometryGrid? Grid { get; }

        /// <summary>How many rows the header claims. The offset list may be longer (deleted rows).</summary>
        public int RowCount { get; }

        internal Table(string path)
        {
            _path = path;
            _table = File.OpenRead(path);

            Span<byte> head = stackalloc byte[40];
            _table.ReadExactly(head);
            int signature = BitConverter.ToInt32(head[..4]);
            if (signature != 3)
                throw new InvalidDataException($"{Path.GetFileName(path)}: not a .gdbtable (signature {signature})");
            RowCount = BitConverter.ToInt32(head.Slice(4, 4));
            long fieldsAt = BitConverter.ToInt64(head.Slice(32, 8));

            (Fields, Grid) = ReadFields(fieldsAt);
            _rowOffsets = ReadRowOffsets(Path.ChangeExtension(path, ".gdbtablx"));
        }

        public int FieldIndex(string name)
        {
            for (int i = 0; i < Fields.Count; i++)
                if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// The field descriptor block: a count, then one self-delimiting descriptor each. Every
        /// branch below is a documented field layout; anything else throws with its offset, because
        /// a descriptor read one byte wrong silently turns every row after it into noise.
        /// </summary>
        private (List<Field>, GeometryGrid?) ReadFields(long at)
        {
            _table.Position = at;
            Span<byte> lengthBytes = stackalloc byte[4];
            _table.ReadExactly(lengthBytes);
            int length = BitConverter.ToInt32(lengthBytes);
            if (length is <= 0 or > 1 << 20)
                throw new InvalidDataException($"{Path.GetFileName(_path)}: field block length {length}");
            var block = new byte[length];
            _table.ReadExactly(block);

            int count = BitConverter.ToUInt16(block, 8);
            int off = 10;
            var fields = new List<Field>(count);
            GeometryGrid? grid = null;

            for (int i = 0; i < count; i++)
            {
                string name = ReadText(block, ref off);
                ReadText(block, ref off);                       // alias, unused
                var type = (FieldType)block[off++];
                bool nullable;
                int width = 0;

                switch (type)
                {
                    case FieldType.String or FieldType.Xml:
                        width = BitConverter.ToInt32(block, off); off += 4;
                        nullable = SkipFlagsAndDefault(block, ref off, widthBytes: 0);
                        break;

                    case FieldType.ObjectId or FieldType.Uuid or FieldType.GlobalId:
                        width = block[off++];
                        nullable = (block[off++] & 1) != 0;      // these never carry a default
                        break;

                    case FieldType.Geometry:
                        width = block[off++];
                        nullable = (block[off++] & 1) != 0;
                        grid = ReadGeometryGrid(block, ref off);
                        break;

                    default:
                        width = block[off++];
                        nullable = SkipFlagsAndDefault(block, ref off, widthBytes: 0);
                        break;
                }
                fields.Add(new Field(name, type, nullable, width));
            }

            // The block ends with a single terminator byte after the last descriptor. Landing
            // anywhere else means a descriptor was read wrong, and every row after it would be
            // noise — so this is a hard failure rather than a warning.
            if (off != length && off != length - 1)
                throw new InvalidDataException(
                    $"{Path.GetFileName(_path)}: read {count} field descriptors but ended at {off} of {length} "
                    + "— the field block is not understood, and rows read from it would be noise");
            return (fields, grid);
        }

        /// <summary>The flag byte, and the default value behind it when bit 2 says there is one.</summary>
        private static bool SkipFlagsAndDefault(byte[] block, ref int off, int widthBytes)
        {
            off += widthBytes;
            byte flags = block[off++];
            if ((flags & 4) != 0)
            {
                int defaultLength = block[off++];
                off += defaultLength;
            }
            return (flags & 1) != 0;
        }

        /// <summary>
        /// The geometry column's spatial reference and coordinate packing. The trailing part (bounds
        /// and spatial-index grid sizes) is skipped rather than kept: nothing here needs it, and the
        /// field block's total length check above is what proves it was skipped by the right amount.
        /// </summary>
        private static GeometryGrid ReadGeometryGrid(byte[] block, ref int off)
        {
            int srsBytes = BitConverter.ToUInt16(block, off); off += 2 + srsBytes;   // WKT, unused

            byte flags = block[off++];
            bool hasZ = (flags & 1) != 0, hasM = (flags & 2) != 0;

            double xOrigin = BitConverter.ToDouble(block, off); off += 8;
            double yOrigin = BitConverter.ToDouble(block, off); off += 8;
            double xyScale = BitConverter.ToDouble(block, off); off += 8;
            double mOrigin = 0, mScale = 1, zOrigin = 0, zScale = 1;
            if (hasM) { mOrigin = BitConverter.ToDouble(block, off); off += 8; mScale = BitConverter.ToDouble(block, off); off += 8; }
            if (hasZ) { zOrigin = BitConverter.ToDouble(block, off); off += 8; zScale = BitConverter.ToDouble(block, off); off += 8; }

            off += 8;                                   // xy tolerance
            if (hasM) off += 8;                         // m tolerance
            if (hasZ) off += 8;                         // z tolerance
            off += 32;                                  // xmin, ymin, xmax, ymax

            // The tail is a run of optional bounds pairs (z, m) and then the spatial index: a byte,
            // a grid count, and that many grid sizes. Which bounds pairs are actually written does
            // not follow the hasZ/hasM flags — the swisstopo route networks set both flags but write
            // only the Z pair — so rather than guess, try each possibility and keep the one whose
            // grid count is credible. The field block's total length check in ReadFields is what
            // finally proves the choice was right.
            int afterBounds = -1, grids = 0;
            for (int pairs = 0; pairs <= 2 && afterBounds < 0; pairs++)
            {
                int candidate = off + pairs * 16;
                if (candidate + 5 > block.Length) break;
                int count = BitConverter.ToInt32(block, candidate + 1);
                if (count is >= 0 and <= 4 && candidate + 5 + 8 * count <= block.Length)
                {
                    afterBounds = candidate;
                    grids = count;
                }
            }
            if (afterBounds < 0)
                throw new InvalidDataException(
                    "geometry field: no credible spatial index follows its bounds; the descriptor is not understood");
            off = afterBounds + 5 + 8 * grids;

            return new GeometryGrid(xOrigin, yOrigin, xyScale, zOrigin, zScale, mOrigin, mScale, hasZ, hasM);
        }

        /// <summary>A length-prefixed UTF-16 string, as every name in the field block is stored.</summary>
        private static string ReadText(byte[] block, ref int off)
        {
            int chars = block[off++];
            var text = Encoding.Unicode.GetString(block, off, chars * 2);
            off += chars * 2;
            return text;
        }

        /// <summary>
        /// <c>.gdbtablx</c>: one offset per row id, so row n is reached without reading rows 1..n-1.
        /// A zero offset is a deleted row and is skipped. The offsets are 5 bytes wide in the files
        /// swisstopo publishes, but the header says how wide, so this follows it.
        /// </summary>
        private static long[] ReadRowOffsets(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"no row index beside the table: {path}");
            using var index = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            index.ReadExactly(head);
            int blocks = BitConverter.ToInt32(head.Slice(4, 4));
            int rows = BitConverter.ToInt32(head.Slice(8, 4));
            int width = BitConverter.ToInt32(head.Slice(12, 4));
            if (width is < 4 or > 6) throw new InvalidDataException($"{Path.GetFileName(path)}: offset width {width}");

            long total = (long)blocks * 1024;
            if (rows > total) total = rows;
            var offsets = new long[total];
            var buffer = new byte[width * 1024];
            for (long done = 0; done < total;)
            {
                int want = (int)Math.Min(1024, total - done);
                int got = index.Read(buffer, 0, want * width);
                if (got < want * width) break;              // a short index just means fewer rows
                for (int i = 0; i < want; i++)
                {
                    long offset = 0;
                    for (int b = 0; b < width; b++) offset |= (long)buffer[i * width + b] << (8 * b);
                    offsets[done + i] = offset;
                }
                done += want;
            }
            return offsets;
        }

        /// <summary>
        /// Every live row, in id order. The values array is reused between rows, so a caller that
        /// keeps one must copy what it needs — which is what makes reading a quarter of a million
        /// route segments cost no allocations per row.
        /// </summary>
        public IEnumerable<Row> Rows()
        {
            var values = new object?[Fields.Count];
            var row = new Row(this, values);
            var buffer = new byte[1 << 16];

            for (int id = 0; id < _rowOffsets.Length; id++)
            {
                long at = _rowOffsets[id];
                if (at == 0) continue;                      // deleted

                _table.Position = at;
                Span<byte> sizeBytes = stackalloc byte[4];
                _table.ReadExactly(sizeBytes);
                int size = BitConverter.ToInt32(sizeBytes);
                if (size <= 0) continue;
                if (size > buffer.Length) buffer = new byte[Math.Max(size, buffer.Length * 2)];
                _table.ReadExactly(buffer.AsSpan(0, size));

                Decode(buffer.AsSpan(0, size), values);
                row.ObjectId = id + 1;                      // row ids are 1-based
                yield return row;
            }
        }

        /// <summary>
        /// One row: a null bitmap covering the nullable columns, then the present values in field
        /// order. Variable-length values carry their own length as a varint.
        /// </summary>
        private void Decode(ReadOnlySpan<byte> row, object?[] into)
        {
            int nullable = 0;
            foreach (var f in Fields) if (f.InNullBitmap) nullable++;
            int bitmapBytes = (nullable + 7) / 8;
            var bitmap = row[..bitmapBytes];
            int at = bitmapBytes;
            int nullBit = 0;

            for (int i = 0; i < Fields.Count; i++)
            {
                var field = Fields[i];
                if (!field.InRow) { into[i] = null; continue; }

                bool isNull = false;
                if (field.InNullBitmap)
                {
                    isNull = (bitmap[nullBit / 8] & (1 << (nullBit % 8))) != 0;
                    nullBit++;
                }
                into[i] = isNull ? null : ReadValue(field, row, ref at);
            }
        }

        private static object? ReadValue(Field field, ReadOnlySpan<byte> row, ref int at)
        {
            switch (field.Type)
            {
                case FieldType.Int16:
                    { var v = BitConverter.ToInt16(row.Slice(at, 2)); at += 2; return v; }
                case FieldType.Int32:
                    { var v = BitConverter.ToInt32(row.Slice(at, 4)); at += 4; return v; }
                case FieldType.Float32:
                    { var v = BitConverter.ToSingle(row.Slice(at, 4)); at += 4; return v; }
                case FieldType.Float64:
                    { var v = BitConverter.ToDouble(row.Slice(at, 8)); at += 8; return v; }
                case FieldType.DateTime:
                    {
                        // days since 1899-12-30, the OLE automation epoch
                        double days = BitConverter.ToDouble(row.Slice(at, 8)); at += 8;
                        return DateTime.FromOADate(days);
                    }
                case FieldType.Uuid or FieldType.GlobalId:
                    {
                        var bytes = row.Slice(at, 16); at += 16;
                        // Esri writes the braced, upper-case form everywhere it names one
                        return "{" + new Guid(bytes).ToString("D").ToUpperInvariant() + "}";
                    }
                case FieldType.String or FieldType.Xml:
                    {
                        int length = ReadVarint(row, ref at);
                        var text = Encoding.UTF8.GetString(row.Slice(at, length));
                        at += length;
                        return text;
                    }
                case FieldType.Binary or FieldType.Geometry:
                    {
                        int length = ReadVarint(row, ref at);
                        var blob = row.Slice(at, length).ToArray();
                        at += length;
                        return blob;
                    }
                default:
                    throw new InvalidDataException($"field {field.Name} has unsupported type {field.Type}");
            }
        }

        /// <summary>The format's variable-length integer: seven bits a byte, high bit means more.</summary>
        private static int ReadVarint(ReadOnlySpan<byte> row, ref int at)
        {
            int value = 0, shift = 0;
            while (true)
            {
                byte b = row[at++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
                if (shift > 28) throw new InvalidDataException("varint longer than an int");
            }
        }

        public void Dispose() => _table.Dispose();
    }

    /// <summary>
    /// One row's values, by field index. The same instance is handed out for every row of a scan:
    /// read what you need before asking for the next one.
    /// </summary>
    public sealed class Row
    {
        private readonly Table _table;
        private readonly object?[] _values;

        internal Row(Table table, object?[] values)
        {
            _table = table;
            _values = values;
        }

        /// <summary>The row's own id, which is where an ObjectId column's value actually lives.</summary>
        public int ObjectId { get; internal set; }

        public object? this[int field] => _values[field];

        public string? Text(int field) => _values[field] as string;

        public byte[]? Blob(int field) => _values[field] as byte[];

        public double? Number(int field) => _values[field] switch
        {
            short v => v,
            int v => v,
            float v => v,
            double v => v,
            _ => null,
        };
    }

    // ---- zips --------------------------------------------------------------------------------

    /// <summary>
    /// swisstopo publishes each FileGDB as a zip holding one <c>.gdb</c> directory. The reader needs
    /// random access (the row index exists precisely so rows are not read in order), and a deflated
    /// zip entry has none, so the directory is unpacked to <paramref name="workDir"/> first and the
    /// caller is handed a reader over it.
    /// </summary>
    public static FileGdb OpenZip(string zipPath, string workDir)
    {
        string name = Path.GetFileNameWithoutExtension(zipPath);
        string target = Path.Combine(workDir, name);
        Directory.CreateDirectory(target);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0) continue;
                // Only what the reader reads: the indexes and spatial indexes are megabytes each
                // and nothing here opens them.
                string extension = Path.GetExtension(entry.Name);
                if (extension is not (".gdbtable" or ".gdbtablx")) continue;

                string to = Path.Combine(target, entry.Name);
                if (File.Exists(to) && new FileInfo(to).Length == entry.Length) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                entry.ExtractToFile(to, overwrite: true);
            }
        }
        return Open(target);
    }
}
