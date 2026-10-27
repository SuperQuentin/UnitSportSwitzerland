using System.Text;

namespace UnitSport.Audio.Tracker;

/// <summary>
/// A tracker song read from a ProTracker <c>.mod</c> or a FastTracker 2 <c>.xm</c> file (#1244): the
/// venues' music when the radio relay has none. One model for both: a MOD is read as an XM in Amiga
/// frequency mode, one instrument per sample. Pure C#, no Godot, so the unit tests can read it.
/// Formats: the XM 1.04 document (Mr.H, 1994) and the ProTracker 2.3 module layout.
/// </summary>
public sealed class TrackerModule
{
    public string Title = "";
    public int Channels;
    /// <summary>Linear frequencies (XM flag 1), else Amiga periods.</summary>
    public bool Linear;
    public bool IsMod;
    public int[] Order = Array.Empty<int>();
    public int Restart;
    public int Speed = 6, Bpm = 125;
    public Pattern[] Patterns = Array.Empty<Pattern>();
    /// <summary>1-based in the patterns; index 0 here is instrument 1.</summary>
    public Instrument[] Instruments = Array.Empty<Instrument>();

    /// <summary>One row of one channel. Note 0 none, 1..96 a note (1 = C-0), 97 key off.</summary>
    public struct Cell
    {
        public byte Note, Instrument, Volume, Effect, Param;
    }

    public sealed class Pattern
    {
        public int Rows;
        public Cell[] Cells = Array.Empty<Cell>();   // row * channels + channel
    }

    public sealed class Envelope
    {
        public (int Tick, int Value)[] Points = Array.Empty<(int, int)>();
        public bool On, Sustain, Loop;
        public int SustainPoint, LoopStart, LoopEnd;
    }

    public sealed class Sample
    {
        /// <summary>Samples in -1..1.</summary>
        public float[] Data = Array.Empty<float>();
        public int LoopStart, LoopLength;
        /// <summary>0 none, 1 forward, 2 ping-pong.</summary>
        public int LoopType;
        public int Volume = 64, Panning = 128, RelativeNote, FineTune;
    }

    public sealed class Instrument
    {
        public Sample[] Samples = Array.Empty<Sample>();
        /// <summary>For each of the 96 notes, the sample it plays.</summary>
        public byte[] NoteSample = new byte[96];
        public Envelope Volume = new(), Panning = new();
        public int Fadeout;
    }

    /// <summary>Reads a module, telling the format by its header; null for anything else.</summary>
    public static TrackerModule? Load(byte[] b)
    {
        if (b.Length > 60 && Encoding.ASCII.GetString(b, 0, 17) == "Extended Module: ") return LoadXm(b);
        if (b.Length > 1084 && ModChannels(Encoding.ASCII.GetString(b, 1080, 4)) is int ch && ch > 0) return LoadMod(b, ch);
        return null;
    }

    private static int? ModChannels(string sig) => sig switch
    {
        "M.K." or "M!K!" or "FLT4" or "4CHN" => 4,
        "6CHN" => 6,
        "8CHN" or "OCTA" or "FLT8" or "CD81" => 8,
        _ when sig.Length == 4 && sig.EndsWith("CH") && int.TryParse(sig[..2], out int n) => n,
        _ when sig.Length == 4 && sig.EndsWith("CHN") && int.TryParse(sig[..1], out int n) => n,
        _ => null,
    };

    private static string Text(byte[] b, int at, int len) =>
        Encoding.Latin1.GetString(b, at, Math.Min(len, Math.Max(0, b.Length - at))).TrimEnd('\0', ' ');

    // ---- ProTracker --------------------------------------------------------------------------

    /// <summary>The ProTracker periods of C-1 .. B-3 (finetune 0), to turn a cell's period into a note.</summary>
    private static readonly int[] ModPeriods =
    {
        856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
        428, 404, 381, 360, 340, 320, 302, 285, 269, 254, 240, 226,
        214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
    };

