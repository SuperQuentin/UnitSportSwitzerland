using Godot;
using static UnitSport.Interiors.Pinball.PinballTable;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// The four tables (#1325), original designs on the real conventions (<c>docs/notes/terrain/pinball.md</c>):
/// one shared lower third (two 3 in flippers 7.4 cm apart at the tips, inlanes and outlanes, slingshots,
/// the shooter lane on the right, the arch at the top) and an upper playfield of their own each:
/// <list type="bullet">
/// <item>Clockwork watchmaker (score reels): a left ramp returning to the right inlane, both orbits, a spinner, drop targets, the watch-case scoop.</item>
/// <item>Space mission (7-segment): a right ramp returning to the left inlane, a left orbit and spinner, a bank of four planets, the airlock's subway back to the plunger.</item>
/// <item>Haunted castle (dot-matrix): a centre staircase ramp, a right orbit with the bat's spinner, tombstone drop targets, the crypt's subway.</item>
/// <item>Alpine ski race (LCD): two crossing ramps, both orbits, slalom gates, the start hut scoop.</item>
/// </list>
/// Pure; built once each and shared (<see cref="Get"/>).
/// </summary>
public static class PinballLayouts
{
    public const int Count = 4;
    private static readonly PinballTable?[] Built = new PinballTable?[Count];

    public static PinballTable Get(int index) => Built[index] ??= Make(index);

    public static IEnumerable<PinballTable> All()
    {
        for (int i = 0; i < Count; i++) yield return Get(i);
    }

    private static PinballTable Make(int index) => index switch
    {
        0 => Clockwork(),
        1 => Space(),
        2 => Castle(),
        _ => Alpine(),
    };

    // ---- the shared parts ----------------------------------------------------------------------

    /// <summary>The flipper area's centre and half width: the left wall to the shooter lane's inner wall.</summary>
    public const float Xc = -0.01675f, Hw = 0.23825f;
    public const float Left = -Width / 2, Right = Width / 2, InnerShooter = 0.2215f;
    /// <summary>The arch at the top: a half circle across the whole width.</summary>
    public const float ArchY = Length - Width / 2;
    public const float FlipperY = 0.135f, FlipperDx = 0.088f;

    private static float X(int side, float dx) => Xc + side * dx;

    private sealed class B
    {
        public readonly PinballTable T;
        public B(PinballTable t) => T = t;

        public void Wall(WallKind kind, params Vector2[] pts) => WallSw(kind, -1, pts);

        public void WallSw(WallKind kind, int sw, params Vector2[] pts)
        {
            for (int i = 1; i < pts.Length; i++) T.Walls.Add(new Wall { A = pts[i - 1], B = pts[i], Kind = kind, Switch = sw });
        }

        public void Post(float x, float y, float r = 0.006f) => T.Discs.Add(new Disc { C = new(x, y), R = r, Kind = DiscKind.Post });

        public int Bumper(string name, float x, float y)
        {
            int sw = T.AddSwitch(name, SwitchKind.Bumper);
            T.Discs.Add(new Disc { C = new(x, y), R = 0.029f, Kind = DiscKind.Bumper, Switch = sw });
            T.Lamps.Add(new Lamp { At = new(x, y), Kind = LampKind.BumperCap, Index = sw, Size = 0.029f, Color = T.AccentA });
            return sw;
        }

        public int Rollover(string name, SwitchKind kind, Vector2 a, Vector2 b)
        {
            int sw = T.AddSwitch(name, kind);
            T.Sensors.Add(new Sensor { A = a, B = b, Kind = SensorKind.Rollover, Switch = sw });
            return sw;
        }

        public int Spinner(string name, Vector2 a, Vector2 b)
        {
            int sw = T.AddSwitch(name, SwitchKind.Spinner);
            T.Sensors.Add(new Sensor { A = a, B = b, Kind = SensorKind.Spinner, Switch = sw });
            return sw;
        }

        public int Target(string name, Vector2 a, Vector2 b, Color lamp)
        {
            int sw = T.AddSwitch(name, SwitchKind.Target);
            var face = Face(a, b);
            T.Walls.Add(new Wall { A = a, B = b, Kind = WallKind.Target, Switch = sw, Pass = face, Height = 0.03f });
            T.Lamps.Add(new Lamp { At = (a + b) / 2 + face * 0.03f, Kind = LampKind.Target, Index = sw, Size = 0.008f, Color = lamp });
            return sw;
        }

