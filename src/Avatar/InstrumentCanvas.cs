using Godot;
using static UnitSport.Avatar.CockpitInstruments;

namespace UnitSport.Avatar;

/// <summary>
/// An airliner's instruments drawn into one small atlas (#421), PS1 resolution: 128 px cells side by
/// side in a <see cref="SubViewport"/> that renders only when the <see cref="Readout"/> changed
/// (<see cref="AircraftCockpit"/>). The A320's glass: PFD, ND, E/WD, SD, standby, and a strip with the
/// FCU windows; the steam set (freighter, AN-124): airspeed, attitude, altimeter, vertical speed, HSI,
/// the engines' N1 and a dial cell (EGT, fuel, flaps).
/// </summary>
public partial class InstrumentCanvas : Control
{
    public const int Cell = 128, Strip = 32;
    /// <summary>Glass cells: PFD, ND, E/WD, SD, ISIS; the strip below holds the FCU.</summary>
    public const int Pfd = 0, Nd = 1, Ewd = 2, Sd = 3, Isis = 4, GlassCells = 5;
    /// <summary>Steam cells: airspeed, attitude, altimeter, vertical speed, HSI, N1, misc (EGT, fuel, flaps).</summary>
    public const int Asi = 0, Adi = 1, Alt = 2, Vsi = 3, Hsi = 4, N1Cell = 5, Misc = 6, SteamCells = 7;

    public static Vector2I AtlasSize(bool glass) => glass ? new Vector2I(Cell * GlassCells, Cell + Strip) : new Vector2I(Cell * SteamCells, Cell);

    public Readout R;
    public bool Glass;
    public int Engines = 2;
    public int Vmo = 350;
    public string[] FlapNames = { "0" };
    public string Mode = "";

    private static readonly Color Black = new(0, 0, 0), Sky = new(0.15f, 0.45f, 0.85f), Ground = new(0.55f, 0.32f, 0.12f),
        White = new(0.95f, 0.95f, 0.95f), Green = new(0.1f, 0.95f, 0.3f), Amber = new(1f, 0.65f, 0.05f), Red = new(1f, 0.15f, 0.1f),
        Cyan = new(0.2f, 0.9f, 1f), Magenta = new(1f, 0.3f, 1f), Yellow = new(1f, 0.95f, 0.1f), Grey = new(0.35f, 0.37f, 0.4f),
        Face = new(0.06f, 0.06f, 0.07f), Bezel = new(0.18f, 0.19f, 0.21f);

