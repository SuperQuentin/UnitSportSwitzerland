using Godot;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>A box of the stage, in the stadium frame (u, y over the pitch, v); <see cref="Yaw"/> turns it about +Y.</summary>
public readonly record struct StageBox(Vector3 Centre, Vector3 Size, float Yaw, Color Color, bool Solid, bool Hidden = false);

/// <summary>
/// A PA cabinet (<see cref="ConcertPa"/> draws it and makes it bounce): a line array (stacked into
/// elements) or a sub stack, stadium frame; <see cref="Size"/> is across its face, its height, its depth.
/// </summary>
public readonly record struct PaCab(Vector3 Centre, Vector3 Size, Vector3 Facing, bool Sub);

/// <summary>A walkable slope (stairs up to a deck), four corners in the stadium frame.</summary>
public readonly record struct StageRamp(Vector3 A, Vector3 B, Vector3 C, Vector3 D);

/// <summary>
/// The stage of a <see cref="ConcertEvent"/> and its rig (#885): what stands on the pitch (deck,
/// roof grid on four towers, flown PA, wings, screens, catwalk and B-stage, front-of-house riser,
/// delay towers, the barrier) and every fixture where a production would hang it, patched into DMX.
/// Laid out in the stage's own frame (x across it, z towards the audience, z = 0 the deck's front
/// edge) and placed in the stadium frame by the layout. Pure, built once per event.
/// </summary>
public sealed class StagePlan
{
    public readonly List<StageBox> Boxes = new();
    public readonly List<StageRamp> Ramps = new();
    public readonly DmxPatch Patch = new();
    /// <summary>Where the PA hangs, stadium frame: where the music comes from.</summary>
    public readonly List<Vector3> PaHangs = new();

    /// <summary>The PA's cabinets, drawn and pumped by <see cref="ConcertPa"/> (a sub stack also stands in <see cref="Boxes"/>, unseen, for its collision).</summary>
    public readonly List<PaCab> Pa = new();
    /// <summary>The lighting desk on the front-of-house riser, stadium frame: where its screen opens.</summary>
    public Vector3 Desk { get; private set; }
    public readonly ConcertEvent Event;

    // stage frame -> stadium frame
    private Vector2 _origin, _forward, _right;

    /// <summary>The deck's front edge's middle and the way the stage faces (the round's: the centre, north).</summary>
    public Vector2 Origin => _origin;
    public Vector2 Forward => _forward;
    public bool Round => Event.Layout == StageLayout.Center;

    /// <summary>Whether a point of the pitch (u, v) is under any part of the stage, give or take <paramref name="margin"/>.</summary>
    public bool Blocked(Vector2 uv, float margin)
    {
        foreach (var b in Boxes)
        {
            if (b.Size.X * b.Size.Z < 0.5f) continue;   // a pole
            var d = uv - new Vector2(b.Centre.X, b.Centre.Z);
            float c = MathF.Cos(b.Yaw), s = MathF.Sin(b.Yaw);
            // into the box's own axes (x along its yaw)
            float lx = d.X * c - d.Y * s, lz = d.X * s + d.Y * c;
            if (MathF.Abs(lx) < b.Size.X / 2 + margin && MathF.Abs(lz) < b.Size.Z / 2 + margin) return true;
        }
        return false;
    }

    /// <summary>
    /// Where the stage stands on the grass (#956): a rectangle of the stage's own frame (x across, z
    /// towards the audience), the deck, its wings and its back to the stand. The pitch's protection
    /// floor is laid everywhere else.
    /// </summary>
    public (float X0, float X1, float Z0, float Z1) Grass { get; private set; }

    /// <summary>Whether a point of the pitch (u, v) is in the stage's <see cref="Grass"/>.</summary>
    public bool OnGrass(Vector2 uv)
    {
        var d = uv - _origin;
        float x = d.Dot(_right), z = d.Dot(_forward);
        return x > Grass.X0 && x < Grass.X1 && z > Grass.Z0 && z < Grass.Z1;
    }

    // colours, sRGB
    private static readonly Color Deck = new(0.12f, 0.12f, 0.13f), Skirt = new(0.05f, 0.05f, 0.06f),
        Truss = new(0.62f, 0.63f, 0.66f), Canopy = new(0.2f, 0.2f, 0.22f), Speaker = new(0.07f, 0.07f, 0.08f),
        Frame = new(0.1f, 0.1f, 0.11f), Tent = new(0.86f, 0.86f, 0.84f), Barrier = new(0.55f, 0.56f, 0.58f), Scrim = new(0.09f, 0.09f, 0.1f);

