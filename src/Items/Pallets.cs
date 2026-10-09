using Godot;
using UnitSport.Avatar;

namespace UnitSport.Items;

/// <summary>What is stacked on a pallet, drawn by <c>InteriorMeshBuilder</c>'s goods (#497).</summary>
public enum PalletGoods : byte
{
    /// <summary>Two or three cartons, stacked a little untidily.</summary>
    Cartons,
    /// <summary>A pair of drums.</summary>
    Drums,
    /// <summary>Sacks, in two crossed courses.</summary>
    Sacks,
    /// <summary>A shrink-wrapped block.</summary>
    Wrapped,
}

/// <summary>Where a pallet comes from, read out of its id.</summary>
public enum PalletSource : byte
{
    /// <summary>A loose floor pallet a hall's plan put down: <c>"&lt;building&gt;:f&lt;furniture&gt;"</c>.</summary>
    Hall,
    /// <summary>One of a site's yard stacks (#583 phase 3): <c>"&lt;building&gt;:y&lt;slot&gt;"</c>.</summary>
    Yard,
    /// <summary>One somebody set down: <c>"#&lt;id&gt;"</c>, numbered by the server for the session.</summary>
    Loose,
    /// <summary>A building site's pallet of bricks or cement (#615): <c>"&lt;building&gt;:c&lt;slot&gt;"</c>.</summary>
    Site,
}

/// <summary>A pallet id taken apart.</summary>
public readonly record struct PalletRef(PalletSource Source, string Building, int Index, long Loose);

/// <summary>
/// A pallet you can pick up with a forklift (#583 phase 2), as pure rules: what its <b>load
/// byte</b> means, how its <b>id</b> is written, and the <b>fork rule</b> — when the forks are under
/// one, when raising them lifts it, and when lowering them sets it down. No button: driving the
/// forks in and raising them is the interaction. Godot maths only (tier 0, <c>PalletTests</c>);
/// the server's records are <c>PalletService</c>'s. See <c>docs/notes/vehicles/pallets.md</c>.
///
/// <para>
/// The load byte is the whole of a pallet's identity once it has left the plan: the low seven bits
/// are the roll the hall drew its goods from (<see cref="Roll"/>), the top bit says the deck is the
/// square 1.2 m one of an aisle rather than the 1.2 x 1.0 m Swiss pallet. A pallet of drums
/// stays a pallet of drums, and the same size, wherever it is put down.
/// </para>
/// </summary>
public static class Pallets
{
    /// <summary>The deck's length along its runners — the way the tines go in — m.</summary>
    public const float Length = 1.2f;
    /// <summary>The deck's depth across the runners: the Swiss pallet, and the square one of an aisle.</summary>
    public const float NarrowDepth = 1.0f, SquareDepth = 1.2f;
    /// <summary>A loaded pallet's height, deck and goods, m (every floor pallet a hall plans).</summary>
    public const float Height = 1.1f;
    /// <summary>The bare deck: runners and top boards, m.</summary>
    public const float Deck = 0.14f;

    /// <summary>The load byte's bit for the square deck.</summary>
    public const byte SquareBit = 0x80;

    /// <summary>
    /// The fork height at which the tines, run in under the deck, meet it and take its weight: a
    /// forked pallet is lifted there, and is drawn this far below the tines' top face while it
    /// rides on them, so it leaves the floor without a jump. Over <see cref="ForkliftLayout.SetDown"/>
    /// on purpose: a pallet just set down is not picked straight back up.
    /// </summary>
    public const float Seat = 0.15f;

    /// <summary>Faster than this the forks are driving past a pallet, not into it, m/s.</summary>
    public const float MaxForkSpeed = 2f;

    /// <summary>
    /// A Swiss or EUR pallet takes the tines from all four sides, but square to one: they run
    /// along its runners to within 30° (the cosine of the angle between them at least this), or
    /// across them to within 30° (at most <see cref="MaxAcross"/>). Half-way between, the tines
    /// would meet a corner block and push the pallet, not lift it.
    /// </summary>
    public const float MinAlong = 0.866f, MaxAcross = 0.5f;

