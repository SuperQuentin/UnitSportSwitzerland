using Godot;

namespace UnitSport.Avatar;

/// <summary>A figure's build (#394): one skeleton, different flesh on it.</summary>
public enum BodyBuild
{
    Slim = 0,
    Curvy = 1,
    Broad = 2,
}

/// <summary>Hair drawn over the head's crown (#394 prototype).</summary>
public enum HairStyle
{
    None = 0,
    Spiky = 1,
    Bob = 2,
    Ponytail = 3,
    Long = 4,
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
    float Hand, float Head)
{
    public static Physique Of(BodyBuild build) => build switch
    {
        BodyBuild.Curvy => new(
            Crotch: 0.125f, Hip: 0.168f, HipBack: 0.112f, Waist: 0.100f, UnderBust: 0.112f,
            Chest: 0.132f, ChestFront: 0.112f, ChestBack: 0.074f, Shoulder: 0.140f, Neck: 0.044f,
            ThighTop: 0.096f, ThighMid: 0.084f, Knee: 0.052f, Calf: 0.057f, Ankle: 0.033f,
            Deltoid: 0.050f, UpperArm: 0.044f, Elbow: 0.033f, Forearm: 0.038f, Wrist: 0.026f,
            Hand: 0.92f, Head: 1.08f),
        BodyBuild.Broad => new(
            Crotch: 0.118f, Hip: 0.140f, HipBack: 0.090f, Waist: 0.132f, UnderBust: 0.150f,
            Chest: 0.162f, ChestFront: 0.094f, ChestBack: 0.090f, Shoulder: 0.168f, Neck: 0.058f,
            ThighTop: 0.090f, ThighMid: 0.081f, Knee: 0.058f, Calf: 0.063f, Ankle: 0.040f,
            Deltoid: 0.066f, UpperArm: 0.058f, Elbow: 0.044f, Forearm: 0.050f, Wrist: 0.034f,
            Hand: 1.08f, Head: 1.05f),
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
        var head = new Head(r, shoulderSide, shape.Head);
        s.Tube(r.Neck, r.HeadBase + head.UpAxis * 0.04f, shape.Neck, look.Skin, 7);
        head.Draw(s, look.Skin);
        head.Face(s, look.Face, look.Skin);
        head.Ears(s, look.Skin);
        head.Hair(s, look.HairStyle, look.Hair);
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
    /// A head in the anime proportions of the references: a wide cranium over a narrow jaw and a
    /// pointed chin, built as rings up the head's axis; a pixel face over its front.
    /// </summary>
    private sealed class Head
    {
        private readonly Vector3 _base, _side, _fwd;
        public Vector3 UpAxis { get; }
        private readonly float _k;
        private const int Sides = 12;

        // (height above the head's base, half width, front depth, back depth, forward offset), metres at scale 1
        private static readonly Vector4[] Shape =
        {
            new(0.020f, 0.017f, 0.015f, 0.050f), new(0.052f, 0.052f, 0.040f, 0.028f),
            new(0.072f, 0.074f, 0.072f, 0.010f), new(0.081f, 0.084f, 0.088f, 0f),
            new(0.084f, 0.080f, 0.097f, -0.006f), new(0.071f, 0.062f, 0.088f, -0.012f),
            new(0.036f, 0.030f, 0.048f, -0.012f),
        };
        private static readonly float[] Heights = { -0.040f, 0.000f, 0.050f, 0.105f, 0.160f, 0.215f, 0.250f };

        public Head(Rig r, Vector3 shoulderSide, float scale)
        {
            var axis = r.HeadTop - r.HeadBase;
            UpAxis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
            _side = (shoulderSide - UpAxis * shoulderSide.Dot(UpAxis)).Normalized();
            _fwd = _side.Cross(UpAxis);
            _base = r.HeadBase;
            _k = scale * axis.Length() / 0.19f;
        }

        private static Vector4 Profile(float y)
        {
            if (y <= Heights[0]) return Shape[0];
            for (int i = 1; i < Heights.Length; i++)
                if (y <= Heights[i])
                    return Shape[i - 1].Lerp(Shape[i], (y - Heights[i - 1]) / (Heights[i] - Heights[i - 1]));
            return Shape[^1];
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

        /// <summary>The pixel face: a band over the front of the head, from brow to chin, sampling <see cref="FaceAtlas"/>.</summary>
        public void Face(MeshScratch s, int face, Color skin)
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
            s.FaceBand(band, FaceAtlas.Uv(face), FaceMark(skin), Centre(0.10f));
        }

        public void Ears(MeshScratch s, Color skin)
        {
            var basis = new Basis(_side, UpAxis, _fwd);
            foreach (float a in stackalloc[] { 0f, Mathf.Pi })
                s.Box(Point(0.095f, a, 0.006f) - _fwd * 0.004f * _k, new Vector3(0.016f, 0.048f, 0.030f) * _k, skin, basis);
        }

        public void Hair(MeshScratch s, HairStyle style, Color colour)
        {
            if (style == HairStyle.None) return;
            // the cap over the crown, a little proud of the scalp
            var cap = new List<Vector3[]>();
            foreach (float y in stackalloc[] { 0.160f, 0.215f, 0.250f })
                cap.Add(Ring(y, 0.013f));
            cap.Add(Ring(0.276f, 0f));   // above the scalp: the top profile, raised
            var colours = new Color[Sides];
            Array.Fill(colours, colour);
            s.Loft(cap, colours, colour);

            // the fringe: locks from the hairline down over the forehead, stopping above the eyes
            void Lock(float y0, float a0, float y1, float a1, float lift, float r) =>
                s.Tube(Point(y0, a0, 0.010f), Point(y1, a1, lift), r * _k, 0.003f * _k, colour, 4);
            var deg = Mathf.Pi / 180f;
            ReadOnlySpan<float> fringe = [58f, 74f, 90f, 106f, 122f];
            for (int i = 0; i < fringe.Length; i++)
                Lock(0.215f, fringe[i] * deg, style == HairStyle.Spiky ? 0.142f : 0.150f, (fringe[i] + (i - 2) * 4f) * deg, 0.020f, 0.028f);

            switch (style)
            {
                case HairStyle.Spiky:
                    // spikes off the crown, out and up, longer at the back
                    for (int i = 0; i < 9; i++)
                    {
                        float a = (i * 40f + 20f) * deg;
                        var root = Point(0.230f, a, 0.008f);
                        var outward = (root - Centre(0.230f)).Normalized();
                        float back = Mathf.Sin(a) < 0 ? 1.4f : 1f;
                        s.Tube(root, root + (outward * 0.75f + UpAxis * 0.55f).Normalized() * 0.10f * back * _k,
                            0.032f * _k, 0.003f * _k, colour, 4);
                    }
                    Lock(0.20f, 15f * deg, 0.08f, 12f * deg, 0.02f, 0.026f);
                    Lock(0.20f, 165f * deg, 0.08f, 168f * deg, 0.02f, 0.026f);
                    break;

                case HairStyle.Bob:
                case HairStyle.Long:
                {
                    bool longHair = style == HairStyle.Long;
                    float end = longHair ? -0.20f : 0.005f;
                    var top = Centre(0.19f) - _fwd * 0.01f * _k;
                    var bottom = _base + (UpAxis * end - _fwd * (longHair ? 0.03f : 0.012f)) * _k;
                    s.Skirt(top, bottom, 0.100f * _k, (longHair ? 0.125f : 0.108f) * _k, colour, 12,
                        gap: _fwd, gapAngle: 1.05f);
                    // side locks framing the face
                    Lock(0.19f, 25f * deg, longHair ? -0.10f : 0.02f, 22f * deg, 0.03f, 0.032f);
                    Lock(0.19f, 155f * deg, longHair ? -0.10f : 0.02f, 158f * deg, 0.03f, 0.032f);
                    break;
                }

                case HairStyle.Ponytail:
                {
                    var root = Point(0.170f, -90f * deg, 0.012f);
                    var tie = root - _fwd * 0.03f * _k;
                    s.Tube(root, tie, 0.022f * _k, colour.Darkened(0.4f), 6);
                    s.Tube(tie, tie + (-UpAxis * 0.9f - _fwd * 0.35f).Normalized() * 0.26f * _k, 0.038f * _k, 0.008f * _k, colour, 6);
                    // long strands in front of the ears
                    Lock(0.19f, 12f * deg, -0.07f, 10f * deg, 0.025f, 0.022f);
                    Lock(0.19f, 168f * deg, -0.07f, 170f * deg, 0.025f, 0.022f);
                    break;
                }
            }
        }
    }
}
