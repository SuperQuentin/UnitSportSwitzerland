using System.Globalization;
using Godot;

namespace UnitSport.Avatar;

/// <summary>One rental colour: the plastic of the nose and the side pods, and the tubes of the frame.</summary>
public readonly record struct KartLivery(string Name, Color Paint);

/// <summary>
/// A 270 cc rental go-kart (#715) from <see cref="MeshScratch"/> boxes, tubes and one loft, at real
/// dimensions: a 1.05 m wheelbase on a 1.2 m track, a bare tube frame with a solid rear axle and no
/// suspension, a moulded seat, a small steering wheel on a raked column, plastic side pods and a nose
/// that hides the driver's feet, a front and a rear bumper, the engine on a stand beside the seat with
/// its chain to the axle, a number plate on the nose and on each pod. Authored facing +Z with the
/// origin on the ground under the middle of the wheelbase, like <see cref="CarMeshBuilder"/>, which
/// hands a <see cref="BodyShape.Kart"/> body to <see cref="Build"/>; the result is the same
/// <see cref="CarParts"/>, so a <see cref="CarRig"/> steers, spins and seats a kart like any car.
///
/// <para>
/// It has no doors, no glass, no mirrors and no dials: the rig's parts for those are built empty.
/// The driver sits low with the legs out straight ahead (<see cref="SeatFor"/>): every number of
/// the seat is derived from the hip, as for a car (<c>riding-position-derived-from-bike</c>).
/// </para>
/// </summary>
public static class KartMeshBuilder
{
    private static readonly Color Rubber = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Black = new(0.06f, 0.06f, 0.07f);
    private static readonly Color Steel = new(0.62f, 0.63f, 0.67f);
    private static readonly Color DarkSteel = new(0.3f, 0.3f, 0.33f);
    private static readonly Color Rim = new(0.82f, 0.83f, 0.86f);
    private static readonly Color Plate = new(0.96f, 0.96f, 0.93f);
    private static readonly Color EngineGrey = new(0.5f, 0.51f, 0.54f);
    private static readonly Color EngineRed = new(0.8f, 0.1f, 0.08f);

    /// <summary>Wheel centre planes' half spacing (the 1.2 m track), and the tyres: a rental kart's 10x4.5-5 fronts and 11x7.1-5 rears.</summary>
    private const float HalfTrack = 0.60f, FrontWidth = 0.115f, RearWidth = 0.18f, FrontScale = 0.93f;

    /// <summary>The hip of the seated driver, m above the ground: the cushion is 9 cm under it.</summary>
    private const float HipY = 0.20f;
    private const float Recline = 0.42f;

    /// <summary>
    /// The bright colours the rental hall paints its karts, one per seed (a player's index, or the
    /// slot a bot drives): a pack of karts reads as different karts.
    /// </summary>
    public static readonly KartLivery[] Liveries =
    {
        new("Red", new Color(0.88f, 0.07f, 0.08f)),
        new("Blue", new Color(0.08f, 0.3f, 0.92f)),
        new("Yellow", new Color(1f, 0.8f, 0.04f)),
        new("Green", new Color(0.08f, 0.68f, 0.2f)),
        new("Orange", new Color(1f, 0.44f, 0.03f)),
        new("Purple", new Color(0.55f, 0.14f, 0.82f)),
        new("Cyan", new Color(0.04f, 0.74f, 0.86f)),
        new("Pink", new Color(1f, 0.3f, 0.6f)),
        new("Lime", new Color(0.62f, 0.9f, 0.08f)),
        new("White", new Color(0.95f, 0.95f, 0.93f)),
    };

    /// <summary>The livery of seed <paramref name="seed"/>: any int, negative too, always the same colour and number.</summary>
    public static KartLivery LiveryOf(int seed) => Liveries[Mod(seed, Liveries.Length)];

    /// <summary>The kart's race number for a seed, 1..99, as painted on its plates.</summary>
    public static int NumberOf(int seed) => 1 + Mod(unchecked(seed * 7 + 3), 99);