    /// <summary>
    /// <see cref="Carried"/>'s bit for a pallet forked across its runners, so it rides the way it
    /// was picked up and is set down so, instead of turning a quarter on the forks.
    /// </summary>
    public const int AcrossBit = 0x100;

    // ---- the load byte ---------------------------------------------------------------------------

    /// <summary>A pallet's load byte, from the goods roll its hall drew it with and its deck's depth.</summary>
    public static byte LoadOf(float roll, float depth)
    {
        int bits = Mathf.Clamp((int)(roll * 128f), 0, 127);
        return (byte)(bits | (depth > (NarrowDepth + SquareDepth) * 0.5f ? SquareBit : 0));
    }

    /// <summary>The goods roll a load byte draws, in [0, 1): the middle of its 1/128 step.</summary>
    public static float Roll(byte load) => ((load & 0x7F) + 0.5f) / 128f;

    /// <summary>The deck's depth across its runners, m.</summary>
    public static float Depth(byte load) => (load & SquareBit) != 0 ? SquareDepth : NarrowDepth;

    /// <summary>What a goods roll stacks; the thresholds every pallet and rack bay is drawn with.</summary>
    public static PalletGoods GoodsOf(float roll) =>
        roll < 0.45f ? PalletGoods.Cartons : roll < 0.7f ? PalletGoods.Drums : roll < 0.88f ? PalletGoods.Sacks : PalletGoods.Wrapped;

    /// <summary>What a load byte stacks.</summary>
    public static PalletGoods Goods(byte load) => GoodsOf(Roll(load));

    /// <summary>
    /// What a forklift's <c>Carrying</c> holds for a load: 0 is empty forks, so a pallet is
    /// 1 + its load byte, plus <see cref="AcrossBit"/> when the tines went in across its runners
    /// (1..512, ten bits).
    /// </summary>
    public static int Carried(byte load, bool across = false) => 1 + load + (across ? AcrossBit : 0);

    /// <summary>The load on the forks, or null for empty ones.</summary>
    public static byte? LoadCarried(int carrying) => carrying is >= 1 and <= 2 * AcrossBit ? (byte)((carrying - 1) & 0xFF) : null;

    /// <summary>Whether the pallet on the forks was picked up across its runners.</summary>
    public static bool CarriedAcross(int carrying) => LoadCarried(carrying) != null && carrying - 1 >= AcrossBit;

    /// <summary>
    /// How a pallet sits on the forks, from <paramref name="along"/> (|cos| of the angle between
    /// the tines and its runners): along them, across them, or neither (null: not forkable so).
    /// </summary>
    public static bool? Across(float along) => along >= MinAlong ? false : along <= MaxAcross ? true : null;

    // ---- ids -------------------------------------------------------------------------------------

    /// <summary>A hall's own pallet: its building key and its index in the plan's furniture.</summary>
    public static string HallId(string building, int furniture) => $"{building}:f{furniture}";

    /// <summary>A yard pallet: its site's building key and its slot in the yard (phase 3).</summary>
    public static string YardId(string building, int slot) => $"{building}:y{slot}";

    /// <summary>A building site's pallet of materials (#615): its building key and its slot in the materials' row.</summary>
    public static string SiteId(string building, int slot) => $"{building}:c{slot}";

