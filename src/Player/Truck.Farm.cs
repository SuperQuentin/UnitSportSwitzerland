using Godot;
using UnitSport.Avatar;
using UnitSport.Farming;

namespace UnitSport.Player;

/// <summary>
/// The farm machines' own state (#494): an implement or header lowered (the bus's kneel bit), a
/// combine's grain tank (in its pose flags), a tipping trailer's harvest (in its code), the soil's
/// pull on a lowered implement and the combine's working speed.
/// </summary>
public sealed partial class Truck
{
    /// <summary>The implement or the header is down working: replicated and parked as the kneel bit.</summary>
    public bool Lowered { get => Kneeling; set => Kneeling = value; }

    /// <summary>The combine's unloading auger swung out over a trailer: a door bit no cab door uses (the fourth).</summary>
    public const byte AugerBit = 8;
    public bool AugerOut
    {
        get => (DoorsOpen & AugerBit) != 0;
        set => DoorsOpen = (byte)(value ? DoorsOpen | AugerBit : DoorsOpen & ~AugerBit);
    }

    /// <summary>The mounted implement on the linkage (plough, drill, mower), or null.</summary>
    public TrailerSpec? Implement => Trailer is { Mounted: true } ? Trailer : null;

    /// <summary>
    /// The lowered implement's bar is over soil (a field, open ground), not over a road or paving:
    /// set by the driver's peer before each step (<c>FootPlayer.SenseSoil</c>). Off the soil it
    /// neither pulls nor works.
    /// </summary>
    public bool OnSoil { get; set; } = true;

    /// <summary>What works the ground now: the combine's header or the implement, lowered (and over soil); None raised.</summary>
    public FarmTool WorkTool => !Spec.Farm || !Lowered ? FarmTool.None
        : Spec.Tool != FarmTool.None ? Spec.Tool : OnSoil ? Implement?.Tool ?? FarmTool.None : FarmTool.None;

    /// <summary>The working width of the header or the implement, m.</summary>
    public float WorkWidth => Spec.Tool != FarmTool.None ? Spec.WorkWidth : Implement?.WorkWidth ?? 0f;

    /// <summary>
    /// The working bar's middle (the cutter bar, the plough's bodies, the coulters), in the first
    /// section's node space, on the ground. Zero without one.
    /// </summary>
    public Vector3 WorkBarNode
    {
        get
        {
            if (Spec.Tool != FarmTool.None) return new Vector3(0f, 0f, -(Train.Bodies[0].CgAt - Spec.WorkAt));
            if (Implement is not { } imp) return Vector3.Zero;
            int k = SectionCount - 1;
            return NodeLocal(k) * new Vector3(0f, 0f, -(Train.Bodies[k].CgAt - imp.WorkAt));
        }
    }

    // ---- tanks ----

    /// <summary>The combine's grain tank: whole sacks (replicated in the pose flags) and the fraction toward the next (owner only).</summary>
    public Tank Tank { get; private set; }
    public int TankCapacity => Spec.TankItems;

    /// <summary>The coupled tipping trailer's harvest, from its code (empty without one).</summary>
    public Tank TrailerTank => TrailerCatalog.TankOf(TrailerCode);
    public int TrailerCapacity => Trailer?.TankItems ?? 0;

    /// <summary>
    /// The combine's tank: its weight follows the sacks. <paramref name="keepPartial"/>: a value
    /// from the flags (whole sacks only) leaves the fraction the owner has gathered alone.
    /// </summary>
    public void SetTank(Tank t, bool keepPartial = false)
    {
        if (Spec.TankItems <= 0) return;
        if (keepPartial && t.Items == Tank.Items && t.Crop == Tank.Crop) return;
        bool weighed = t.Items != Tank.Items;
        Tank = t;
        if (!weighed) return;
        Load = Mathf.Clamp(t.Items / (float)Spec.TankItems, 0f, 1f);
        Train.Bodies[0].SetPayload(Spec.Sections[0].PayloadMax * Load);
        Train.ComputeLoads();
    }

