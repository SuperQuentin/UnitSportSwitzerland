using Godot;

namespace UnitSport.Avatar;

/// <summary>A figure's build (#394): one skeleton, different flesh on it.</summary>
public enum BodyBuild
{
    Slim = 0,
    Curvy = 1,
    /// <summary>Masculine, athletic: wide shoulders and chest, narrow hips, square jaw.</summary>
    Broad = 2,
    /// <summary>Masculine and slim: flat chest, straight waist, square jaw.</summary>
    Lean = 3,
    /// <summary>Masculine and heavy: thick waist and neck, big arms.</summary>
    Stocky = 4,
}

/// <summary>Hair drawn over the head's crown (#394 prototype).</summary>
public enum HairStyle
{
    None = 0,
    Spiky = 1,
    Bob = 2,
    Ponytail = 3,
    Long = 4,
    /// <summary>A crew cut: the cap only, short at the front.</summary>
    Short = 5,
    /// <summary>A quiff: short sides, the front swept up and forward.</summary>
    Quiff = 6,
    /// <summary>Shaggy: short locks falling all round, over the ears and the brow.</summary>
    Shaggy = 7,
    /// <summary>A mohawk: a crest of spikes front to back, the sides shaved.</summary>
    Mohawk = 8,
    /// <summary>Long and straight with a blunt fringe cut straight across the brow.</summary>
    BluntBangs = 9,
    /// <summary>Chin length, a long fringe swept over one eye.</summary>
    SideSwept = 10,
    /// <summary>Two tails high on the sides, and a fringe.</summary>
    Twintails = 11,
    /// <summary>Hair tied in a bun at the back of the crown, a short fringe.</summary>
    Bun = 12,
}

/// <summary>
/// The flesh of a build: half-widths and radii in metres at the body's landmarks (#394). The bone
/// lengths are the skeleton's and shared by every build, so the gait, the IK, the dances and every
/// seat are the same for all of them; only what is drawn round the bones changes.
/// </summary>
public readonly record struct Physique(
    float Crotch, float Hip, float HipBack, float Waist, float UnderBust,
    float Chest, float ChestFront, float ChestBack, float Shoulder, float Neck,
    float ThighTop, float ThighMid, float Knee, float Calf, float Ankle,
    float Deltoid, float UpperArm, float Elbow, float Forearm, float Wrist,
    float Hand, float Head, float Jaw = 0f)
{
    public static Physique Of(BodyBuild build) => build switch
    {
        BodyBuild.Lean => new(
            Crotch: 0.112f, Hip: 0.130f, HipBack: 0.084f, Waist: 0.118f, UnderBust: 0.132f,
            Chest: 0.146f, ChestFront: 0.084f, ChestBack: 0.080f, Shoulder: 0.172f, Neck: 0.052f,
            ThighTop: 0.082f, ThighMid: 0.074f, Knee: 0.053f, Calf: 0.056f, Ankle: 0.037f,
            Deltoid: 0.062f, UpperArm: 0.050f, Elbow: 0.039f, Forearm: 0.044f, Wrist: 0.031f,
            Hand: 1.04f, Head: 1.05f, Jaw: 1f),
        BodyBuild.Stocky => new(
            Crotch: 0.128f, Hip: 0.152f, HipBack: 0.100f, Waist: 0.158f, UnderBust: 0.166f,
            Chest: 0.168f, ChestFront: 0.108f, ChestBack: 0.096f, Shoulder: 0.180f, Neck: 0.066f,
            ThighTop: 0.100f, ThighMid: 0.090f, Knee: 0.062f, Calf: 0.066f, Ankle: 0.043f,
            Deltoid: 0.070f, UpperArm: 0.063f, Elbow: 0.048f, Forearm: 0.054f, Wrist: 0.036f,
            Hand: 1.12f, Head: 1.02f, Jaw: 1f),
        BodyBuild.Curvy => new(
            Crotch: 0.125f, Hip: 0.168f, HipBack: 0.112f, Waist: 0.100f, UnderBust: 0.112f,
            Chest: 0.132f, ChestFront: 0.112f, ChestBack: 0.074f, Shoulder: 0.140f, Neck: 0.044f,
            ThighTop: 0.096f, ThighMid: 0.084f, Knee: 0.052f, Calf: 0.057f, Ankle: 0.033f,
            Deltoid: 0.050f, UpperArm: 0.044f, Elbow: 0.033f, Forearm: 0.038f, Wrist: 0.026f,
            Hand: 0.92f, Head: 1.08f),
        BodyBuild.Broad => new(
            Crotch: 0.118f, Hip: 0.140f, HipBack: 0.090f, Waist: 0.132f, UnderBust: 0.150f,
            Chest: 0.170f, ChestFront: 0.096f, ChestBack: 0.090f, Shoulder: 0.182f, Neck: 0.058f,
            ThighTop: 0.090f, ThighMid: 0.081f, Knee: 0.058f, Calf: 0.063f, Ankle: 0.040f,
            Deltoid: 0.072f, UpperArm: 0.058f, Elbow: 0.044f, Forearm: 0.050f, Wrist: 0.034f,
            Hand: 1.08f, Head: 1.05f, Jaw: 1f),
        _ => new(
            Crotch: 0.110f, Hip: 0.138f, HipBack: 0.092f, Waist: 0.106f, UnderBust: 0.120f,
            Chest: 0.136f, ChestFront: 0.086f, ChestBack: 0.076f, Shoulder: 0.150f, Neck: 0.046f,
            ThighTop: 0.082f, ThighMid: 0.073f, Knee: 0.050f, Calf: 0.053f, Ankle: 0.034f,
            Deltoid: 0.054f, UpperArm: 0.047f, Elbow: 0.036f, Forearm: 0.040f, Wrist: 0.028f,
            Hand: 0.97f, Head: 1.08f),
    };
}