    private const float TrussSize = 0.6f;

    public StagePlan(ConcertEvent e)
    {
        Event = e;
        var s = e.Stadium;
        float backMargin = 1.5f;
        switch (e.Layout)
        {
            case StageLayout.Center:
                BuildRound(e);
                return;
            case StageLayout.EndNorth:
                Place(new Vector2(0, -s.FrontHalfV + backMargin + e.Depth), new Vector2(0, 1));
                break;
            case StageLayout.SideEast:
                Place(new Vector2(s.FrontHalfU - backMargin - e.Depth, 0), new Vector2(-1, 0));
                break;
            case StageLayout.SideWest:
                Place(new Vector2(-s.FrontHalfU + backMargin + e.Depth, 0), new Vector2(1, 0));
                break;
            default:   // the south end, a thrust's too
                Place(new Vector2(0, s.FrontHalfV - backMargin - e.Depth), new Vector2(0, -1));
                break;
        }
        BuildEnd(e);
    }

    private void Place(Vector2 origin, Vector2 forward)
    {
        _origin = origin;
        _forward = forward.Normalized();
        _right = new Vector2(-_forward.Y, _forward.X);
    }

    /// <summary>A point of the stage frame in the stadium frame.</summary>
    private Vector3 At(float x, float y, float z)
    {
        var p = _origin + _right * x + _forward * z;
        return new Vector3(p.X, y, p.Y);
    }

    /// <summary>A point of the stage's own frame (x across, y up, z towards the audience) in the stadium frame: for framing a shot.</summary>
    public Vector3 StagePoint(float x, float y, float z) => At(x, y, z);

    /// <summary>A direction of the stage frame in the stadium frame.</summary>
    private Vector3 Dir(float x, float y, float z)
    {
        var p = _right * x + _forward * z;
        return new Vector3(p.X, y, p.Y).Normalized();
    }

    private float Yaw => Mathf.Atan2(-_right.Y, _right.X);

