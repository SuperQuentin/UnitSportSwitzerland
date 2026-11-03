using Godot;

namespace UnitSport.Player;

/// <summary>
/// A road bike's gears (#1124): the spec's chainrings and cassette, the gear the chain is in, the
/// cadence it gives at a speed, and Shimano Di2's synchronized shifting doing the choosing. Pure, so
/// the unit tests can ride it without Godot.
///
/// <para>
/// Synchro shift is what a Di2 rider actually has: the rider only ever asks for "harder" or "easier"
/// and the system picks the ring. Running out of small cogs on the inner ring jumps to the outer one
/// and moves the rear two cogs easier, so the step is small; running into the big cogs on the outer
/// ring does the opposite. Here the rider's thumb is a cadence window as well: a fit rider spins
/// 80-97 rpm and shifts when they leave it.
/// </para>
/// </summary>
public sealed class BikeDrivetrain
{
    /// <summary>Cadence the automatic shifting keeps between, rpm.</summary>
    public const float ShiftUpAbove = 97f, ShiftDownBelow = 80f;

    /// <summary>
    /// Time between shifts, s: a Di2 rear derailleur moves one cog in ~0.1 s and the chain needs a
    /// pedal stroke to settle, so a rider shifting through the block goes a cog every quarter second.
    /// </summary>
    public const float ShiftInterval = 0.25f;

    /// <summary>On the inner ring, shifting harder from this cog (and smaller) goes to the outer ring.</summary>
    private const int ToOuterAtCog = 15;
    /// <summary>On the outer ring, shifting easier into this cog (and bigger) goes to the inner ring.</summary>
    private const int ToInnerAtCog = 23;
    /// <summary>Cogs the rear moves the other way when the front changes, so the step stays small.</summary>
    private const int Compensate = 2;

    private readonly int[] _rings;
    private readonly int[] _cogs;
    private readonly float _circumference;
    private float _sinceShift;

    /// <summary>Index into the chainrings: 0 the big (outer) ring.</summary>
    public int Front { get; private set; }

    /// <summary>Index into the cassette: 0 the smallest (hardest) cog.</summary>
    public int Rear { get; private set; }

    public int RingTeeth => _rings[Front];
    public int CogTeeth => _cogs[Rear];

    /// <summary>Wheel turns per crank turn.</summary>
    public float Ratio => (float)RingTeeth / CogTeeth;

    /// <summary>Distance per crank turn, m: what riders call development (52×11 is 10 m, 36×28 is 2.7 m).</summary>
    public float Development => Ratio * _circumference;

    /// <summary>
    /// A bike's gears, in the gear a rider sets off in on the flat: the inner ring and the middle of
    /// the cassette.
    /// </summary>
    /// <param name="chainrings">Teeth, any order (big first or not).</param>
    /// <param name="cassette">Teeth, any order.</param>
    public BikeDrivetrain(int[] chainrings, int[] cassette, float circumference)
    {
        _rings = (int[])chainrings.Clone();
        System.Array.Sort(_rings, (a, b) => b.CompareTo(a));
        _cogs = (int[])cassette.Clone();
        System.Array.Sort(_cogs);
        _circumference = circumference;
        Front = _rings.Length - 1;
        Rear = _cogs.Length / 2;
    }

    public BikeDrivetrain(BicycleSpec spec) : this(spec.Chainrings, spec.Cassette, spec.Circumference) { }

    /// <summary>Crank rpm at <paramref name="speed"/> m/s in the current gear.</summary>
    public float Cadence(float speed) => speed / Development * 60f;

    /// <summary>Puts the chain on a gear: a scripted start, a test.</summary>
    public void Set(int front, int rear)
    {
        Front = Mathf.Clamp(front, 0, _rings.Length - 1);
        Rear = Mathf.Clamp(rear, 0, _cogs.Length - 1);
    }

    /// <summary>One cog harder, the ring changed for it as synchro shift does. False at 52×11.</summary>
    public bool ShiftHarder()
    {
        bool inner = Front > 0;
        if (inner && CogTeeth <= ToOuterAtCog)
        {
            Front--;
            Rear = Mathf.Min(Rear + Compensate, _cogs.Length - 1);
            return true;
        }
        if (Rear == 0)
        {
            if (!inner) return false;
            Front--;
            Rear = Mathf.Min(Compensate, _cogs.Length - 1);
            return true;
        }
        Rear--;
        return true;
    }

    /// <summary>One cog easier, the ring changed for it as synchro shift does. False at 36×28.</summary>
    public bool ShiftEasier()
    {
        bool outer = Front < _rings.Length - 1;
        if (outer && Rear + 1 < _cogs.Length && _cogs[Rear + 1] >= ToInnerAtCog)
        {
            Front++;
            Rear = Mathf.Max(Rear - Compensate, 0);
            return true;
        }
        if (Rear == _cogs.Length - 1)
        {
            if (!outer) return false;
            Front++;
            Rear = Mathf.Max(_cogs.Length - 1 - Compensate, 0);
            return true;
        }
        Rear++;
        return true;
    }

    /// <summary>
    /// The automatic shifting, once a frame: out of the cadence window, one shift toward it, no more
    /// often than <see cref="ShiftInterval"/>. Only with the cranks turning, as on a real bike: the
    /// chain cannot move across the cogs while the rider freewheels.
    /// </summary>
    /// <returns>Whether it shifted.</returns>
    public bool AutoShift(float speed, bool pedalling, float dt)
    {
        _sinceShift += dt;
        if (!pedalling || _sinceShift < ShiftInterval) return false;
        float rpm = Cadence(speed);
        bool shifted = rpm > ShiftUpAbove ? ShiftHarder() : rpm < ShiftDownBelow && ShiftEasier();
        if (shifted) _sinceShift = 0f;
        return shifted;
    }

    /// <summary>
    /// The share of a rider's power left at <paramref name="rpm"/>: 1 at 90 rpm, 0.86 at 60, three
    /// quarters at 130 (sustained power against cadence, e.g. Foss &amp; Hallén 2004 for trained
    /// riders). Below 60 rpm the pedal force cap takes over (<c>Bicycle</c>), so the curve stops there.
    /// </summary>
    public static float PowerAtCadence(float rpm)
    {
        float off = (Mathf.Clamp(rpm, 60f, 130f) - 90f) / 40f;
        return 1f - 0.25f * off * off;
    }

    /// <summary>"52×17", for the HUD.</summary>
    public override string ToString() => $"{RingTeeth}×{CogTeeth}";
}
