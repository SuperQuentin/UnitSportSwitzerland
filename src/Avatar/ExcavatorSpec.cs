using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// One size of tracked excavator (#611, #614): the numbers its ride, its arm node and its checks
/// work from. <see cref="ExcavatorLayout.Spec"/> is the 20 t crawler, <see cref="MiniExcavatorLayout.Spec"/>
/// the 2.7 t mini with its dozer blade. The two share everything but these numbers and their
/// drawn bodies. AUTHORED frame like every avatar mesh: +Z forward (the arm's way), +X the left
/// side, origin on the ground under the slewing ring. Metres and radians.
///
/// <para>
/// Godot maths only, so the unit tests link it.
/// </para>
/// </summary>
public sealed class ExcavatorSpec
{
    // ---- the undercarriage ------------------------------------------------------------------
    /// <summary>The tracks: half their length, the half gauge (track centre to centre), a shoe's width, their height.</summary>
    public required float TrackHalfLength { get; init; }
    public required float HalfGauge { get; init; }
    public required float ShoeWidth { get; init; }
    public required float TrackHeight { get; init; }
    /// <summary>The slewing ring's top: where the upper structure stands.</summary>
    public required float RingTop { get; init; }

    // ---- the upper structure ----------------------------------------------------------------
    /// <summary>The house: half its width, its front and back (the counterweight's face), its roof.</summary>
    public required float HouseHalf { get; init; }
    public required float HouseFront { get; init; }
    public required float HouseBack { get; init; }
    public required float HouseTop { get; init; }

    // ---- the arm ----------------------------------------------------------------------------
    /// <summary>The boom's foot pivot, authored.</summary>
    public required Vector3 BoomFoot { get; init; }
    /// <summary>Boom, stick and bucket (pivot to cutting edge), m.</summary>
    public required float BoomLength { get; init; }
    public required float StickLength { get; init; }
    public required float BucketLength { get; init; }

    /// <summary>
    /// The joints' travel, rad. The boom from the horizontal, up positive; the stick and the
    /// bucket each from the line of the part before, down (folded in) negative.
    /// </summary>
    public required float BoomMin { get; init; }
    public required float BoomMax { get; init; }
    public required float StickMin { get; init; }
    public required float StickMax { get; init; }
    public required float BucketMin { get; init; }
    public required float BucketMax { get; init; }

    /// <summary>How fast each moves at full stick, rad/s.</summary>
    public required float SlewRate { get; init; }
    public required float BoomRate { get; init; }
    public required float StickRate { get; init; }
    public required float BucketRate { get; init; }

    /// <summary>The arm as it is parked and delivered.</summary>
    public required float RestBoom { get; init; }
    public required float RestStick { get; init; }
    public required float RestBucket { get; init; }

    // ---- the dozer blade (a mini's): its arms' turn about their pivot, + raises it -----------
    /// <summary>Whether it has a dozer blade at the front of its undercarriage.</summary>
    public bool HasBlade { get; init; }
    /// <summary>The blade's travel, rad (0: its edge on the ground; below that it cuts in), and its rate, rad/s.</summary>
    public float BladeMin { get; init; }
    public float BladeMax { get; init; }
    public float BladeRate { get; init; }

    // ---- the tracks -------------------------------------------------------------------------
    /// <summary>Top speed on the tracks either way, m/s, and how fast it gets there.</summary>
    public required float TopSpeed { get; init; }
    public required float Accel { get; init; }
    /// <summary>A full counter-rotation of the tracks, turning on the spot, rad/s.</summary>
    public required float TurnRate { get; init; }

    // ---- what a parked one keeps ------------------------------------------------------------
    /// <summary>
    /// The bits of <c>VehicleState.Flags</c> each of slew, boom, stick, bucket and blade gets; 32 in
    /// all. The big one's are 8, 8, 8, 8 and none, which is its packing from #611 unchanged.
    /// </summary>
    public required (int Slew, int Boom, int Stick, int Bucket, int Blade) Bits { get; init; }

    public float ClampBoom(float a) => Mathf.Clamp(a, BoomMin, BoomMax);
    public float ClampStick(float a) => Mathf.Clamp(a, StickMin, StickMax);
    public float ClampBucket(float a) => Mathf.Clamp(a, BucketMin, BucketMax);
    public float ClampBlade(float a) => HasBlade ? Mathf.Clamp(a, BladeMin, BladeMax) : 0f;