    private static TrackerModule LoadMod(byte[] b, int channels)
    {
        var m = new TrackerModule { Title = Text(b, 0, 20), Channels = channels, IsMod = true, Linear = false };
        int Word(int at) => at + 1 < b.Length ? (b[at] << 8) | b[at + 1] : 0;
        var samples = new Sample[31];
        var lengths = new int[31];
        for (int i = 0; i < 31; i++)
        {
            int at = 20 + i * 30;
            lengths[i] = Word(at + 22) * 2;
            int ft = b[at + 24] & 0x0F;
            if (ft > 7) ft -= 16;
            int loopStart = Word(at + 26) * 2, loopLen = Word(at + 28) * 2;
            samples[i] = new Sample
            {
                Volume = Math.Min(64, (int)b[at + 25]),
                FineTune = ft * 16,
                LoopStart = loopStart,
                LoopLength = loopLen > 2 ? loopLen : 0,
                LoopType = loopLen > 2 ? 1 : 0,
            };
        }
        int songLength = Math.Clamp((int)b[950], 1, 128);
        m.Restart = b[951] < songLength ? b[951] : 0;
        m.Order = Enumerable.Range(0, songLength).Select(i => (int)b[952 + i]).ToArray();
        int patterns = Enumerable.Range(0, 128).Max(i => (int)b[952 + i]) + 1;
        int pos = 1084;
        m.Patterns = new Pattern[patterns];
        for (int p = 0; p < patterns; p++)
        {
            var pat = new Pattern { Rows = 64, Cells = new Cell[64 * channels] };
            for (int k = 0; k < 64 * channels; k++, pos += 4)
            {
                if (pos + 3 >= b.Length) break;
                int sample = (b[pos] & 0xF0) | (b[pos + 2] >> 4);
                int period = ((b[pos] & 0x0F) << 8) | b[pos + 1];
                var cell = new Cell { Instrument = (byte)sample, Effect = (byte)(b[pos + 2] & 0x0F), Param = b[pos + 3] };
                if (period > 0)
                {
                    // the nearest period of the table: C-1 of ProTracker is C-4 of FastTracker (note 49)
                    int best = 0;
                    for (int n = 1; n < ModPeriods.Length; n++)
                        if (Math.Abs(ModPeriods[n] - period) < Math.Abs(ModPeriods[best] - period)) best = n;
                    cell.Note = (byte)(49 + best);
                }
                pat.Cells[k] = cell;
            }
            m.Patterns[p] = pat;
        }
        for (int i = 0; i < 31; i++)
        {
            int len = Math.Min(lengths[i], Math.Max(0, b.Length - pos));
            var data = new float[len];
            for (int k = 0; k < len; k++) data[k] = (sbyte)b[pos + k] / 128f;
            pos += lengths[i];
            samples[i].Data = data;
            if (samples[i].LoopStart + samples[i].LoopLength > len)
            {
                samples[i].LoopLength = Math.Max(0, len - samples[i].LoopStart);
                if (samples[i].LoopLength <= 2) samples[i].LoopType = 0;
            }
        }
        m.Instruments = samples.Select(s => new Instrument { Samples = new[] { s } }).ToArray();
        return m;
    }

    // ---- FastTracker 2 -----------------------------------------------------------------------

    private static TrackerModule? LoadXm(byte[] b)
    {
        int U16(int at) => at + 1 < b.Length ? b[at] | (b[at + 1] << 8) : 0;
        int S32(int at) => at + 3 < b.Length ? b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24) : 0;
        var m = new TrackerModule { Title = Text(b, 17, 20) };
        int header = S32(60);
        int songLength = U16(64);
        m.Restart = U16(66);
        m.Channels = Math.Clamp(U16(68), 1, 64);
        int patterns = U16(70), instruments = U16(72);
        m.Linear = (U16(74) & 1) != 0;
        m.Speed = Math.Max(1, U16(76));
        m.Bpm = Math.Max(32, U16(78));
        m.Order = Enumerable.Range(0, Math.Clamp(songLength, 1, 256)).Select(i => (int)b[80 + i]).ToArray();
        if (m.Restart >= m.Order.Length) m.Restart = 0;

