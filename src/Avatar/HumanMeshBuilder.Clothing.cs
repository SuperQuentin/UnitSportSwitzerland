using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The clothes (#251, docs/notes/avatar/clothing.md): a dressed figure is the same tubes and boxes
/// as the plain one, with the jersey, shorts and shoes swapped for what is worn and the rest laid
/// over the body 4-10 mm proud (closer and it z-fights at distance). Everything hangs off the rig's
/// joints, so clothes follow every pose, the dances and the ragdoll without knowing about them.
/// A garment's finish rides in its colours' alpha (<see cref="Garments.Fx"/>), so it costs nothing
/// here: <c>shaders/avatar.gdshader</c> does the rest.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>A frame on a bone: Y along it, Z the figure's forward made square to it, X = Y × Z (the figure's left).</summary>
    private readonly record struct Frame(Vector3 Side, Vector3 Up, Vector3 Fwd)
    {
        public Basis Basis => new(Side, Up, Fwd);

        public static Frame Along(Vector3 axis)
        {
            var up = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
            var fwd = Vector3.Back - up * up.Dot(Vector3.Back);
            fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Forward.Cross(up).Normalized();
            return new Frame(up.Cross(fwd), up, fwd);
        }

        /// <summary>A frame whose forward is <paramref name="fwd"/> (a foot), up as near world up as it can be.</summary>
        public static Frame Facing(Vector3 fwd)
        {
            fwd = fwd.LengthSquared() > 1e-8f ? fwd.Normalized() : Vector3.Back;
            var up = Vector3.Up - fwd * fwd.Dot(Vector3.Up);
            up = up.LengthSquared() > 1e-6f ? up.Normalized() : Vector3.Forward;
            return new Frame(up.Cross(fwd), up, fwd);
        }
    }

    /// <summary>A garment's three colours with its finish on: the main one always, the trims only on a special.</summary>
    private readonly record struct Cols(Color A, Color B, Color C)
    {
        public static Cols Of(Garment g) => new(
            Garments.Fx(g.A, g.Finish),
            g.IsSpecial ? Garments.Fx(g.B, g.Finish) : g.B,
            g.IsSpecial ? Garments.Fx(g.C, g.Finish) : g.C);
    }

    /// <summary>A colour darker or lighter by <paramref name="k"/>, its finish (alpha) kept.</summary>
    private static Color Shade(Color c, float k) => new(Mathf.Clamp(c.R * k, 0, 1), Mathf.Clamp(c.G * k, 0, 1), Mathf.Clamp(c.B * k, 0, 1), c.A);

    private static readonly Color Leather = new("5a3a22");

    private static bool IsSkirt(Garment? g) => g?.Shape is GarmentShape.HighLowSkirt or GarmentShape.SlitMaxi
        or GarmentShape.RuffleMini or GarmentShape.PleatedSkirt or GarmentShape.LongPleated;

    private static bool IsTrousers(Garment? g) => g?.Shape is GarmentShape.Pants or GarmentShape.Cargo;

    /// <summary><see cref="AppendRig"/> for a figure with something on.</summary>
    private static void AppendDressed(MeshScratch s, HumanPalette p, Rig rig, bool includeLegs, bool helmet,
        Headwear hat, bool body, bool head)
    {
        var o = p.Outfit;
        var top = o[WearSlot.Top];
        // a one-piece takes the bottom slot: the inventory never lets both on, and a packed
        // outfit from elsewhere draws the dress
        var bottom = top is { CoversBottom: true } ? null : o[WearSlot.Bottom];
        if (body)
        {
            AppendTorso(s, p, rig, top, bottom);
            AppendArm(s, p, rig.ShoulderL, rig.ElbowL, rig.WristL, top, o[WearSlot.Hands]);
            AppendArm(s, p, rig.ShoulderR, rig.ElbowR, rig.WristR, top, o[WearSlot.Hands]);
            if (includeLegs)
            {
                AppendLeg(s, p, rig.HipL, rig.KneeL, rig.AnkleL, rig.ToeL, top, bottom, o[WearSlot.Legs], o[WearSlot.Feet]);
                AppendLeg(s, p, rig.HipR, rig.KneeR, rig.AnkleR, rig.ToeR, top, bottom, o[WearSlot.Legs], o[WearSlot.Feet]);
            }
            // with the legs or without (a cyclist's are their own mesh, driven by the cranks)
            AppendSkirt(s, rig, top, bottom, p.Wind);
        }
        if (!head) return;

        // the head exactly as AppendRig draws it
        var headAxis = rig.HeadTop - rig.HeadBase;
        var headBasis = UprightBasis(headAxis);
        var headCentre = (rig.HeadBase + rig.HeadTop) * 0.5f;
        s.Tube(rig.Neck, rig.HeadBase, 0.052f, p.Skin, 6);
        s.Box(headCentre, new Vector3(0.150f, headAxis.Length() + 0.055f, 0.180f), p.Skin, headBasis);

        if (o[WearSlot.Neck] is { } neck) AppendNeckwear(s, rig, neck);
        if (helmet)
            s.Box(headCentre + headBasis.Y * 0.062f, new Vector3(0.168f, 0.085f, 0.205f), p.Helmet, headBasis);
        else if (o[WearSlot.Head] is { } headwear)
            AppendHeadwear(s, headwear, headCentre, headAxis);
        else if (hat != Headwear.None)
            AppendHat(s, hat, headCentre, headAxis);

        var f = Frame.Along(headAxis);
        float half = headAxis.Length() * 0.5f + 0.0275f;   // the head box's half height
        if (o[WearSlot.Face] is { } mask) AppendMask(s, mask, headCentre, f);
        if (o[WearSlot.Eyes] is { } eyes) AppendGlasses(s, eyes, headCentre, f);
        if (o[WearSlot.Ears] is { } ears) AppendPiercings(s, ears, headCentre, f, half);
    }

    // ------------------------------------------------------------------------------------
    // body
    // ------------------------------------------------------------------------------------

    /// <summary>The trunk: hips, belly, chest and the shoulder caps, in the top's and bottom's colours.</summary>
    private static void AppendTorso(MeshScratch s, HumanPalette p, Rig r, Garment? top, Garment? bottom)
    {
        var skin = p.Skin;
        // the hips: the bottom's waistband, a one-piece's body, else the cycling shorts
        Color pelvis = bottom != null ? Cols.Of(bottom).A : top is { CoversBottom: true } ? Cols.Of(top).A : p.Shorts;
        s.Tube(r.Hip, r.Waist, 0.130f, 0.140f, pelvis, 8);

        if (bottom != null) AppendBottomDetail(s, r, bottom);

        if (top == null)
        {
            s.Tube(r.Waist, r.Chest, 0.140f, 0.158f, p.Jersey, 8);
            s.Tube(r.Chest, r.Neck, 0.158f, 0.098f, p.Jersey, 8);
            s.Tube(r.ShoulderL, r.ShoulderR, 0.082f, p.Jersey, 6);
            return;
        }

        var c = Cols.Of(top);
        var ft = Frame.Along(r.Chest - r.Waist);
        var fn = Frame.Along(r.Neck - r.Chest);
        // a point on the front of the belly-chest tube, t from the waist (0) to the chest (1), lifted proud
        Vector3 Front(float t, float proud = 0.006f) =>
            r.Waist.Lerp(r.Chest, t) + ft.Fwd * (Mathf.Lerp(0.140f, 0.158f, t) + proud);
        Vector3 Upper(float t, float proud = 0.006f) =>
            r.Chest.Lerp(r.Neck, t) + fn.Fwd * (Mathf.Lerp(0.158f, 0.098f, t) + proud);

        void Trunk(Color mid, Color high, Color caps)
        {
            s.Tube(r.Waist, r.Chest, 0.140f, 0.158f, mid, 8);
            s.Tube(r.Chest, r.Neck, 0.158f, 0.098f, high, 8);
            s.Tube(r.ShoulderL, r.ShoulderR, 0.082f, caps, 6);
        }

        switch (top.Shape)
        {
            case GarmentShape.TShirt:
                Trunk(c.A, c.A, c.A);
                break;

            case GarmentShape.PrintTee:
                Trunk(c.A, c.A, c.A);
                // the print on the chest: a block and a smaller one inside it (a band logo, a heart)
                s.Box(Front(0.62f, 0.004f), new Vector3(0.13f, 0.11f, 0.012f), c.B, ft.Basis);
                s.Box(Front(0.62f, 0.010f), new Vector3(0.06f, 0.05f, 0.012f), c.C, ft.Basis);
                break;

            case GarmentShape.Polo:
                Trunk(c.A, c.A, c.A);
                // collar round the neck, the button placket, the small emblem on the chest
                s.Tube(r.Chest.Lerp(r.Neck, 0.78f), r.Neck + fn.Up * 0.02f, 0.118f, 0.104f, c.C, 8);
                s.Box(Upper(0.38f), new Vector3(0.028f, 0.10f, 0.012f), c.C, fn.Basis);
                foreach (float t in new[] { 0.30f, 0.50f })
                    s.Box(Upper(t, 0.012f), new Vector3(0.012f, 0.012f, 0.008f), c.B, fn.Basis);
                s.Box(Front(0.88f) + ft.Side * 0.065f, new Vector3(0.030f, 0.018f, 0.012f), c.B, ft.Basis);
                break;

            case GarmentShape.Marcel:
                // a tank top: bare shoulders, a scoop neck, a strap over each shoulder
                Trunk(c.A, skin, skin);
                s.Tube(r.Chest, r.Chest.Lerp(r.Neck, 0.42f), 0.164f, 0.140f, c.A, 8);
                foreach (float t in new[] { 0.28f, 0.72f })
                {
                    var at = r.ShoulderL.Lerp(r.ShoulderR, t);
                    var along = (r.ShoulderR - r.ShoulderL).Normalized() * 0.014f;
                    s.Tube(at - along, at + along, 0.088f, c.A, 6);
                }
                break;

            case GarmentShape.Corset:
                // strapless: bare from the bust up, buckled straps across the front, a zip down the middle
                Trunk(c.A, skin, skin);
                s.Tube(r.Chest, r.Chest.Lerp(r.Neck, 0.24f), 0.164f, 0.150f, c.A, 8);
                var strap = Shade(c.A, 1.6f);
                foreach (float t in new[] { 0.12f, 0.38f, 0.64f, 0.90f })
                {
                    s.Box(Front(t, 0.004f), new Vector3(0.15f, 0.018f, 0.012f), strap, ft.Basis);
                    s.Box(Front(t, 0.010f) + ft.Side * 0.035f, new Vector3(0.026f, 0.024f, 0.010f), c.B, ft.Basis);
                }
                s.Box(Front(0.55f, 0.008f), new Vector3(0.008f, 0.27f, 0.008f), c.B, ft.Basis);
                break;

            case GarmentShape.CropTop:
            {
                bool goth = top.Style == GarmentStyle.Gothic;
                // the belly bare, the top from half way up to a high neck
                Trunk(skin, c.A, goth ? skin : c.A);
                s.Tube(r.Waist.Lerp(r.Chest, 0.5f), r.Chest, 0.150f, 0.164f, c.A, 8);
                if (goth)
                {
                    // two chains slung across the bare belly from the hem
                    for (int row = 0; row < 2; row++)
                        for (int i = 0; i <= 8; i++)
                        {
                            float u = i / 8f * 2f - 1f;
                            float sag = (1f - u * u) * (0.045f + row * 0.03f);
                            var at = r.Waist.Lerp(r.Chest, 0.48f) + ft.Fwd * (0.152f - 0.01f * u * u)
                                + ft.Side * u * 0.11f - ft.Up * sag;
                            s.Box(at, new Vector3(0.013f, 0.013f, 0.013f), c.B, ft.Basis);
                        }
                }
                else
                    AppendBow(s, Upper(0.15f, 0.012f), fn, 0.55f, c.C, c.C);
                break;
            }

            case GarmentShape.Longsleeve:
                Banded(s, r.Waist, r.Chest, 0.140f, 0.158f, c.A, c.B, 5);
                Banded(s, r.Chest, r.Neck, 0.158f, 0.098f, c.B, c.A, 3);
                s.Tube(r.ShoulderL, r.ShoulderR, 0.082f, c.A, 6);
                break;

            case GarmentShape.Hoodie:
            {
                Trunk(c.A, c.A, c.A);
                // the hood lying behind the neck, the pocket, the drawstrings
                s.Box(r.Neck - fn.Fwd * 0.085f + fn.Up * 0.005f, new Vector3(0.20f, 0.12f, 0.09f), Shade(c.A, 0.9f), fn.Basis);
                s.Box(Front(0.22f, 0.004f), new Vector3(0.18f, 0.08f, 0.014f), Shade(c.A, 0.85f), ft.Basis);
                foreach (float x in new[] { -0.03f, 0.03f })
                    s.Box(Upper(0.55f, 0.008f) + fn.Side * x - fn.Up * 0.045f, new Vector3(0.008f, 0.08f, 0.008f), c.B, fn.Basis);
                break;
            }

            case GarmentShape.CroppedJacket:
            {
                // a white blouse under a short blue jacket; jabot and brooch at the throat, a strap across
                Trunk(c.B, c.A, c.A);
                s.Tube(r.Waist.Lerp(r.Chest, 0.42f), r.Chest, 0.152f, 0.168f, c.A, 8);
                s.Box(Upper(0.62f, 0.010f), new Vector3(0.055f, 0.075f, 0.022f), c.B, fn.Basis);
                s.Box(Upper(0.72f, 0.024f), new Vector3(0.024f, 0.024f, 0.012f), c.C, fn.Basis);
                s.Tube(Front(0.98f, 0.016f) - ft.Side * 0.075f, Front(0.45f, 0.020f) + ft.Side * 0.11f, 0.008f, Leather, 4);
                s.Tube(Front(0.70f, 0.020f), Front(0.70f, 0.030f), 0.020f, new Color("e0b848"), 8);
                break;
            }

            case GarmentShape.Robe:
            case GarmentShape.Dress:
            {
                Trunk(c.A, c.A, c.A);
                if (top.Shape == GarmentShape.Robe)
                {
                    // a high collar standing behind the neck, a belt at the waist
                    s.Box(r.Neck - fn.Fwd * 0.07f + fn.Up * 0.045f, new Vector3(0.21f, 0.10f, 0.02f), c.B, fn.Basis);
                    s.Tube(r.Waist - ft.Up * 0.012f, r.Waist + ft.Up * 0.016f, 0.147f, c.C, 8);
                }
                else
                {
                    // a white collar, a bow at the chest
                    s.Tube(r.Chest.Lerp(r.Neck, 0.80f), r.Neck + fn.Up * 0.015f, 0.112f, 0.100f, c.B, 8);
                    AppendBow(s, Upper(0.25f, 0.012f), fn, 0.7f, c.C, c.C);
                }
                break;
            }

            default:
                Trunk(c.A, c.A, c.A);
                break;
        }
    }

    /// <summary>What sits on the hips with a bottom on: drawstrings, a belt, pockets.</summary>
    private static void AppendBottomDetail(MeshScratch s, Rig r, Garment b)
    {
        var c = Cols.Of(b);
        var f = Frame.Along(r.Waist - r.Hip);
        Vector3 Front(float t, float proud) => r.Hip.Lerp(r.Waist, t) + f.Fwd * (Mathf.Lerp(0.130f, 0.140f, t) + proud);
        switch (b.Shape)
        {
            case GarmentShape.Shorts:
                foreach (float x in new[] { -0.02f, 0.02f })
                    s.Box(Front(0.65f, 0.008f) + f.Side * x, new Vector3(0.008f, 0.07f, 0.008f), c.B, f.Basis);
                break;
            case GarmentShape.Pants:
            case GarmentShape.Cargo:
                // a belt with its buckle
                s.Tube(r.Waist - f.Up * 0.028f, r.Waist - f.Up * 0.004f, 0.146f, Shade(c.A, 0.55f), 8);
                s.Box(Front(0.88f, 0.012f), new Vector3(0.035f, 0.026f, 0.010f), c.B, f.Basis);
                break;
            case GarmentShape.SlitMaxi:
            case GarmentShape.HighLowSkirt:
                // a belt slung low, rings at the hips
                s.Tube(r.Waist - f.Up * 0.03f, r.Waist - f.Up * 0.008f, 0.150f, Shade(c.C, 1.4f), 8);
                s.Box(Front(0.80f, 0.020f), new Vector3(0.034f, 0.028f, 0.010f), c.B, f.Basis);
                foreach (float x in new[] { -1f, 1f })
                    s.Ring(r.Hip.Lerp(r.Waist, 0.5f) + f.Side * x * 0.15f + f.Fwd * 0.03f, f.Side, 0.010f, 0.017f, 0.006f, c.B, 8);
                break;
        }
    }

    /// <summary>One arm: sleeve to whatever length the top has, then what is on the hand.</summary>
    private static void AppendArm(MeshScratch s, HumanPalette p, Vector3 shoulder, Vector3 elbow, Vector3 wrist,
        Garment? top, Garment? hands)
    {
        var skin = p.Skin;
        if (top == null)
        {
            s.Tube(shoulder, elbow, 0.058f, 0.045f, p.Jersey, 6);
            s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
        }
        else
        {
            var c = Cols.Of(top);
            switch (top.Shape)
            {
                case GarmentShape.TShirt:
                case GarmentShape.PrintTee:
                case GarmentShape.Polo:
                    s.Tube(shoulder, elbow, 0.058f, 0.045f, skin, 6);
                    s.Tube(shoulder, shoulder.Lerp(elbow, 0.55f), 0.064f, 0.056f, c.A, 6);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
                case GarmentShape.Marcel:
                case GarmentShape.Corset:
                    s.Tube(shoulder, elbow, 0.058f, 0.045f, skin, 6);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
                case GarmentShape.CropTop when top.Style == GarmentStyle.Gothic:
                    s.Tube(shoulder, elbow, 0.058f, 0.045f, skin, 6);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
                case GarmentShape.CropTop:
                case GarmentShape.Dress:
                    // a short puffed sleeve
                    s.Tube(shoulder, elbow, 0.058f, 0.045f, skin, 6);
                    s.Tube(shoulder, shoulder.Lerp(elbow, 0.42f), 0.076f, 0.068f, c.A, 7);
                    s.Tube(shoulder.Lerp(elbow, 0.40f), shoulder.Lerp(elbow, 0.46f), 0.064f, c.B, 7);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
                case GarmentShape.Longsleeve:
                    Banded(s, shoulder, elbow, 0.058f, 0.045f, c.A, c.B, 3);
                    Banded(s, elbow, wrist, 0.045f, 0.035f, c.B, c.A, 3);
                    break;
                case GarmentShape.Hoodie:
                    s.Tube(shoulder, elbow, 0.060f, 0.047f, c.A, 6);
                    s.Tube(elbow, wrist, 0.047f, 0.037f, c.A, 6);
                    s.Tube(elbow.Lerp(wrist, 0.86f), wrist, 0.041f, c.B, 6);
                    break;
                case GarmentShape.CroppedJacket:
                    // puffed at the shoulder, gathered at the elbow, a white cuff
                    s.Tube(shoulder, elbow, 0.072f, 0.056f, c.A, 7);
                    s.Tube(elbow, wrist, 0.050f, 0.038f, c.A, 6);
                    s.Tube(elbow.Lerp(wrist, 0.85f), wrist, 0.042f, c.B, 6);
                    break;
                case GarmentShape.Robe:
                {
                    // a bell sleeve flaring past the wrist, its lining showing
                    s.Tube(shoulder, elbow, 0.062f, 0.050f, c.A, 7);
                    var past = wrist + (wrist - elbow).Normalized() * 0.03f;
                    s.Skirt(elbow, past, 0.050f, 0.092f, c.A, 8);
                    s.Skirt(elbow.Lerp(past, 0.92f), past, 0.082f, 0.088f, c.B, 8);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
                }
                default:
                    s.Tube(shoulder, elbow, 0.058f, 0.045f, c.A, 6);
                    s.Tube(elbow, wrist, 0.045f, 0.033f, skin, 6);
                    break;
            }
        }
        s.Box(wrist, new Vector3(0.055f, 0.075f, 0.085f), skin);
        if (hands != null) AppendHandwear(s, hands, elbow, wrist);
    }

    private static void AppendHandwear(MeshScratch s, Garment g, Vector3 elbow, Vector3 wrist)
    {
        var c = Cols.Of(g);
        switch (g.Shape)
        {
            case GarmentShape.ArmWarmers:
                s.Tube(elbow.Lerp(wrist, 0.05f), wrist, 0.050f, 0.038f, c.A, 6);
                s.Box(wrist + new Vector3(0, 0.012f, 0), new Vector3(0.062f, 0.052f, 0.092f), c.A);
                break;
            case GarmentShape.StripedWarmers:
                Banded(s, elbow.Lerp(wrist, 0.05f), wrist, 0.050f, 0.039f, c.A, c.B, 5);
                break;
            case GarmentShape.Fingerless:
                s.Tube(elbow.Lerp(wrist, 0.75f), wrist, 0.040f, 0.038f, c.A, 6);
                s.Box(wrist + new Vector3(0, 0.012f, 0), new Vector3(0.062f, 0.052f, 0.092f), c.A);
                s.Box(wrist + new Vector3(0, 0.03f, 0), new Vector3(0.066f, 0.010f, 0.096f), c.B);
                break;
            case GarmentShape.Paws:
                s.Tube(elbow.Lerp(wrist, 0.8f), wrist, 0.044f, 0.042f, c.A, 6);
                s.Box(wrist, new Vector3(0.074f, 0.086f, 0.098f), c.A);
                foreach (var pad in new[] { new Vector3(-0.022f, -0.028f, 0.05f), new Vector3(0, -0.032f, 0.05f), new Vector3(0.022f, -0.028f, 0.05f), new Vector3(0, -0.005f, 0.05f) })
                    s.Box(wrist + pad, new Vector3(0.016f, 0.016f, 0.008f), c.B);
                break;
            case GarmentShape.Gloves:
                s.Tube(elbow.Lerp(wrist, 0.70f), wrist, 0.042f, 0.040f, c.A, 6);
                s.Box(wrist, new Vector3(0.062f, 0.082f, 0.092f), c.A);
                break;
        }
    }

    /// <summary>
    /// One leg: thigh and shin in the bottom's colours (skin under a skirt), then socks and shoes
    /// over them. Trousers hide the socks, so they are left out.
    /// </summary>
    private static void AppendLeg(MeshScratch s, HumanPalette p, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe,
        Garment? top, Garment? bottom, Garment? legs, Garment? feet)
    {
        var skin = p.Skin;
        Color thigh, shin = skin;
        if (bottom == null) thigh = top is { CoversBottom: true } ? skin : p.Shorts;
        else if (IsTrousers(bottom)) thigh = shin = Cols.Of(bottom).A;
        else thigh = skin;

        s.Tube(hip, knee, 0.088f, 0.062f, thigh, 6);
        s.Tube(knee, ankle, 0.062f, 0.040f, shin, 6);

        if (bottom != null)
        {
            var c = Cols.Of(bottom);
            if (bottom.Shape == GarmentShape.Shorts)
                s.Tube(hip, hip.Lerp(knee, 0.6f), 0.096f, 0.082f, c.A, 6);
            else if (bottom.Shape == GarmentShape.Cargo)
            {
                // a pocket on the outside of each thigh
                var fl = Frame.Along(knee - hip);
                var outward = hip.X < 0 ? -fl.Side : fl.Side;
                s.Box(hip.Lerp(knee, 0.5f) + outward * 0.077f, new Vector3(0.012f, 0.10f, 0.08f), c.B, fl.Basis);
            }
        }

        if (legs != null && !IsTrousers(bottom)) AppendLegwear(s, legs, hip, knee, ankle);
        AppendFootwear(s, p, feet, legs, bottom, knee, ankle, toe);
    }

    private static void AppendLegwear(MeshScratch s, Garment g, Vector3 hip, Vector3 knee, Vector3 ankle)
    {
        var c = Cols.Of(g);
        // radii of the bare leg, plus a few mm
        float Thigh(float t) => Mathf.Lerp(0.088f, 0.062f, t) + 0.004f;
        float Shin(float t) => Mathf.Lerp(0.062f, 0.040f, t) + 0.004f;
        switch (g.Shape)
        {
            case GarmentShape.ThighHigh:
                s.Tube(hip.Lerp(knee, 0.45f), knee, Thigh(0.45f), Thigh(1f), c.A, 6);
                s.Tube(knee, ankle, Shin(0f), Shin(1f), c.A, 6);
                s.Tube(hip.Lerp(knee, 0.43f), hip.Lerp(knee, 0.52f), Thigh(0.43f) + 0.003f, Thigh(0.52f) + 0.003f, c.C, 6);
                break;
            case GarmentShape.StripedThighHigh:
                Banded(s, hip.Lerp(knee, 0.45f), knee, Thigh(0.45f), Thigh(1f), c.A, c.B, 3);
                Banded(s, knee, ankle, Shin(0f), Shin(1f), c.A, c.B, 6);
                break;
            case GarmentShape.Fishnets:
                s.Tube(hip.Lerp(knee, 0.04f), knee, Thigh(0.04f) - 0.001f, Thigh(1f) - 0.001f, c.A, 6);
                s.Tube(knee, ankle, Shin(0f) - 0.001f, Shin(1f) - 0.001f, c.A, 6);
                break;
            case GarmentShape.KneeSock:
                s.Tube(knee.Lerp(ankle, 0.1f), ankle, Shin(0.1f), Shin(1f), c.A, 6);
                s.Tube(knee.Lerp(ankle, 0.08f), knee.Lerp(ankle, 0.2f), Shin(0.08f) + 0.004f, Shin(0.2f) + 0.004f, c.B, 6);
                break;
        }
    }

    private static void AppendFootwear(MeshScratch s, HumanPalette p, Garment? g, Garment? legs, Garment? bottom,
        Vector3 knee, Vector3 ankle, Vector3 toe)
    {
        var dir = toe - ankle;
        if (dir.LengthSquared() < 1e-6f) return;
        if (g == null)
        {
            s.Tube(ankle, toe, 0.048f, 0.038f, p.Shoes, 5);
            return;
        }
        var c = Cols.Of(g);
        var ff = Frame.Facing(dir);
        float len = dir.Length();
        void Sole(float thick, Color colour, float extra) =>
            s.Box(ankle.Lerp(toe, 0.5f) + dir.Normalized() * (extra * 0.3f) - ff.Up * (0.036f + thick * 0.5f - 0.012f),
                new Vector3(0.094f, thick, len + extra), colour, ff.Basis);
        switch (g.Shape)
        {
            case GarmentShape.Sneakers:
                s.Tube(ankle, toe + dir.Normalized() * 0.03f, 0.053f, 0.042f, c.A, 6);
                Sole(0.024f, c.B, 0.08f);
                s.Tube(ankle, ankle + (knee - ankle).Normalized() * 0.04f, 0.050f, c.B, 6);   // the collar
                break;
            case GarmentShape.PlatformBoots:
            {
                s.Tube(ankle, toe + dir.Normalized() * 0.04f, 0.060f, 0.050f, c.A, 6);
                Sole(0.05f, Shade(c.A, 0.7f), 0.10f);
                // knee-high, buckled three times up the shaft
                var top = ankle.Lerp(knee, 0.82f);
                s.Tube(ankle, top, 0.054f, 0.070f, c.A, 7);
                foreach (float t in new[] { 0.22f, 0.48f, 0.74f })
                {
                    var at = ankle.Lerp(top, t);
                    var along = (top - ankle).Normalized() * 0.010f;
                    s.Tube(at - along, at + along, Mathf.Lerp(0.054f, 0.070f, t) + 0.006f, c.B, 7);
                }
                break;
            }
            case GarmentShape.CombatBoots:
            {
                s.Tube(ankle, toe + dir.Normalized() * 0.035f, 0.058f, 0.048f, c.A, 6);
                Sole(0.03f, c.B, 0.09f);
                var top = ankle.Lerp(knee, 0.34f);
                s.Tube(ankle, top, 0.052f, 0.058f, c.A, 7);
                var along = (top - ankle).Normalized() * 0.006f;
                s.Tube(top - along * 2f, top, 0.062f, c.B, 7);
                break;
            }
            case GarmentShape.MaryJanes:
            {
                s.Tube(ankle, toe + dir.Normalized() * 0.02f, 0.050f, 0.040f, c.A, 6);
                Sole(0.016f, Shade(c.A, 0.8f), 0.05f);
                var strapAt = ankle.Lerp(toe, 0.30f);
                var along = dir.Normalized() * 0.008f;
                s.Tube(strapAt - along, strapAt + along, 0.054f, c.A, 6);
                // frilly white ankle socks, unless there are stockings on
                if (legs == null && !IsTrousers(bottom))
                    s.Tube(ankle, ankle + (knee - ankle).Normalized() * 0.06f, 0.047f, 0.045f, c.B, 6);
                break;
            }
            default:
                s.Tube(ankle, toe, 0.050f, 0.040f, c.A, 5);
                break;
        }
    }

    /// <summary>
    /// A skirt, or the lower half of a robe or dress: an open cone from the waist, its hem following
    /// the knees or the ankles so it swings with the stride, blown back by <paramref name="wind"/>
    /// (<see cref="HumanPalette.Wind"/>) and fluttering faster the harder it blows.
    /// </summary>
    private static void AppendSkirt(MeshScratch s, Rig r, Garment? top, Garment? bottom, Vector3 wind)
    {
        var knees = (r.KneeL + r.KneeR) * 0.5f;
        var ankles = (r.AnkleL + r.AnkleR) * 0.5f;
        // how wide the legs are apart at the hem, so a stride does not poke through it
        float spreadK = (r.KneeL - r.KneeR).Length() * 0.5f;
        float spreadA = (r.AnkleL - r.AnkleR).Length() * 0.5f;

        // full streaming by ~50 km/h; a walk barely stirs it
        float speed = wind.Length();
        float gust = Mathf.Clamp(speed / 14f, 0f, 1f);
        var downwind = speed > 0.05f ? wind / speed : Vector3.Zero;
        float ripple = speed > 0.05f ? 0.03f + 0.13f * gust : 0f;
        float phase = Time.GetTicksMsec() / 1000f * (5f + 1.1f * Mathf.Min(speed, 30f));
        // the hem carried downwind, never further than the skirt is long, nor up past the waist
        Vector3 Blown(Vector3 from, Vector3 hem)
        {
            if (gust <= 0f) return hem;
            float length = (hem - from).Length();
            var shifted = hem + downwind * length * 0.65f * gust;
            return from + (shifted - from).Normalized() * length;
        }
        void Cone(Vector3 from, Vector3 hem, float ra, float rb, Color colour, int sides, Vector3 gap = default, float gapAngle = 0f) =>
            s.Skirt(from, Blown(r.Waist, hem) + (from - r.Waist), ra, rb, colour, sides, gap, gapAngle, ripple, phase);

        if (top is { CoversBottom: true })
        {
            var c = Cols.Of(top);
            if (top.Shape == GarmentShape.Robe)
            {
                var hem = ankles + Vector3.Up * 0.035f;
                float rh = Mathf.Max(0.30f, spreadA + 0.07f);
                Cone(r.Waist, hem, 0.148f, rh, c.A, 12);
                Cone(r.Waist.Lerp(hem, 0.95f), hem, rh * 0.97f + 0.004f, rh + 0.004f, c.B, 12);
            }
            else
            {
                var hem = r.Hip.Lerp(knees, 0.85f);
                float rh = Mathf.Max(0.29f, spreadK + 0.12f);
                Cone(r.Waist, hem, 0.150f, rh, c.A, 12);
                // the petticoat frothing out under it
                Cone(r.Waist.Lerp(hem, 0.78f), hem - Vector3.Up * 0.035f, rh * 0.9f, rh + 0.03f, c.B, 12);
            }
            return;
        }
        if (!IsSkirt(bottom)) return;

        var b = Cols.Of(bottom!);
        switch (bottom!.Shape)
        {
            case GarmentShape.PleatedSkirt:
                Cone(r.Waist, r.Hip.Lerp(knees, 0.55f), 0.150f, Mathf.Max(0.25f, spreadK + 0.09f), b.A, 14);
                break;
            case GarmentShape.RuffleMini:
            {
                var hem = r.Hip.Lerp(knees, 0.40f);
                float rh = Mathf.Max(0.22f, spreadK + 0.08f);
                Cone(r.Waist, hem, 0.150f, rh, b.A, 12);
                // a second tier of ruffle under the first
                Cone(r.Waist.Lerp(hem, 0.55f), hem - Vector3.Up * 0.045f, rh * 0.95f, rh + 0.035f, b.B, 12);
                break;
            }
            case GarmentShape.HighLowSkirt:
                // the axis leans back, so the hem rides high in front and trails low behind
                Cone(r.Waist, r.Hip.Lerp(knees, 0.85f) + new Vector3(0, -0.04f, -0.14f), 0.152f, Mathf.Max(0.27f, spreadK + 0.11f), b.A, 14);
                break;
            case GarmentShape.SlitMaxi:
                // to the ankles, with a slit up the front of the right leg (−X)
                Cone(r.Waist, ankles + Vector3.Up * 0.05f, 0.152f, Mathf.Max(0.29f, spreadA + 0.08f), b.A, 14,
                    gap: new Vector3(-0.55f, 0, 1f), gapAngle: 0.42f);
                break;
            case GarmentShape.LongPleated:
            {
                // to mid-calf, brown straps running down it front and back
                var hem = Blown(r.Waist, r.Hip.Lerp(ankles, 0.80f));
                float rh = Mathf.Max(0.29f, spreadK + 0.12f);
                s.Skirt(r.Waist, hem, 0.150f, rh, b.A, 16, ripple: ripple, phase: phase);
                var axis = Frame.Along(r.Waist - hem);
                foreach (float a in new[] { -0.45f, 0.45f, Mathf.Pi - 0.45f, Mathf.Pi + 0.45f })
                {
                    var dir = axis.Fwd * Mathf.Cos(a) + axis.Side * Mathf.Sin(a);
                    s.Tube(r.Waist + dir * 0.160f, hem + dir * (rh + 0.006f), 0.007f, b.B, 4);
                }
                break;
            }
        }
    }

    /// <summary>Whether the outfit has something that blows in the wind: a skirt, a robe, a dress.</summary>
    public static bool Flutters(Outfit o) =>
        o[WearSlot.Top] is { CoversBottom: true } || IsSkirt(o[WearSlot.Bottom]);

    /// <summary>
    /// One leg in the clothes of <paramref name="p"/>, from free joints: a cyclist's, driven by the
    /// cranks. The bare leg with the cycling shorts and shoes when nothing is worn.
    /// </summary>
    public static void AppendLeg(MeshScratch s, HumanPalette p, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe)
    {
        var o = p.Outfit;
        if (o.IsEmpty)
        {
            Leg(s, p, hip, knee, ankle, toe);
            return;
        }
        var top = o[WearSlot.Top];
        var bottom = top is { CoversBottom: true } ? null : o[WearSlot.Bottom];
        AppendLeg(s, p, hip, knee, ankle, toe, top, bottom, o[WearSlot.Legs], o[WearSlot.Feet]);
    }

    // ------------------------------------------------------------------------------------
    // head and neck
    // ------------------------------------------------------------------------------------

    private static void AppendNeckwear(MeshScratch s, Rig r, Garment g)
    {
        var c = Cols.Of(g);
        var axis = r.HeadBase - r.Neck;
        var f = Frame.Along(axis);
        var lo = r.Neck.Lerp(r.HeadBase, 0.30f);
        var hi = r.Neck.Lerp(r.HeadBase, 0.62f);
        var mid = (lo + hi) * 0.5f;
        switch (g.Shape)
        {
            case GarmentShape.SpikedChoker:
                s.Tube(lo, hi, 0.060f, c.A, 8);
                for (int i = -3; i <= 3; i++)
                {
                    float a = i * 0.42f;
                    var dir = f.Fwd * Mathf.Cos(a) + f.Side * Mathf.Sin(a);
                    s.Tube(mid + dir * 0.055f, mid + dir * 0.085f, 0.008f, 0.0005f, c.B, 4);
                }
                break;
            case GarmentShape.HeartChoker:
                s.Tube(lo, hi, 0.058f, c.A, 8);
                AppendHeart(s, mid + f.Fwd * 0.066f - f.Up * 0.025f, f, 0.030f, c.B);
                break;
            case GarmentShape.BellCollar:
                s.Tube(lo, hi, 0.060f, c.A, 8);
                var bell = mid + f.Fwd * 0.07f - f.Up * 0.03f;
                s.Tube(bell - f.Up * 0.02f, bell + f.Up * 0.01f, 0.022f, 0.012f, c.B, 8);
                s.Box(bell - f.Up * 0.012f + f.Fwd * 0.02f, new Vector3(0.014f, 0.004f, 0.006f), new Color("3a2a10"), f.Basis);
                break;
            case GarmentShape.Chain:
            {
                // hangs from the base of the neck onto the chest, a small cross at the bottom
                var ft = Frame.Along(r.Neck - r.Chest);
                var start = r.Neck - ft.Up * 0.01f;
                for (int i = 0; i <= 10; i++)
                {
                    float u = i / 10f * 2f - 1f;
                    float a = u * 1.25f;
                    var dir = ft.Fwd * Mathf.Cos(a) + ft.Side * Mathf.Sin(a);
                    var at = start + dir * (0.085f + 0.035f * (1f - u * u)) - ft.Up * (0.075f * (1f - u * u));
                    s.Box(at, new Vector3(0.011f, 0.011f, 0.011f), c.A, ft.Basis);
                }
                var cross = start + ft.Fwd * 0.128f - ft.Up * 0.105f;
                s.Box(cross, new Vector3(0.010f, 0.045f, 0.008f), c.A, ft.Basis);
                s.Box(cross + ft.Up * 0.008f, new Vector3(0.030f, 0.010f, 0.008f), c.A, ft.Basis);
                break;
            }
        }
    }

    /// <summary>Clothes on the head (in place of a hat), built in the head's frame like <see cref="AppendHat"/>.</summary>
    private static void AppendHeadwear(MeshScratch s, Garment g, Vector3 centre, Vector3 axis)
    {
        var c = Cols.Of(g);
        var f = Frame.Along(axis);
        var (side, up, fwd) = (f.Side, f.Up, f.Fwd);
        var top = centre + up * (axis.Length() * 0.5f + 0.0275f);

        void Ears(Color outer, Color inner, float height)
        {
            foreach (float sgn in new[] { -1f, 1f })
            {
                var root = top - up * 0.012f + side * sgn * 0.052f + fwd * 0.005f;
                var tip = root + up * height + side * sgn * 0.016f;
                s.Tube(root, tip, 0.036f, 0.003f, outer, 3);
                s.Tube(root + fwd * 0.012f + up * 0.006f, tip + fwd * 0.006f - up * 0.016f, 0.020f, 0.002f, inner, 3);
            }
        }

        float half = axis.Length() * 0.5f + 0.0275f;   // the head box's half height; it is 0.150 wide
        // a band over the crown from ear to ear, hugging the box-shaped head (an arc would sink into its corners)
        Vector3[] BandPath(float forward)
        {
            Vector3 P(float x, float y) => centre + fwd * forward + side * x + up * y;
            return new[]
            {
                P(-0.085f, -0.01f), P(-0.085f, half - 0.03f), P(-0.055f, half + 0.012f), P(0f, half + 0.017f),
                P(0.055f, half + 0.012f), P(0.085f, half - 0.03f), P(0.085f, -0.01f),
            };
        }
        void Band(Color colour, float forward)
        {
            var path = BandPath(forward);
            for (int i = 0; i + 1 < path.Length; i++) s.Tube(path[i], path[i + 1], 0.010f, colour, 5);
        }

        void Cups(Color shell, Color cushion)
        {
            foreach (float sgn in new[] { -1f, 1f })
            {
                var at = centre + side * sgn * 0.080f;
                s.Tube(at, at + side * sgn * 0.030f, 0.044f, 0.040f, shell, 8);
                s.Tube(at - side * sgn * 0.004f, at + side * sgn * 0.006f, 0.046f, cushion, 8);
            }
        }

        switch (g.Shape)
        {
            case GarmentShape.CatEars:
                Band(c.A, 0.01f);
                Ears(c.A, c.B, 0.075f);
                break;
            case GarmentShape.CatHeadset:
                Band(c.A, 0f);
                Cups(c.A, c.B);
                Ears(c.A, c.C, 0.065f);
                // the lit rim round each cup
                foreach (float sgn in new[] { -1f, 1f })
                    s.Ring(centre + side * sgn * 0.112f, side, 0.026f, 0.034f, 0.006f, Garments.Fx(g.C, Finish.Neon), 10);
                break;
            case GarmentShape.Headset:
            {
                Band(c.A, 0f);
                Cups(c.A, c.B);
                // the boom mic round to the mouth
                var root = centre + side * 0.105f - up * 0.02f;
                var bend = root + fwd * 0.06f - up * 0.035f;
                var mic = centre + fwd * 0.10f - up * 0.065f + side * 0.035f;
                s.Tube(root, bend, 0.005f, c.A, 4);
                s.Tube(bend, mic, 0.005f, c.A, 4);
                s.Box(mic, new Vector3(0.018f, 0.014f, 0.016f), c.C, f.Basis);
                break;
            }
            case GarmentShape.BunnyEars:
                Band(c.A, 0.01f);
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var root = top - up * 0.01f + side * sgn * 0.035f;
                    var knee = root + up * 0.13f + side * sgn * 0.025f;
                    // the left one flops forward
                    var tip = sgn > 0 ? knee + fwd * 0.07f + up * 0.02f + side * 0.02f : knee + up * 0.10f + side * sgn * 0.01f;
                    s.Tube(root, knee, 0.022f, 0.026f, c.A, 6);
                    s.Tube(knee, tip, 0.026f, 0.012f, c.A, 6);
                    s.Tube(root + fwd * 0.016f + up * 0.02f, knee + fwd * 0.018f, 0.010f, 0.013f, c.B, 5);
                }
                break;
            case GarmentShape.Horns:
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var root = top - up * 0.01f + side * sgn * 0.048f + fwd * 0.035f;
                    var a = root + up * 0.05f + side * sgn * 0.022f;
                    var b = a + up * 0.035f + side * sgn * 0.005f - fwd * 0.03f;
                    var tip = b + up * 0.012f - fwd * 0.045f - side * sgn * 0.008f;
                    s.Tube(root - up * 0.004f, root + up * 0.006f, 0.026f, c.B, 6);
                    s.Tube(root, a, 0.022f, 0.016f, c.A, 6);
                    s.Tube(a, b, 0.016f, 0.010f, c.A, 6);
                    s.Tube(b, tip, 0.010f, 0.001f, c.A, 5);
                }
                break;
            case GarmentShape.Bow:
                AppendBow(s, top + fwd * 0.03f + side * 0.045f + up * 0.01f, f, 1.5f, c.A, c.B);
                break;
            case GarmentShape.LaceHeadband:
            {
                Band(c.A, 0.035f);
                // the white lace frill standing along the band's top, in front of it
                var path = BandPath(0.035f);
                for (int i = 1; i < path.Length - 1; i++)
                    foreach (var at in new[] { path[i], (path[i] + path[i + 1]) * 0.5f })
                        if (at.DistanceTo(path[^1]) > 0.05f)
                            s.Box(at + fwd * 0.012f + up * 0.012f, new Vector3(0.026f, 0.024f, 0.010f), c.B, f.Basis);
                break;
            }
            case GarmentShape.Beanie:
                s.Tube(centre + up * 0.035f, top + up * 0.045f, 0.120f, 0.075f, c.A, 8);
                s.Tube(centre + up * 0.025f, centre + up * 0.065f, 0.123f, 0.121f, c.B, 8);
                break;
        }
    }

    /// <summary>Glasses: rims on the front of the face, arms back to the ears.</summary>
    private static void AppendGlasses(MeshScratch s, Garment g, Vector3 centre, Frame f)
    {
        var c = Cols.Of(g);
        var eyes = centre + f.Up * 0.022f + f.Fwd * 0.098f;
        const float apart = 0.036f;
        // the arms, from the outer edge of the frame back past the ears
        foreach (float sgn in new[] { -1f, 1f })
            s.Tube(eyes + f.Side * sgn * 0.066f, centre + f.Up * 0.022f + f.Side * sgn * 0.078f - f.Fwd * 0.03f, 0.004f, c.A, 4);
        switch (g.Shape)
        {
            case GarmentShape.RoundGlasses:
            case GarmentShape.RoundShades:
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var at = eyes + f.Side * sgn * apart;
                    s.Ring(at, f.Fwd, 0.022f, 0.029f, 0.006f, c.A, 10);
                    s.Tube(at - f.Fwd * 0.002f, at + f.Fwd * 0.001f, 0.023f, c.B, 10);
                }
                s.Tube(eyes + f.Side * (apart - 0.026f), eyes - f.Side * (apart - 0.026f), 0.004f, c.A, 4);
                break;
            case GarmentShape.HeartShades:
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var at = eyes + f.Side * sgn * apart;
                    AppendHeart(s, at, f, 0.046f, c.A);
                    AppendHeart(s, at + f.Fwd * 0.005f, f, 0.034f, c.B);
                }
                s.Tube(eyes + f.Side * 0.012f, eyes - f.Side * 0.012f, 0.004f, c.A, 4);
                break;
            case GarmentShape.Shades:
                // one wraparound visor, the frame across its top
                s.Box(eyes, new Vector3(0.150f, 0.034f, 0.010f), c.B, f.Basis);
                s.Box(eyes + f.Up * 0.018f + f.Fwd * 0.002f, new Vector3(0.156f, 0.008f, 0.012f), c.A, f.Basis);
                break;
            case GarmentShape.StarGlasses:
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var at = eyes + f.Side * sgn * apart;
                    s.Tube(at - f.Fwd * 0.003f, at + f.Fwd * 0.003f, 0.032f, c.A, 5);
                    s.Tube(at + f.Fwd * 0.002f, at + f.Fwd * 0.005f, 0.022f, c.B, 5);
                }
                break;
        }
    }

    /// <summary>A face mask over the mouth and nose, loops round the ears, a pixel face printed on it.</summary>
    private static void AppendMask(MeshScratch s, Garment g, Vector3 centre, Frame f)
    {
        var c = Cols.Of(g);
        var front = centre - f.Up * 0.052f + f.Fwd * 0.093f;
        s.Box(front, new Vector3(0.140f, 0.080f, 0.014f), c.A, f.Basis);
        foreach (float sgn in new[] { -1f, 1f })
        {
            // wrapping the cheeks, and the loop to the ear
            s.Box(centre - f.Up * 0.052f + f.Side * sgn * 0.080f + f.Fwd * 0.058f, new Vector3(0.012f, 0.072f, 0.064f), c.A, f.Basis);
            s.Tube(centre - f.Up * 0.03f + f.Side * sgn * 0.080f + f.Fwd * 0.03f, centre + f.Side * sgn * 0.080f - f.Fwd * 0.012f, 0.004f, c.A, 4);
        }
        if (MaskFaces.TryGetValue(g.Face, out var rows))
            Pixels(s, rows, front + f.Fwd * 0.009f, f, 0.0086f, c.B, c.C);
    }

    /// <summary>
    /// The masks' faces as pixel art, 15×7, top row up, left column the viewer's left (the figure's
    /// right): '1' the mask's second colour (white), '2' its third (blush pink). After the reference
    /// kawaii masks: ω with blush, swirl cheeks round an A or an ω, a fanged grin, a tongue out.
    /// </summary>
    private static readonly Dictionary<MaskFace, string[]> MaskFaces = new()
    {
        [MaskFace.Uwu] = new[]
        {
            ".2.2.2...2.2.2.",
            "2.2.2...2.2.2..",
            "...............",
            "...1...1...1...",
            "...1...1...1...",
            "....1.1.1.1....",
            ".....1...1.....",
        },
        [MaskFace.SwirlA] = new[]
        {
            "...............",
            "......111......",
            ".22..1...1..22.",
            "2.2..11111..2.2",
            "222..1...1..222",
            ".....1...1.....",
            "...............",
        },
        [MaskFace.SwirlW] = new[]
        {
            "...............",
            "...............",
            ".22.........22.",
            "2.2..1.1.1..2.2",
            "222..1.1.1..222",
            "......1.1......",
            "...............",
        },
        [MaskFace.Fang] = new[]
        {
            ".2.2.2...2.2.2.",
            "2.2.2...2.2.2..",
            "...............",
            "..1.........1..",
            "...111111111...",
            "....1.....1....",
            "...............",
        },
        [MaskFace.Tongue] = new[]
        {
            ".2.2.2...2.2.2.",
            "2.2.2...2.2.2..",
            "...............",
            "....1111111....",
            "........1.1....",
            "........111....",
            "...............",
        },
        [MaskFace.Skull] = new[]
        {
            "...............",
            "...111111111...",
            "...1.1.1.1.1...",
            "...111111111...",
            "...1.1.1.1.1...",
            "...111111111...",
            "...............",
        },
        [MaskFace.CatMouth] = new[]
        {
            "...............",
            ".......2.......",
            "111....1....111",
            "......1.1......",
            ".111.......111.",
            "...............",
            "...............",
        },
    };

    /// <summary>
    /// Pixel art as boxes on a plane facing <see cref="Frame.Fwd"/>, centred on <paramref name="origin"/>:
    /// one box per run of the same character along a row.
    /// </summary>
    private static void Pixels(MeshScratch s, string[] rows, Vector3 origin, Frame f, float px, Color one, Color two)
    {
        int h = rows.Length;
        for (int y = 0; y < h; y++)
        {
            var row = rows[y];
            int w = row.Length;
            for (int x = 0; x < w;)
            {
                char ch = row[x];
                int end = x;
                while (end < w && row[end] == ch) end++;
                if (ch is '1' or '2')
                {
                    float cx = (x + end - 1) * 0.5f - (w - 1) * 0.5f;
                    float cy = (h - 1) * 0.5f - y;
                    // seen from the front, the figure's left (+Side) is on the viewer's right
                    var at = origin + f.Side * cx * px + f.Up * cy * px;
                    s.Box(at, new Vector3((end - x) * px, px, 0.004f), ch == '1' ? one : two, f.Basis);
                }
                x = end;
            }
        }
    }

    /// <summary>Piercings: in both ears, and for some sets the nose or lip too.</summary>
    private static void AppendPiercings(MeshScratch s, Garment g, Vector3 centre, Frame f, float half)
    {
        var c = Cols.Of(g);
        foreach (float sgn in new[] { -1f, 1f })
        {
            var ear = centre + f.Side * sgn * 0.078f - f.Fwd * 0.012f;
            var lobe = ear - f.Up * 0.035f;
            var helix = ear + f.Up * 0.025f - f.Fwd * 0.006f;
            var outward = f.Side * sgn;
            switch (g.Shape)
            {
                case GarmentShape.Studs:
                    s.Box(lobe, new Vector3(0.012f, 0.012f, 0.012f), c.A, f.Basis);
                    s.Box(helix, new Vector3(0.010f, 0.010f, 0.010f), c.A, f.Basis);
                    break;
                case GarmentShape.Hoops:
                    s.Ring(lobe - f.Up * 0.016f + outward * 0.003f, f.Side, 0.013f, 0.018f, 0.004f, c.A, 10);
                    s.Ring(helix - f.Up * 0.006f + outward * 0.003f, f.Side, 0.007f, 0.011f, 0.004f, c.A, 8);
                    break;
                case GarmentShape.Industrial:
                    s.Box(lobe, new Vector3(0.012f, 0.012f, 0.012f), c.A, f.Basis);
                    if (sgn > 0)
                        s.Tube(helix + f.Fwd * 0.022f + f.Up * 0.004f + outward * 0.004f, helix - f.Fwd * 0.03f + f.Up * 0.012f + outward * 0.004f, 0.004f, c.A, 4);
                    break;
                case GarmentShape.Spikes:
                    s.Box(lobe, new Vector3(0.012f, 0.012f, 0.012f), c.B, f.Basis);
                    s.Tube(lobe, lobe + outward * 0.028f, 0.007f, 0.0005f, c.A, 4);
                    s.Tube(helix, helix + outward * 0.022f + f.Up * 0.008f, 0.006f, 0.0005f, c.A, 4);
                    break;
                case GarmentShape.StarStuds:
                    s.Tube(lobe, lobe + outward * 0.006f, 0.014f, c.A, 5);
                    s.Tube(helix, helix + outward * 0.006f, 0.010f, c.B, 5);
                    break;
            }
        }
        var nose = centre + f.Fwd * 0.096f - f.Up * 0.012f;
        if (g.Shape == GarmentShape.Industrial)
            s.Ring(nose - f.Up * 0.012f, f.Fwd, 0.008f, 0.012f, 0.003f, c.A, 8);   // a septum ring
        else if (g.Shape == GarmentShape.Spikes)
        {
            var lip = centre + f.Fwd * 0.092f - f.Up * 0.075f;
            s.Tube(lip, lip + f.Fwd * 0.022f, 0.006f, 0.0005f, c.A, 4);
        }
    }

    // ------------------------------------------------------------------------------------
    // shared shapes
    // ------------------------------------------------------------------------------------

    /// <summary>A tube in <paramref name="bands"/> stripes of two colours, the first at <paramref name="a"/>.</summary>
    private static void Banded(MeshScratch s, Vector3 a, Vector3 b, float ra, float rb, Color one, Color two, int bands)
    {
        for (int i = 0; i < bands; i++)
        {
            float t0 = (float)i / bands, t1 = (float)(i + 1) / bands;
            s.Tube(a.Lerp(b, t0), a.Lerp(b, t1), Mathf.Lerp(ra, rb, t0), Mathf.Lerp(ra, rb, t1), i % 2 == 0 ? one : two, 6);
        }
    }

    /// <summary>A bow facing <see cref="Frame.Fwd"/>: two loops and a knot; <paramref name="scale"/> 1 is 10 cm across.</summary>
    private static void AppendBow(MeshScratch s, Vector3 at, Frame f, float scale, Color loops, Color knot)
    {
        foreach (float sgn in new[] { -1f, 1f })
        {
            var tilt = new Basis(f.Fwd, sgn * 0.35f) * f.Basis;
            s.Box(at + f.Side * sgn * 0.026f * scale, new Vector3(0.048f, 0.034f, 0.016f) * scale, loops, tilt);
            var tail = new Basis(f.Fwd, sgn * 0.25f) * f.Basis;
            s.Box(at + f.Side * sgn * 0.012f * scale - f.Up * 0.03f * scale, new Vector3(0.014f, 0.04f, 0.010f) * scale, loops, tail);
        }
        s.Box(at + f.Fwd * 0.004f * scale, new Vector3(0.018f, 0.020f, 0.020f) * scale, knot, f.Basis);
    }

    /// <summary>A heart facing <see cref="Frame.Fwd"/>, <paramref name="size"/> across: a diamond with two lobes on top.</summary>
    private static void AppendHeart(MeshScratch s, Vector3 at, Frame f, float size, Color colour)
    {
        var diamond = new Basis(f.Fwd, Mathf.Pi / 4f) * f.Basis;
        float d = size * 0.5f;
        s.Box(at - f.Up * size * 0.08f, new Vector3(d, d, 0.006f), colour, diamond);
        foreach (float sgn in new[] { -1f, 1f })
            s.Tube(at + f.Side * sgn * size * 0.2f + f.Up * size * 0.12f - f.Fwd * 0.003f,
                at + f.Side * sgn * size * 0.2f + f.Up * size * 0.12f + f.Fwd * 0.003f, size * 0.25f, colour, 8);
    }
}