        /// <summary>A bank of drop targets along a line, facing the playfield's centre side.</summary>
        public int[] Drops(string name, Vector2 from, Vector2 to, int n, Color lamp)
        {
            var bank = new int[n];
            var sws = new int[n];
            var dir = (to - from) / n;
            var face = Face(from, to);
            for (int i = 0; i < n; i++)
            {
                int sw = T.AddSwitch($"{name} {i + 1}", SwitchKind.Drop);
                var a = from + dir * (i + 0.08f);
                var b = from + dir * (i + 0.92f);
                bank[i] = T.Walls.Count;
                sws[i] = sw;
                T.Walls.Add(new Wall { A = a, B = b, Kind = WallKind.Drop, Switch = sw, Pass = face, Height = 0.032f });
                T.Lamps.Add(new Lamp { At = (a + b) / 2 + face * 0.032f, Kind = LampKind.Target, Index = sw, Size = 0.007f, Color = lamp });
            }
            T.DropBanks.Add(bank);
            return sws;
        }

        /// <summary>The side of AB facing the playfield's middle (towards x = Xc, y = 0.5).</summary>
        private static Vector2 Face(Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var n = new Vector2(-ab.Y, ab.X).Normalized();
            return n.Dot(new Vector2(Xc, 0.5f) - (a + b) / 2) >= 0 ? n : -n;
        }

        public int Scoop(string name, Vector2 at, Vector2 kick, Vector2? subwayTo = null)
        {
            int sw = T.AddSwitch(name, SwitchKind.Scoop);
            T.Scoops.Add(new Scoop { Name = name, C = at, Switch = sw, Exit = subwayTo ?? at + kick.Normalized() * 0.02f, Kick = kick, Subway = subwayTo != null });
            return sw;
        }

        /// <summary>A ramp: its mouth (two side walls, a back wall under the ramp, the entry line), and its track.</summary>
        public int Ramp(string name, Vector2 mouthL, Vector2 mouthR, Vector2 inDir, bool plastic, Color tint, params Vector3[] track)
        {
            int sw = T.AddSwitch(name, SwitchKind.Ramp);
            int ramp = T.Ramps.Count;
            T.Ramps.Add(new Ramp { Name = name, Track = track, Switch = sw, Plastic = plastic, Tint = tint });
            var back = inDir * 0.045f;
            Wall(WallKind.Guide, mouthL - inDir * 0.03f, mouthL + back);
            Wall(WallKind.Guide, mouthR - inDir * 0.03f, mouthR + back);
            T.Walls.Add(new Wall { A = mouthL + back, B = mouthR + back, Kind = WallKind.Guide, Visible = false });
            T.Sensors.Add(new Sensor { A = mouthL, B = mouthR, Kind = SensorKind.RampEntry, Ramp = ramp, Dir = inDir });
            return sw;
        }

        public void Shot(string name, bool major, params int[] switches) =>
            T.Shots.Add(new Shot { Name = name, Major = major, Switches = switches });

        public void Arrow(string shot, Vector2 at, Vector2 dir, Color c) =>
            T.Lamps.Add(new Lamp { At = at, Dir = dir.Normalized(), Kind = LampKind.Shot, Shot = shot, Size = 0.014f, Color = c });

        public void Insert(LampKind kind, Vector2 at, Color c, int index = 0, float size = 0.01f, string shot = "") =>
            T.Lamps.Add(new Lamp { At = at, Kind = kind, Index = index, Size = size, Color = c, Shot = shot });

        public void Mission(string name, float seconds, int award, params (string, int)[] goals) =>
            T.Missions.Add(new Mission { Name = name, Seconds = seconds, Award = award, Goals = goals });
    }

    /// <summary>
    /// The frame every table shares: the side walls, the arch, the shooter lane and its gate, and the
    /// lower third (outlanes, inlanes, slingshots, flippers), plus the inserts there.
    /// </summary>
    private static B Frame(PinballTable t)
    {
        var b = new B(t);
        // the shooter lane: the cabinet's right wall and the lane's inner wall, the ball on the plunger
        t.ShooterX = (InnerShooter + Right) / 2;
        t.ShooterRest = 0.06f;
        t.ShooterTop = 0.78f;
        b.Wall(WallKind.Guide, new(Right, 0.02f), new(Right, ArchY));
        b.Wall(WallKind.Guide, new(InnerShooter, 0.0f), new(InnerShooter, t.ShooterTop));
        b.Wall(WallKind.Guide, new(InnerShooter, 0.035f), new(Right, 0.035f));
        // the one-way gate at the lane's top: out of the lane, never back into it
        t.Walls.Add(new Wall { A = new(InnerShooter, t.ShooterTop), B = new(Right, t.ShooterTop), Kind = WallKind.Gate, Pass = new Vector2(0, 1), Height = 0.02f });
        // the arch, right to left
        const int n = 28;
        var arch = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = Mathf.Pi * i / n;
            arch[i] = new Vector2(Mathf.Cos(a) * Width / 2, ArchY + Mathf.Sin(a) * Width / 2);
        }
        b.Wall(WallKind.Guide, arch);
        b.Wall(WallKind.Guide, new(Left, ArchY), new(Left, 0f));
        // the orbits' exits: a ball coming down either side wall is turned into the playfield above the
        // slingshots, not straight down the outlane
        b.Wall(WallKind.Rubber, new(Left, 0.50f), new(-0.198f, 0.43f));
        b.Wall(WallKind.Rubber, new(InnerShooter, 0.62f), new(0.152f, 0.545f));