    private static int Mod(int value, int by) => ((value % by) + by) % by;

    /// <summary><paramref name="body"/> in the colour and number of <paramref name="seed"/>.</summary>
    public static CarBody Dress(CarBody body, int seed) => body with { Paint = LiveryOf(seed).Paint, Number = NumberOf(seed), Plate = "" };

    /// <summary>The Swiss army's olive drab: the body, the pods and the nose.</summary>
    public static readonly Color Olive = new(0.29f, 0.33f, 0.17f);

    /// <summary>
    /// The army skin (the <c>Army</c> preset, <see cref="Player.CarSetups.ArmyId"/>): olive plastic, a
    /// black frame and bumpers, a white-on-black military plate on the nose ("M 40 245", Swiss military
    /// plates are the only black ones and start with M) and the race number stencilled in white on the
    /// pods. The number and the plate come from <paramref name="seed"/>, as a rental kart's colour does.
    /// </summary>
    public static CarBody Army(CarBody body, int seed) =>
        body with { Paint = Olive, Lower = Black, Number = NumberOf(seed), Plate = MilitaryPlate(seed) };

    /// <summary>"M 40 245": the army's category (two digits) and the vehicle's number (three), from a seed.</summary>
    public static string MilitaryPlate(int seed) =>
        string.Create(CultureInfo.InvariantCulture, $"M {10 + Mod(unchecked(seed * 13 + 5), 90)} {100 + Mod(unchecked(seed * 37 + 11), 900)}");

    /// <summary>
    /// The driver's seat for a kart of this wheelbase: low, the back reclined 24°, the legs out straight
    /// ahead to pedals a little past the front axle (under the nose), the wheel close to the chest on a
    /// column raked 37° from the horizontal, the left foot on a dead pedal. Cheap: no meshes.
    /// </summary>
    public static DriverSeat SeatFor(float wheelbase)
    {
        var hip = new Vector3(0f, HipY, -0.19f * wheelbase);
        var back = new Vector3(0, Mathf.Cos(Recline), -Mathf.Sin(Recline));
        var shoulders = hip + back * HumanMeshBuilder.DriverTorso;
        var wheel = shoulders + new Vector3(0, -0.10f, 0.40f);
        var column = new Vector3(0, Mathf.Sin(0.65f), -Mathf.Cos(0.65f));
        // the ankle 0.80 m from the hip: the knee a little bent, the foot flat on the pedal
        const float ballY = 0.14f, ankleAbove = 0.07f, ankleBehind = 0.09f, leg = 0.80f;
        float pedalZ = hip.Z + Mathf.Sqrt(leg * leg - Mathf.Pow(ballY + ankleAbove - HipY, 2f)) + ankleBehind;
        return new DriverSeat(hip, Recline, wheel, column, 0.13f,
            Throttle: new Vector3(-0.10f, ballY, pedalZ),
            Brake: new Vector3(0.03f, ballY, pedalZ),
            Rest: new Vector3(0.16f, ballY, pedalZ - 0.02f));
    }

    /// <summary>The wheel, column and pedals' sizes: a kart's wheel is a small black ring.</summary>
    private static CockpitSpec Kit => _kit ??= new(Black, Black,
        ColumnFrom: 0.03f, ColumnTo: 0.6f, ColumnRadius: 0.016f, ColumnFoot: 0.016f,
        RimIn: 0.014f, RimOut: 0.008f, RimDepth: 0.03f, RimSides: 14,
        HubBack: 0.012f, Hub: new Vector3(0.12f, 0.07f, 0.04f), SpokeBack: 0.012f, SpokeShort: 0.01f, SpokeRadius: 0.01f,
        Mark: new Vector3(0.02f, 0.015f, 0.02f),
        DialsAhead: 0.13f,
        PedalArm: 0.01f, ThrottlePad: new Vector3(0.05f, 0.11f, 0.015f), Pad: new Vector3(0.075f, 0.07f, 0.015f),
        MirrorRim: 0.02f);
    private static CockpitSpec? _kit;

