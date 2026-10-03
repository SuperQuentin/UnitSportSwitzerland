using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The figure and its clothes (#251, #394; docs/notes/avatar/clothing.md). The body
/// (<c>HumanMeshBuilder.Body.cs</c>) is coloured where the clothes are, so a sleeve, trousers or
/// tights are the body's own surface in the garment's colour (<see cref="Dress"/>); what does not
/// follow the skin (collars, prints, skirts, buckles, hoods, headwear, glasses) is then laid over it
/// on the body's real surfaces, 4-10 mm proud (closer and it z-fights at distance). Everything
/// hangs off the rig's joints, so clothes follow every pose, the dances and the ragdoll without
/// knowing about them. A garment's finish rides in its colours' alpha (<see cref="Garments.Fx"/>),
/// so it costs nothing here: <c>shaders/body/avatar.gdshaderinc</c> does the rest.
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

    /// <summary>A finish that cuts holes (fishnet, lace): drawn over the skin, never as it.</summary>
    private static bool Holed(Garment g) => g.Finish is Finish.Fishnet or Finish.Lace;

    /// <summary>
    /// The figure: its body in its appearance and clothes, then what lies over them. Every figure
    /// is drawn here, walker, rider, driver, passenger, swimmer and ragdoll alike.
    /// <paramref name="fullFace"/>: a full-face motorbike helmet round the head, its visor in front.
    /// </summary>
    private static void AppendRig(MeshScratch scratch, HumanPalette palette, Rig rig,
        bool includeLegs, bool helmet, Headwear hat = Headwear.None, bool body = true, bool head = true, bool fullFace = false)
    {
        // the figure only: a vehicle drawn into the same scratch keeps its own look
        using var smoothing = scratch.Smoothing(SmoothFigures);
        var s = scratch;
        var o = palette.Outfit;
        var top = o[WearSlot.Top];
        // a one-piece takes the bottom slot: the inventory never lets both on, and a packed
        // outfit from elsewhere draws the dress
        var bottom = top is { CoversBottom: true } ? null : o[WearSlot.Bottom];
        var headwear = o[WearSlot.Head];
        // head clothes replace an occasion hat; a helmet replaces both
        if (helmet || fullFace || headwear != null) hat = Headwear.None;
        var cover = fullFace || hat == Headwear.PumpkinHead ? HairCover.Head
            : helmet || hat is Headwear.WitchHat or Headwear.SantaHat || headwear?.Shape == GarmentShape.Beanie ? HairCover.Hat
            : HairCover.None;

        var look = Dress(palette, o, top, bottom);
        var fit = AppendBody(s, look, rig, includeLegs, body, head, cover);
        var r = rig;

        if (body)
        {
            if (top != null) TopDetail(s, fit, top);
            if (bottom != null) BottomDetail(s, fit, bottom);
            foreach (float side in stackalloc[] { -1f, 1f })
            {
                bool left = side < 0;
                var (shoulder, elbow, wrist) = left ? (r.ShoulderL, r.ElbowL, r.WristL) : (r.ShoulderR, r.ElbowR, r.WristR);
                ArmDetail(s, fit.Shape, fit.Side * side, shoulder, elbow, wrist, side, top, o[WearSlot.Hands]);
                if (includeLegs)
                {
                    var (hip, knee, ankle, toe) = left ? (r.HipL, r.KneeL, r.AnkleL, r.ToeL) : (r.HipR, r.KneeR, r.AnkleR, r.ToeR);
                    LegDetail(s, fit.Shape, fit.Side * side, hip, knee, ankle, toe, bottom, o[WearSlot.Legs], o[WearSlot.Feet]);
                }
            }
            // with the legs or without (a cyclist's are their own mesh, driven by the cranks)
            AppendSkirt(s, fit, top, bottom, palette.Wind);
        }
        if (!head) return;

        var h = fit.Head;
        bool hair = look.HairStyle != HairStyle.None && cover != HairCover.Head;
        var f = new Frame(h.Side, h.UpAxis, h.Fwd);
        if (o[WearSlot.Neck] is { } neck) AppendNeckwear(s, fit, neck);
        if (fullFace)
        {
            // a full-face helmet round the whole head, dark visor at the front
            var centre = h.Centre(0.108f);
            float hw = h.HalfWidth(false);
            // chin (−0.035) to crown (0.25) is the head's height; a shell a couple of centimetres round it
            s.Box(centre, new Vector3(2f * hw + 0.035f, (Head.Crown + 0.035f) * h.K + 0.035f, 0.185f * h.K + 0.04f), palette.Helmet, f.Basis);
            s.Box(h.Point(0.10f, Mathf.Pi / 2f, 0f) + f.Fwd * (0.02f + 0.012f), new Vector3(0.17f, 0.07f, 0.03f), new Color(0.08f, 0.09f, 0.12f), f.Basis);
        }
        else if (helmet)
            s.Box(h.Top(hair) - f.Up * 0.035f, new Vector3(2f * h.HalfWidth(hair) + 0.024f, 0.085f, 0.177f * h.K + 0.035f), palette.Helmet, f.Basis);
        else if (headwear != null)
            AppendHeadwear(s, headwear, h, hair);
        else if (hat != Headwear.None)
            AppendHat(s, hat, h.Top(hair), f.Side, f.Up, f.Fwd, h.HalfWidth(hair) / 0.075f);

        if (cover == HairCover.Head) return;
        if (o[WearSlot.Face] is { } mask) AppendMask(s, mask, h);
        if (o[WearSlot.Eyes] is { } eyes) AppendGlasses(s, eyes, h);
        if (o[WearSlot.Ears] is { } ears) AppendPiercings(s, ears, h);
    }

    /// <summary>
    /// The body's look in these clothes: the appearance, the default jersey, shorts and shoes, and
    /// over them each garment as the colours and lengths of the body's own surface.
    /// </summary>
    private static BodyLook Dress(HumanPalette p, Outfit o, Garment? top, Garment? bottom)
    {
        var a = p.Appearance;
        var look = new BodyLook(a.Build, p.Skin)
        {
            Face = a.Face, Eyes = a.EyeColour, HairStyle = a.Hair, Hair = a.HairTint,
            Top = p.Jersey, Bottom = p.Shorts, Shoes = p.Shoes,
        };
        if (o.IsEmpty) return look;

        if (top != null)
        {
            var c = Cols.Of(top);
            bool goth = top.Style == GarmentStyle.Gothic;
            look = top.Shape switch
            {
                GarmentShape.TShirt or GarmentShape.PrintTee or GarmentShape.Polo => look with { Top = c.A, SleeveTo = 0.5f },
                GarmentShape.Marcel => look with { Top = c.A, SleeveTo = 0f, TopTo = 3.45f },
                GarmentShape.Corset => look with { Top = c.A, SleeveTo = 0f, TopFrom = 1.65f, TopTo = 3.15f },
                GarmentShape.CropTop => look with { Top = c.A, TopFrom = 2.5f, SleeveTo = goth ? 0f : 0.38f },
                GarmentShape.Longsleeve => look with { Top = c.A, SleeveTo = 2f },
                GarmentShape.Hoodie => look with { Top = c.A, SleeveTo = 2f, TopFrom = 1.55f },
                // the blouse under a short jacket: the jacket is laid over it (TopDetail)
                GarmentShape.CroppedJacket => look with { Top = c.B, SleeveTo = 2f },
                GarmentShape.Robe => look with { Top = c.A, SleeveTo = 1f, TopFrom = 1.5f },
                GarmentShape.Dress => look with { Top = c.A, SleeveTo = 0.38f, TopFrom = 1.5f },
                _ => look with { Top = c.A },
            };
            if (top.CoversBottom) look = look with { Bottom = c.A, LegTo = 0.08f };
        }
        if (bottom != null)
        {
            var c = Cols.Of(bottom);
            look = look with
            {
                Bottom = c.A,
                LegTo = bottom.Shape == GarmentShape.Shorts ? 0.55f : IsTrousers(bottom) ? 2f : 0.08f,
            };
        }

        if (o[WearSlot.Legs] is { } legs && !IsTrousers(bottom))
        {
            var c = Cols.Of(legs);
            look = look with
            {
                Legwear = c.A,
                LegwearOver = Holed(legs),
                LegwearFrom = legs.Shape switch
                {
                    GarmentShape.ThighHigh or GarmentShape.StripedThighHigh => 0.45f,
                    GarmentShape.KneeSock => 1.1f,
                    _ => 0.04f,
                },
            };
        }

        if (o[WearSlot.Feet] is { } feet)
        {
            var c = Cols.Of(feet);
            look = feet.Shape switch
            {
                GarmentShape.Sneakers => look with { Shoes = c.A, Sole = c.B, BootFrom = 1.92f, Platform = 1.2f },
                GarmentShape.PlatformBoots => look with { Shoes = c.A, Sole = Shade(c.A, 0.7f), BootFrom = 1.18f, Platform = 2.6f },
                GarmentShape.CombatBoots => look with { Shoes = c.A, Sole = c.B, BootFrom = 1.66f, Platform = 1.4f },
                GarmentShape.MaryJanes => look with
                {
                    Shoes = c.A, Sole = Shade(c.A, 0.8f), BootFrom = 1.97f,
                    // frilly white ankle socks, unless there are stockings on or trousers over them
                    Legwear = look.Legwear == null && !IsTrousers(bottom) ? c.B : look.Legwear,
                    LegwearFrom = look.Legwear == null && !IsTrousers(bottom) ? 1.84f : look.LegwearFrom,
                },
                _ => look with { Shoes = c.A },
            };
        }

        if (o[WearSlot.Hands] is { } hands)
        {
            var c = Cols.Of(hands);
            look = hands.Shape switch
            {
                GarmentShape.Gloves => look with { Gloves = c.A, GloveFrom = 1.7f, Fingerless = false },
                GarmentShape.Fingerless => look with { Gloves = c.A, GloveFrom = 1.76f, Fingerless = true },
                GarmentShape.ArmWarmers or GarmentShape.StripedWarmers => look with { Gloves = c.A, GloveFrom = 1.05f, Fingerless = true },
                GarmentShape.Paws => look with { Gloves = c.A, GloveFrom = 1.8f, Fingerless = false },
                _ => look,
            };
        }
        return look;
    }

    // ------------------------------------------------------------------------------------
    // trunk
    // ------------------------------------------------------------------------------------

    /// <summary>The trunk's frame at spine <paramref name="sp"/> as a basis (X its side, Y up it, Z out of its front).</summary>
    private static Basis TrunkBasis(in Fit fit, float sp)
    {
        var (side, up, fwd) = fit.Torso.Frame(sp);
        return new Basis(side, up, fwd);
    }

    /// <summary>The trunk's flat front at <paramref name="sp"/>: how wide what is laid on it may be.</summary>
    private static float FrontWidth(in Fit fit, float sp) => 2f * fit.Torso.Width(sp) * 0.62f;

    private const float Deg = Mathf.Pi / 180f;

    /// <summary>What a top has besides its colours: prints, collars, straps, a hood, a jacket over a blouse.</summary>
    private static void TopDetail(MeshScratch s, in Fit fit, Garment top)
    {
        var c = Cols.Of(top);
        var t = fit.Torso;
        switch (top.Shape)
        {
            case GarmentShape.PrintTee:
            {
                // the print on the chest: a block and a smaller one inside it (a band logo, a heart)
                float w = Mathf.Min(0.13f, FrontWidth(fit, 2.62f) * 0.95f);
                s.Box(t.Front(2.62f, 0.004f), new Vector3(w, 0.11f, 0.012f), c.B, TrunkBasis(fit, 2.62f));
                s.Box(t.Front(2.62f, 0.010f), new Vector3(w * 0.46f, 0.05f, 0.012f), c.C, TrunkBasis(fit, 2.62f));
                break;
            }

            case GarmentShape.Polo:
                // collar round the neck, the button placket, the small emblem on the chest
                t.Band(s, 3.8f, 4f, c.C, 0.008f);
                s.Box(t.Front(3.38f, 0.006f), new Vector3(0.028f, 0.10f, 0.012f), c.C, TrunkBasis(fit, 3.38f));
                foreach (float sp in stackalloc[] { 3.30f, 3.50f })
                    s.Box(t.Front(sp, 0.012f), new Vector3(0.012f, 0.012f, 0.008f), c.B, TrunkBasis(fit, sp));
                s.Box(t.Surface(2.88f, 68f * Deg, 0.006f), new Vector3(0.030f, 0.018f, 0.012f), c.B, TrunkBasis(fit, 2.88f));
                break;

            case GarmentShape.Marcel:
                // a strap over each shoulder from the scoop neck, front to back
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    float Mirror(float a) => sgn > 0 ? a : 180f - a;
                    var front = t.Surface(3.45f, Mirror(62f) * Deg, 0.004f);
                    var over = t.Surface(3.93f, Mirror(22f) * Deg, 0.008f);
                    var back = t.Surface(3.45f, Mirror(298f) * Deg, 0.004f);
                    s.Tube(front, over, 0.013f, c.A, 5);
                    s.Tube(over, back, 0.013f, c.A, 5);
                }
                break;

            case GarmentShape.Corset:
            {
                // strapless: buckled straps round the waist, a zip down the middle
                var strap = Shade(c.A, 1.6f);
                foreach (float sp in stackalloc[] { 2.12f, 2.38f, 2.64f, 2.90f })
                {
                    t.Band(s, sp - 0.035f, sp + 0.035f, strap, 0.005f);
                    s.Box(t.Surface(sp, 62f * Deg, 0.011f), new Vector3(0.026f, 0.024f, 0.010f), c.B, TrunkBasis(fit, sp));
                }
                s.Box(t.Front(2.55f, 0.008f), new Vector3(0.008f, 0.27f, 0.008f), c.B, TrunkBasis(fit, 2.55f));
                break;
            }

            case GarmentShape.CropTop when top.Style == GarmentStyle.Gothic:
                // two chains slung across the bare belly from the hem
                for (int row = 0; row < 2; row++)
                    for (int i = 0; i <= 8; i++)
                    {
                        float u = i / 8f * 2f - 1f;
                        float sag = (1f - u * u) * (0.045f + row * 0.03f) / 0.255f;
                        float sp = 2.48f - sag;
                        s.Box(t.Surface(sp, (90f - u * 55f) * Deg, 0.006f), new Vector3(0.013f, 0.013f, 0.013f), c.B, TrunkBasis(fit, sp));
                    }
                break;

            case GarmentShape.CropTop:
                AppendBow(s, t.Front(3.15f, 0.012f), FrameAt(fit, 3.15f), 0.55f, c.C, c.C);
                break;

            case GarmentShape.Longsleeve:
                t.Stripes(s, 1.75f, 3.8f, 8, c.B);
                break;

            case GarmentShape.Hoodie:
            {
                // the hood lying behind the neck, the pocket, the drawstrings
                var (side, up, fwd) = t.Frame(3.9f);
                var basis = new Basis(side, up, fwd);
                s.Box(t.At(4f) - fwd * (fit.Shape.Neck + 0.05f) + up * 0.005f, new Vector3(0.20f, 0.12f, 0.09f), Shade(c.A, 0.9f), basis);
                s.Box(t.Front(2.22f, 0.004f), new Vector3(Mathf.Min(0.18f, FrontWidth(fit, 2.22f) * 1.1f), 0.08f, 0.014f), Shade(c.A, 0.85f), TrunkBasis(fit, 2.22f));
                foreach (float x in stackalloc[] { -0.03f, 0.03f })
                    s.Box(t.Front(3.55f, 0.010f) + side * x - up * 0.045f, new Vector3(0.008f, 0.08f, 0.008f), c.B, basis);
                break;
            }

            case GarmentShape.CroppedJacket:
            {
                // a short blue jacket over the white blouse; jabot and brooch at the throat, a strap across
                t.Band(s, 2.42f, 4f, c.A, 0.006f);
                s.Box(t.Front(3.62f, 0.016f), new Vector3(0.055f, 0.075f, 0.022f), c.B, TrunkBasis(fit, 3.62f));
                s.Box(t.Front(3.72f, 0.030f), new Vector3(0.024f, 0.024f, 0.012f), c.C, TrunkBasis(fit, 3.72f));
                s.Tube(t.Surface(2.98f, 120f * Deg, 0.022f), t.Surface(2.45f, 48f * Deg, 0.022f), 0.008f, Leather, 4);
                s.Tube(t.Front(2.7f, 0.022f), t.Front(2.7f, 0.032f), 0.020f, new Color("e0b848"), 8);
                break;
            }

            case GarmentShape.Robe:
            {
                // a high collar standing behind the neck, a belt at the waist
                var (side, up, fwd) = t.Frame(3.95f);
                s.Box(t.At(4f) - fwd * (fit.Shape.Neck + 0.03f) + up * 0.045f, new Vector3(0.21f, 0.10f, 0.02f), c.B, new Basis(side, up, fwd));
                t.Band(s, 1.92f, 2.06f, c.C, 0.008f);
                break;
            }

            case GarmentShape.Dress:
                // a white collar, a bow at the chest
                t.Band(s, 3.8f, 4f, c.B, 0.008f);
                AppendBow(s, t.Front(3.25f, 0.012f), FrameAt(fit, 3.25f), 0.7f, c.C, c.C);
                break;
        }
    }

    private static Frame FrameAt(in Fit fit, float sp)
    {
        var (side, up, fwd) = fit.Torso.Frame(sp);
        return new Frame(side, up, fwd);
    }

    /// <summary>What sits on the hips with a bottom on: drawstrings, a belt, rings, pockets are on the legs.</summary>
    private static void BottomDetail(MeshScratch s, in Fit fit, Garment b)
    {
        var c = Cols.Of(b);
        var t = fit.Torso;
        switch (b.Shape)
        {
            case GarmentShape.Shorts:
            {
                var (side, _, _) = t.Frame(1.65f);
                foreach (float x in stackalloc[] { -0.02f, 0.02f })
                    s.Box(t.Front(1.65f, 0.008f) + side * x, new Vector3(0.008f, 0.07f, 0.008f), c.B, TrunkBasis(fit, 1.65f));
                break;
            }
            case GarmentShape.Pants:
            case GarmentShape.Cargo:
                // a belt with its buckle
                t.Band(s, 1.86f, 1.98f, Shade(c.A, 0.55f), 0.007f);
                s.Box(t.Front(1.92f, 0.013f), new Vector3(0.035f, 0.026f, 0.010f), c.B, TrunkBasis(fit, 1.92f));
                break;
            case GarmentShape.SlitMaxi:
            case GarmentShape.HighLowSkirt:
            {
                // a belt slung low, rings at the hips
                t.Band(s, 1.82f, 1.94f, Shade(c.C, 1.4f), 0.022f);
                s.Box(t.Front(1.88f, 0.030f), new Vector3(0.034f, 0.028f, 0.010f), c.B, TrunkBasis(fit, 1.88f));
                var (side, _, _) = t.Frame(1.5f);
                foreach (float a in stackalloc[] { 0f, Mathf.Pi })
                    s.Ring(t.Surface(1.5f, a, 0.030f), side, 0.010f, 0.017f, 0.006f, c.B, 8);
                break;
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // limbs
    // ------------------------------------------------------------------------------------

    /// <summary>What an arm has besides its colours: puffed or bell sleeves, a jacket's sleeve, cuffs, stripes, paw pads.</summary>
    private static void ArmDetail(MeshScratch s, Physique p, Vector3 outward, Vector3 shoulder, Vector3 elbow, Vector3 wrist,
        float side, Garment? top, Garment? hands)
    {
        var down = (elbow - shoulder).Normalized();
        if (top != null)
        {
            var c = Cols.Of(top);
            switch (top.Shape)
            {
                case GarmentShape.CropTop when top.Style != GarmentStyle.Gothic:
                case GarmentShape.Dress:
                    // a short puffed sleeve with a band round its hem
                    s.Tube(shoulder - down * 0.03f - outward * 0.005f, shoulder.Lerp(elbow, 0.42f), p.Deltoid + 0.014f, p.UpperArm + 0.016f, c.A, 8);
                    LimbBand(s, shoulder, elbow, wrist, 0.38f, 0.46f, p, arm: true, 0.020f, c.B, 8);
                    break;
                case GarmentShape.Longsleeve:
                    for (int i = 1; i < 6; i += 2) LimbBand(s, shoulder, elbow, wrist, i / 3f, (i + 1) / 3f, p, arm: true, 0.004f, c.B);
                    break;
                case GarmentShape.Hoodie:
                    LimbBand(s, shoulder, elbow, wrist, 1.86f, 2f, p, arm: true, 0.006f, c.B);
                    break;
                case GarmentShape.CroppedJacket:
                    // puffed at the shoulder, the jacket's sleeve over the blouse's, a white cuff
                    s.Tube(shoulder - down * 0.03f - outward * 0.005f, shoulder + down * 0.12f, p.Deltoid + 0.016f, p.UpperArm + 0.012f, c.A, 8);
                    LimbBand(s, shoulder, elbow, wrist, 0.3f, 1.85f, p, arm: true, 0.007f, c.A);
                    LimbBand(s, shoulder, elbow, wrist, 1.85f, 2f, p, arm: true, 0.010f, c.B);
                    break;
                case GarmentShape.Robe:
                {
                    // a bell sleeve flaring past the wrist, its lining showing
                    var past = wrist + (wrist - elbow).Normalized() * 0.03f;
                    s.Skirt(elbow, past, p.Elbow + 0.012f, p.Elbow + 0.055f, c.A, 8);
                    s.Skirt(elbow.Lerp(past, 0.92f), past, p.Elbow + 0.046f, p.Elbow + 0.052f, c.B, 8);
                    break;
                }
            }
        }
        if (hands == null) return;
        var h = Cols.Of(hands);
        switch (hands.Shape)
        {
            case GarmentShape.StripedWarmers:
                for (int i = 1; i < 5; i += 2) LimbBand(s, shoulder, elbow, wrist, 1.05f + i * 0.19f, 1.05f + (i + 1) * 0.19f, p, arm: true, 0.006f, h.B);
                break;
            case GarmentShape.Fingerless:
                LimbBand(s, shoulder, elbow, wrist, 1.70f, 1.78f, p, arm: true, 0.008f, h.B);
                break;
            case GarmentShape.Paws:
            {
                // a fat paw over the hand, pads on the palm
                var f = HandFrameOf(elbow, wrist, side, p.Hand);
                var basis = new Basis(f.Thumb, f.Along, f.Thumb.Cross(f.Along));
                s.Box(f.Palm + f.Along * 0.02f * f.Scale, new Vector3(0.086f, 0.11f, 0.05f) * f.Scale, h.A, basis);
                ReadOnlySpan<Vector2> pads = [new(-0.022f, 0.030f), new(0f, 0.036f), new(0.022f, 0.030f), new(0f, 0.004f)];
                foreach (var pad in pads)
                    s.Box(f.Palm + (f.Thumb * pad.X + f.Along * (pad.Y + 0.02f) + f.Inward * 0.027f) * f.Scale,
                        new Vector3(0.016f, 0.016f, 0.008f) * f.Scale, h.B, basis);
                break;
            }
        }
    }

    /// <summary>What a leg has besides its colours: a cargo pocket, the tops of stockings and socks, boots' buckles and collars.</summary>
    private static void LegDetail(MeshScratch s, Physique p, Vector3 outward, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe,
        Garment? bottom, Garment? legs, Garment? feet)
    {
        if (bottom?.Shape == GarmentShape.Cargo)
        {
            // a pocket on the outside of each thigh
            var fl = Frame.Along(knee - hip);
            var o = (outward - fl.Up * outward.Dot(fl.Up)).Normalized();
            s.Box(hip.Lerp(knee, 0.5f) + o * (LegRadius(p, 0.5f) + 0.006f), new Vector3(0.012f, 0.10f, 0.08f), Cols.Of(bottom).B,
                new Basis(fl.Up.Cross(o), fl.Up, o).Orthonormalized());
        }
        if (legs != null && !IsTrousers(bottom))
        {
            var c = Cols.Of(legs);
            switch (legs.Shape)
            {
                case GarmentShape.ThighHigh:
                    LimbBand(s, hip, knee, ankle, 0.43f, 0.52f, p, arm: false, 0.004f, c.C);
                    break;
                case GarmentShape.StripedThighHigh:
                    for (int i = 1; i < 9; i += 2) LimbBand(s, hip, knee, ankle, 0.45f + i * 0.17f, 0.45f + (i + 1) * 0.17f, p, arm: false, 0.003f, c.B);
                    break;
                case GarmentShape.KneeSock:
                    LimbBand(s, hip, knee, ankle, 1.08f, 1.2f, p, arm: false, 0.005f, c.B);
                    break;
            }
        }
        if (feet == null) return;
        var b = Cols.Of(feet);
        switch (feet.Shape)
        {
            case GarmentShape.Sneakers:
                LimbBand(s, hip, knee, ankle, 1.9f, 1.99f, p, arm: false, 0.016f, b.B);   // the collar
                break;
            case GarmentShape.PlatformBoots:
                // knee-high, buckled three times up the shaft
                foreach (float u in stackalloc[] { 0.22f, 0.48f, 0.74f })
                {
                    float t = 2f - 0.82f * u;
                    LimbBand(s, hip, knee, ankle, t - 0.02f, t + 0.02f, p, arm: false, 0.018f, b.B);
                }
                break;
            case GarmentShape.CombatBoots:
                LimbBand(s, hip, knee, ankle, 1.66f, 1.70f, p, arm: false, 0.018f, b.B);
                break;
            case GarmentShape.MaryJanes:
            {
                // the strap over the instep
                var dir = toe - ankle;
                if (dir.LengthSquared() < 1e-6f) break;
                var at = ankle.Lerp(toe, 0.30f);
                var along = dir.Normalized() * 0.008f;
                s.Tube(at - along, at + along, 0.054f, b.A, 6);
                break;
            }
        }
    }

    /// <summary>
    /// One leg in the clothes of <paramref name="p"/>, from free joints: a cyclist's, driven by the
    /// cranks. The bare leg with the cycling shorts and shoes when nothing is worn.
    /// </summary>
    public static void AppendLeg(MeshScratch s, HumanPalette p, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe)
    {
        using var smoothing = s.Smoothing(SmoothFigures);
        var o = p.Outfit;
        var top = o[WearSlot.Top];
        var bottom = top is { CoversBottom: true } ? null : o[WearSlot.Bottom];
        var look = Patterned(Dress(p, o, top, bottom));
        var shape = Physique.Of(look.Build);
        var outward = hip.X < 0 ? Vector3.Left : Vector3.Right;
        DrawLeg(s, look, shape, hip, knee, ankle, toe, Vector3.Right);
        LegDetail(s, shape, outward, hip, knee, ankle, toe, bottom, o[WearSlot.Legs], o[WearSlot.Feet]);
    }

    /// <summary>The radius a cone from the waist must start at to clear the hips on its way to a hem of radius <paramref name="hem"/>.</summary>
    private static float ConeStart(in Fit fit, Vector3 hemAt, float hem)
    {
        var t = fit.Torso;
        float waist = Mathf.Max(t.Width(2f), 0.075f) + 0.014f;
        float length = Mathf.Max((hemAt - t.At(2f)).Length(), 0.05f);
        float down = (t.At(2f) - t.At(1f)).Length() / length;   // how far down the cone the widest of the hips is
        float hips = t.Width(1f) + 0.016f;
        if (down >= 1f) return Mathf.Max(waist, hips);
        // radius at the hips, straight from the start to the hem, must clear them
        float needed = (hips - hem * down) / (1f - down);
        return Mathf.Max(waist, needed);
    }

    /// <summary>
    /// A skirt, or the lower half of a robe or dress: an open cone from the waist, its hem following
    /// the knees or the ankles so it swings with the stride, blown back by <paramref name="wind"/>
    /// (<see cref="HumanPalette.Wind"/>) and fluttering faster the harder it blows.
    /// </summary>
    private static void AppendSkirt(MeshScratch s, in Fit fit, Garment? top, Garment? bottom, Vector3 wind)
    {
        var r = fit.Rig;
        var knees = (r.KneeL + r.KneeR) * 0.5f;
        var ankles = (r.AnkleL + r.AnkleR) * 0.5f;
        // how wide the legs are apart at the hem, so a stride does not poke through it
        float spreadK = (r.KneeL - r.KneeR).Length() * 0.5f;
        float spreadA = (r.AnkleL - r.AnkleR).Length() * 0.5f;
        // hips wider than the old figure's (a curvy build) widen every hem with them
        float extra = Mathf.Max(0f, fit.Torso.Width(1f) - 0.135f);

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
        var waist = r.Waist;
        void Cone(Vector3 from, Vector3 hem, float ra, float rb, Color colour, int sides, Vector3 gap = default, float gapAngle = 0f) =>
            s.Skirt(from, Blown(waist, hem) + (from - waist), ra, rb, colour, sides, gap, gapAngle, ripple, phase);

        if (top is { CoversBottom: true })
        {
            var c = Cols.Of(top);
            if (top.Shape == GarmentShape.Robe)
            {
                var hem = ankles + Vector3.Up * 0.035f;
                float rh = Mathf.Max(0.30f, spreadA + 0.07f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, c.A, 12);
                Cone(waist.Lerp(hem, 0.95f), hem, rh * 0.97f + 0.004f, rh + 0.004f, c.B, 12);
            }
            else
            {
                var hem = r.Hip.Lerp(knees, 0.85f);
                float rh = Mathf.Max(0.29f, spreadK + 0.12f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, c.A, 12);
                // the petticoat frothing out under it
                Cone(waist.Lerp(hem, 0.78f), hem - Vector3.Up * 0.035f, rh * 0.9f, rh + 0.03f, c.B, 12);
            }
            return;
        }
        if (!IsSkirt(bottom)) return;

        var b = Cols.Of(bottom!);
        switch (bottom!.Shape)
        {
            case GarmentShape.PleatedSkirt:
            {
                var hem = r.Hip.Lerp(knees, 0.55f);
                float rh = Mathf.Max(0.25f, spreadK + 0.09f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, b.A, 14);
                break;
            }
            case GarmentShape.RuffleMini:
            {
                var hem = r.Hip.Lerp(knees, 0.40f);
                float rh = Mathf.Max(0.22f, spreadK + 0.08f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, b.A, 12);
                // a second tier of ruffle under the first
                Cone(waist.Lerp(hem, 0.55f), hem - Vector3.Up * 0.045f, rh * 0.95f, rh + 0.035f, b.B, 12);
                break;
            }
            case GarmentShape.HighLowSkirt:
            {
                // the axis leans back, so the hem rides high in front and trails low behind
                var hem = r.Hip.Lerp(knees, 0.85f) + new Vector3(0, -0.04f, -0.14f);
                float rh = Mathf.Max(0.27f, spreadK + 0.11f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, b.A, 14);
                break;
            }
            case GarmentShape.SlitMaxi:
            {
                // to the ankles, with a slit up the front of the right leg (−X)
                var hem = ankles + Vector3.Up * 0.05f;
                float rh = Mathf.Max(0.29f, spreadA + 0.08f) + extra;
                Cone(waist, hem, ConeStart(fit, hem, rh), rh, b.A, 14, gap: new Vector3(-0.55f, 0, 1f), gapAngle: 0.42f);
                break;
            }
            case GarmentShape.LongPleated:
            {
                // to mid-calf, brown straps running down it front and back
                var hem = Blown(waist, r.Hip.Lerp(ankles, 0.80f));
                float rh = Mathf.Max(0.29f, spreadK + 0.12f) + extra;
                float ra = ConeStart(fit, hem, rh);
                s.Skirt(waist, hem, ra, rh, b.A, 16, ripple: ripple, phase: phase);
                var axis = Frame.Along(waist - hem);
                foreach (float a in stackalloc[] { -0.45f, 0.45f, Mathf.Pi - 0.45f, Mathf.Pi + 0.45f })
                {
                    var dir = axis.Fwd * Mathf.Cos(a) + axis.Side * Mathf.Sin(a);
                    s.Tube(waist + dir * (ra + 0.008f), hem + dir * (rh + 0.006f), 0.007f, b.B, 4);
                }
                break;
            }
        }
    }

    /// <summary>Whether the outfit has something that blows in the wind: a skirt, a robe, a dress.</summary>
    public static bool Flutters(Outfit o) =>
        o[WearSlot.Top] is { CoversBottom: true } || IsSkirt(o[WearSlot.Bottom]);

    // ------------------------------------------------------------------------------------
    // head and neck
    // ------------------------------------------------------------------------------------

    private static void AppendNeckwear(MeshScratch s, in Fit fit, Garment g)
    {
        var c = Cols.Of(g);
        var r = fit.Rig;
        var axis = fit.Head.NeckTop - r.Neck;
        var f = Frame.Along(axis);
        var lo = r.Neck.Lerp(fit.Head.NeckTop, 0.30f);
        var hi = r.Neck.Lerp(fit.Head.NeckTop, 0.62f);
        var mid = (lo + hi) * 0.5f;
        float neck = fit.Shape.Neck;
        switch (g.Shape)
        {
            case GarmentShape.SpikedChoker:
                s.Tube(lo, hi, neck + 0.008f, c.A, 8);
                for (int i = -3; i <= 3; i++)
                {
                    float a = i * 0.42f;
                    var dir = f.Fwd * Mathf.Cos(a) + f.Side * Mathf.Sin(a);
                    s.Tube(mid + dir * (neck + 0.004f), mid + dir * (neck + 0.034f), 0.008f, 0.0005f, c.B, 4);
                }
                break;
            case GarmentShape.HeartChoker:
                s.Tube(lo, hi, neck + 0.006f, c.A, 8);
                AppendHeart(s, mid + f.Fwd * (neck + 0.014f) - f.Up * 0.025f, f, 0.030f, c.B);
                break;
            case GarmentShape.BellCollar:
            {
                s.Tube(lo, hi, neck + 0.008f, c.A, 8);
                var bell = mid + f.Fwd * (neck + 0.018f) - f.Up * 0.03f;
                s.Tube(bell - f.Up * 0.02f, bell + f.Up * 0.01f, 0.022f, 0.012f, c.B, 8);
                s.Box(bell - f.Up * 0.012f + f.Fwd * 0.02f, new Vector3(0.014f, 0.004f, 0.006f), new Color("3a2a10"), f.Basis);
                break;
            }
            case GarmentShape.Chain:
            {
                // hangs from the base of the neck onto the chest, a small cross at the bottom
                var t = fit.Torso;
                for (int i = 0; i <= 10; i++)
                {
                    float u = i / 10f * 2f - 1f;
                    float sp = 3.95f - 0.45f * (1f - u * u);
                    s.Box(t.Surface(sp, (90f - u * 72f) * Deg, 0.007f), new Vector3(0.011f, 0.011f, 0.011f), c.A, TrunkBasis(fit, sp));
                }
                var cross = t.Front(3.42f, 0.010f);
                var basis = TrunkBasis(fit, 3.42f);
                s.Box(cross, new Vector3(0.010f, 0.045f, 0.008f), c.A, basis);
                s.Box(cross + basis.Y * 0.008f, new Vector3(0.030f, 0.010f, 0.008f), c.A, basis);
                break;
            }
        }
    }

    /// <summary>Clothes on the head (in place of a hat), on the head's real surface, over the hair when there is some.</summary>
    private static void AppendHeadwear(MeshScratch s, Garment g, in Head head, bool hair)
    {
        var c = Cols.Of(g);
        var (side, up, fwd) = (head.Side, head.UpAxis, head.Fwd);
        var f = new Frame(side, up, fwd);
        var top = head.Top(hair);
        float hw = head.HalfWidth(hair);
        float rx = hw / 0.075f;   // the old box head was 0.150 wide: offsets authored on it scale with this
        var centre = head.Centre(0.11f);
        var ears = head.Centre(0.095f);   // the cups' height (a local: the local functions cannot capture an in parameter)
        float half = (top - centre).Dot(up);

        void Ears(Color outer, Color inner, float height)
        {
            foreach (float sgn in stackalloc[] { -1f, 1f })
            {
                var root = top - up * 0.014f + side * sgn * hw * 0.62f + fwd * 0.005f;
                var tip = root + up * height + side * sgn * 0.016f;
                s.Tube(root, tip, 0.036f, 0.003f, outer, 3);
                s.Tube(root + fwd * 0.012f + up * 0.006f, tip + fwd * 0.006f - up * 0.016f, 0.020f, 0.002f, inner, 3);
            }
        }

        // a band over the crown from ear to ear
        Vector3 BandPoint(int i, float forward)
        {
            ReadOnlySpan<Vector2> path = [new(-1.0f, -0.10f), new(-1.0f, 0.70f), new(-0.62f, 1.02f), new(0f, 1.06f), new(0.62f, 1.02f), new(1.0f, 0.70f), new(1.0f, -0.10f)];
            var p = path[i];
            return centre + fwd * forward + side * p.X * (hw + 0.010f) + up * p.Y * half;
        }
        void Band(Color colour, float forward)
        {
            for (int i = 0; i + 1 < 7; i++) s.Tube(BandPoint(i, forward), BandPoint(i + 1, forward), 0.010f, colour, 5);
        }

        void Cups(Color shell, Color cushion)
        {
            foreach (float sgn in stackalloc[] { -1f, 1f })
            {
                var at = ears + side * sgn * (hw + 0.004f);
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
                foreach (float sgn in stackalloc[] { -1f, 1f })
                    s.Ring(head.Centre(0.095f) + side * sgn * (hw + 0.036f), side, 0.026f, 0.034f, 0.006f, Garments.Fx(g.C, Finish.Neon), 10);
                break;
            case GarmentShape.Headset:
            {
                Band(c.A, 0f);
                Cups(c.A, c.B);
                // the boom mic round to the mouth
                var root = head.Centre(0.075f) + side * (hw + 0.03f);
                var mic = head.Point(0.04f, 62f * Deg, 0.028f);
                var bend = root.Lerp(mic, 0.5f) + side * 0.02f + fwd * 0.01f;
                s.Tube(root, bend, 0.005f, c.A, 4);
                s.Tube(bend, mic, 0.005f, c.A, 4);
                s.Box(mic, new Vector3(0.018f, 0.014f, 0.016f), c.C, f.Basis);
                break;
            }
            case GarmentShape.BunnyEars:
                Band(c.A, 0.01f);
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    var root = top - up * 0.01f + side * sgn * 0.035f * rx;
                    var knee = root + up * 0.13f + side * sgn * 0.025f;
                    // the left one flops forward
                    var tip = sgn > 0 ? knee + fwd * 0.07f + up * 0.02f + side * 0.02f : knee + up * 0.10f + side * sgn * 0.01f;
                    s.Tube(root, knee, 0.022f, 0.026f, c.A, 6);
                    s.Tube(knee, tip, 0.026f, 0.012f, c.A, 6);
                    s.Tube(root + fwd * 0.016f + up * 0.02f, knee + fwd * 0.018f, 0.010f, 0.013f, c.B, 5);
                }
                break;
            case GarmentShape.Horns:
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    var root = top - up * 0.012f + side * sgn * 0.048f * rx + fwd * 0.035f;
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
                AppendBow(s, top + fwd * 0.03f + side * 0.045f * rx + up * 0.005f, f, 1.5f, c.A, c.B);
                break;
            case GarmentShape.LaceHeadband:
                Band(c.A, 0.035f);
                // the white lace frill standing along the band's top, in front of it
                for (int i = 1; i < 6; i++)
                {
                    var a = BandPoint(i, 0.035f);
                    foreach (var at in stackalloc[] { a, (a + BandPoint(i + 1, 0.035f)) * 0.5f })
                        if (at.DistanceTo(BandPoint(6, 0.035f)) > 0.05f)
                            s.Box(at + fwd * 0.012f + up * 0.012f, new Vector3(0.026f, 0.024f, 0.010f), c.B, f.Basis);
                }
                break;
            case GarmentShape.Beanie:
            {
                var from = head.Centre(0.14f);
                s.Tube(from + up * 0.01f, top + up * 0.04f, hw + 0.028f, (hw + 0.028f) * 0.62f, c.A, 8);
                s.Tube(from, from + up * 0.04f, hw + 0.031f, hw + 0.030f, c.B, 8);
                break;
            }
        }
    }

    /// <summary>Glasses: rims over the eyes, arms back to the ears.</summary>
    private static void AppendGlasses(MeshScratch s, Garment g, in Head head)
    {
        var c = Cols.Of(g);
        var f = new Frame(head.Side, head.UpAxis, head.Fwd);
        // the eyes sit at the atlas's rows 6-8: 0.10 up the head's profile
        var eyes = head.Point(0.100f, Mathf.Pi / 2f, 0.014f);
        float apart = 0.036f * head.K;
        // the arms, from the outer edge of the frame back past the ears
        foreach (float sgn in stackalloc[] { -1f, 1f })
            s.Tube(eyes + f.Side * sgn * 0.066f, head.Ear(sgn) + f.Side * sgn * 0.006f + f.Up * 0.012f, 0.004f, c.A, 4);
        switch (g.Shape)
        {
            case GarmentShape.RoundGlasses:
            case GarmentShape.RoundShades:
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    var at = eyes + f.Side * sgn * apart;
                    s.Ring(at, f.Fwd, 0.022f, 0.029f, 0.006f, c.A, 10);
                    s.Tube(at - f.Fwd * 0.002f, at + f.Fwd * 0.001f, 0.023f, c.B, 10);
                }
                s.Tube(eyes + f.Side * (apart - 0.026f), eyes - f.Side * (apart - 0.026f), 0.004f, c.A, 4);
                break;
            case GarmentShape.HeartShades:
                foreach (float sgn in stackalloc[] { -1f, 1f })
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
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    var at = eyes + f.Side * sgn * apart;
                    s.Tube(at - f.Fwd * 0.003f, at + f.Fwd * 0.003f, 0.032f, c.A, 5);
                    s.Tube(at + f.Fwd * 0.002f, at + f.Fwd * 0.005f, 0.022f, c.B, 5);
                }
                break;
        }
    }

    /// <summary>A face mask over the mouth and nose, loops round the ears, a pixel face printed on it.</summary>
    private static void AppendMask(MeshScratch s, Garment g, in Head head)
    {
        var c = Cols.Of(g);
        var f = new Frame(head.Side, head.UpAxis, head.Fwd);
        var front = head.Point(0.045f, Mathf.Pi / 2f, 0.012f);
        s.Box(front, new Vector3(0.140f, 0.080f, 0.014f), c.A, f.Basis);
        foreach (float sgn in stackalloc[] { -1f, 1f })
        {
            // wrapping the cheeks, and the loop to the ear
            var cheek = head.Point(0.045f, (sgn > 0 ? 28f : 152f) * Deg, 0.010f);
            s.Box(cheek, new Vector3(0.012f, 0.072f, 0.064f), c.A, f.Basis);
            s.Tube(cheek + f.Up * 0.02f, head.Ear(sgn) + f.Side * sgn * 0.004f, 0.004f, c.A, 4);
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
    private static void AppendPiercings(MeshScratch s, Garment g, in Head head)
    {
        var c = Cols.Of(g);
        var f = new Frame(head.Side, head.UpAxis, head.Fwd);
        foreach (float sgn in stackalloc[] { -1f, 1f })
        {
            var ear = head.Ear(sgn) + f.Side * sgn * 0.010f;
            var lobe = ear - f.Up * 0.022f;
            var helix = ear + f.Up * 0.020f - f.Fwd * 0.006f;
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
        if (g.Shape == GarmentShape.Industrial)
            s.Ring(head.Point(0.062f, Mathf.Pi / 2f, 0.010f), f.Fwd, 0.008f, 0.012f, 0.003f, c.A, 8);   // a septum ring
        else if (g.Shape == GarmentShape.Spikes)
        {
            var lip = head.Point(0.030f, Mathf.Pi / 2f, 0.004f);
            s.Tube(lip, lip + f.Fwd * 0.022f, 0.006f, 0.0005f, c.A, 4);
        }
    }

    // ------------------------------------------------------------------------------------
    // shared shapes
    // ------------------------------------------------------------------------------------


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