/// <summary>
/// What a refined figure looks like (#394 prototype): build, skin, face, hair, and where its
/// clothes start and end along the body. Lengths are along the body: <c>Spine</c> values from the
/// crotch (0) through hip (1), waist (2) and chest (3) to the neck (4); <c>Arm</c> values from the
/// shoulder (0) through the elbow (1) to the wrist (2); <c>Leg</c> values from the hip (0)
/// through the knee (1) to the ankle (2).
/// </summary>
public sealed record BodyLook(BodyBuild Build, Color Skin)
{
    public Color Top { get; init; } = new(0.85f, 0.24f, 0.20f);
    public Color Bottom { get; init; } = new(0.16f, 0.17f, 0.20f);
    public Color Shoes { get; init; } = new(0.92f, 0.92f, 0.90f);
    public Color Sole { get; init; } = new(0.16f, 0.16f, 0.17f);
    /// <summary>Tights or stockings between the bottom's hem and the boots; null: bare legs.</summary>
    public Color? Legwear { get; init; }
    public Color? Gloves { get; init; }
    public bool Fingerless { get; init; } = true;
    public Color? Belt { get; init; }
    public Color Hair { get; init; } = new(0.20f, 0.13f, 0.08f);
    public HairStyle HairStyle { get; init; }
    public int Face { get; init; }
    /// <summary>The iris, whatever the face (the atlas keys it, the shader paints it).</summary>
    public Color Eyes { get; init; } = new(0.35f, 0.22f, 0.12f);
    /// <summary>Cloth patterns (<see cref="Finish"/>: Checker, Stripes, Studs, Tartan, Fishnet…) on the top, the bottom and the legwear.</summary>
    public Finish TopPattern { get; init; }
    public Finish BottomPattern { get; init; }
    public Finish LegPattern { get; init; }
    /// <summary>Spine: where the top's hem is (above the waist, 2.5: a crop top).</summary>
    public float TopFrom { get; init; } = 1.75f;
    /// <summary>Spine: the bottom's waistband.</summary>
    public float Waistband { get; init; } = 2.0f;
    /// <summary>Arm: the sleeve's end (0: sleeveless, 2: to the wrist).</summary>
    public float SleeveTo { get; init; } = 0.45f;
    /// <summary>Arm: where gloves start (past 2: none).</summary>
    public float GloveFrom { get; init; } = 2.5f;
    /// <summary>Leg: the bottom's hem (0.3 shorts, 2 trousers to the ankle).</summary>
    public float LegTo { get; init; } = 0.35f;
    /// <summary>Leg: the top of the boots (2: shoes only).</summary>
    public float BootFrom { get; init; } = 1.85f;
    /// <summary>Sole thickness, 1 a trainer's.</summary>
    public float Platform { get; init; } = 1f;
}

