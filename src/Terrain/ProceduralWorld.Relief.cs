using System.Buffers.Binary;
using System.IO.Compression;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    /// <summary>
    /// The real country, coarsely: a 500 m heightmap of Switzerland and its border areas
    /// (swisstopo swissALTIRegio averaged down by <c>tools/swiss_relief.py</c>, embedded in the
    /// assembly), and what is derived from it once at load: the local relief the generator's
    /// roughness scales with, the lakes (the source models a lake as its flat surface), and the
    /// drainage — where every node's water goes and how much land drains through it — that the
    /// river network is traced from.
    ///
    /// <para>
    /// Everything here is integer arithmetic on the stored decimetres, or evaluated in a fixed
    /// order, so every peer derives the same network from the same file.
    /// </para>
    /// </summary>
    private sealed class Relief
    {
        private const string ResourceName = "swiss_relief.gz";

        public readonly int Cols, Rows;
        public readonly double MinE, MaxN, Spacing;
        public double MaxE => MinE + (Cols - 1) * Spacing;
        public double MinN => MaxN - (Rows - 1) * Spacing;

        /// <summary>Node heights in decimetres, row 0 the northernmost.</summary>
        public readonly ushort[] Dm;
        /// <summary>Node heights in metres.</summary>
        public readonly float[] H;
        /// <summary>Highest minus lowest node within 1 km of each node, in metres.</summary>
        public readonly float[] Rough;
        /// <summary>The highest node within 1.5 km of each node, in metres.</summary>
        public readonly float[] Peak;
        /// <summary>The lake a node lies in, or -1.</summary>
        public readonly int[] LakeOf;
        public readonly List<float> LakeLevels = new();
        /// <summary>Per lake: its area in m² (its nodes, shore included) and the deepest its bed gets (#298, <see cref="WaterBed.MaxDepthForArea"/>).</summary>
        public readonly List<double> LakeAreaM2 = new(), LakeMaxDepth = new();
        /// <summary>The node each node drains into, or -1 at an outlet on the edge.</summary>
        public readonly int[] Receiver;
        /// <summary>How many nodes drain through each node, itself included.</summary>
        public readonly int[] Area;

        public double NodeAreaKm2 => Spacing * Spacing / 1e6;

        private static readonly Lazy<Relief> Shared = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
        public static Relief Instance => Shared.Value;

        private Relief(int cols, int rows, double minE, double maxN, double spacing, ushort[] dm)
        {
            Cols = cols;
            Rows = rows;
            MinE = minE;
            MaxN = maxN;
            Spacing = spacing;
            Dm = dm;
            H = new float[dm.Length];
            for (int k = 0; k < dm.Length; k++) H[k] = dm[k] / 10f;
            Rough = BuildRough();
            Peak = BuildPeak(3);
            LakeOf = FindLakes();
            for (int l = 0; l < LakeLevels.Count; l++) LakeAreaM2.Add(0);
            foreach (int l in LakeOf)
                if (l >= 0) LakeAreaM2[l] += spacing * spacing;
            foreach (double a in LakeAreaM2) LakeMaxDepth.Add(WaterBed.MaxDepthForArea(a));
            (Receiver, Area) = Drain();
        }

        private static Relief Load()
        {
            using var raw = typeof(Relief).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            gz.CopyTo(bytes);
            var b = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
            if (b.Length < 28 || System.Text.Encoding.ASCII.GetString(b[..4]) != "SWRL")
                throw new InvalidDataException("not a relief file");
            int version = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
            if (version != 1) throw new InvalidDataException($"relief version {version}");
            int minE = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
            int maxN = BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
            int spacing = BinaryPrimitives.ReadInt32LittleEndian(b[16..]);
            int cols = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
            int rows = BinaryPrimitives.ReadInt32LittleEndian(b[24..]);
            var dm = new ushort[cols * rows];
            for (int k = 0; k < dm.Length; k++) dm[k] = BinaryPrimitives.ReadUInt16LittleEndian(b[(28 + 2 * k)..]);
            return new Relief(cols, rows, minE, maxN, spacing, dm);
        }

        public (double E, double N) NodePos(int k) => (MinE + (k % Cols) * Spacing, MaxN - (k / Cols) * Spacing);

        private float At(int c, int r) =>
            H[Math.Clamp(r, 0, Rows - 1) * Cols + Math.Clamp(c, 0, Cols - 1)];

        /// <summary>
        /// The macro height: Catmull-Rom across the nodes, so it is C1 and passes through every
        /// node. Past the edge of the map the edge nodes carry on outward.
        /// </summary>
        public double Macro(double e, double n)
        {
            double fx = (e - MinE) / Spacing, fy = (MaxN - n) / Spacing;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
            double u = fx - c, v = fy - r;
            double Row(int rr) => CatmullRom(At(c - 1, rr), At(c, rr), At(c + 1, rr), At(c + 2, rr), u);
            return CatmullRom(Row(r - 1), Row(r), Row(r + 1), Row(r + 2), v);
        }

        /// <summary>Local relief, bilinear between nodes.</summary>
        public double RoughAt(double e, double n) => Bilinear(Rough, e, n);

        /// <summary>How high the ground gets within about 1.5 km, bilinear between nodes.</summary>
        public double PeakAt(double e, double n) => Bilinear(Peak, e, n);

        /// <summary>
        /// How much of the neighbourhood is lake, bilinear between nodes (1 inside, 0 away from
        /// any), and the level of the lake it belongs to.
        /// </summary>
        public double LakeWeight(double e, double n, out double level) => LakeWeight(e, n, out level, out _);

        /// <summary><see cref="LakeWeight(double, double, out double)"/>, and which lake (-1: none).</summary>
        public double LakeWeight(double e, double n, out double level, out int lakeId)
        {
            double fx = (e - MinE) / Spacing, fy = (MaxN - n) / Spacing;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
            double u = fx - c, v = fy - r;
            level = 0;
            int lake = int.MaxValue;
            double w = 0;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    int cc = c + i, rr = r + j;
                    if (cc < 0 || rr < 0 || cc >= Cols || rr >= Rows) continue;
                    int l = LakeOf[rr * Cols + cc];
                    if (l < 0) continue;
                    w += (i == 0 ? 1 - u : u) * (j == 0 ? 1 - v : v);
                    lake = Math.Min(lake, l);
                }
            if (lake != int.MaxValue) level = LakeLevels[lake];
            lakeId = lake == int.MaxValue ? -1 : lake;
            return w;
        }

        private double Bilinear(float[] field, double e, double n)
        {
            double fx = (e - MinE) / Spacing, fy = (MaxN - n) / Spacing;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
            double u = fx - c, v = fy - r;
            float F(int cc, int rr) => field[Math.Clamp(rr, 0, Rows - 1) * Cols + Math.Clamp(cc, 0, Cols - 1)];
            double s = F(c, r) + (F(c + 1, r) - F(c, r)) * u;
            double t = F(c, r + 1) + (F(c + 1, r + 1) - F(c, r + 1)) * u;
            return s + (t - s) * v;
        }

        private float[] BuildRough()
        {
            var rough = new float[H.Length];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            float h = At(c + dc, r + dr);
                            lo = Math.Min(lo, h);
                            hi = Math.Max(hi, h);
                        }
                    rough[r * Cols + c] = hi - lo;
                }
            return rough;
        }

        /// <summary>A square max filter of ±radius nodes, separable: rows, then columns.</summary>
        private float[] BuildPeak(int radius)
        {
            var rows = new float[H.Length];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    float hi = float.MinValue;
                    for (int d = -radius; d <= radius; d++) hi = Math.Max(hi, At(c + d, r));
                    rows[r * Cols + c] = hi;
                }
            var peak = new float[H.Length];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    float hi = float.MinValue;
                    for (int d = -radius; d <= radius; d++)
                        hi = Math.Max(hi, rows[Math.Clamp(r + d, 0, Rows - 1) * Cols + c]);
                    peak[r * Cols + c] = hi;
                }
            return peak;
        }

        /// <summary>
        /// Lakes: the source models a lake as its surface, so averaged to 500 m a node well inside
        /// one is exactly flat with all its neighbours. Connected flat nodes of at least a few
        /// nodes are a lake at their median level, grown by the shore nodes within 3 dm of it
        /// (half land, half water, they average a little above).
        /// </summary>
        private int[] FindLakes()
        {
            int count = Cols * Rows;
            var core = new bool[count];
            for (int r = 1; r < Rows - 1; r++)
                for (int c = 1; c < Cols - 1; c++)
                {
                    int k = r * Cols + c;
                    bool flat = true;
                    for (int dr = -1; dr <= 1 && flat; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                            if (Math.Abs(Dm[k + dr * Cols + dc] - Dm[k]) > 1) { flat = false; break; }
                    core[k] = flat;
                }

            var lakeOf = new int[count];
            Array.Fill(lakeOf, -1);
            var queue = new Queue<int>();
            var members = new List<int>();
            Span<int> nb = stackalloc int[8];
            for (int start = 0; start < count; start++)
            {
                if (!core[start] || lakeOf[start] != -1) continue;
                members.Clear();
                int id = LakeLevels.Count;
                lakeOf[start] = id;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int k = queue.Dequeue();
                    members.Add(k);
                    int nc = Neighbours(k, nb, diagonals: false);
                    for (int q = 0; q < nc; q++)
                    {
                        int m = nb[q];
                        if (core[m] && lakeOf[m] == -1 && Math.Abs(Dm[m] - Dm[start]) <= 3)
                        {
                            lakeOf[m] = id;
                            queue.Enqueue(m);
                        }
                    }
                }
                if (members.Count < 4)
                {
                    foreach (int k in members) lakeOf[k] = -2;   // flat, but too small to be a lake
                    continue;
                }
                var levels = members.Select(k => Dm[k]).OrderBy(d => d).ToList();
                ushort level = levels[levels.Count / 2];
                LakeLevels.Add(level / 10f);
                // the shore: nodes next to the lake barely above its level
                foreach (int k in members)
                {
                    int nc = Neighbours(k, nb);
                    for (int q = 0; q < nc; q++)
                        if (lakeOf[nb[q]] < 0 && Dm[nb[q]] >= level - 1 && Dm[nb[q]] <= level + 3) lakeOf[nb[q]] = id;
                }
            }
            for (int k = 0; k < count; k++)
                if (lakeOf[k] < -1) lakeOf[k] = -1;
            return lakeOf;
        }

        private static readonly int[] Dc8 = [-1, 0, 1, -1, 1, -1, 0, 1];
        private static readonly int[] Dr8 = [-1, -1, -1, 0, 0, 1, 1, 1];

        /// <summary>The neighbours of a node, 8 or 4 of them, into a buffer; returns how many.</summary>
        private int Neighbours(int k, Span<int> into, bool diagonals = true)
        {
            int r = k / Cols, c = k % Cols, count = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!diagonals && Dc8[i] != 0 && Dr8[i] != 0) continue;
                int rr = r + Dr8[i], cc = c + Dc8[i];
                if (rr >= 0 && cc >= 0 && rr < Rows && cc < Cols) into[count++] = rr * Cols + cc;
            }
            return count;
        }

        /// <summary>
        /// Where the water goes: a priority flood from the map's edge (Barnes et al.), which drains
        /// every pit and flat without filling anything in first. Nodes come off the queue lowest
        /// first — by their flooded height, ties in the order they went in — and each node drains
        /// into the neighbour that reached it, so the drainage always leads out of the map. The pop
        /// order is a topological order, so the areas are one reverse pass.
        /// </summary>
        private (int[] Receiver, int[] Area) Drain()
        {
            int count = Cols * Rows;
            var receiver = new int[count];
            Array.Fill(receiver, -2);   // not reached yet
            var order = new int[count];
            int popped = 0;
            long seq = 0;
            var queue = new PriorityQueue<int, long>(count / 8);
            Span<int> nb = stackalloc int[8];
            void Push(int k, int floodDm) => queue.Enqueue(k, ((long)floodDm << 32) | seq++);

            for (int k = 0; k < count; k++)
            {
                int r = k / Cols, c = k % Cols;
                if (r == 0 || c == 0 || r == Rows - 1 || c == Cols - 1)
                {
                    receiver[k] = -1;
                    Push(k, Dm[k]);
                }
            }
            while (queue.TryDequeue(out int k, out long priority))
            {
                order[popped++] = k;
                int flood = (int)(priority >> 32);
                int nc = Neighbours(k, nb);
                for (int q = 0; q < nc; q++)
                {
                    int m = nb[q];
                    if (receiver[m] != -2) continue;
                    receiver[m] = k;
                    Push(m, Math.Max(Dm[m], flood));
                }
            }

            var area = new int[count];
            Array.Fill(area, 1);
            for (int i = popped - 1; i >= 0; i--)
            {
                int k = order[i];
                if (receiver[k] >= 0) area[receiver[k]] += area[k];
            }
            return (receiver, area);
        }
    }
}