    private Font _font = null!;
    private readonly Vector2[] _poly = new Vector2[8];
    private readonly Vector2[][] _sized = { new Vector2[3], new Vector2[4], new Vector2[5], new Vector2[6] };

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public override void _Draw()
    {
        var size = AtlasSize(Glass);
        DrawRect(new Rect2(0, 0, size.X, size.Y), Black);
        if (!R.Power) return;
        if (Glass)
        {
            DrawPfd(new Vector2(Pfd * Cell, 0));
            DrawNd(new Vector2(Nd * Cell, 0));
            DrawEwd(new Vector2(Ewd * Cell, 0));
            DrawSd(new Vector2(Sd * Cell, 0));
            DrawIsis(new Vector2(Isis * Cell, 0));
            DrawFcu(new Vector2(0, Cell));
        }
        else
        {
            DrawAsi(new Vector2(Asi * Cell, 0));
            DrawAdi(new Vector2(Adi * Cell, 0));
            DrawAltimeter(new Vector2(Alt * Cell, 0));
            DrawVsi(new Vector2(Vsi * Cell, 0));
            DrawHsi(new Vector2(Hsi * Cell, 0));
            DrawN1(new Vector2(N1Cell * Cell, 0));
            DrawMisc(new Vector2(Misc * Cell, 0));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private void Text(Vector2 at, string s, Color c, int size = 10, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1f) =>
        DrawString(_font, at, s, align, width, size, c);

    private void Centred(Vector2 centre, string s, Color c, int size = 10) =>
        DrawString(_font, centre + new Vector2(-40f, size * 0.36f), s, HorizontalAlignment.Center, 80f, size, c);

    /// <summary>Clockwise from 12 o'clock, as a dial is read, to a screen direction (y down).</summary>
    private static Vector2 Dir(float a) => new(Mathf.Sin(a), -Mathf.Cos(a));

    private void Needle(Vector2 c, float angle, float length, Color colour, float width = 2f)
    {
        var d = Dir(angle);
        DrawLine(c - d * length * 0.15f, c + d * length, colour, width);
    }

    private void Ticks(Vector2 c, float r, float from, float to, int count, float len, Color colour)
    {
        for (int i = 0; i <= count; i++)
        {
            var d = Dir(Mathf.Lerp(from, to, (float)i / count));
            DrawLine(c + d * (r - len), c + d * r, colour, 1f);
        }
    }

    private void Dial(Vector2 c, float r)
    {
        DrawCircle(c, r + 3f, Bezel);
        DrawCircle(c, r, Face);
    }

    private void Polygon(int n, Color colour)
    {
        if (n < 3) return;
        var a = _sized[Mathf.Min(n, 6) - 3];
        System.Array.Copy(_poly, a, a.Length);
        DrawColoredPolygon(a, colour);
    }

    /// <summary>
    /// An attitude display in <paramref name="box"/>: sky over ground split by the horizon (rolled, moved
    /// by the pitch), the ladder every 5°, clipped to the box.
    /// </summary>
    private void Attitude(Rect2 box, float pxPerDeg)
    {
        DrawRect(box, Sky);
        var c = box.GetCenter();
        float roll = Mathf.DegToRad(R.Roll), pitch = R.Pitch * 0.5f;
        // the horizon's direction and the way to the ground, in the screen's frame
        var along = new Vector2(Mathf.Cos(roll), -Mathf.Sin(roll));
        var down = new Vector2(Mathf.Sin(roll), Mathf.Cos(roll));
        var origin = c - down * pitch * pxPerDeg;
        // the ground: the box's corners on the ground side, cut by the horizon (Sutherland-Hodgman, one plane)
        Span<Vector2> corners = stackalloc Vector2[4] { box.Position, new(box.End.X, box.Position.Y), box.End, new(box.Position.X, box.End.Y) };
        int n = 0, cuts = 0;
        Span<Vector2> cut = stackalloc Vector2[2];
        for (int i = 0; i < 4; i++)
        {
            var p = corners[i];
            var q = corners[(i + 1) % 4];
            float dp = (p - origin).Dot(down), dq = (q - origin).Dot(down);
            if (dp >= 0) _poly[n++] = p;
            if (dp >= 0 != dq >= 0)
            {
                var x = p + (q - p) * (dp / (dp - dq));
                _poly[n++] = x;
                if (cuts < 2) cut[cuts++] = x;
            }
        }
        Polygon(n, Ground);
        // the horizon where it crosses the box, never past its edges into the next screen
        if (cuts == 2) DrawLine(cut[0], cut[1], White, 1f);
        // clipped to the box by hand: a line drawn only if both ends are inside
        for (int deg = -30; deg <= 30; deg += 5)
        {
            if (deg == 0) continue;
            var mid = origin - down * deg * pxPerDeg;
            float half = deg % 10 == 0 ? 12f : 6f;
            var a = mid - along * half;
            var b = mid + along * half;
            if (box.HasPoint(a) && box.HasPoint(b)) DrawLine(a, b, White, 1f);
        }
    }

    private void Warn(Vector2 centre)
    {
        if (!R.Flash) return;
        var w = R.Warn;
        if ((w & CockpitWarning.Stall) != 0) Centred(centre, "STALL", Red, 14);
        else if ((w & CockpitWarning.Overspeed) != 0) Centred(centre, "OVERSPEED", Red, 12);
        else if ((w & CockpitWarning.Gear) != 0) Centred(centre, "GEAR", Red, 14);
    }

    // ---- A320 glass ------------------------------------------------------------------------------

    private void DrawPfd(Vector2 o)
    {
        // flight mode annunciator
        Text(o + new Vector2(4, 10), Mode, Green, 9);
        if (R.Autopilot) Text(o + new Vector2(96, 10), "AP1", White, 9);
        Attitude(new Rect2(o + new Vector2(30, 16), new Vector2(66, 82)), 2f);
        var c = o + new Vector2(63, 57);
        DrawLine(c + new Vector2(-24, 0), c + new Vector2(-8, 0), Yellow, 3f);
        DrawLine(c + new Vector2(8, 0), c + new Vector2(24, 0), Yellow, 3f);
        DrawRect(new Rect2(c - new Vector2(2, 2), new Vector2(4, 4)), Yellow);
        // the roll scale's pointer
        Needle(c, Mathf.DegToRad(-R.Roll), 38f, Yellow, 1f);

        // speed tape, 10 kt marks, 2 px a knot
        var tape = new Rect2(o + new Vector2(2, 16), new Vector2(26, 82));
        DrawRect(tape, Grey);
        float mid = tape.Position.Y + tape.Size.Y * 0.5f;
        var (first, count) = TapeMarks(R.Ias, 20f, 10);
        for (int i = 0; i < count; i++)
        {
            int kt = first + i * 10;
            if (kt < 30) continue;
            float y = mid - TapeOffset(kt, R.Ias, 2f);
            if (y < tape.Position.Y || y > tape.End.Y) continue;
            DrawLine(new Vector2(tape.End.X - 5, y), new Vector2(tape.End.X, y), White, 1f);
            if (kt % 20 == 0) Text(new Vector2(tape.Position.X, y + 3), Num(kt), White, 8);
            if (kt > Vmo) DrawLine(new Vector2(tape.End.X - 2, y - 10), new Vector2(tape.End.X - 2, y), Red, 3f);
        }
        DrawRect(new Rect2(tape.Position.X, mid - 6, tape.Size.X, 12), Black);
        Text(new Vector2(tape.Position.X + 1, mid + 4), Num(R.Ias), (R.Warn & CockpitWarning.Overspeed) != 0 ? Red : Yellow, 10);

        // altitude tape, 100 ft marks, 0.2 px a foot
        tape = new Rect2(o + new Vector2(98, 16), new Vector2(28, 82));
        DrawRect(tape, Grey);
        (first, count) = TapeMarks(R.Alt, 210f, 100);
        for (int i = 0; i < count; i++)
        {
            int ft = first + i * 100;
            float y = mid - TapeOffset(ft, R.Alt, 0.2f);
            if (y < tape.Position.Y || y > tape.End.Y) continue;
            DrawLine(new Vector2(tape.Position.X, y), new Vector2(tape.Position.X + 4, y), White, 1f);
            if (ft % 500 == 0) Text(new Vector2(tape.Position.X + 5, y + 3), Num(ft / 100), White, 8);
        }
        DrawRect(new Rect2(tape.Position.X, mid - 6, tape.Size.X, 12), Black);
        Text(new Vector2(tape.Position.X + 1, mid + 4), Num(R.Alt), Green, 9);
        // vertical speed under the tape, green, in hundreds
        if (Mathf.Abs(R.Vs) >= 100) Text(o + new Vector2(100, 108), (R.Vs > 0 ? "+" : "") + Num(R.Vs / 100), Green, 9);

        // heading tape
        tape = new Rect2(o + new Vector2(30, 102), new Vector2(66, 22));
        DrawRect(tape, Grey);
        float cx = tape.Position.X + tape.Size.X * 0.5f;
        (first, count) = TapeMarks(R.Hdg, 20f, 5);
        for (int i = 0; i < count; i++)
        {
            int h = first + i * 5;
            float x = cx + TapeOffset(h, R.Hdg, 1.6f);
            if (x < tape.Position.X || x > tape.End.X) continue;
            DrawLine(new Vector2(x, tape.Position.Y), new Vector2(x, tape.Position.Y + (h % 10 == 0 ? 5 : 3)), White, 1f);
            if (h % 10 == 0) Text(new Vector2(x - 6, tape.Position.Y + 15), Num(Wrap360(h) / 10), White, 8, HorizontalAlignment.Center, 12);
        }
        DrawLine(new Vector2(cx, tape.Position.Y - 2), new Vector2(cx, tape.Position.Y + 8), Yellow, 2f);
        Warn(o + new Vector2(63, 80));
    }

    private void DrawNd(Vector2 o)
    {
        var c = o + new Vector2(64, 112);
        const float r = 92f;
        // the simple map: a 2 km grid of dots fixed to the ground, turning with the heading and sliding with the aircraft
        float hdg = Mathf.DegToRad(R.Hdg);
        float east = R.Track >= 0 ? 0 : 0;
        for (int gx = -12; gx <= 12; gx++)
        for (int gz = -12; gz <= 12; gz++)
        {
            // ground point (east, north) relative to the aircraft, km; 2 km apart, posted in 100 m
            float de = gx * 2f - Mathf.PosMod(PosE * 0.1f, 2f) + east, dn = gz * 2f - Mathf.PosMod(PosN * 0.1f, 2f);
            // to the heading-up screen: right = along the heading's right, up = ahead; 40 nm range ~ 74 km over the radius
            float ahead = de * Mathf.Sin(hdg) + dn * Mathf.Cos(hdg), right = de * Mathf.Cos(hdg) - dn * Mathf.Sin(hdg);
            var p = c + new Vector2(right, -ahead) * (r / 40f);
            if ((p - c).Length() < r - 4 && p.Y < c.Y) DrawRect(new Rect2(p, new Vector2(1, 1)), Grey);
        }
        // the compass arc, heading up, ticks every 5°, labels every 30
        DrawArc(c, r, -Mathf.Pi * 0.5f - 1.0f, -Mathf.Pi * 0.5f + 1.0f, 24, White, 1f);
        DrawArc(c, r * 0.5f, -Mathf.Pi * 0.5f - 1.0f, -Mathf.Pi * 0.5f + 1.0f, 16, Grey, 1f);
        for (int h = 0; h < 360; h += 5)
        {
            float a = Mathf.DegToRad(h - R.Hdg);
            a = Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi);
            if (Mathf.Abs(a) > 1.0f) continue;
            var d = Dir(a);
            DrawLine(c + d * r, c + d * (r - (h % 10 == 0 ? 6 : 3)), White, 1f);
            if (h % 30 == 0) Text(c + d * (r - 14) + new Vector2(-6, 4), Num(h / 10), White, 8, HorizontalAlignment.Center, 12);
        }
        // the track (a green diamond on the arc) and the aircraft
        if (R.Track >= 0)
        {
            float t = Mathf.Wrap(Mathf.DegToRad(R.Track - R.Hdg), -Mathf.Pi, Mathf.Pi);
            if (Mathf.Abs(t) < 1.0f)
            {
                DrawLine(c, c + Dir(t) * r, Green, 1f);
                DrawCircle(c + Dir(t) * r, 2.5f, Green);
            }
        }
        DrawLine(c + new Vector2(0, -10), c + new Vector2(0, 6), Yellow, 2f);
        DrawLine(c + new Vector2(-8, -2), c + new Vector2(8, -2), Yellow, 2f);
        DrawLine(new Vector2(c.X, c.Y - r - 6), new Vector2(c.X, c.Y - r + 2), Yellow, 2f);
        Text(o + new Vector2(3, 10), "GS", White, 8);
        Text(o + new Vector2(18, 10), Num(R.Gs), Green, 9);
        Text(o + new Vector2(52, 10), Num3(R.Hdg), Green, 10);
        Text(o + new Vector2(96, 10), "40", Cyan, 8);
    }

    private void DrawEwd(Vector2 o)
    {
        // N1 dials, one per engine (the A320's two)
        for (int i = 0; i < Engines; i++)
        {
            var c = o + new Vector2(32 + i * 64, 30);
            float n1 = R.N1(i);
            DrawArc(c, 20, Mathf.DegToRad(-225), Mathf.DegToRad(45), 20, White, 1f);
            Needle(c, DialAngle(n1, 100f), 20, n1 > 0 ? Green : Grey, 2f);
            Text(c + new Vector2(-4, 16), Num(Mathf.RoundToInt(n1)), Green, 10);
        }
        Text(o + new Vector2(54, 30), "N1", Cyan, 8);
        Text(o + new Vector2(54, 40), "%", Cyan, 8);
        // EGT and fuel flow
        Text(o + new Vector2(50, 66), "EGT", Cyan, 8);
        Text(o + new Vector2(50, 78), "FF", Cyan, 8);
        for (int i = 0; i < Engines; i++)
        {
            float x = 10 + i * 78;
            bool on = R.N1(i) > 0;
            Text(o + new Vector2(x, 66), on ? Num(R.Egt) : "XX", on ? Green : Amber, 9);
            Text(o + new Vector2(x, 78), on ? Num(R.Ff) : "XX", on ? Green : Amber, 9);
        }
        Text(o + new Vector2(4, 92), "FOB", Cyan, 8);
        Text(o + new Vector2(26, 92), Num(R.Fuel), (R.Warn & CockpitWarning.FuelLow) != 0 ? Amber : Green, 9);
        Text(o + new Vector2(66, 92), "KG", Cyan, 8);
        // flaps: the slats and flaps' position against the lever, the lever's setting under it
        Text(o + new Vector2(86, 92), "FLAP", White, 8);
        int last = Mathf.Max(1, FlapNames.Length - 1);
        DrawLine(o + new Vector2(80, 104), o + new Vector2(124, 104), Grey, 1f);
        DrawRect(new Rect2(o + new Vector2(80, 101), new Vector2(44f * R.Flaps / (last * 10f), 5)), Green);
        Text(o + new Vector2(96, 118), FlapNames[Mathf.Clamp(R.FlapLever, 0, FlapNames.Length - 1)], Cyan, 10);
        // memos and warnings
        int row = 0;
        void Memo(string s, Color colour) { Text(o + new Vector2(4, 106 + row * 10), s, colour, 8); row++; }
        if ((R.Warn & CockpitWarning.Stall) != 0) Memo("STALL", Red);
        if ((R.Warn & CockpitWarning.Overspeed) != 0) Memo("OVERSPEED", Red);
        if ((R.Warn & CockpitWarning.Gear) != 0) Memo("GEAR NOT DN", Red);
        if ((R.Warn & CockpitWarning.EngineOut) != 0 && row < 2) Memo("ENG SHUT DN", Amber);
        if ((R.Warn & CockpitWarning.FuelLow) != 0 && row < 2) Memo("FUEL LO LVL", Amber);
        if (R.Park && row < 2) Memo("PARK BRK", Green);
        if (R.Speedbrake > 0 && row < 2) Memo("SPEED BRK", Green);
    }

    private void DrawSd(Vector2 o)
    {
        Text(o + new Vector2(46, 10), "WHEEL", White, 9);
        // three gear triangles: green down and locked, red moving, empty up; crosses when broken
        for (int i = 0; i < 3; i++)
        {
            var c = o + new Vector2(i == 0 ? 64 : i == 1 ? 30 : 98, i == 0 ? 36 : 62);
            var colour = R.Gear == 2 ? Green : R.Gear == 1 ? Red : R.Gear == 3 ? Amber : Grey;
            _poly[0] = c + new Vector2(-8, -6);
            _poly[1] = c + new Vector2(8, -6);
            _poly[2] = c + new Vector2(0, 7);
            if (R.Gear is 1 or 2) Polygon(3, colour);
            else
            {
                DrawLine(_poly[0], _poly[1], colour, 1f);
                DrawLine(_poly[1], _poly[2], colour, 1f);
                DrawLine(_poly[2], _poly[0], colour, 1f);
            }
            if (R.Gear == 3) DrawLine(c + new Vector2(-8, -6), c + new Vector2(8, 7), Amber, 1f);
        }
        // the spoilers, five a side, up with the speedbrake
        for (int i = 0; i < 5; i++)
        {
            float h = R.Speedbrake * 4f;
            DrawRect(new Rect2(o + new Vector2(10 + i * 8, 92 - h), new Vector2(5, 2 + h)), Green);
            DrawRect(new Rect2(o + new Vector2(80 + i * 8, 92 - h), new Vector2(5, 2 + h)), Green);
        }
        Text(o + new Vector2(50, 96), "SPLR", Cyan, 8);
        if (R.Park) Text(o + new Vector2(40, 116), "PARK BRK", Green, 9);
        else Text(o + new Vector2(44, 116), "BRAKES", White, 8);
    }

    private void DrawIsis(Vector2 o)
    {
        Attitude(new Rect2(o + new Vector2(30, 20), new Vector2(68, 88)), 1.5f);
        var c = o + new Vector2(64, 64);
        DrawLine(c + new Vector2(-20, 0), c + new Vector2(-6, 0), Yellow, 2f);
        DrawLine(c + new Vector2(6, 0), c + new Vector2(20, 0), Yellow, 2f);
        DrawRect(new Rect2(o + new Vector2(2, 56), new Vector2(26, 14)), Black);
        Text(o + new Vector2(3, 67), Num(R.Ias), White, 10);
        DrawRect(new Rect2(o + new Vector2(100, 56), new Vector2(28, 14)), Black);
        Text(o + new Vector2(100, 67), Num(R.Alt), White, 9);
        Text(o + new Vector2(52, 14), Num3(R.Hdg), White, 9);
    }

    /// <summary>The FCU's windows on the glareshield: speed, heading, altitude, vertical speed (the autopilot's targets when it holds them).</summary>
    private void DrawFcu(Vector2 o)
    {
        DrawRect(new Rect2(o, new Vector2(Cell * GlassCells, Strip)), new Color(0.02f, 0.02f, 0.02f));
        var c = R.Autopilot ? Amber : new Color(0.6f, 0.4f, 0.05f);
        Text(o + new Vector2(8, 12), "SPD", White, 8);
        Text(o + new Vector2(8, 28), R.Autopilot ? Num(R.Ias) : "---", c, 12);
        Text(o + new Vector2(136, 12), "HDG", White, 8);
        Text(o + new Vector2(136, 28), R.Autopilot ? Num3(R.Hdg) : "---", c, 12);
        Text(o + new Vector2(264, 12), "ALT", White, 8);
        Text(o + new Vector2(264, 28), Num(R.Alt / 100 * 100), c, 12);
        Text(o + new Vector2(392, 12), "V/S", White, 8);
        Text(o + new Vector2(392, 28), R.Autopilot ? Num(R.Vs / 100 * 100) : "-----", c, 12);
        Text(o + new Vector2(520, 20), R.Autopilot ? "AP1 A/THR" : "", Green, 10);
    }

    // ---- steam gauges --------------------------------------------------------------------------

    private void DrawAsi(Vector2 o)
    {
        var c = o + new Vector2(64, 64);
        Dial(c, 58);
        Ticks(c, 56, AsiAngle(40), AsiAngle(400), 36, 4, White);
        for (int kt = 40; kt <= 400; kt += 40) Centred(c + Dir(AsiAngle(kt)) * 42, Num(kt / 10), White, 9);
        // the barber pole's start at Vmo
        DrawArc(c, 54, AsiAngle(Vmo) - Mathf.Pi / 2f, AsiAngle(400) - Mathf.Pi / 2f, 8, Red, 3f);
        Text(c + new Vector2(-10, 22), "KT", White, 8);
        Needle(c, AsiAngle(R.Ias), 50, White, 3f);
        DrawCircle(c, 4, Bezel);
    }

    private void DrawAdi(Vector2 o)
    {
        var c = o + new Vector2(64, 64);
        DrawRect(new Rect2(o, new Vector2(Cell, Cell)), Black);
        Attitude(new Rect2(o + new Vector2(6, 6), new Vector2(116, 116)), 2.2f);
        // the round case over the ball's corners
        DrawArc(c, 80, 0, Mathf.Tau, 48, Black, 44f);
        DrawArc(c, 58, 0, Mathf.Tau, 48, Bezel, 4f);
        Needle(c, Mathf.DegToRad(-R.Roll), 54, White, 2f);
        DrawLine(c + new Vector2(-30, 0), c + new Vector2(-10, 0), Amber, 3f);
        DrawLine(c + new Vector2(10, 0), c + new Vector2(30, 0), Amber, 3f);
        DrawCircle(c, 3, Amber);
        Warn(c + new Vector2(0, 30));
    }

    private void DrawAltimeter(Vector2 o)
    {
        var c = o + new Vector2(64, 64);
        Dial(c, 58);
        Ticks(c, 56, 0, Mathf.Tau, 50, 3, White);
        for (int i = 0; i < 10; i++) Centred(c + Dir(i * Mathf.Tau / 10f) * 44, Num(i), White, 10);
        // the drum: the altitude in figures
        DrawRect(new Rect2(c + new Vector2(-22, 12), new Vector2(44, 13)), Black);
        Text(c + new Vector2(-21, 23), Num(R.Alt), White, 10);
        var (h, t) = AltimeterAngles(R.Alt);
        Needle(c, t, 30, White, 4f);
        Needle(c, h, 52, White, 2f);
        DrawCircle(c, 4, Bezel);
    }

    private void DrawVsi(Vector2 o)
    {
        var c = o + new Vector2(64, 64);
        Dial(c, 58);
        foreach (int fpm in new[] { -6000, -4000, -2000, -1000, -500, 0, 500, 1000, 2000, 4000, 6000 })
        {
            var d = Dir(VsiAngle(fpm));
            DrawLine(c + d * 50, c + d * 56, White, 1f);
            if (fpm % 1000 == 0) Centred(c + d * 41, Num(Mathf.Abs(fpm) / 1000), White, 9);
        }
        Text(c + new Vector2(6, -18), "UP", White, 7);
        Text(c + new Vector2(6, 24), "DN", White, 7);
        Needle(c, VsiAngle(R.Vs), 50, White, 3f);
        DrawCircle(c, 4, Bezel);
    }

    private void DrawHsi(Vector2 o)
    {
        var c = o + new Vector2(64, 64);
        Dial(c, 58);
        // the compass card turns: the heading under the lubber line at the top
        for (int h = 0; h < 360; h += 10)
        {
            var d = Dir(Mathf.DegToRad(h - R.Hdg));
            DrawLine(c + d * 56, c + d * (h % 30 == 0 ? 48 : 52), White, 1f);
            if (h % 30 == 0) Centred(c + d * 40, h switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => Num(h / 10) }, h % 90 == 0 ? Amber : White, 9);
        }
        DrawLine(c + new Vector2(0, -58), c + new Vector2(0, -48), Amber, 3f);
        if (R.Track >= 0) Needle(c, Mathf.DegToRad(R.Track - R.Hdg), 34, Magenta, 2f);
        DrawLine(c + new Vector2(0, -12), c + new Vector2(0, 10), Amber, 2f);
        DrawLine(c + new Vector2(-10, -2), c + new Vector2(10, -2), Amber, 2f);
        DrawRect(new Rect2(c + new Vector2(-14, 16), new Vector2(28, 12)), Black);
        Text(c + new Vector2(-12, 26), Num3(R.Hdg), White, 9);
    }