    /// <summary>The tipping trailer's harvest changed: a new code (its load follows the sacks), the joints kept.</summary>
    public bool SetTrailerTank(Tank t)
    {
        if (Trailer is not { TankItems: > 0 }) return false;
        int code = TrailerCatalog.WithTank(TrailerCode, t);
        if (code == TrailerCode) return false;
        TrailerCode = code;
        Rebuild();
        return true;
    }

    // ---- driving ----

    /// <summary>Before the step: the combine's working speed, a lowered implement's pull and where its weight is.</summary>
    private void PrepareFarm(float dt, float u)
    {
        if (!Spec.Farm) return;
        Box.LimitKmh = Spec.Tool != FarmTool.None && Lowered ? Spec.WorkKmh : null;
        if (Implement is not { } imp) return;
        var b = Train.Bodies[SectionCount - 1];
        if (b.Grounded != Lowered)
        {
            b.Grounded = Lowered;
            Train.ComputeLoads();
        }
        // on tarmac the bodies or coulters ride on the surface: no soil to pull through
        b.Draft = Lowered && OnSoil ? DraftOf(imp, Mathf.Abs(u) * 3.6f) : 0f;
    }

    /// <summary>The soil's pull on a lowered implement at a speed, N (<see cref="MachineLoad"/>, ASABE D497).</summary>
    public static float DraftOf(TrailerSpec imp, float kmh) => imp.Tool switch
    {
        FarmTool.Plough => MachineLoad.PloughDraft(kmh, imp.WorkWidth, imp.DepthCm),
        FarmTool.Sow => MachineLoad.DrillDraft(imp.WorkWidth),
        FarmTool.Mow => MachineLoad.MowerDraft(kmh, imp.WorkWidth),
        _ => 0f,
    };

    /// <summary>The farm parts of a section's rig: the implement or header down, the tank's heap, the auger, the wheels' sizes.</summary>
    private void DressFarm(HeavyRig rig, int k)
    {
        rig.SpinRadius = WheelRadius;
        if (!Spec.Farm) return;
        rig.Kneeling = false;   // the kneel bit is the implement's here
        rig.Lowered = Lowered;
        if (k == 0 && Spec.TankItems > 0)
        {
            rig.Fill = Tank.Items / (float)Spec.TankItems;
            rig.AugerOut = AugerOut;
        }
        else if (k >= OwnSections && Trailer is { TankItems: > 0 } t) rig.Fill = TrailerTank.Items / (float)t.TankItems;
    }

    /// <summary>
    /// A tractor collides as its bonnet and, above it, only its cab; a combine as its body and,
    /// low down in front, its header (#494): wider than the body, it would otherwise be cut to it.
    /// </summary>
    private (Aabb, Aabb) FarmHull
    {
        get
        {
            var s = Spec.Sections[0];
            float cg = Train.Bodies[0].CgAt;
            if (Spec.Class == HeavyClass.Combine)
            {
                var (h0, h1, hw) = (FarmMeshBuilder.HeaderFrom, FarmMeshBuilder.HeaderTo, FarmMeshBuilder.HeaderWidth);
                var header = new Aabb(new Vector3(-hw * 0.5f, 0f, -cg + h0), new Vector3(hw, FarmMeshBuilder.HeaderTop, h1 - h0));
                var body = new Aabb(new Vector3(-s.Width * 0.5f, 0f, -cg + h1), new Vector3(s.Width, s.Height, s.Length - h1));
                return (header, body);
            }
            float w = s.Width;
            var bonnet = new Aabb(new Vector3(-w * 0.5f, 0f, -cg), new Vector3(w, FarmMeshBuilder.TractorBonnetTop, s.Length));
            float c0 = FarmMeshBuilder.TractorCabFrom, c1 = FarmMeshBuilder.TractorCabTo;
            var cab = new Aabb(new Vector3(-w * 0.5f, FarmMeshBuilder.TractorBonnetTop, -cg + c0),
                new Vector3(w, s.Height - FarmMeshBuilder.TractorBonnetTop, c1 - c0));
            return (bonnet, cab);
        }
    }
}