    private void Box(float x0, float x1, float y0, float y1, float z0, float z1, Color c, bool solid = true) =>
        Boxes.Add(new StageBox(At((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), new Vector3(x1 - x0, y1 - y0, z1 - z0), Yaw, c, solid));

    private const float SideScreenW = 11f;

    /// <summary>How far out a side screen stands: beside the wings, but never into the stands (the run-off is what is left).</summary>
    private static float SideScreenX(ConcertEvent e)
    {
        float room = (e.Layout is StageLayout.SideEast or StageLayout.SideWest ? e.Stadium.FrontHalfV : e.Stadium.FrontHalfU) - 6.5f;
        return Math.Min(e.Width / 2 + 10f, room);
    }

    /// <summary>
    /// An open air's stage on the ground as it lies (#948): every box that stands on the pitch level
    /// (the front of house, the delay towers, the barrier, the catwalk, the stairs) raised or lowered to
    /// the ground under its centre, the desk with its riser, the subs with their stacks. The deck stands
    /// where the rig's origin was put on the ground, so its own boxes barely move.
    /// </summary>
    public void Lift(Func<Vector2, float> ground)
    {
        for (int i = 0; i < Boxes.Count; i++)
        {
            var b = Boxes[i];
            if (b.Centre.Y - b.Size.Y / 2 > 0.6f) continue;   // flown or on the deck: it goes with the stage
            Boxes[i] = b with { Centre = b.Centre + Vector3.Up * ground(new Vector2(b.Centre.X, b.Centre.Z)) };
        }
        for (int i = 0; i < Pa.Count; i++)
            if (Pa[i].Sub || Pa[i].Centre.Y < 14f)
                Pa[i] = Pa[i] with { Centre = Pa[i].Centre + Vector3.Up * ground(new Vector2(Pa[i].Centre.X, Pa[i].Centre.Z)) };
        for (int i = 0; i < Ramps.Count; i++)
        {
            var r = Ramps[i];
            Vector3 L(Vector3 p) => p + Vector3.Up * ground(new Vector2(p.X, p.Z));
            Ramps[i] = new StageRamp(L(r.A), L(r.B), L(r.C), L(r.D));
        }
        Desk += Vector3.Up * ground(new Vector2(Desk.X, Desk.Z));
    }

    /// <summary>A PA cabinet in the stage frame, facing (<paramref name="fx"/>, <paramref name="fz"/>).</summary>
    private void Cab(float x0, float x1, float y0, float y1, float z0, float z1, float fx, float fz, bool sub)
    {
        bool alongX = MathF.Abs(fx) > MathF.Abs(fz);
        float w = alongX ? z1 - z0 : x1 - x0, depth = alongX ? x1 - x0 : z1 - z0;
        Pa.Add(new PaCab(At((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), new Vector3(w, y1 - y0, depth), Dir(fx, 0, fz), sub));
        if (sub)
            Boxes.Add(new StageBox(At((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), new Vector3(x1 - x0, y1 - y0, z1 - z0), Yaw, Speaker, true, Hidden: true));
    }

    /// <summary>A vertical truss tower, its top at <paramref name="top"/>.</summary>
    private void Tower(float x, float z, float top) => Box(x - TrussSize / 2, x + TrussSize / 2, 0.012f, top, z - TrussSize / 2, z + TrussSize / 2, Truss);   // its foot a hair up: never coplanar with a deck's underside

    /// <summary>Stairs up to a deck, beside it: a ramp a body walks up, stepped for the eye.</summary>
    private void Stairs(float x, float zTop, float zFoot, float height, float width = 1.6f)
    {
        int steps = Math.Max(2, (int)MathF.Ceiling(height / 0.25f));
        for (int i = 0; i < steps; i++)
        {
            float z0 = zFoot + (zTop - zFoot) * i / steps, z1 = zFoot + (zTop - zFoot) * (i + 1) / steps;
            Box(x - width / 2, x + width / 2, 0, height * (i + 1) / steps, Math.Min(z0, z1), Math.Max(z0, z1), Truss, solid: false);
        }
        Ramps.Add(new StageRamp(At(x - width / 2, 0, zFoot), At(x + width / 2, 0, zFoot), At(x + width / 2, height, zTop), At(x - width / 2, height, zTop)));
    }

    // ---- an end stage (and a side one, and a thrust) ----------------------------------------------

    private void BuildEnd(ConcertEvent e)
    {
        float w = e.Width, d = e.Depth, h = e.DeckHeight, r = e.RoofHeight, hw = w / 2;
        // the deck and its wings (8 m at most) on the grass, back to the stand behind it
        Grass = (-hw - 9f, hw + 9f, -d - 4f, 0.5f);

        // the deck, its black skirt, and stairs up at the back corners
        Box(-hw, hw, 0, h, -d, 0, Deck);
        Box(-hw, hw, 0.012f, h - 0.05f, 0.005f, 0.03f, Skirt, solid: false);
        Stairs(-hw - 1f, -d + 4f, -d + 4f + h * 1.6f, h);
        Stairs(hw + 1f, -d + 4f, -d + 4f + h * 1.6f, h);

        // the roof: four towers, the grid's perimeter and its three trusses across, the canopy over it
        float tx = hw - 0.5f;
        foreach (float x in new[] { -tx, tx })
            foreach (float z in new[] { -d + 0.8f, -1f })
                Tower(x, z, r + 1f);
        Box(-hw, hw, r, r + TrussSize, -1.3f, -0.7f, Truss, solid: false);             // front truss
        Box(-hw, hw, r, r + TrussSize, -d / 2 - 0.3f, -d / 2 + 0.3f, Truss, solid: false); // mid truss
        Box(-hw, hw, r, r + TrussSize, -d + 1.2f, -d + 1.8f, Truss, solid: false);      // back truss
        foreach (float x in new[] { -tx, tx })
            Box(x - 0.3f, x + 0.3f, r, r + TrussSize, -d + 0.5f, -0.7f, Truss, solid: false);
        Box(-hw - 1f, hw + 1f, r + 2.5f, r + 2.8f, -d - 0.5f, 0.5f, Canopy);           // the canopy, pitched over the grid
        // the back: a black scrim from the deck to the roof, and the wings beside the stage
        Box(-hw, hw, h, r, -d - 0.1f, -d + 0.1f, Scrim, solid: false);
        // the room beside the stage, to the seating front (#948: an arena's floor is narrow)
        bool sideways = e.Layout is StageLayout.SideEast or StageLayout.SideWest;
        float room = (sideways ? e.Stadium.FrontHalfV : e.Stadium.FrontHalfU) - hw - 0.5f;
        float wing = Math.Min(8f, room);
        // a line array no longer than the stage's roof allows
        float array = Math.Min(10.5f, r - 3.5f);
        foreach (float side in new[] { -1f, 1f })
        {
            float x0 = side * hw, x1 = side * (hw + wing);
            if (wing >= 1f) Box(Math.Min(x0, x1), Math.Max(x0, x1), 0, Math.Max(h + 1f, r - 2f), -d, -d + 1f, Scrim);
            // the flown PA: a line array hung off the wing, its subs stacked under the deck's edge; with
            // side screens, beside the screen's inner edge (or past its outer one), never through it
            float hang = hw + Math.Clamp(room - 0.8f, 0.7f, 3f);
            if (e.HasSideScreens)
            {
                float inner = SideScreenX(e) - SideScreenW / 2 - 0.3f;
                hang = inner - 1.2f >= hw + 1.2f ? Math.Min(hang, inner - 1.2f) : SideScreenX(e) + SideScreenW / 2 + 1.5f;
            }
            float px = side * hang, sub = side * (hw + Math.Clamp(room - 1.3f, 1.2f, 3f));
            Cab(px - 0.6f, px + 0.6f, r - 1.5f - array, r - 1.5f, -1.7f, -1.05f, 0, 1, sub: false);
            PaHangs.Add(At(px, r - 1.5f - array / 2, -1.3f));
            Cab(sub - 1.2f, sub + 1.2f, 0.012f, 1.2f, -2f, 0f, 0, 1, sub: true);
        }

        // video
        if (e.HasCenterScreen)
        {
            float sw = Math.Min(w * 0.62f, 34f), sh = sw * 0.42f;
            // under the stage's roof: a low hall's wall is shorter, and narrower with it
            if (sh > r - h - 2.5f)
            {
                sh = Math.Max(2f, r - h - 2.5f);
                sw = sh / 0.42f;
            }
            var centre = At(0, h + 1.2f + sh / 2, -d + 0.5f);
            Box(-sw / 2 - 0.3f, sw / 2 + 0.3f, h + 0.9f, h + 1.5f + sh, -d + 0.3f, -d + 0.45f, Frame, solid: false);
            Patch.AddScreen(centre, Dir(0, 0, 1), new Vector3(sw, sh, 0), (int)(sw / 0.55f), (int)(sh / 0.55f));
        }
        if (e.HasSideScreens)
            foreach (float side in new[] { -1f, 1f })
            {
                // beside the wings, but never into the stands: the run-off is what is left
                float sx = side * SideScreenX(e), sw = SideScreenW, sh = 6.5f, bottom = r - 13f;
                Box(sx - sw / 2 - 0.3f, sx + sw / 2 + 0.3f, bottom - 0.3f, bottom + sh + 0.3f, -1.2f, -0.9f, Frame, solid: false);
                // its legs behind the ground subs, never through them
                Tower(sx - sw / 2 - 0.6f, -2.6f, bottom + sh);
                Tower(sx + sw / 2 + 0.6f, -2.6f, bottom + sh);
                Patch.AddScreen(At(sx, bottom + sh / 2, -0.85f), Dir(0, 0, 1), new Vector3(sw, sh, 0), 24, 14);
            }

        // the thrust: a catwalk down the pitch to a B-stage
        float catwalk = 0;
        // the floor ahead of the stage's front, as far as the far seating front (#948: arenas are short)
        bool across = e.Layout is StageLayout.SideEast or StageLayout.SideWest;
        float ahead = 2 * (across ? e.Stadium.FrontHalfU : e.Stadium.FrontHalfV) - 1.5f - d;
        float bStage = Math.Min(12f, ahead * 0.12f);
        if (e.Layout == StageLayout.Thrust)
        {
            catwalk = Math.Min(30f, ahead * 0.32f);
            float ch = h - 0.6f;
            Box(-2f, 2f, 0, ch, 0, catwalk, Deck);
            Box(-6f, 6f, 0, ch, catwalk, catwalk + bStage, Deck);
            Stairs(6.8f, catwalk + 4f, catwalk + 4f + ch * 1.6f, ch);
        }

        // front of house and the delay towers, out on the pitch
        float foh = Math.Min(Math.Max(42f, catwalk + 22f), Math.Max(catwalk + bStage + 6f, ahead - 12f));
        Box(-5f, 5f, 0, 1.2f, foh, foh + 6f, Tent);
        Box(-5f, 5f, 3.6f, 3.8f, foh, foh + 6f, Tent, solid: false);
        // the consoles, facing the stage
        Box(-2.5f, 2.5f, 1.2f, 2.15f, foh + 1.2f, foh + 2.2f, Speaker);
        Desk = At(0, 1.2f, foh + 2.8f);
        foreach (float x in new[] { -4.8f, 4.8f })
            foreach (float z in new[] { foh + 0.2f, foh + 5.8f })
                Box(x - 0.1f, x + 0.1f, 1.2f, 3.6f, z - 0.1f, z + 0.1f, Truss, solid: false);
        foreach (float side in new[] { -1f, 1f })
        {
            float dx = side * Math.Min(18f, (across ? e.Stadium.FrontHalfV : e.Stadium.FrontHalfU) * 0.7f);
            // a delay tower under an arena's ceiling
            float tower = e.Stadium.Kind == VenueKind.Arena ? Math.Min(12f, e.Stadium.RoofInner - e.Stadium.RoofThickness - 1.5f) : 12f;
            Tower(dx, foh - 6f, tower);
            Cab(dx - 0.6f, dx + 0.6f, tower * 0.5f, tower - 0.5f, foh - 7.1f, foh - 6.5f, 0, 1, sub: false);
        }
        // the barrier along the stage front, a pit for security behind it
        if (catwalk > 0)
        {
            // open where the catwalk runs out through it
            Box(-hw, -2.05f, 0, 1.2f, 2.4f, 2.6f, Barrier);
            Box(2.05f, hw, 0, 1.2f, 2.4f, 2.6f, Barrier);
        }
        else Box(-hw, hw, 0, 1.2f, 2.4f, 2.6f, Barrier);

        Rig(e, hw, d, h, r, catwalk);
        IrRig(e, hw, d, r, foh, across);
    }

    /// <summary>The fixtures of an end stage, by position, as a lighting designer would hang them.</summary>
    private void Rig(ConcertEvent e, float hw, float d, float h, float r, float catwalk)
    {
        var down = Vector3.Down;
        var toward = Dir(0, 0, 1);
        void Row(int n, FixtureType t, RigGroup g, float z, float y, float span, Vector3 up, Vector3 home)
        {
            for (int i = 0; i < n; i++)
            {
                float x = n == 1 ? 0 : -span / 2 + span * i / (n - 1);
                Patch.Add(t, g, At(x, y, z), home, up);
            }
        }
        float span = hw * 2 - 3f;
        Row(e.Spots, FixtureType.Spot, RigGroup.FrontTruss, -1f, r - 0.6f, span, down, toward);
        Row(e.Blinders, FixtureType.Blinder, RigGroup.Blinders, -1f, r - 1.1f, span * 0.9f, toward, Dir(0, -0.3f, 1));
        Row(e.Beams, FixtureType.Beam, RigGroup.MidTruss, -d / 2, r - 0.6f, span, down, toward);
        Row(e.Strobes, FixtureType.Strobe, RigGroup.Strobes, -d / 2, r - 1.1f, span * 0.8f, toward, Dir(0, -0.2f, 1));
        Row(e.Washes, FixtureType.Wash, RigGroup.BackTruss, -d + 1.5f, r - 0.6f, span, down, toward);
        Row(e.FloorBeams, FixtureType.Beam, RigGroup.Floor, -0.7f, h + 0.3f, span, Vector3.Up, toward);
        Row(e.PixelBars, FixtureType.PixelBar, RigGroup.PixelBars, 0.06f, h * 0.5f, span, toward, toward);
        if (catwalk > 0)
            foreach (float x in new[] { -5.5f, 5.5f })
                foreach (float z in new[] { catwalk + 0.5f, catwalk + 11.5f })
                    Patch.Add(FixtureType.Beam, RigGroup.Floor, At(x, h - 0.3f, z), toward, Vector3.Up);
        Patch.Add(FixtureType.Hazer, RigGroup.Haze, At(-hw + 2f, h, -d + 2f), toward, Vector3.Up);
        Patch.Add(FixtureType.Hazer, RigGroup.Haze, At(hw - 2f, h, -d + 2f), toward, Vector3.Up);

        // the effects, on the deck's front edge; the airbursts launch from it and burst over the
        // open middle of the bowl (the roof of a stadium like Geneva's forbids anything under it)
        Row(e.Flames, FixtureType.Flame, RigGroup.Flames, -0.6f, h, span * 0.85f, Vector3.Up, Vector3.Up);
        Row(e.Gerbs, FixtureType.Gerb, RigGroup.Gerbs, -0.3f, h, span * 0.95f, Vector3.Up, Vector3.Up);
        Row(e.Co2Jets, FixtureType.Co2, RigGroup.Co2, -1.2f, h, span * 0.6f, Vector3.Up, Dir(0, 1, 0.25f));
        Row(e.ConfettiCannons, FixtureType.Confetti, RigGroup.Confetti, -1.5f, h, span, Vector3.Up, Dir(0, 1, 0.6f));
        for (int i = 0; i < e.Airbursts; i++)
        {
            float x = (i - (e.Airbursts - 1) / 2f) * 14f;
            // home: the burst's point, as a direction and distance from the launcher
            var from = At(x, h, -0.5f);
            Patch.Add(FixtureType.Airburst, RigGroup.Airbursts, from, At(x * 1.5f, 24f, 20f + catwalk * 0.5f) - from, Vector3.Up);
        }
        Patch.Add(FixtureType.LowFog, RigGroup.LowFog, At(-hw * 0.5f, h, -d + 1f), toward, Vector3.Up);
        Patch.Add(FixtureType.LowFog, RigGroup.LowFog, At(hw * 0.5f, h, -d + 1f), toward, Vector3.Up);
    }

    // ---- the wristbands' emitters (#986) -----------------------------------------------------------

    /// <summary>How far the floor runs ahead of the stage's front, and half its width across (stage frame): where the IR heads sweep.</summary>
    public float CrowdAhead { get; private set; }
    public float CrowdHalf { get; private set; }

    /// <summary>
    /// The IR emitters of an end stage, patched after the rest of the rig (the earlier addresses stay
    /// put): four heads on the front truss and two on each delay tower, sweeping the floor and the far
    /// stand; four floods, one for the floor (the front truss's middle), one for each side stand (the
    /// truss's ends) and one for the far stand; the transmitter at front of house.
    /// </summary>
    private void IrRig(ConcertEvent e, float hw, float d, float r, float foh, bool across)
    {
        float half = across ? e.Stadium.FrontHalfV : e.Stadium.FrontHalfU;
        CrowdAhead = 2 * (across ? e.Stadium.FrontHalfU : e.Stadium.FrontHalfV) - 1.5f - d;
        CrowdHalf = half;
        var toward = Dir(0, 0, 1);
        foreach (float k in new[] { -0.45f, -0.15f, 0.15f, 0.45f })
            Patch.Add(FixtureType.IrHead, RigGroup.IrHeads, At(k * (hw * 2 - 3f), r - 0.9f, -0.7f), toward, Vector3.Down);
        float tower = e.Stadium.Kind == VenueKind.Arena ? Math.Min(12f, e.Stadium.RoofInner - e.Stadium.RoofThickness - 1.5f) : 12f;
        foreach (float side in new[] { -1f, 1f })
        {
            float dx = side * Math.Min(18f, half * 0.7f);
            // on the tower's top, standing: they reach the floor all round and the stands behind
            foreach (float o in new[] { -0.2f, 0.2f })
                Patch.Add(FixtureType.IrHead, RigGroup.IrHeads, At(dx + o, tower + 0.3f, foh - 6f), toward, Vector3.Up);
        }
        // the floods: home is where they point, for good
        var floor = At(0, r - 0.6f, -0.7f);
        Patch.Add(FixtureType.IrFlood, RigGroup.IrFloods, floor, At(0, 0, CrowdAhead * 0.45f) - floor, Vector3.Up);
        foreach (float side in new[] { -1f, 1f })
        {
            var from = At(side * hw, r - 0.6f, -0.7f);
            Patch.Add(FixtureType.IrFlood, RigGroup.IrFloods, from, At(side * (half + 16f), 12f, CrowdAhead * 0.45f) - from, Vector3.Up);
        }
        var far = At(0, r - 0.3f, -0.7f);
        Patch.Add(FixtureType.IrFlood, RigGroup.IrFloods, far, At(0, 14f, CrowdAhead + 16f) - far, Vector3.Up);
        Patch.Add(FixtureType.Wristbands, RigGroup.Wristbands, Desk, toward, Vector3.Up);
    }

    /// <summary>In the round: two IR heads and a flood on each side of the grid, facing out to its stand; the transmitter at front of house.</summary>
    private void IrRound(ConcertEvent e, float g, float r)
    {
        CrowdHalf = Math.Min(e.Stadium.FrontHalfU, e.Stadium.FrontHalfV);
        CrowdAhead = CrowdHalf;
        foreach (var n in new[] { new Vector2(0, 1), new Vector2(0, -1), new Vector2(1, 0), new Vector2(-1, 0) })
        {
            var outward = Dir(n.X, 0, n.Y);
            var along = new Vector2(-n.Y, n.X);
            foreach (float a in new[] { -g * 0.5f, g * 0.5f })
            {
                var p = n * g + along * a;
                Patch.Add(FixtureType.IrHead, RigGroup.IrHeads, At(p.X, r - 0.9f, p.Y), outward, Vector3.Down);
            }
            // the stand this side faces: the pitch's end or its side (stage x is u, stage z is -v)
            float stand = n.X != 0 ? e.Stadium.FrontHalfU : e.Stadium.FrontHalfV;
            var from = At(n.X * g, r - 0.6f, n.Y * g);
            Patch.Add(FixtureType.IrFlood, RigGroup.IrFloods, from, At(n.X * (stand + 16f), 12f, n.Y * (stand + 16f)) - from, Vector3.Up);
        }
        Patch.Add(FixtureType.Wristbands, RigGroup.Wristbands, Desk, Dir(0, 0, 1), Vector3.Up);
    }

    // ---- in the round ------------------------------------------------------------------------------

    private void BuildRound(ConcertEvent e)
    {
        Place(Vector2.Zero, new Vector2(0, -1));
        float w = e.Width, hw = w / 2, h = e.DeckHeight, r = e.RoofHeight;
        // the deck and its four towers on the grass
        Grass = (-hw - 3f, hw + 3f, -hw - 3f, hw + 3f);
        Box(-hw, hw, 0, h, -hw, hw, Deck);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
            {
                Tower(sx * (hw + 2f), sz * (hw + 2f), r + 1f);
                // a flown PA hang at each corner, turned out to its quarter
                Cab(sx * (hw + 3.2f) - 0.5f, sx * (hw + 3.2f) + 0.5f, r - 11f, r - 1.5f, sz * (hw + 3.2f) - 0.5f, sz * (hw + 3.2f) + 0.5f, sx, sz, sub: false);
                PaHangs.Add(At(sx * (hw + 3.2f), r - 6f, sz * (hw + 3.2f)));
            }
        // the square grid on the four towers
        float g = hw + 2f;
        Box(-g, g, r, r + TrussSize, -g - 0.3f, -g + 0.3f, Truss, solid: false);
        Box(-g, g, r, r + TrussSize, g - 0.3f, g + 0.3f, Truss, solid: false);
        Box(-g - 0.3f, -g + 0.3f, r, r + TrussSize, -g, g, Truss, solid: false);
        Box(g - 0.3f, g + 0.3f, r, r + TrussSize, -g, g, Truss, solid: false);
        Stairs(0, -hw - 0.2f, -hw - 0.2f - h * 1.6f, h);
        Stairs(0, hw + 0.2f, hw + 0.2f + h * 1.6f, h);
        // front of house, out on the floor towards the main stand
        var tent = new Color(0.86f, 0.86f, 0.84f);
        float foh = Math.Min(hw + 22f, Math.Max(hw + 8f, Math.Min(e.Stadium.FrontHalfU, e.Stadium.FrontHalfV) - 8f));   // inside a small arena's floor too (#948)
        Box(-5f, 5f, 0, 1.2f, foh, foh + 6f, tent);
        Box(-5f, 5f, 3.6f, 3.8f, foh, foh + 6f, tent, solid: false);
        Box(-2.5f, 2.5f, 1.2f, 2.15f, foh + 1.2f, foh + 2.2f, Speaker);
        Desk = At(0, 1.2f, foh + 2.8f);

        // the video cube hung over the middle, a face to each quarter
        if (e.HasCenterScreen)
        {
            float sw = Math.Min(w * 0.6f, 14f), sh = sw * 0.55f, cy = r - 2.5f - sh / 2;
            Box(-sw / 2, sw / 2, cy - sh / 2 - 0.3f, cy + sh / 2 + 0.3f, -sw / 2, sw / 2, Frame, solid: false);
            foreach (var n in new[] { new Vector3(0, 0, 1), new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(-1, 0, 0) })
                Patch.AddScreen(At(0, cy, 0) + Dir(n.X, 0, n.Z) * (sw / 2 + 0.05f), Dir(n.X, 0, n.Z), new Vector3(sw, sh, 0), 26, 14);
        }

        // the rig round the grid, each side facing out
        var down = Vector3.Down;
        foreach (var n in new[] { new Vector2(0, 1), new Vector2(0, -1), new Vector2(1, 0), new Vector2(-1, 0) })
        {
            var outward = Dir(n.X, 0, n.Y);
            var along = new Vector2(-n.Y, n.X);
            void Side(int count, FixtureType t, RigGroup grp, float inset, float y, Vector3 up, Vector3 home)
            {
                int per = Math.Max(1, count / 4);
                for (int i = 0; i < per; i++)
                {
                    float a = per == 1 ? 0 : -g + 1f + (2 * g - 2f) * i / (per - 1);
                    var p = n * (g - inset) + along * a;
                    Patch.Add(t, grp, At(p.X, y, p.Y), home, up);
                }
            }
            Side(e.Spots, FixtureType.Spot, RigGroup.FrontTruss, 0f, r - 0.6f, down, outward);
            Side(e.Beams, FixtureType.Beam, RigGroup.MidTruss, 1.5f, r - 0.6f, down, outward);
            Side(e.Washes, FixtureType.Wash, RigGroup.BackTruss, 3f, r - 0.6f, down, outward);
            Side(e.Blinders, FixtureType.Blinder, RigGroup.Blinders, 0f, r - 1.1f, outward, outward + Vector3.Down * 0.4f);
            Side(e.Strobes, FixtureType.Strobe, RigGroup.Strobes, 1f, r - 1.1f, outward, outward + Vector3.Down * 0.3f);
            Side(e.FloorBeams, FixtureType.Beam, RigGroup.Floor, g - hw + 0.6f, h + 0.3f, Vector3.Up, outward);
            Side(e.PixelBars, FixtureType.PixelBar, RigGroup.PixelBars, g - hw - 0.06f, h * 0.5f, outward, outward);
        }
        Patch.Add(FixtureType.Hazer, RigGroup.Haze, At(-hw + 1f, h, 0), Dir(0, 0, 1), Vector3.Up);
        Patch.Add(FixtureType.Hazer, RigGroup.Haze, At(hw - 1f, h, 0), Dir(0, 0, -1), Vector3.Up);

        // the effects round the deck's edge, a share to each side; airbursts over the four quarters
        foreach (var n in new[] { new Vector2(0, 1), new Vector2(0, -1), new Vector2(1, 0), new Vector2(-1, 0) })
        {
            var along = new Vector2(-n.Y, n.X);
            void Edge(int count, FixtureType t, RigGroup grp, Vector3 home)
            {
                int per = count / 4;
                for (int i = 0; i < per; i++)
                {
                    float a = per == 1 ? 0 : -hw + 1.5f + (2 * hw - 3f) * i / (per - 1);
                    var p = n * (hw - 0.5f) + along * a;
                    Patch.Add(t, grp, At(p.X, h, p.Y), home, Vector3.Up);
                }
            }
            Edge(e.Flames, FixtureType.Flame, RigGroup.Flames, Vector3.Up);
            Edge(e.Gerbs, FixtureType.Gerb, RigGroup.Gerbs, Vector3.Up);
            Edge(Math.Max(4, e.Co2Jets), FixtureType.Co2, RigGroup.Co2, Vector3.Up);
            var from = At(n.X * hw, h, n.Y * hw);
            Patch.Add(FixtureType.Airburst, RigGroup.Airbursts, from, At(n.X * 30f, 24f, n.Y * 30f) - from, Vector3.Up);
        }
        Patch.Add(FixtureType.Confetti, RigGroup.Confetti, At(-hw, h, -hw), Dir(-0.4f, 1, -0.4f), Vector3.Up);
        Patch.Add(FixtureType.Confetti, RigGroup.Confetti, At(hw, h, hw), Dir(0.4f, 1, 0.4f), Vector3.Up);        IrRound(e, g, r);
    }
}