public static partial class HumanMeshBuilder
{
    /// <summary>A refined figure (#394 prototype) in a fixed pose.</summary>
    public static ArrayMesh BuildBody(BodyLook look, HumanPose pose = HumanPose.Standing, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        AppendBody(scratch, look, RigFor(pose));
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    /// <summary>A refined figure mid-stride (<see cref="BuildStride"/>'s gait).</summary>
    public static ArrayMesh BuildBodyStride(BodyLook look, float speed, float phase, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        AppendBody(scratch, look, GaitRig(speed, phase));
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    // a colour carrying the pixel face's finish id in its alpha (the clothes' Garments.Fx convention)
    private static Color FaceMark(Color skin) => new(skin.R, skin.G, skin.B, 1f - FaceAtlas.FinishId / 255f);

    private static void AppendBody(MeshScratch s, BodyLook look, Rig r)
    {
        using var smoothing = s.Smoothing(SmoothFigures);
        // the patterns ride in the colours' alpha, as the clothes' finishes do
        look = look with
        {
            Top = Garments.Fx(look.Top, look.TopPattern),
            Bottom = Garments.Fx(look.Bottom, look.BottomPattern),
            Legwear = look.Legwear is { } legs ? Garments.Fx(legs, look.LegPattern) : null,
        };
        var shape = Physique.Of(look.Build);
        var torso = new Torso(r, shape);

        // ---- trunk: the bottom up to its waistband, bare skin to the top's hem, the top to the neck
        float band = look.Waistband, hem = Mathf.Min(look.TopFrom, 3.8f);
        torso.Band(s, 0f, Mathf.Min(band, hem), look.Bottom);
        if (hem > band) torso.Band(s, band, hem, look.Skin);
        torso.Band(s, hem, 4f, look.Top);
        if (look.Belt is { } belt) torso.Band(s, band - 0.13f, band, belt, inflate: 0.007f);

        // ---- limbs
        var shoulderSide = (r.ShoulderR - r.ShoulderL).Normalized();
        var legZones = new Zones(look.LegTo, look.BootFrom, look.Bottom, look.Legwear ?? look.Skin, look.Shoes, 0.012f);
        var armZones = new Zones(look.SleeveTo, look.GloveFrom, look.Top, look.Skin, look.Gloves ?? look.Skin, 0.003f);
        foreach (float side in stackalloc[] { -1f, 1f })
        {
            bool left = side < 0;
            var (hip, knee, ankle, toe) = left ? (r.HipL, r.KneeL, r.AnkleL, r.ToeL) : (r.HipR, r.KneeR, r.AnkleR, r.ToeR);
            var (shoulder, elbow, wrist) = left ? (r.ShoulderL, r.ElbowL, r.WristL) : (r.ShoulderR, r.ElbowR, r.WristR);

            // the thigh starts a little inside the pelvis, so no seam opens when it swings
            var thighRoot = hip + (hip - knee).Normalized() * 0.035f;
            Span(s, thighRoot, hip.Lerp(knee, 0.4f), shape.ThighTop, shape.ThighMid, 0f, 0.4f, legZones);
            Span(s, hip.Lerp(knee, 0.4f), knee, shape.ThighMid, shape.Knee, 0.4f, 1f, legZones);
            Span(s, knee, knee.Lerp(ankle, 0.3f), shape.Knee, shape.Calf, 1f, 1.3f, legZones);
            Span(s, knee.Lerp(ankle, 0.3f), ankle, shape.Calf, shape.Ankle, 1.3f, 2f, legZones);
            Boot(s, ankle, toe, shoulderSide, look);

            // the shoulder's round: the deltoid, sleeve-coloured when there is a sleeve
            var down = (elbow - shoulder).Normalized();
            var outward = shoulderSide * side;
            // a dome over the joint along the arm, then down into it: no flat cap on top
            var deltoid = look.SleeveTo > 0.05f ? look.Top : look.Skin;
            var crown = shoulder - down * 0.032f - outward * 0.006f;
            var belly = shoulder + down * 0.022f;
            s.Tube(crown, belly, shape.Deltoid * 0.45f, shape.Deltoid, deltoid, 7);
            s.Tube(belly, shoulder + down * 0.12f, shape.Deltoid, shape.UpperArm, deltoid, 7);
            Span(s, shoulder, shoulder.Lerp(elbow, 0.5f), shape.UpperArm, shape.UpperArm * 0.93f, 0f, 0.5f, armZones);
            Span(s, shoulder.Lerp(elbow, 0.5f), elbow, shape.UpperArm * 0.93f, shape.Elbow, 0.5f, 1f, armZones);
            Span(s, elbow, elbow.Lerp(wrist, 0.3f), shape.Elbow, shape.Forearm, 1f, 1.3f, armZones);
            Span(s, elbow.Lerp(wrist, 0.3f), wrist, shape.Forearm, shape.Wrist, 1.3f, 2f, armZones);
            var palm = look.Gloves is { } g ? g : look.Skin;
            Hand(s, elbow, wrist, side, shape.Hand, palm, look.Gloves is { } g2 && !look.Fingerless ? g2 : look.Skin);
        }

        // ---- head
        var head = new Head(r, shoulderSide, shape.Head, shape.Jaw);
        // a straight neck up into the skull behind the jaw: the jaw's underside overhangs it, so the
        // head reads as a head on a neck rather than one cone running down into the collar
        s.Tube(r.Neck - torso.Up(4f) * 0.03f, head.NeckTop, shape.Neck, look.Skin, 8);
        head.Draw(s, look.Skin);
        head.Face(s, look.Face, look.Eyes);
        head.Ears(s, look.Skin);
        head.Hair(s, look.HairStyle, look.Hair, look.Skin);
    }

    /// <summary>
    /// Three colours along a limb, by its length parameter: <see cref="Low"/> up to <see cref="A"/>,
    /// <see cref="Mid"/> up to <see cref="B"/>, then <see cref="High"/>, which stands
    /// <see cref="HighInflate"/> proud (a boot over the calf).
    /// </summary>
    private readonly record struct Zones(float A, float B, Color Low, Color Mid, Color High, float HighInflate)
    {
        public (Color Colour, float Inflate) At(float t) =>
            t < Mathf.Min(A, B) ? (Low, 0f) : t < B ? (Mid, 0f) : (High, HighInflate);
    }

    /// <summary>A tapered tube from <paramref name="a"/> (limb parameter <paramref name="ta"/>) to <paramref name="b"/>, cut where its colour changes.</summary>
    private static void Span(MeshScratch s, Vector3 a, Vector3 b, float ra, float rb, float ta, float tb, Zones z, int sides = 7)
    {
        Span<float> cuts = stackalloc float[4];
        int n = 0;
        cuts[n++] = ta;
        foreach (float c in stackalloc[] { Mathf.Min(z.A, z.B), z.B })
            if (c > ta + 1e-3f && c < tb - 1e-3f && c > cuts[n - 1]) cuts[n++] = c;
        cuts[n++] = tb;
        for (int i = 0; i + 1 < n; i++)
        {
            float f0 = (cuts[i] - ta) / (tb - ta), f1 = (cuts[i + 1] - ta) / (tb - ta);
            var (colour, inflate) = z.At((cuts[i] + cuts[i + 1]) * 0.5f);
            s.Tube(a.Lerp(b, f0), a.Lerp(b, f1), Mathf.Lerp(ra, rb, f0) + inflate, Mathf.Lerp(ra, rb, f1) + inflate, colour, sides);
        }
    }

    /// <summary>
    /// A hand: palm, four fingers curled a little toward the palm, and a thumb on the side facing
    /// forward when the arm hangs. The palm faces the body.
    /// </summary>
    private static void Hand(MeshScratch s, Vector3 elbow, Vector3 wrist, float side, float scale, Color palm, Color fingers)
    {
        var dir = (wrist - elbow).Normalized();
        var inward = new Vector3(-side, 0, 0);
        var n = inward - dir * inward.Dot(dir);
        n = n.LengthSquared() > 1e-4f ? n.Normalized() : (Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Back).Cross(dir).Normalized();
        var thumbSide = dir.Cross(n) * -side;   // forward for both hands while the arms hang
        var thick = thumbSide.Cross(dir);
        var basis = new Basis(thumbSide, dir, thick);

        float k = scale;
        var palmCentre = wrist + dir * 0.042f * k;
        s.Box(palmCentre, new Vector3(0.068f, 0.072f, 0.028f) * k, palm, basis);

        // fingers from the knuckle line, the index on the thumb side
        var curl = (dir * Mathf.Cos(0.35f) + n * Mathf.Sin(0.35f)).Normalized();
        var fingerBasis = new Basis(thumbSide, curl, thumbSide.Cross(curl));
        var knuckles = palmCentre + dir * 0.036f * k;
        ReadOnlySpan<float> lengths = [0.066f, 0.072f, 0.068f, 0.054f];
        for (int i = 0; i < 4; i++)
        {
            float across = (1.5f - i) * 0.0168f * k;
            float len = lengths[i] * k;
            s.Box(knuckles + thumbSide * across + curl * len * 0.5f, new Vector3(0.0145f, len, 0.019f) * k, fingers, fingerBasis);
        }

        var thumbRoot = wrist + dir * 0.022f * k + thumbSide * 0.028f * k;
        var thumbDir = (dir * 0.65f + thumbSide * 0.5f + n * 0.45f).Normalized();
        s.Tube(thumbRoot, thumbRoot + thumbDir * 0.055f * k, 0.013f * k, 0.009f * k, fingers, 5);
    }

    /// <summary>
    /// A boot or shoe along ankle→toe: a heel, the instep and a toe box lofted through five
    /// sections, its lowest band the sole's colour so a platform reads from the side.
    /// </summary>
    private static void Boot(MeshScratch s, Vector3 ankle, Vector3 toe, Vector3 sideAxis, BodyLook look)
    {
        var f = toe - ankle;
        float len = f.Length();
        if (len < 1e-4f) return;
        f /= len;
        var side = (sideAxis - f * sideAxis.Dot(f)).Normalized();
        var up = f.Cross(side);
        float k = len / 0.152f;
        // the sole's share of the shoe's height: a platform's grows up into the boot, the ground stays put
        float sole = Mathf.Clamp(0.22f * look.Platform, 0.1f, 0.6f);

        // (along, centre offset up, half width, half height): the sole stays on the standing rig's ground line
        ReadOnlySpan<Vector4> stations =
        [
            new(-0.048f, -0.050f, 0.043f, 0.050f),
            new(0.000f, -0.026f, 0.049f, 0.060f),
            new(0.070f, -0.021f, 0.051f, 0.045f),
            new(0.130f, -0.020f, 0.050f, 0.028f),
            new(0.172f, -0.018f, 0.036f, 0.017f),
        ];
        var sections = new Vector3[stations.Length][];
        for (int i = 0; i < stations.Length; i++)
        {
            var st = stations[i];
            float w = st.Z, h = st.W, welt = -h + sole * 2f * h;
            var c = ankle + f * st.X * k + up * st.Y;
            ReadOnlySpan<Vector2> ring =
            [
                new(w, 0.45f * h), new(0.55f * w, h), new(-0.55f * w, h), new(-w, 0.45f * h),
                new(-w, welt), new(-0.95f * w, -h), new(0.95f * w, -h), new(w, welt),
            ];
            sections[i] = new Vector3[ring.Length];
            for (int j = 0; j < ring.Length; j++) sections[i][j] = c + side * ring[j].X + up * ring[j].Y;
        }
        var colours = new Color[8];
        for (int j = 0; j < 8; j++) colours[j] = j is 4 or 5 or 6 ? look.Sole : look.Shoes;
        s.Loft(sections, colours, look.Shoes);
    }

    /// <summary>
    /// The trunk as rings along the spine, crotch (0) to neck (4): wide at the shoulders and
    /// hips, in at the waist, front and back depths of their own so a chest and a seat read.
    /// </summary>
    private readonly struct Torso
    {
        private readonly Vector3 _crotch, _hip, _waist, _chest, _neck, _hipSide, _shoulderSide;
        private readonly Physique _b;
        private const int Sides = 10;

        // (spine position, half width, front depth, back depth, forward offset)
        private Vector4 Key(int i, out float s) => i switch
        {
            0 => Key(0f, _b.Crotch, 0.062f, _b.HipBack * 0.78f, 0f, out s),
            1 => Key(1f, _b.Hip, 0.080f, _b.HipBack, -0.006f, out s),
            2 => Key(2f, _b.Waist, 0.072f, 0.070f, 0f, out s),
            3 => Key(2.5f, _b.UnderBust, 0.077f, 0.074f, 0f, out s),
            4 => Key(3f, _b.Chest, _b.ChestFront, _b.ChestBack, 0f, out s),
            5 => Key(3.55f, _b.Shoulder, 0.064f, 0.072f, -0.004f, out s),
            _ => Key(4f, _b.Neck + 0.026f, _b.Neck + 0.006f, _b.Neck + 0.012f, 0f, out s),
        };

        private const int Keys = 7;

        private static Vector4 Key(float at, float w, float df, float db, float dz, out float s)
        {
            s = at;
            return new Vector4(w, df, db, dz);
        }

        public Torso(Rig r, Physique b)
        {
            _hip = r.Hip; _waist = r.Waist; _chest = r.Chest; _neck = r.Neck;
            _crotch = r.Hip - (r.Waist - r.Hip).Normalized() * 0.085f;
            _hipSide = (r.HipR - r.HipL).Normalized();
            _shoulderSide = (r.ShoulderR - r.ShoulderL).Normalized();
            _b = b;
        }

        private Vector3 At(float s)
        {
            s = Mathf.Clamp(s, 0f, 4f);
            int i = Mathf.Min((int)s, 3);
            float f = s - i;
            Vector3 a = i switch { 0 => _crotch, 1 => _hip, 2 => _waist, _ => _chest };
            Vector3 b = i switch { 0 => _hip, 1 => _waist, 2 => _chest, _ => _neck };
            return a.Lerp(b, f);
        }

        public Vector3 Up(float s) => (At(s + 0.25f) - At(s - 0.25f)).Normalized();

        private Vector4 Profile(float s)
        {
            Key(0, out float prev);
            var last = Key(0, out _);
            for (int i = 1; i < Keys; i++)
            {
                var next = Key(i, out float at);
                if (s <= at) return last.Lerp(next, Mathf.Clamp((s - prev) / (at - prev), 0f, 1f));
                last = next;
                prev = at;
            }
            return last;
        }

        private Vector3[] Ring(float s, float inflate)
        {
            var p = Profile(s);
            var up = Up(s);
            var side = _hipSide.Lerp(_shoulderSide, s / 4f);
            side = (side - up * side.Dot(up)).Normalized();
            var fwd = side.Cross(up);
            var centre = At(s) + fwd * p.W;
            var ring = new Vector3[Sides];
            for (int k = 0; k < Sides; k++)
            {
                float a = Mathf.Tau * k / Sides, c = Mathf.Cos(a), sn = Mathf.Sin(a);
                float z = sn > 0 ? (p.Y + inflate) * sn : (p.Z + inflate) * sn;
                ring[k] = centre + side * (p.X + inflate) * c + fwd * z;
            }
            return ring;
        }

        /// <summary>The trunk from spine position <paramref name="s0"/> to <paramref name="s1"/> in one colour.</summary>
        public void Band(MeshScratch s, float s0, float s1, Color colour, float inflate = 0f)
        {
            s0 = Mathf.Clamp(s0, 0f, 4f);
            s1 = Mathf.Clamp(s1, 0f, 4f);
            if (s1 - s0 < 0.01f) return;
            var sections = new List<Vector3[]> { Ring(s0, inflate) };
            for (int i = 0; i < Keys; i++)
            {
                Key(i, out float at);
                if (at > s0 + 0.01f && at < s1 - 0.01f) sections.Add(Ring(at, inflate));
            }
            sections.Add(Ring(s1, inflate));
            var colours = new Color[Sides];
            Array.Fill(colours, colour);
            s.Loft(sections, colours, colour);
        }
    }

    /// <summary>
    /// A head in the anime proportions of the references: a wide cranium over a narrower jaw,
    /// built as rings up the head's axis, with a flat underside to the jaw that overhangs the neck;
    /// a pixel face over its front. <c>jaw</c> 0 is a soft, pointed chin, 1 a square one.
    /// </summary>
    private sealed class Head
    {
        private readonly Vector3 _base, _side, _fwd;
        public Vector3 UpAxis { get; }
        private readonly float _k;
        private readonly Vector4[] _shape;
        private const int Sides = 12;

        // (half width, front depth, back depth, forward offset) at each height, metres at scale 1
        private static readonly float[] Heights = { -0.035f, -0.022f, 0.010f, 0.050f, 0.105f, 0.160f, 0.215f, 0.250f };
        private static readonly Vector4[] Soft =
        {
            new(0.018f, 0.014f, 0.012f, 0.058f), new(0.046f, 0.040f, 0.020f, 0.034f),
            new(0.060f, 0.058f, 0.046f, 0.020f), new(0.074f, 0.072f, 0.070f, 0.010f),
            new(0.081f, 0.084f, 0.088f, 0f), new(0.084f, 0.080f, 0.097f, -0.006f),
            new(0.071f, 0.062f, 0.088f, -0.012f), new(0.036f, 0.030f, 0.048f, -0.012f),
        };
        private static readonly Vector4[] Square =
        {
            new(0.032f, 0.014f, 0.012f, 0.052f), new(0.060f, 0.040f, 0.028f, 0.030f),
            new(0.071f, 0.058f, 0.054f, 0.018f), new(0.079f, 0.072f, 0.075f, 0.008f),
            new(0.082f, 0.084f, 0.089f, 0f), new(0.084f, 0.080f, 0.097f, -0.006f),
            new(0.071f, 0.062f, 0.088f, -0.012f), new(0.036f, 0.030f, 0.048f, -0.012f),
        };

        public Head(Rig r, Vector3 shoulderSide, float scale, float jaw)
        {
            var axis = r.HeadTop - r.HeadBase;
            UpAxis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
            _side = (shoulderSide - UpAxis * shoulderSide.Dot(UpAxis)).Normalized();
            _fwd = _side.Cross(UpAxis);
            _base = r.HeadBase;
            _k = scale * axis.Length() / 0.19f;
            _shape = new Vector4[Soft.Length];
            for (int i = 0; i < Soft.Length; i++) _shape[i] = Soft[i].Lerp(Square[i], jaw);
        }

        /// <summary>Where the neck goes into the skull: up behind the jaw.</summary>
        public Vector3 NeckTop => _base + (UpAxis * 0.045f - _fwd * 0.012f) * _k;

        private Vector4 Profile(float y)
        {
            if (y <= Heights[0]) return _shape[0];
            for (int i = 1; i < Heights.Length; i++)
                if (y <= Heights[i])
                    return _shape[i - 1].Lerp(_shape[i], (y - Heights[i - 1]) / (Heights[i] - Heights[i - 1]));
            return _shape[^1];
        }

        private Vector3 Vertex(float y, int k, float inflate)
        {
            var p = Profile(y);
            float a = Mathf.Tau * k / Sides, c = Mathf.Cos(a), sn = Mathf.Sin(a);
            float z = sn > 0 ? (p.Y + inflate) * sn : (p.Z + inflate) * sn;
            return _base + (UpAxis * y + _side * (p.X + inflate) * c + _fwd * (z + p.W)) * _k;
        }

        /// <summary>A point on the head's surface (its flat facets) at height <paramref name="y"/> and angle <paramref name="angle"/> (90° the front), lifted <paramref name="lift"/>.</summary>
        public Vector3 Point(float y, float angle, float lift = 0f)
        {
            float t = Mathf.PosMod(angle, Mathf.Tau) / Mathf.Tau * Sides;
            int k = (int)t;
            var p = Vertex(y, k, 0f).Lerp(Vertex(y, k + 1, 0f), t - k);
            var radial = p - Centre(y);
            radial -= UpAxis * radial.Dot(UpAxis);
            return p + radial.Normalized() * lift * _k;
        }

        private Vector3 Centre(float y) => _base + (UpAxis * y + _fwd * Profile(y).W) * _k;

        private Vector3[] Ring(float y, float inflate)
        {
            var ring = new Vector3[Sides];
            for (int k = 0; k < Sides; k++) ring[k] = Vertex(y, k, inflate);
            return ring;
        }

        public void Draw(MeshScratch s, Color skin)
        {
            var sections = new Vector3[Heights.Length][];
            for (int i = 0; i < Heights.Length; i++) sections[i] = Ring(Heights[i], 0f);
            var colours = new Color[Sides];
            Array.Fill(colours, skin);
            s.Loft(sections, colours, skin);
        }

        /// <summary>The pixel face: a band over the front of the head, from brow to chin, sampling <see cref="FaceAtlas"/>; its vertex colour is the eye colour.</summary>
        public void Face(MeshScratch s, int face, Color eyes)
        {
            ReadOnlySpan<float> rows = [0.178f, 0.135f, 0.090f, 0.045f, 0.010f];
            const int columns = 9;
            var band = new Vector3[rows.Length][];
            for (int r = 0; r < rows.Length; r++)
            {
                band[r] = new Vector3[columns];
                for (int i = 0; i < columns; i++)
                    band[r][i] = Point(rows[r], Mathf.DegToRad(Mathf.Lerp(145f, 35f, i / (columns - 1f))), 0.004f);
            }
            s.FaceBand(band, FaceAtlas.Uv(face), FaceMark(eyes), Centre(0.10f));
        }

        public void Ears(MeshScratch s, Color skin)
        {
            var basis = new Basis(_side, UpAxis, _fwd);
            foreach (float a in stackalloc[] { 0f, Mathf.Pi })
                s.Box(Point(0.095f, a, 0.006f) - _fwd * 0.004f * _k, new Vector3(0.016f, 0.048f, 0.030f) * _k, skin, basis);
        }

        private const float Deg = Mathf.Pi / 180f;

        /// <summary>A lock of hair: a four-sided tube tapering to a point, from the scalp out to <paramref name="lift"/> off it.</summary>
        private void Lock(MeshScratch s, Color colour, float y0, float a0, float y1, float a1, float lift, float r) =>
            s.Tube(Point(y0, a0 * Deg, 0.010f), Point(y1, a1 * Deg, lift), r * _k, 0.003f * _k, colour, 4);

        /// <summary>A spike off the scalp along the head's outward normal there, tilted up by <paramref name="rise"/>.</summary>
        private void Spike(MeshScratch s, Color colour, float y, float a, float length, float rise, float r)
        {
            var root = Point(y, a * Deg, 0.006f);
            var outward = (root - Centre(y)).Normalized();
            s.Tube(root, root + (outward * (1f - rise) + UpAxis * rise).Normalized() * length * _k, r * _k, 0.003f * _k, colour, 4);
        }

        /// <summary>The hair as a cap over the crown <paramref name="inflate"/> off the scalp, from <paramref name="low"/> up.</summary>
        private void Cap(MeshScratch s, Color colour, float inflate = 0.013f, float low = 0.160f)
        {
            // the hairline slants: high on the brow, down over the ears, low at the nape, so short
            // hair does not sit on the head like a beanie
            var hairline = new Vector3[Sides];
            for (int k = 0; k < Sides; k++)
            {
                float front = (Mathf.Sin(Mathf.Tau * k / Sides) + 1f) * 0.5f;
                hairline[k] = Vertex(Mathf.Lerp(0.060f, low + 0.012f, front), k, inflate);
            }
            var cap = new List<Vector3[]> { hairline, Ring(0.200f, inflate), Ring(0.250f, inflate) };
            cap.Add(Ring(0.250f + inflate * 2f, inflate - 0.013f));   // the top profile, raised over the scalp
            var colours = new Color[Sides];
            Array.Fill(colours, colour);
            s.Loft(cap, colours, colour);
        }

        /// <summary>Hair hanging round the back and sides down to <paramref name="end"/>, open over the face.</summary>
        private void Fall(MeshScratch s, Color colour, float end, float flare, float gapAngle = 1.05f)
        {
            var top = Centre(0.19f) - _fwd * 0.01f * _k;
            var bottom = _base + (UpAxis * end - _fwd * Mathf.Lerp(0.012f, 0.03f, Mathf.Clamp(-end * 5f, 0f, 1f))) * _k;
            s.Skirt(top, bottom, 0.100f * _k, flare * _k, colour, 12, gap: _fwd, gapAngle: gapAngle);
        }

        private enum Fringe { None, Short, Locks, Spiky, Blunt, Swept }

        private void Bangs(MeshScratch s, Color colour, Fringe fringe)
        {
            ReadOnlySpan<float> at = [58f, 74f, 90f, 106f, 122f];
            switch (fringe)
            {
                case Fringe.Short:
                    for (int i = 0; i < at.Length; i++) Lock(s, colour, 0.225f, at[i], 0.185f, at[i] + (i - 2) * 3f, 0.018f, 0.024f);
                    break;
                case Fringe.Locks:
                case Fringe.Spiky:
                    for (int i = 0; i < at.Length; i++)
                        Lock(s, colour, 0.215f, at[i], fringe == Fringe.Spiky ? 0.142f : 0.150f, at[i] + (i - 2) * 4f, 0.020f, 0.028f);
                    break;
                case Fringe.Blunt:
                {
                    // cut straight across just above the brows: a band round the front of the head
                    var top = Centre(0.212f);
                    var bottom = Centre(0.150f) + _fwd * 0.004f * _k;
                    s.Skirt(top, bottom, 0.097f * _k, 0.100f * _k, colour, 16, gap: -_fwd, gapAngle: 2.0f);
                    break;
                }
                case Fringe.Swept:
                    // long locks all swept to one side, the longest down over one eye
                    for (int i = 0; i < at.Length; i++)
                        Lock(s, colour, 0.222f, at[i] - 8f, Mathf.Lerp(0.165f, 0.085f, i / 4f), at[i] + 22f, 0.022f, 0.030f);
                    break;
            }
        }

        public void Hair(MeshScratch s, HairStyle style, Color colour, Color skin)
        {
            switch (style)
            {
                case HairStyle.None:
                    return;

                case HairStyle.Spiky:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Spiky);
                    // spikes off the crown, out and up, longer at the back
                    for (int i = 0; i < 9; i++)
                    {
                        float a = i * 40f + 20f;
                        Spike(s, colour, 0.230f, a, Mathf.Sin(a * Deg) < 0 ? 0.14f : 0.10f, 0.45f, 0.032f);
                    }
                    Lock(s, colour, 0.20f, 15f, 0.08f, 12f, 0.02f, 0.026f);
                    Lock(s, colour, 0.20f, 165f, 0.08f, 168f, 0.02f, 0.026f);
                    break;

                case HairStyle.Bob:
                case HairStyle.Long:
                {
                    bool longHair = style == HairStyle.Long;
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    Fall(s, colour, longHair ? -0.20f : 0.005f, longHair ? 0.125f : 0.108f);
                    Lock(s, colour, 0.19f, 25f, longHair ? -0.10f : 0.02f, 22f, 0.03f, 0.032f);
                    Lock(s, colour, 0.19f, 155f, longHair ? -0.10f : 0.02f, 158f, 0.03f, 0.032f);
                    break;
                }

                case HairStyle.Ponytail:
                {
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    Tail(s, colour, Point(0.170f, -90f * Deg, 0.012f), -_fwd, (-UpAxis * 0.9f - _fwd * 0.35f).Normalized(), 0.26f);
                    // long strands in front of the ears
                    Lock(s, colour, 0.19f, 12f, -0.07f, 10f, 0.025f, 0.022f);
                    Lock(s, colour, 0.19f, 168f, -0.07f, 170f, 0.025f, 0.022f);
                    break;
                }

                case HairStyle.Short:
                    Cap(s, colour, 0.009f, 0.150f);
                    Bangs(s, colour, Fringe.Short);
                    break;

                case HairStyle.Quiff:
                    Cap(s, colour, 0.008f, 0.150f);
                    // the front swept up and forward over the brow, tallest in the middle
                    foreach (float a in stackalloc[] { 58f, 74f, 90f, 106f, 122f })
                    {
                        var root = Point(0.200f, a * Deg, 0.008f);
                        float tall = 1f - Mathf.Abs(a - 90f) / 110f;
                        var tip = Point(0.255f, a * Deg, 0.065f * tall) + UpAxis * 0.05f * tall * _k;
                        s.Tube(root, tip, 0.042f * _k, 0.014f * _k, colour, 5);
                    }
                    break;

                case HairStyle.Shaggy:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    // short locks falling all round the sides and the back, over the ears
                    for (int i = 0; i < 9; i++)
                    {
                        float a = 150f + i * 30f;
                        Lock(s, colour, 0.20f, a, 0.075f, a + 6f, 0.026f, 0.030f);
                    }
                    Lock(s, colour, 0.20f, 20f, 0.075f, 18f, 0.026f, 0.030f);
                    break;

                case HairStyle.Mohawk:
                {
                    // the sides shaved: a scalp just darker than the skin, then a crest front to back
                    Cap(s, skin.Lerp(colour, 0.35f), 0.007f, 0.150f);
                    ReadOnlySpan<Vector2> crest = [new(0.215f, 90f), new(0.245f, 90f), new(0.262f, 90f), new(0.262f, 270f), new(0.245f, 270f), new(0.215f, 270f), new(0.180f, 270f)];
                    foreach (var c in crest) Spike(s, colour, c.X, c.Y, 0.11f, 0.88f, 0.026f);
                    break;
                }

                case HairStyle.BluntBangs:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Blunt);
                    Fall(s, colour, -0.22f, 0.118f);
                    // the straight side locks of a hime cut, cut at the jaw
                    foreach (float a in stackalloc[] { 22f, 158f })
                        Lock(s, colour, 0.19f, a, 0.0f, a, 0.028f, 0.034f);
                    break;

                case HairStyle.SideSwept:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Swept);
                    Fall(s, colour, 0.0f, 0.110f);
                    break;

                case HairStyle.Twintails:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    foreach (float a in stackalloc[] { 15f, 165f })
                    {
                        var outward = (Point(0.20f, a * Deg) - Centre(0.20f)).Normalized();
                        Tail(s, colour, Point(0.20f, a * Deg, 0.012f), outward, (-UpAxis * 0.95f + outward * 0.10f - _fwd * 0.12f).Normalized(), 0.30f);
                    }
                    break;

                case HairStyle.Bun:
                {
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Short);
                    var root = Point(0.225f, -90f * Deg, 0.010f);
                    var dir = (-_fwd * 0.6f + UpAxis * 0.8f).Normalized();
                    s.Tube(root, root + dir * 0.04f * _k, 0.050f * _k, 0.060f * _k, colour, 8);
                    s.Tube(root + dir * 0.04f * _k, root + dir * 0.09f * _k, 0.060f * _k, 0.025f * _k, colour, 8);
                    break;
                }
            }
        }

        /// <summary>A tail of hair: a tie at <paramref name="root"/>, out along <paramref name="outward"/>, then hanging along <paramref name="hang"/>.</summary>
        private void Tail(MeshScratch s, Color colour, Vector3 root, Vector3 outward, Vector3 hang, float length)
        {
            var tie = root + outward * 0.025f * _k;
            s.Tube(root, tie, 0.022f * _k, colour.Darkened(0.4f), 6);
            var bend = tie + (hang * 0.4f + outward * 0.6f).Normalized() * 0.04f * _k;
            s.Tube(tie, bend, 0.030f * _k, 0.038f * _k, colour, 6);
            s.Tube(bend, bend + hang * length * _k, 0.038f * _k, 0.008f * _k, colour, 6);
        }
    }
}