    private void DrawN1(Vector2 o)
    {
        // four small dials, 2 x 2 (the freighter's props read their torque share the same way)
        for (int i = 0; i < 4; i++)
        {
            var c = o + new Vector2(32 + i % 2 * 64, 32 + i / 2 * 64);
            Dial(c, 27);
            if (i >= Engines) continue;
            float n1 = R.N1(i);
            DrawArc(c, 25, DialAngle(0, 100) - Mathf.Pi / 2f, DialAngle(100, 100) - Mathf.Pi / 2f, 16, White, 1f);
            DrawArc(c, 25, DialAngle(100, 100) - Mathf.Pi / 2f, DialAngle(110, 100) - Mathf.Pi / 2f, 4, Red, 2f);
            Needle(c, DialAngle(n1, 100f), 23, White, 2f);
            Text(c + new Vector2(-9, 18), Num(Mathf.RoundToInt(n1)), Green, 9);
            Text(c + new Vector2(-4, -8), Num(i + 1), Grey, 7);
        }
    }

    private void DrawMisc(Vector2 o)
    {
        // EGT bars, one per engine
        Text(o + new Vector2(4, 10), "EGT", White, 8);
        for (int i = 0; i < Engines; i++)
        {
            float h = Mathf.Clamp(R.N1(i) > 0 ? R.Egt / 1000f : 0f, 0f, 1f) * 40f;
            DrawRect(new Rect2(o + new Vector2(6 + i * 12, 14), new Vector2(8, 40)), Face);
            DrawRect(new Rect2(o + new Vector2(6 + i * 12, 54 - h), new Vector2(8, h)), h > 36 ? Red : Green);
        }
        // fuel: a dial in tonnes per full
        var f = o + new Vector2(96, 34);
        Dial(f, 26);
        DrawArc(f, 24, DialAngle(0, 100) - Mathf.Pi / 2f, DialAngle(100, 100) - Mathf.Pi / 2f, 16, White, 1f);
        Needle(f, DialAngle(FuelPct, 100f), 22, (R.Warn & CockpitWarning.FuelLow) != 0 ? Amber : White, 2f);
        Text(f + new Vector2(-10, 18), "FUEL", White, 7);
        // flaps: a needle over the settings, the lever's name under it
        var fl = o + new Vector2(32, 96);
        Dial(fl, 26);
        int last = Mathf.Max(1, FlapNames.Length - 1);
        Ticks(fl, 24, DialAngle(0, 1), DialAngle(1, 1), last, 4, White);
        Needle(fl, DialAngle(R.Flaps / (last * 10f), 1f), 22, White, 2f);
        Text(fl + new Vector2(-12, 20), "FLAPS", White, 7);
        Text(o + new Vector2(66, 96), FlapNames[Mathf.Clamp(R.FlapLever, 0, FlapNames.Length - 1)], White, 10);
        if (R.Park) Text(o + new Vector2(66, 110), "PARK", Red, 9);
        if (R.Speedbrake > 0) Text(o + new Vector2(66, 122), "SPLR", Amber, 9);
        Text(o + new Vector2(98, 110), Mode, Green, 8);
    }

    // ---- numbers without a string per frame ----------------------------------------------------

    /// <summary>The fuel as a share of full, %, for the steam dial (the readout carries kg).</summary>
    public float FuelPct;
    /// <summary>The aircraft's ground position, 100 m units, for the ND's map.</summary>
    public int PosE, PosN;

    private static readonly System.Collections.Generic.Dictionary<int, string> Nums = new(), Nums3 = new();

    /// <summary>An integer's text, made once and kept: the screens redraw on change, a few numbers at a time.</summary>
    public static string Num(int v)
    {
        if (Nums.TryGetValue(v, out var s)) return s;
        if (Nums.Count > 4096) Nums.Clear();
        return Nums[v] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A heading in three figures (005).</summary>
    public static string Num3(int v)
    {
        if (Nums3.TryGetValue(v, out var s)) return s;
        return Nums3[v] = v.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
    }
}