    /// <summary>
    /// Where the bucket's cutting edge is in the arm's own plane: metres forward of the slewing
    /// ring and up from the ground, for these joint angles.
    /// </summary>
    public Vector2 Edge(float boom, float stick, float bucket)
    {
        float a1 = boom, a2 = a1 + stick, a3 = a2 + bucket;
        var p = new Vector2(BoomFoot.Z, BoomFoot.Y);
        p += new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * BoomLength;
        p += new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * StickLength;
        p += new Vector2(Mathf.Cos(a3), Mathf.Sin(a3)) * BucketLength;
        return p;
    }

    /// <summary>
    /// The tracks' speeds, m/s, for a travel speed and a turn rate (+ turning left): on the spot,
    /// one runs back as the other runs forward.
    /// </summary>
    public (float Left, float Right) Tracks(float speed, float yawRate) =>
        (speed - yawRate * HalfGauge, speed + yawRate * HalfGauge);

    /// <summary>
    /// The slew, the joints and the blade in 32 bits, each over its travel in <see cref="Bits"/>.
    /// Zero is "never set": every field packs to at least 1, so a fresh machine parks in its rest pose.
    /// </summary>
    public int Pack(float slew, float boom, float stick, float bucket, float blade = 0f)
    {
        int at = 0, word = 0;
        void Put(float v, float lo, float hi, int bits)
        {
            if (bits == 0) return;
            int top = (1 << bits) - 2;
            int q = Mathf.Clamp(Mathf.RoundToInt((v - lo) / (hi - lo) * top), 0, top) + 1;
            word |= q << at;
            at += bits;
        }
        Put(Mathf.PosMod(slew, Mathf.Tau), 0f, Mathf.Tau, Bits.Slew);
        Put(boom, BoomMin, BoomMax, Bits.Boom);
        Put(stick, StickMin, StickMax, Bits.Stick);
        Put(bucket, BucketMin, BucketMax, Bits.Bucket);
        if (HasBlade) Put(blade, BladeMin, BladeMax, Bits.Blade);
        return word;
    }

    /// <summary>What a packed word holds, or the rest pose (blade down) for zero.</summary>
    public (float Slew, float Boom, float Stick, float Bucket, float Blade) Unpack(int flags)
    {
        if (flags == 0) return (0f, RestBoom, RestStick, RestBucket, 0f);
        int at = 0;
        float Get(float lo, float hi, int bits)
        {
            if (bits == 0) return 0f;
            int mask = (1 << bits) - 1, top = (1 << bits) - 2;
            int q = Mathf.Clamp((int)((uint)flags >> at & (uint)mask), 1, top + 1) - 1;
            at += bits;
            return lo + q / (float)top * (hi - lo);
        }
        float slew = Get(0f, Mathf.Tau, Bits.Slew);
        float boom = Get(BoomMin, BoomMax, Bits.Boom);
        float stick = Get(StickMin, StickMax, Bits.Stick);
        float bucket = Get(BucketMin, BucketMax, Bits.Bucket);
        float blade = HasBlade ? Get(BladeMin, BladeMax, Bits.Blade) : 0f;
        return (slew > Mathf.Pi ? slew - Mathf.Tau : slew, boom, stick, bucket, blade);
    }

    /// <summary>The blade's steps on the wire: 32, about 1.4° of a mini's blade each.</summary>
    public const int BladeSteps = 31;

    /// <summary>
    /// The bucket and the blade in one float, for the pose a copy is drawn from (four floats, all
    /// taken by the slew and the joints): the bucket, which never leaves ±4 rad, plus 8 for each
    /// step of the blade. Without a blade it is the bucket exactly, as #611 sent it.
    /// </summary>
    public float PoseW(float bucket, float blade)
    {
        if (!HasBlade) return bucket;
        int step = Mathf.Clamp(Mathf.RoundToInt((blade - BladeMin) / (BladeMax - BladeMin) * BladeSteps), 0, BladeSteps);
        return bucket + 8f * step;
    }

    /// <summary>The bucket and the blade back out of <see cref="PoseW"/>.</summary>
    public (float Bucket, float Blade) FromPoseW(float w)
    {
        if (!HasBlade) return (w, 0f);
        int step = Mathf.Clamp(Mathf.FloorToInt((w + 4f) / 8f), 0, BladeSteps);
        return (w - 8f * step, BladeMin + step / (float)BladeSteps * (BladeMax - BladeMin));
    }
}