    public static CarParts Build(CarBody body, float wheelbase)
    {
        float axF = wheelbase * 0.5f, axR = -axF;
        float rearR = body.WheelRadius, frontR = rearR * FrontScale;
        var paint = body.Paint;
        var frame = body.Lower ?? paint;
        var seat = SeatFor(wheelbase);
        var s = new MeshScratch();   // the shell: frame, plastic, engine, bumpers
        var c = new MeshScratch();   // the cabin (lit with the driver): floor tray, seat, steering column

        Frame(s, frame, axF, axR, rearR);
        Bodywork(s, paint, body.Number, body.Plate);
        Engine(s, axR, rearR);

        // ---- the floor tray, the moulded seat ----
        c.Box(new Vector3(0, 0.076f, 0.32f), new Vector3(0.46f, 0.012f, 0.80f), Black);
        Seat(c, seat);

        var steering = CockpitKit.Wheel(c, seat, Kit);
        var pedals = new[] { seat.Throttle, seat.Brake, seat.Rest }.Select(Pedal).ToArray();
        var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);
        var none = new MeshScratch().Build();
        var hub = CarMeshBuilder.Turned(seat.WheelCentre);
        var cabin = new CarCabin(c.Build(), none, steering, CarMeshBuilder.Turned(seat.WheelAxis),
            new CarNeedle(none, hub, Vector3.Right), new CarNeedle(none, hub, Vector3.Right), CarGauges.Default,
            Enumerable.Repeat(none, 8).ToArray(), Enumerable.Repeat(none, 3).ToArray(), pedals,
            System.Array.Empty<CarMirror>(), seat, CarMeshBuilder.Turned(eye),
            new[] { new SeatAnchor(0, CarMeshBuilder.Turned(seat.Hip), seat.Recline, 0.076f) });