    /// <summary>A pallet somebody set down, by the server's number for it.</summary>
    public static string LooseId(long id) => "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads an id back; false for anything that is not one.</summary>
    public static bool TryParse(string? id, out PalletRef parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(id)) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (id[0] == '#')
        {
            if (!long.TryParse(id.AsSpan(1), System.Globalization.NumberStyles.None, inv, out long n) || n <= 0) return false;
            parsed = new PalletRef(PalletSource.Loose, "", -1, n);
            return true;
        }
        int colon = id.LastIndexOf(':');
        // a building key, a kind letter and at least one digit
        if (colon <= 0 || colon + 2 >= id.Length) return false;
        char kind = id[colon + 1];
        if (kind is not ('f' or 'y' or 'c')) return false;
        if (!int.TryParse(id.AsSpan(colon + 2), System.Globalization.NumberStyles.None, inv, out int index)) return false;
        parsed = new PalletRef(kind switch { 'f' => PalletSource.Hall, 'y' => PalletSource.Yard, _ => PalletSource.Site }, id[..colon], index, 0);
        return true;
    }

    // ---- the fork rule ---------------------------------------------------------------------------

    /// <summary>
    /// Whether a pallet is on the forks: its centre (<paramref name="x"/>, <paramref name="z"/>, in
    /// the forklift's own authored frame, +Z ahead) inside the rectangle the tines sweep, the
    /// carriage below <see cref="ForkliftLayout.ForkEntry"/> so the tines went in under the deck,
    /// <paramref name="along"/> (|cos| of the angle between the tines and the pallet's runners)
    /// square to one of its sides (<see cref="Across"/>), and the machine under <see cref="MaxForkSpeed"/>.
    /// </summary>
    public static bool Forked(float x, float z, float along, float lift, float speed) =>
        OnTines(x, z - ForkliftLayout.MastZ, along, lift, speed, ForkliftLayout.TineLength, ForkliftLayout.ForkHalfSpan);

    /// <summary>
    /// The fork rule for any machine with tines (#615: the telehandler, the loader's forks, the
    /// forklift): a pallet's centre <paramref name="x"/> across the tines' middle and
    /// <paramref name="ahead"/> of their heel (the carriage's face), in the tines' own frame, inside
    /// the <paramref name="length"/> by twice <paramref name="halfSpan"/> they sweep; the tines'
    /// top face <paramref name="height"/> over the ground under <see cref="ForkliftLayout.ForkEntry"/>,
    /// so they went in under the deck; square to one of its sides; the machine under <see cref="MaxForkSpeed"/>.
    /// </summary>
    public static bool OnTines(float x, float ahead, float along, float height, float speed, float length, float halfSpan) =>
        height < ForkliftLayout.ForkEntry
        && speed < MaxForkSpeed
        && Across(along) != null
        && Mathf.Abs(x) <= halfSpan
        && ahead >= 0f && ahead <= length;

    // ---- the bucket rule (#615): a loader scoops a pallet up, and dumps it ---------------------

    /// <summary>Under this over the ground, the bucket's floor is down to scoop, m (a bucket on the ground digs in a little).</summary>
    public const float ScoopHeight = 0.4f;

    /// <summary>
    /// The bucket's pitch from level (+ rolled back), rad: past <see cref="CurlCarry"/> with a pallet
    /// in it, it holds it; past <see cref="DumpDrop"/> the other way, it tips it out. Far apart, so a
    /// pallet just scooped is not dropped by the bucket settling, nor one just dropped scooped again.
    /// </summary>
    public const float CurlCarry = 0.35f, DumpDrop = -0.4f;

    public static bool Curled(float pitch) => pitch >= CurlCarry;
    public static bool Dumped(float pitch) => pitch <= DumpDrop;

    /// <summary>
    /// Whether a pallet is in a bucket: its centre <paramref name="x"/> across the bucket's middle,
    /// <paramref name="ahead"/> of the pin it hangs from, <paramref name="up"/> over its floor, in
    /// the bucket's frame; within the bucket's width less half a deck, out to its lip and a little
    /// past (a pallet's 1.2 m is near a bucket's depth), and down on its floor.
    /// </summary>
    public static bool InBucket(float x, float ahead, float up, float halfWidth, float reach) =>
        Mathf.Abs(x) <= halfWidth - 0.45f && ahead >= 0.1f && ahead <= reach + 0.5f && Mathf.Abs(up) <= 0.6f;

    /// <summary>
    /// How far ahead of the tines' heel a carried pallet's centre rides, m: a pallet's length out from
    /// the face and 0.12 m clear of it, as <see cref="ForkliftLayout.LoadCentre"/> puts the forklift's.
    /// </summary>
    public const float LoadAhead = 0.12f + Length * 0.5f;

    /// <summary>Raised this far, the forks under a pallet have lifted it off the floor.</summary>
    public static bool Lifts(float lift) => lift >= Seat;

    /// <summary>Lowered under this, the forks have put their pallet down.</summary>
    public static bool SetsDown(float lift) => lift < ForkliftLayout.SetDown;
}