        foreach (int side in new[] { -1, 1 })
        {
            string s = side < 0 ? "Left" : "Right";
            // the outlane / inlane separator, its post at the top
            b.Post(X(side, 0.190f), 0.36f);
            b.Wall(WallKind.Guide, new(X(side, 0.190f), 0.36f), new(X(side, 0.190f), 0.205f), new(X(side, 0.0796f), 0.1594f));
            // the slingshot: its kicking face towards the middle, rubber on the inlane side
            Vector2 top = new(X(side, 0.150f), 0.335f), bo = new(X(side, 0.150f), 0.225f), bi = new(X(side, 0.103f), 0.200f);
            int sling = t.AddSwitch($"{s} sling", SwitchKind.Sling);
            var face = new Vector2(-(bi - top).Y, (bi - top).X).Normalized();
            if (face.X * side > 0) face = -face;
            t.Walls.Add(new Wall { A = top, B = bi, Kind = WallKind.Sling, Switch = sling, Pass = face, Height = 0.04f });
            b.Wall(WallKind.Rubber, top, bo);
            b.Wall(WallKind.Guide, bo, bi);
            b.Post(top.X, top.Y, 0.005f);
            // the lanes' switches
            int inlane = b.Rollover($"{s} inlane", SwitchKind.Inlane, new(X(side, 0.190f), 0.27f), new(X(side, 0.150f), 0.27f));
            int outlane = b.Rollover($"{s} outlane", SwitchKind.Outlane, new(X(side, Hw), 0.27f), new(X(side, 0.190f), 0.27f));
            b.Insert(LampKind.General, new(X(side, 0.170f), 0.30f), t.AccentB, inlane, 0.006f);
            b.Insert(LampKind.General, new(X(side, 0.214f), 0.30f), t.AccentA, outlane, 0.006f);
            // the flipper: rest 28° below level, its stroke 50°
            float rest = Mathf.DegToRad(-28f), up = Mathf.DegToRad(22f);
            t.Flippers.Add(new Flipper
            {
                Pivot = new(X(side, FlipperDx), FlipperY), Side = side < 0 ? 0 : 1,
                Rest = side < 0 ? rest : Mathf.Pi - rest, Up = side < 0 ? up : Mathf.Pi - up,
            });
        }
        // shoot again between the flippers, the bonus multiplier's row above it
        b.Insert(LampKind.ShootAgain, new(Xc, 0.07f), new Color(1f, 0.3f, 0.2f), 0, 0.011f);
        for (int i = 0; i < 4; i++)
            b.Insert(LampKind.BonusX, new(Xc + (i - 1.5f) * 0.03f, 0.235f), new Color(1f, 0.85f, 0.25f), i + 2, 0.009f);
        return b;
    }

    /// <summary>Three top lanes under the arch and their guides, lit for the bonus multiplier.</summary>
    private static void TopLanes(B b, float cx, Color lamp)
    {
        var t = b.T;
        var lanes = new int[3];
        for (int i = 0; i < 4; i++)
        {
            float x = cx + (i - 1.5f) * 0.05f;
            b.Wall(WallKind.Guide, new(x, 0.895f), new(x, 0.945f));
            b.Post(x, 0.895f, 0.005f);
        }
        for (int i = 0; i < 3; i++)
        {
            float x = cx + (i - 1f) * 0.05f;
            lanes[i] = b.Rollover($"Top lane {i + 1}", SwitchKind.TopLane, new(x - 0.02f, 0.92f), new(x + 0.02f, 0.92f));
            b.Insert(LampKind.Lane, new(x, 0.875f), lamp, lanes[i], 0.008f);
        }
        t.TopLanes = lanes;
    }

    /// <summary>A mission insert per mission in an arc above the flippers, the wizard's in the middle.</summary>
    private static void MissionInserts(B b, Color c)
    {
        int n = b.T.Missions.Count;
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Pi * (0.25f + 0.5f * i / Math.Max(1, n - 1));
            b.Insert(LampKind.Mission, new(Xc - Mathf.Cos(a) * 0.075f, 0.375f + Mathf.Sin(a) * 0.035f), c, i, 0.011f);
        }
        b.Insert(LampKind.Wizard, new(Xc, 0.355f), new Color(1f, 1f, 1f), 0, 0.013f);
    }

    /// <summary>An orbit: a guide wall making a lane along a side, its rollover half way up.</summary>
    private static int Orbit(B b, string name, int side, float guideX, float from, float to)
    {
        b.Wall(WallKind.Guide, new(guideX, from), new(guideX, to));
        b.Post(guideX, from, 0.006f);
        float outer = side < 0 ? Left : InnerShooter;
        return b.Rollover(name, SwitchKind.Orbit, new(guideX, (from + to) / 2), new(outer, (from + to) / 2));
    }

    // ---- 1: the clockwork watchmaker ------------------------------------------------------------

    private static PinballTable Clockwork()
    {
        var t = new PinballTable
        {
            Name = "clockwork", Title = "Clockwork", Era = PinballEra.Reels,
            Ground = new Color(0.16f, 0.10f, 0.06f), AccentA = new Color(0.95f, 0.72f, 0.25f), AccentB = new Color(0.30f, 0.62f, 0.75f),
            Cabinet = new Color(0.45f, 0.24f, 0.10f), Trim = new Color(0.85f, 0.70f, 0.40f),
            ScoopShot = "Watch case", LockShot = "Escapement", JackpotShot = "Escapement", Wizard = "Grand complication",
        };
        var b = Frame(t);
        var gold = t.AccentA;
        TopLanes(b, 0.06f, gold);
        int p1 = b.Bumper("Gear 1", -0.055f, 0.80f), p2 = b.Bumper("Gear 2", 0.055f, 0.80f), p3 = b.Bumper("Gear 3", 0.0f, 0.715f);
        int lo = Orbit(b, "Left orbit", -1, -0.200f, 0.64f, 0.80f);
        int spin = b.Spinner("Mainspring", new(-0.200f, 0.68f), new(Left, 0.68f));
        int ro = Orbit(b, "Right orbit", 1, 0.172f, 0.66f, 0.80f);
        // the escapement: a left ramp up the back, over the top, a wire form down to the right inlane
        int ramp = b.Ramp("Escapement", new(-0.165f, 0.60f), new(-0.115f, 0.60f), new Vector2(-0.1f, 1f).Normalized(), true, new Color(1f, 0.85f, 0.5f, 0.45f),
            new(-0.14f, 0.60f, 0f), new(-0.152f, 0.70f, 0.03f), new(-0.158f, 0.82f, 0.06f), new(-0.13f, 0.95f, 0.085f),
            new(-0.05f, 1.01f, 0.095f), new(0.06f, 1.0f, 0.09f), new(0.145f, 0.93f, 0.085f), new(0.196f, 0.80f, 0.075f),
            new(0.196f, 0.55f, 0.055f), new(0.17f, 0.40f, 0.03f), new(X(1, 0.170f), 0.33f, 0f));
        // the hours: three drop targets right of centre facing the flippers, their pocket behind
        var drops = b.Drops("Hour", new(0.0f, 0.555f), new(0.08f, 0.555f), 3, t.AccentB);
        b.Wall(WallKind.Guide, new(-0.003f, 0.555f), new(-0.003f, 0.585f), new(0.083f, 0.585f), new(0.083f, 0.555f));
        // the watch case: a scoop up the right, met by the left flipper's cross shot
        int scoop = b.Scoop("Watch case", new(0.148f, 0.715f), new Vector2(-0.45f, -0.65f));
        b.Wall(WallKind.Guide, new(0.124f, 0.722f), new(0.127f, 0.745f), new(0.172f, 0.745f));
        int t1 = b.Target("Dial 1", new(-0.07f, 0.645f), new(-0.04f, 0.655f), gold);
        int t2 = b.Target("Dial 2", new(-0.015f, 0.66f), new(0.015f, 0.66f), gold);
        int t3 = b.Target("Dial 3", new(0.04f, 0.655f), new(0.07f, 0.645f), gold);
        t.LockTargets = new[] { t1, t2, t3 };

        b.Shot("Escapement", true, ramp);
        b.Shot("Left orbit", true, lo);
        b.Shot("Right orbit", true, ro);
        b.Shot("Watch case", false, scoop);
        b.Shot("Hours", false, drops);
        b.Shot("Gears", false, p1, p2, p3);
        b.Shot("Mainspring", false, spin);
        b.Shot("Dials", false, t1, t2, t3);
        b.Arrow("Escapement", new(-0.135f, 0.53f), new(-0.1f, 1f), gold);
        b.Arrow("Left orbit", new(-0.228f, 0.40f), new Vector2(0, 1), gold);
        b.Arrow("Right orbit", new(0.197f, 0.42f), new Vector2(0, 1), gold);
        b.Arrow("Watch case", new(0.125f, 0.64f), new(0.25f, 1f), gold);
        b.Arrow("Hours", new(0.04f, 0.50f), new(0f, 1f), gold);
        b.Insert(LampKind.Lock, new(-0.135f, 0.49f), new Color(0.3f, 0.7f, 1f), 0, 0.01f);
        b.Insert(LampKind.ExtraBall, new(0.10f, 0.61f), new Color(1f, 0.3f, 0.2f), 0, 0.009f);
        b.Insert(LampKind.Scoop, new(0.14f, 0.68f), new Color(1f, 1f, 1f), 0, 0.009f);
        b.Insert(LampKind.Jackpot, new(-0.11f, 0.50f), new Color(1f, 0.3f, 0.9f), 0, 0.009f);

        b.Mission("Wind the mainspring", 40, 200_000, ("Mainspring", 25));
        b.Mission("Escapement", 45, 300_000, ("Escapement", 3));
        b.Mission("Balance wheel", 45, 300_000, ("Left orbit", 2), ("Right orbit", 2));
        b.Mission("Strike the hours", 50, 350_000, ("Hours", 3), ("Gears", 10));
        MissionInserts(b, gold);
        return t;
    }

    // ---- 2: the space mission --------------------------------------------------------------------

    private static PinballTable Space()
    {
        var t = new PinballTable
        {
            Name = "space", Title = "Space Mission", Era = PinballEra.Segments,
            Ground = new Color(0.04f, 0.05f, 0.14f), AccentA = new Color(0.25f, 0.85f, 1f), AccentB = new Color(1f, 0.45f, 0.12f),
            Cabinet = new Color(0.10f, 0.12f, 0.30f), Trim = new Color(0.80f, 0.82f, 0.88f),
            ScoopShot = "Airlock", LockShot = "Launch ramp", JackpotShot = "Launch ramp", Wizard = "Deep space",
        };
        var b = Frame(t);
        var cyan = t.AccentA;
        TopLanes(b, 0.06f, cyan);
        int p1 = b.Bumper("Asteroid 1", 0.0f, 0.79f), p2 = b.Bumper("Asteroid 2", 0.10f, 0.80f), p3 = b.Bumper("Asteroid 3", 0.05f, 0.715f);
        int lo = Orbit(b, "Orbit", -1, -0.200f, 0.64f, 0.80f);
        int spin = b.Spinner("Thrusters", new(-0.200f, 0.68f), new(Left, 0.68f));
        // the launch ramp: from the right of centre up the back right, over the top, a wire form down the left to the left inlane
        int ramp = b.Ramp("Launch ramp", new(0.085f, 0.60f), new(0.135f, 0.60f), new Vector2(0.15f, 1f).Normalized(), true, new Color(0.6f, 0.9f, 1f, 0.45f),
            new(0.11f, 0.60f, 0f), new(0.135f, 0.72f, 0.035f), new(0.16f, 0.86f, 0.07f), new(0.12f, 0.97f, 0.09f),
            new(0.0f, 1.01f, 0.095f), new(-0.12f, 0.97f, 0.09f), new(-0.215f, 0.86f, 0.08f), new(-0.228f, 0.70f, 0.065f),
            new(-0.228f, 0.48f, 0.045f), new(-0.21f, 0.39f, 0.02f), new(X(-1, 0.170f), 0.33f, 0f));
        var planets = b.Drops("Planet", new(-0.085f, 0.565f), new(0.035f, 0.565f), 4, t.AccentB);
        b.Wall(WallKind.Guide, new(-0.088f, 0.565f), new(-0.088f, 0.595f), new(0.038f, 0.595f), new(0.038f, 0.565f));
        // the airlock: a scoop up on the left whose subway puts the ball back on the plunger, launched
        int scoop = b.Scoop("Airlock", new(-0.135f, 0.72f), new(0, PinballSolver.AutoLaunch), new Vector2(t.ShooterX, t.ShooterRest));
        b.Wall(WallKind.Guide, new(-0.16f, 0.71f), new(-0.16f, 0.75f), new(-0.11f, 0.75f), new(-0.11f, 0.71f));
        int t1 = b.Target("Beacon 1", new(0.140f, 0.41f), new(0.145f, 0.44f), cyan);
        int t2 = b.Target("Beacon 2", new(0.147f, 0.455f), new(0.152f, 0.485f), cyan);
        t.LockTargets = new[] { t1, t2 };
        b.Wall(WallKind.Guide, new(0.162f, 0.40f), new(0.162f, 0.50f));

        b.Shot("Launch ramp", true, ramp);
        b.Shot("Orbit", true, lo);
        b.Shot("Airlock", false, scoop);
        b.Shot("Planets", false, planets);
        b.Shot("Asteroids", false, p1, p2, p3);
        b.Shot("Thrusters", false, spin);
        b.Shot("Beacons", false, t1, t2);
        b.Arrow("Launch ramp", new(0.11f, 0.53f), new(0.15f, 1f), cyan);
        b.Arrow("Orbit", new(-0.228f, 0.40f), new Vector2(0, 1), cyan);
        b.Arrow("Airlock", new(-0.12f, 0.65f), new(-0.2f, 1f), cyan);
        b.Arrow("Planets", new(-0.025f, 0.51f), new Vector2(0, 1), cyan);
        b.Insert(LampKind.Lock, new(0.11f, 0.49f), new Color(0.3f, 1f, 0.4f), 0, 0.01f);
        b.Insert(LampKind.Jackpot, new(0.08f, 0.50f), new Color(1f, 0.3f, 0.9f), 0, 0.009f);
        b.Insert(LampKind.ExtraBall, new(-0.10f, 0.66f), new Color(1f, 0.3f, 0.2f), 0, 0.009f);
        b.Insert(LampKind.Scoop, new(-0.135f, 0.685f), new Color(1f, 1f, 1f), 0, 0.009f);

        b.Mission("Countdown", 45, 250_000, ("Launch ramp", 2), ("Orbit", 1));
        b.Mission("Planetfall", 50, 300_000, ("Planets", 4));
        b.Mission("Asteroid field", 30, 250_000, ("Asteroids", 15));
        b.Mission("Spacewalk", 45, 350_000, ("Thrusters", 20), ("Beacons", 2));
        MissionInserts(b, cyan);
        return t;
    }

    // ---- 3: the haunted castle -------------------------------------------------------------------

    private static PinballTable Castle()
    {
        var t = new PinballTable
        {
            Name = "castle", Title = "Haunted Castle", Era = PinballEra.DotMatrix,
            Ground = new Color(0.10f, 0.06f, 0.14f), AccentA = new Color(0.55f, 1f, 0.35f), AccentB = new Color(1f, 0.45f, 0.10f),
            Cabinet = new Color(0.12f, 0.08f, 0.10f), Trim = new Color(0.55f, 0.20f, 0.65f),
            ScoopShot = "Crypt", LockShot = "Staircase", JackpotShot = "Staircase", Wizard = "Midnight",
        };
        var b = Frame(t);
        var green = t.AccentA;
        TopLanes(b, 0.06f, green);
        int p1 = b.Bumper("Ghost 1", 0.03f, 0.79f), p2 = b.Bumper("Ghost 2", 0.115f, 0.745f);
        int ro = Orbit(b, "Bat orbit", 1, 0.172f, 0.66f, 0.80f);
        int spin = b.Spinner("Bat wings", new(0.172f, 0.69f), new(InnerShooter, 0.69f));
        // the staircase: straight up the middle, turning left at the top, a wire form down the left to the left inlane
        int ramp = b.Ramp("Staircase", new(-0.045f, 0.62f), new(0.005f, 0.62f), new Vector2(0, 1), true, new Color(0.8f, 0.6f, 1f, 0.45f),
            new(-0.02f, 0.62f, 0f), new(-0.02f, 0.74f, 0.035f), new(-0.03f, 0.88f, 0.07f), new(-0.08f, 0.98f, 0.09f),
            new(-0.17f, 0.96f, 0.09f), new(-0.226f, 0.86f, 0.08f), new(-0.232f, 0.68f, 0.065f), new(-0.232f, 0.47f, 0.045f),
            new(-0.21f, 0.39f, 0.02f), new(X(-1, 0.170f), 0.33f, 0f));
        // the tombstones: three drop targets on the left facing the right flipper, a wall behind
        var tombs = b.Drops("Tombstone", new(-0.105f, 0.47f), new(-0.090f, 0.59f), 3, t.AccentB);
        b.Wall(WallKind.Guide, new(-0.108f, 0.465f), new(-0.133f, 0.46f), new(-0.133f, 0.60f), new(-0.092f, 0.595f));
        // the crypt: a scoop on the right of the stairs whose subway comes up above the right slingshot
        int scoop = b.Scoop("Crypt", new(0.065f, 0.66f), new(-0.35f, -0.45f), new Vector2(0.12f, 0.40f));
        b.Wall(WallKind.Guide, new(0.04f, 0.665f), new(0.045f, 0.69f), new(0.085f, 0.69f), new(0.09f, 0.665f));
        int g1 = b.Target("Phantom", new(-0.10f, 0.78f), new(-0.07f, 0.765f), green);
        int g2 = b.Target("Spectre", new(-0.14f, 0.73f), new(-0.115f, 0.705f), green);
        t.LockTargets = new[] { g1, g2 };

        b.Shot("Staircase", true, ramp);
        b.Shot("Bat orbit", true, ro);
        b.Shot("Crypt", false, scoop);
        b.Shot("Tombstones", false, tombs);
        b.Shot("Ghosts", false, p1, p2);
        b.Shot("Bat wings", false, spin);
        b.Shot("Spirits", false, g1, g2);
        b.Arrow("Staircase", new(-0.02f, 0.55f), new Vector2(0, 1), green);
        b.Arrow("Bat orbit", new(0.197f, 0.41f), new Vector2(0, 1), green);
        b.Arrow("Crypt", new(0.06f, 0.60f), new(0.1f, 1f), green);
        b.Arrow("Tombstones", new(-0.06f, 0.50f), new(-1f, 0.2f), green);
        b.Insert(LampKind.Lock, new(-0.02f, 0.50f), new Color(0.3f, 0.7f, 1f), 0, 0.01f);
        b.Insert(LampKind.Jackpot, new(-0.045f, 0.52f), new Color(1f, 0.3f, 0.9f), 0, 0.009f);
        b.Insert(LampKind.ExtraBall, new(0.09f, 0.62f), new Color(1f, 0.3f, 0.2f), 0, 0.009f);
        b.Insert(LampKind.Scoop, new(0.065f, 0.625f), new Color(1f, 1f, 1f), 0, 0.009f);

        b.Mission("Seance", 45, 250_000, ("Staircase", 3));
        b.Mission("Bat swarm", 35, 250_000, ("Bat wings", 30));
        b.Mission("Grave robbers", 50, 300_000, ("Tombstones", 3), ("Spirits", 2));
        b.Mission("Haunting", 45, 350_000, ("Bat orbit", 2), ("Staircase", 2), ("Ghosts", 8));
        MissionInserts(b, green);
        return t;
    }

    // ---- 4: the alpine ski race ------------------------------------------------------------------

    private static PinballTable Alpine()
    {
        var t = new PinballTable
        {
            Name = "alpine", Title = "Alpine Ski Race", Era = PinballEra.Lcd,
            Ground = new Color(0.82f, 0.88f, 0.95f), AccentA = new Color(0.90f, 0.10f, 0.12f), AccentB = new Color(0.10f, 0.40f, 0.95f),
            Cabinet = new Color(0.85f, 0.10f, 0.12f), Trim = new Color(0.95f, 0.95f, 0.95f),
            ScoopShot = "Start hut", LockShot = "Downhill", JackpotShot = "Slalom", Wizard = "Alpine crown",
        };
        var b = Frame(t);
        var red = t.AccentA;
        TopLanes(b, 0.06f, red);
        int p1 = b.Bumper("Mogul 1", -0.06f, 0.80f), p2 = b.Bumper("Mogul 2", 0.05f, 0.80f), p3 = b.Bumper("Mogul 3", -0.005f, 0.725f);
        int lo = Orbit(b, "Left orbit", -1, -0.200f, 0.64f, 0.80f);
        int spin = b.Spinner("Chairlift", new(-0.200f, 0.68f), new(Left, 0.68f));
        int ro = Orbit(b, "Right orbit", 1, 0.172f, 0.66f, 0.80f);
        // two ramps crossing over the middle: the downhill (left mouth) lands in the right inlane, the slalom (right mouth) in the left
        int down = b.Ramp("Downhill", new(-0.165f, 0.60f), new(-0.115f, 0.60f), new Vector2(-0.05f, 1f).Normalized(), true, new Color(0.7f, 0.85f, 1f, 0.45f),
            new(-0.14f, 0.60f, 0f), new(-0.148f, 0.72f, 0.035f), new(-0.13f, 0.86f, 0.075f), new(-0.06f, 0.93f, 0.095f),
            new(0.05f, 0.92f, 0.095f), new(0.145f, 0.84f, 0.085f), new(0.196f, 0.70f, 0.07f), new(0.196f, 0.50f, 0.05f),
            new(0.17f, 0.40f, 0.03f), new(X(1, 0.170f), 0.33f, 0f));
        int slalom = b.Ramp("Slalom", new(0.055f, 0.60f), new(0.105f, 0.60f), new Vector2(0.05f, 1f).Normalized(), true, new Color(1f, 0.7f, 0.7f, 0.45f),
            new(0.08f, 0.60f, 0f), new(0.095f, 0.70f, 0.03f), new(0.08f, 0.80f, 0.06f), new(0.02f, 0.86f, 0.10f),
            new(-0.08f, 0.85f, 0.105f), new(-0.17f, 0.80f, 0.095f), new(-0.226f, 0.68f, 0.08f), new(-0.228f, 0.48f, 0.05f),
            new(-0.21f, 0.39f, 0.02f), new(X(-1, 0.170f), 0.33f, 0f));
        int scoop = b.Scoop("Start hut", new(-0.015f, 0.665f), new(0.15f, -0.65f));
        b.Wall(WallKind.Guide, new(-0.04f, 0.67f), new(-0.035f, 0.695f), new(0.005f, 0.695f), new(0.01f, 0.67f));
        int g1 = b.Target("Gate 1", new(-0.186f, 0.665f), new(-0.186f, 0.695f), red);
        int g2 = b.Target("Gate 2", new(-0.186f, 0.71f), new(-0.186f, 0.74f), t.AccentB);
        int g3 = b.Target("Gate 3", new(0.100f, 0.47f), new(0.105f, 0.44f), red);
        int g4 = b.Target("Gate 4", new(0.108f, 0.52f), new(0.113f, 0.49f), t.AccentB);
        b.Wall(WallKind.Guide, new(0.118f, 0.43f), new(0.118f, 0.53f));
        t.LockTargets = new[] { g1, g2, g3, g4 };

        b.Shot("Downhill", true, down);
        b.Shot("Slalom", true, slalom);
        b.Shot("Left orbit", true, lo);
        b.Shot("Right orbit", true, ro);
        b.Shot("Start hut", false, scoop);
        b.Shot("Gates", false, g1, g2, g3, g4);
        b.Shot("Moguls", false, p1, p2, p3);
        b.Shot("Chairlift", false, spin);
        b.Arrow("Downhill", new(-0.14f, 0.53f), new(-0.05f, 1f), red);
        b.Arrow("Slalom", new(0.08f, 0.53f), new(0.05f, 1f), t.AccentB);
        b.Arrow("Left orbit", new(-0.228f, 0.40f), new Vector2(0, 1), red);
        b.Arrow("Right orbit", new(0.197f, 0.40f), new Vector2(0, 1), red);
        b.Arrow("Start hut", new(-0.015f, 0.60f), new Vector2(0, 1), red);
        b.Insert(LampKind.Lock, new(-0.14f, 0.48f), new Color(0.2f, 0.6f, 1f), 0, 0.01f);
        b.Insert(LampKind.Jackpot, new(0.10f, 0.48f), new Color(1f, 0.3f, 0.9f), 0, 0.009f);
        b.Insert(LampKind.ExtraBall, new(0.03f, 0.62f), new Color(1f, 0.3f, 0.2f), 0, 0.009f);
        b.Insert(LampKind.Scoop, new(-0.015f, 0.635f), new Color(0.1f, 0.1f, 0.1f), 0, 0.009f);

        b.Mission("Downhill", 45, 250_000, ("Downhill", 3));
        b.Mission("Slalom", 50, 300_000, ("Slalom", 2), ("Downhill", 2));
        b.Mission("Giant slalom", 50, 300_000, ("Gates", 4), ("Left orbit", 1), ("Right orbit", 1));
        b.Mission("Ski jump", 40, 350_000, ("Chairlift", 15), ("Slalom", 2));
        MissionInserts(b, red);
        return t;
    }
}