        return new CarParts(s.Build(), none, none, Wheel(rearR, RearWidth), rearR, axF, axR, HalfTrack,
            System.Array.Empty<CarDoor>(), 0f, null, cabin)
        {
            FrontWheel = Wheel(frontR, FrontWidth),
            FrontWheelRadius = frontR,
        };
    }

    /// <summary>
    /// The tube frame: two main rails drawn in to the nose, the side rails the pods sit on, five cross
    /// tubes, the seat stays, the solid rear axle with its brake disc, the front stub axles on their
    /// outriggers with the tie rods to the column, and the two bumper bars on their stays.
    /// </summary>
    private static void Frame(MeshScratch s, Color frame, float axF, float axR, float rearR)
    {
        const float y = 0.09f, r = 0.014f;
        void Tube(Vector3 a, Vector3 b, float radius = r, Color? colour = null) => s.Tube(a, b, radius, colour ?? frame, 6);
        foreach (float sx in new[] { -1f, 1f })
        {
            Tube(new Vector3(sx * 0.30f, y, -0.62f), new Vector3(sx * 0.30f, y, 0.38f));
            Tube(new Vector3(sx * 0.30f, y, 0.38f), new Vector3(sx * 0.19f, y, 0.66f));
            // the side rails the pods are fixed to, tied to the main rails at both ends
            Tube(new Vector3(sx * 0.42f, 0.10f, -0.40f), new Vector3(sx * 0.42f, 0.10f, 0.28f));
            Tube(new Vector3(sx * 0.42f, 0.10f, -0.40f), new Vector3(sx * 0.30f, y, -0.52f));
            Tube(new Vector3(sx * 0.42f, 0.10f, 0.28f), new Vector3(sx * 0.30f, y, 0.38f));
            // the seat stays, from the top of the seat back down to the rear cross tube
            Tube(new Vector3(sx * 0.17f, 0.52f, -0.40f), new Vector3(sx * 0.30f, 0.10f, -0.58f), 0.011f);
            // the front stub axles: an outrigger to the king pin and the stub itself
            Tube(new Vector3(sx * 0.25f, y, axF), new Vector3(sx * 0.50f, 0.10f, axF));
            s.Box(new Vector3(sx * 0.50f, 0.115f, axF), new Vector3(0.05f, 0.08f, 0.06f), Black);
            Tube(new Vector3(sx * 0.50f, 0.115f, axF), new Vector3(sx * 0.57f, 0.115f, axF), 0.012f, Steel);
            // the tie rods to the foot of the steering column
            Tube(new Vector3(0, 0.11f, 0.45f), new Vector3(sx * 0.50f, 0.115f, axF + 0.05f), 0.007f, Steel);
            // the bumpers' stays
            Tube(new Vector3(sx * 0.19f, y, 0.66f), new Vector3(sx * 0.50f, 0.17f, 0.90f), 0.011f);
            Tube(new Vector3(sx * 0.30f, y, -0.60f), new Vector3(sx * 0.46f, 0.18f, -0.86f), 0.011f);
        }
        foreach (float z in new[] { -0.60f, -0.30f, 0.10f, 0.38f })
            Tube(new Vector3(-0.30f, y, z), new Vector3(0.30f, y, z));
        Tube(new Vector3(-0.19f, y, 0.66f), new Vector3(0.19f, y, 0.66f));
        // the bumpers: fat black tube
        Tube(new Vector3(-0.50f, 0.17f, 0.90f), new Vector3(0.50f, 0.17f, 0.90f), 0.022f, Black);
        Tube(new Vector3(-0.46f, 0.18f, -0.86f), new Vector3(0.46f, 0.18f, -0.86f), 0.02f, Black);

        // the solid rear axle: one tube across, no differential, a brake disc on the left of the seat
        Tube(new Vector3(-0.62f, rearR, axR), new Vector3(0.62f, rearR, axR), 0.0255f, Steel);
        s.Ring(new Vector3(0.22f, rearR, axR), Vector3.Right, 0.03f, 0.09f, 0.008f, DarkSteel, 14);
        s.Box(new Vector3(0.22f, rearR + 0.085f, axR - 0.02f), new Vector3(0.04f, 0.05f, 0.05f), EngineRed);   // the caliper
        foreach (float x in new[] { -0.40f, 0.40f })
            s.Box(new Vector3(x, rearR - 0.03f, axR), new Vector3(0.05f, 0.06f, 0.06f), Black);   // the bearing carriers
    }

    /// <summary>
    /// The nose, the side pods and their numbers: all in the livery's plastic. A rental kart has white
    /// plates with the race number on the nose and each pod; an army kart (<paramref name="plate"/>) has
    /// a black military plate on the nose and the number stencilled on the pods.
    /// </summary>
    private static void Bodywork(MeshScratch s, Color paint, int number, string plate)
    {
        bool army = plate.Length > 0;
        // the nose: a loft from the dash to the tip, the top chamfered, sloping down to the front
        static Vector3[] Section(float z, float half, float top) => new[]
        {
            new Vector3(-half, 0.09f, z), new Vector3(-half, top - 0.04f, z), new Vector3(-half + 0.04f, top, z),
            new Vector3(half - 0.04f, top, z), new Vector3(half, top - 0.04f, z), new Vector3(half, 0.09f, z),
        };
        var edges = new[] { paint, paint, paint, paint, paint, paint };
        s.Loft(new[] { Section(0.20f, 0.25f, 0.31f), Section(0.50f, 0.25f, 0.30f), Section(0.86f, 0.19f, 0.19f) }, edges, paint);

        // the plate on the nose faces forward and up, its top edge toward the driver
        float slope = Mathf.Atan2(0.11f, 0.36f);
        var nosePlate = new Vector3(0, 0.30f - 0.11f * (0.68f - 0.50f) / 0.36f + 0.006f, 0.68f);
        var nose = new Basis(Vector3.Right, new Vector3(0, Mathf.Sin(slope), -Mathf.Cos(slope)),
            new Vector3(0, Mathf.Cos(slope), Mathf.Sin(slope)));
        if (army) DrawMilitaryPlate(s, nosePlate, nose, plate);
        else PlateWith(s, nosePlate, nose, 0.24f, 0.17f, number);

        // the side pods: long low sponsons between the wheels, a rounded front; a plate on each
        foreach (float sx in new[] { -1f, 1f })
        {
            s.Box(new Vector3(sx * 0.475f, 0.175f, -0.04f), new Vector3(0.11f, 0.15f, 0.62f), paint);
            s.Box(new Vector3(sx * 0.475f, 0.19f, 0.29f), new Vector3(0.11f, 0.12f, 0.10f), paint, new Basis(Vector3.Right, 0.55f));
            s.Box(new Vector3(sx * 0.475f, 0.19f, -0.37f), new Vector3(0.11f, 0.12f, 0.06f), paint, new Basis(Vector3.Right, -0.45f));
            // read from outside: a left-hand pod (+X) runs front to back, so its right is −Z
            var side = new Basis(new Vector3(0, 0, -sx), Vector3.Up, new Vector3(sx, 0, 0));
            if (army) Text(s, number.ToString(CultureInfo.InvariantCulture), new Vector3(sx * 0.533f, 0.175f, -0.04f), side, 0.1f, Plate);
            else PlateWith(s, new Vector3(sx * 0.535f, 0.185f, -0.04f), side, 0.2f, 0.13f, number);
        }
    }

    /// <summary>A white plate <paramref name="width"/> x <paramref name="height"/> in the plane of <paramref name="plane"/> (X reads right, Y up, Z out), with a number on it.</summary>
    private static void PlateWith(MeshScratch s, Vector3 centre, Basis plane, float width, float height, int number)
    {
        s.Box(centre, new Vector3(width, height, 0.008f), Plate, plane);
        if (number <= 0) return;
        Number(s, number, centre + plane.Z * 0.006f, plane, height * 0.62f);
    }

    /// <summary>A black military plate with its white letters, in the plane of <paramref name="plane"/> (X reads right, Y up, Z out).</summary>
    private static void DrawMilitaryPlate(MeshScratch s, Vector3 centre, Basis plane, string text)
    {
        s.Box(centre, new Vector3(0.34f, 0.115f, 0.008f), Black, plane);
        Text(s, text, centre + plane.Z * 0.006f, plane, 0.06f, Plate);
    }

    private static void Number(MeshScratch s, int number, Vector3 centre, Basis plane, float height) =>
        Text(s, number.ToString(CultureInfo.InvariantCulture), centre, plane, height, new Color(0.05f, 0.05f, 0.06f));

    /// <summary>
    /// <paramref name="text"/> (digits, <c>M</c> and spaces) in strokes, <paramref name="height"/> tall,
    /// centred on <paramref name="centre"/> in the plane of <paramref name="plane"/>: seven segments for
    /// a digit, two uprights and a V for the M.
    /// </summary>
    private static void Text(MeshScratch s, string text, Vector3 centre, Basis plane, float height, Color ink)
    {
        float w = height * 0.52f, gap = height * 0.22f, stroke = height * 0.15f, h = height * 0.5f;
        float total = 0f;
        foreach (char ch in text) total += (ch == ' ' ? w * 0.6f : w) + gap;
        total -= gap;
        float x = -total * 0.5f;
        foreach (char ch in text)
        {
            if (ch == ' ') { x += w * 0.6f + gap; continue; }
            float cx = x + w * 0.5f;
            x += w + gap;
            void Stroke(float dx, float dy, Vector3 size, Basis? turn = null) =>
                s.Box(centre + plane.X * (cx + dx) + plane.Y * dy, size, ink, turn is { } t ? plane * t : plane);
            if (ch == 'M')
            {
                Stroke(-w * 0.5f, 0, new Vector3(stroke, height + stroke, 0.004f));
                Stroke(w * 0.5f, 0, new Vector3(stroke, height + stroke, 0.004f));
                float tilt = Mathf.Atan2(w * 0.5f, h), len = Mathf.Sqrt(w * w * 0.25f + h * h);
                Stroke(-w * 0.25f, h * 0.5f, new Vector3(stroke, len, 0.004f), new Basis(Vector3.Back, tilt));
                Stroke(w * 0.25f, h * 0.5f, new Vector3(stroke, len, 0.004f), new Basis(Vector3.Back, -tilt));
                continue;
            }
            string on = ch switch
            {
                '0' => "abcdef", '1' => "bc", '2' => "abged", '3' => "abgcd", '4' => "fgbc",
                '5' => "afgcd", '6' => "afgedc", '7' => "abc", '9' => "abcdfg", _ => "abcdefg",
            };
            void Bar(char id, float dx, float dy, bool across)
            {
                if (on.Contains(id)) Stroke(dx, dy, across ? new Vector3(w + stroke, stroke, 0.004f) : new Vector3(stroke, h + stroke, 0.004f));
            }
            Bar('a', 0, h, true);
            Bar('g', 0, 0, true);
            Bar('d', 0, -h, true);
            Bar('f', -w * 0.5f, h * 0.5f, false);
            Bar('b', w * 0.5f, h * 0.5f, false);
            Bar('e', -w * 0.5f, -h * 0.5f, false);
            Bar('c', w * 0.5f, -h * 0.5f, false);
        }
    }

    /// <summary>
    /// The engine on its stand to the driver's right (−X): a 270 cc single, the crankcase with the
    /// cylinder and the red tank over it, the air filter behind, the exhaust forward to its muffler
    /// beside the seat; the clutch drum, the chain and the sprocket on the rear axle.
    /// </summary>
    private static void Engine(MeshScratch s, float axR, float rearR)
    {
        s.Box(new Vector3(-0.34f, 0.22f, -0.31f), new Vector3(0.28f, 0.22f, 0.30f), EngineGrey);
        s.Box(new Vector3(-0.34f, 0.38f, -0.27f), new Vector3(0.20f, 0.12f, 0.24f), DarkSteel);
        s.Box(new Vector3(-0.34f, 0.46f, -0.31f), new Vector3(0.24f, 0.06f, 0.30f), EngineRed);
        s.Tube(new Vector3(-0.34f, 0.31f, -0.47f), new Vector3(-0.34f, 0.31f, -0.55f), 0.055f, Black, 8);   // the air filter
        for (int i = 0; i < 4; i++)   // the cylinder's fins
            s.Box(new Vector3(-0.34f, 0.335f + i * 0.03f, -0.27f), new Vector3(0.23f, 0.012f, 0.27f), DarkSteel);
        s.Tube(new Vector3(-0.34f, 0.36f, -0.15f), new Vector3(-0.28f, 0.31f, 0.04f), 0.02f, Steel, 6);        // the exhaust
        s.Tube(new Vector3(-0.28f, 0.31f, 0.04f), new Vector3(-0.28f, 0.31f, 0.22f), 0.038f, Steel, 8);        // the muffler
        s.Tube(new Vector3(-0.34f, 0.12f, -0.40f), new Vector3(-0.34f, 0.12f, -0.38f), 0.045f, DarkSteel, 8);  // the clutch drum
        s.Box(new Vector3(-0.34f, 0.125f, -0.46f), new Vector3(0.012f, 0.05f, 0.13f), Black);                   // the chain
        s.Ring(new Vector3(-0.34f, rearR, axR), Vector3.Right, 0.02f, 0.055f, 0.016f, Steel, 12);                // the sprocket
    }

    /// <summary>
    /// A moulded seat: the pan, a reclined back with side wings and a rear plate's worth of shoulder
    /// room. Hip at <paramref name="seat"/>; the same maths as a car's bucket, scaled to a kart.
    /// </summary>
    private static void Seat(MeshScratch s, DriverSeat seat)
    {
        var hip = seat.Hip;
        var back = new Vector3(0, Mathf.Cos(seat.Recline), -Mathf.Sin(seat.Recline));
        var ahead = new Vector3(0, Mathf.Sin(seat.Recline), Mathf.Cos(seat.Recline));
        var tilt = new Basis(Vector3.Right, -seat.Recline);
        var colour = new Color(0.1f, 0.1f, 0.12f);
        float cushion = hip.Y - 0.09f;
        s.Box(new Vector3(hip.X, (cushion + 0.085f) * 0.5f, hip.Z + 0.01f), new Vector3(0.38f, cushion - 0.085f, 0.42f), colour);
        s.Box(hip + back * 0.28f - ahead * 0.16f, new Vector3(0.42f, 0.52f, 0.05f), colour, tilt);
        foreach (float sx in new[] { -1f, 1f })
            s.Box(hip + new Vector3(sx * 0.2f, 0, 0) + back * 0.26f - ahead * 0.1f, new Vector3(0.05f, 0.42f, 0.1f), colour, tilt);
    }

    /// <summary>A pedal's pad on its hinge (the hinge is a pedal's, <see cref="DriverSeat.PedalHinge"/>; the arm is up under the nose and not drawn).</summary>
    private static HingedPart Pedal(Vector3 pad)
    {
        var hinge = pad + DriverSeat.PedalHinge;
        var m = new MeshScratch();
        m.Box(pad + new Vector3(0, 0, 0.012f), new Vector3(0.06f, 0.09f, 0.015f), Black, new Basis(Vector3.Right, -0.35f));
        return new HingedPart(m.Build(hinge), CarMeshBuilder.Turned(hinge));
    }

    /// <summary>One wheel about X at the origin: a fat slick, a dished magnesium rim with five holes.</summary>
    private static ArrayMesh Wheel(float radius, float width)
    {
        var s = new MeshScratch();
        s.Ring(Vector3.Zero, Vector3.Right, radius * 0.5f, radius, width, Rubber, 16);
        s.Ring(Vector3.Zero, Vector3.Right, 0.03f, radius * 0.52f, width * 0.72f, Rim, 16);
        foreach (float sx in new[] { -1f, 1f })
            for (int i = 0; i < 5; i++)
            {
                float a = Mathf.Tau * i / 5f;
                s.Box(new Vector3(sx * width * 0.37f, Mathf.Cos(a) * radius * 0.3f, Mathf.Sin(a) * radius * 0.3f),
                    new Vector3(0.006f, radius * 0.17f, radius * 0.17f), DarkSteel, new Basis(Vector3.Right, a));
            }
        s.Tube(new Vector3(-width * 0.5f, 0, 0), new Vector3(width * 0.5f, 0, 0), 0.03f, Steel, 6);
        return s.Build();
    }

    /// <summary>The rental hall's colours, one kart each: the model viewer's way to see them all.</summary>
    [Core.Showcase("Cars", "Rental kart")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcaseLiveries()
    {
        var spec = Player.CarCatalog.All.First(c => c.Body.Shape == BodyShape.Kart);
        for (int i = 0; i < Liveries.Length; i++)
        {
            int seed = i;
            yield return ($"{Liveries[i].Name} no. {NumberOf(seed)}", () => CarRig.Create(Dress(spec.Body, seed), spec.Wheelbase, spec.Gauges,
                HumanPalette.ForRider(seed)));
        }
        foreach (int seed in new[] { 1, 2, 3 })
            yield return ($"Army {MilitaryPlate(seed)}", () => CarRig.Create(Army(spec.Body, seed), spec.Wheelbase, spec.Gauges,
                HumanPalette.ForRider(seed)));
    }
}