        int pos = 60 + header;
        m.Patterns = new Pattern[patterns];
        for (int p = 0; p < patterns; p++)
        {
            int ph = S32(pos);
            int rows = Math.Clamp(U16(pos + 5), 1, 256);
            int packed = U16(pos + 7);
            pos += ph;
            var pat = new Pattern { Rows = rows, Cells = new Cell[rows * m.Channels] };
            int end = Math.Min(b.Length, pos + packed), k = 0;
            while (pos < end && k < pat.Cells.Length)
            {
                var c = new Cell();
                int flag = b[pos];
                if ((flag & 0x80) != 0)
                {
                    pos++;
                    if ((flag & 1) != 0) c.Note = b[pos++];
                    if ((flag & 2) != 0) c.Instrument = b[pos++];
                    if ((flag & 4) != 0) c.Volume = b[pos++];
                    if ((flag & 8) != 0) c.Effect = b[pos++];
                    if ((flag & 16) != 0) c.Param = b[pos++];
                }
                else
                {
                    if (pos + 4 >= b.Length) break;
                    c.Note = b[pos]; c.Instrument = b[pos + 1]; c.Volume = b[pos + 2]; c.Effect = b[pos + 3]; c.Param = b[pos + 4];
                    pos += 5;
                }
                pat.Cells[k++] = c;
            }
            pos = end;
            m.Patterns[p] = pat;
        }
        // an order naming a pattern the file has not: an empty one of 64 rows
        int maxOrder = m.Order.Max();
        if (maxOrder >= m.Patterns.Length)
            m.Patterns = m.Patterns.Concat(Enumerable.Range(0, maxOrder + 1 - m.Patterns.Length)
                .Select(_ => new Pattern { Rows = 64, Cells = new Cell[64 * m.Channels] })).ToArray();

        m.Instruments = new Instrument[instruments];
        for (int i = 0; i < instruments; i++)
        {
            if (pos + 29 > b.Length) { m.Instruments[i] = new Instrument(); continue; }
            int ih = S32(pos);
            int count = U16(pos + 27);
            var inst = new Instrument();
            int sampleHeader = 40;
            if (count > 0)
            {
                sampleHeader = S32(pos + 29);
                Array.Copy(b, pos + 33, inst.NoteSample, 0, Math.Min(96, Math.Max(0, b.Length - pos - 33)));
                inst.Volume = Env(pos + 129, b[pos + 225], b[pos + 227], b[pos + 228], b[pos + 229], b[pos + 233]);
                inst.Panning = Env(pos + 177, b[pos + 226], b[pos + 230], b[pos + 231], b[pos + 232], b[pos + 234]);
                inst.Fadeout = U16(pos + 239);
            }
            pos += ih;
            var heads = new (int Length, Sample S, bool Bits16)[count];
            for (int s = 0; s < count; s++)
            {
                int at = pos + s * sampleHeader;
                int type = b[at + 14];
                bool bits16 = (type & 16) != 0;
                int len = S32(at), ls = S32(at + 4), ll = S32(at + 8);
                if (bits16) { len /= 2; ls /= 2; ll /= 2; }
                heads[s] = (len, new Sample
                {
                    LoopStart = ls,
                    LoopLength = ll,
                    LoopType = ll > 0 ? type & 3 : 0,
                    Volume = Math.Min(64, (int)b[at + 12]),
                    FineTune = (sbyte)b[at + 13],
                    Panning = b[at + 15],
                    RelativeNote = (sbyte)b[at + 16],
                }, bits16);
            }
            pos += count * sampleHeader;
            inst.Samples = new Sample[count];
            for (int s = 0; s < count; s++)
            {
                var (len, sample, bits16) = heads[s];
                len = Math.Max(0, Math.Min(len, (b.Length - pos) / (bits16 ? 2 : 1)));
                var data = new float[len];
                // delta-coded, as FastTracker writes them
                if (bits16)
                {
                    short acc = 0;
                    for (int k = 0; k < len; k++) { acc = unchecked((short)(acc + U16(pos + k * 2))); data[k] = acc / 32768f; }
                    pos += len * 2;
                }
                else
                {
                    sbyte acc = 0;
                    for (int k = 0; k < len; k++) { acc = unchecked((sbyte)(acc + b[pos + k])); data[k] = acc / 128f; }
                    pos += len;
                }
                sample.Data = data;
                if (sample.LoopStart >= len || sample.LoopLength <= 0) sample.LoopType = 0;
                else sample.LoopLength = Math.Min(sample.LoopLength, len - sample.LoopStart);
                inst.Samples[s] = sample;
            }
            m.Instruments[i] = inst;
        }
        return m;

        Envelope Env(int at, int points, int sustain, int loopStart, int loopEnd, int flags)
        {
            var e = new Envelope
            {
                On = (flags & 1) != 0, Sustain = (flags & 2) != 0, Loop = (flags & 4) != 0,
                SustainPoint = sustain, LoopStart = loopStart, LoopEnd = loopEnd,
                Points = Enumerable.Range(0, Math.Clamp(points, 0, 12)).Select(k => (U16(at + k * 4), U16(at + k * 4 + 2))).ToArray(),
            };
            if (e.Points.Length == 0) e.On = false;
            return e;
        }
    }
}
