using Godot;

namespace UnitSport.Interiors.Pinball;

/// <summary>The backbox's display, by the era of the table (#1325).</summary>
public enum PinballEra : byte
{
    /// <summary>Electro-mechanical: score reels behind a lit backglass, chimes.</summary>
    Reels,
    /// <summary>Late 70s-80s solid state: 7-segment and alphanumeric displays.</summary>
    Segments,
    /// <summary>90s: an orange 128 x 32 dot-matrix display.</summary>
    DotMatrix,
    /// <summary>Today: a colour LCD.</summary>
    Lcd,
}

/// <summary>What a playfield switch is, which sets its score and its sound.</summary>
public enum SwitchKind : byte
{
    Bumper, Sling, Target, Drop, Rollover, TopLane, Inlane, Outlane, Spinner, Scoop, Ramp, Orbit,
}

/// <summary>
/// One pinball table as data (#1325): its walls, rubbers, posts, pop bumpers, slingshots, flippers,
/// targets, rollovers, spinners, scoops, ramps, lamps and named shots, in the playfield's own frame:
/// metres, x across (the player's right +), y up the table from the bottom edge, h above the
/// playfield. Pure (Godot maths only): the solver, the rules, the cabinet's mesh and the tests all read
/// the same numbers. The four tables are original designs (<see cref="PinballLayouts"/>).
/// </summary>
public sealed class PinballTable
{
    /// <summary>The playfield [m]: 20.25 x 42 in under a 21 x 43 in glass [S: flippers.be].</summary>
    public const float Width = 0.51f, Length = 1.07f;
    /// <summary>The playfield's slope [S: 6.5°, steeper is faster].</summary>
    public const float SlopeDeg = 6.5f;
    /// <summary>The ball: 1 1/16 in steel, 80 g [S].</summary>
    public const float BallRadius = 0.0135f;

    public string Name = "";
    public string Title = "";
    public PinballEra Era;
    /// <summary>The playfield's ground, its art's two accents, the cabinet's paint and the trim.</summary>
    public Color Ground, AccentA, AccentB, Cabinet, Trim;

    public readonly List<Wall> Walls = new();
    public readonly List<Disc> Discs = new();
    public readonly List<Flipper> Flippers = new();
    public readonly List<Sensor> Sensors = new();
    public readonly List<Scoop> Scoops = new();
    public readonly List<Ramp> Ramps = new();
    public readonly List<Switch> Switches = new();
    public readonly List<Lamp> Lamps = new();
    public readonly List<Shot> Shots = new();
    public readonly List<Mission> Missions = new();
    /// <summary>Drop target banks: each a list of indices into <see cref="Walls"/>.</summary>
    public readonly List<int[]> DropBanks = new();
    /// <summary>A set of stand-ups whose completion lights the lock (indices into <see cref="Switches"/>).</summary>
    public int[] LockTargets = Array.Empty<int>();
    /// <summary>The top lanes (switch indices) whose set raises the bonus multiplier.</summary>
    public int[] TopLanes = Array.Empty<int>();

    /// <summary>The shot that starts a mission, collects the extra ball and starts the wizard mode.</summary>
    public string ScoopShot = "";
    /// <summary>The shot that locks a ball when the lock is lit, and the jackpot's in multiball.</summary>
    public string LockShot = "", JackpotShot = "";
    /// <summary>The wizard mode's name, shown on the backbox.</summary>
    public string Wizard = "";

    /// <summary>The shooter lane: its centre x, where the ball rests on the plunger, and where the lane ends.</summary>
    public float ShooterX, ShooterRest, ShooterTop;

    /// <summary>A switch, added once, referenced by index from the parts that close it.</summary>
    public int AddSwitch(string name, SwitchKind kind)
    {
        Switches.Add(new Switch(name, kind));
        return Switches.Count - 1;
    }

    public int SwitchIndex(string name) => Switches.FindIndex(s => s.Name == name);

    public Shot? ShotNamed(string name) => Shots.Find(s => s.Name == name);

    public sealed record Switch(string Name, SwitchKind Kind);

    /// <summary>How a wall behaves when the ball hits it.</summary>
    public enum WallKind : byte
    {
        /// <summary>A wooden or metal guide: a dull bounce.</summary>
        Guide,
        /// <summary>A rubber ring between posts: a livelier one.</summary>
        Rubber,
        /// <summary>A slingshot's kicking rubber: kicks the ball away.</summary>
        Sling,
        /// <summary>A stand-up target.</summary>
        Target,
        /// <summary>A drop target: drops when hit, until its bank is reset.</summary>
        Drop,
        /// <summary>A one-way gate: lets the ball through along <see cref="Wall.Pass"/> only.</summary>
        Gate,
    }

    /// <summary>A straight wall from A to B (a curve is several).</summary>
    public sealed class Wall
    {
        public Vector2 A, B;
        public WallKind Kind;
        public int Switch = -1;
        /// <summary>A gate's way through; a sling's or a target's face (the side the ball comes from).</summary>
        public Vector2 Pass;
        /// <summary>How high it stands over the playfield, for the cabinet's mesh [m].</summary>
        public float Height = 0.035f;
        /// <summary>Drawn (false: an invisible stop, e.g. under a wire form's lip).</summary>
        public bool Visible = true;
    }

    public enum DiscKind : byte { Post, Bumper }

    /// <summary>A round part: a rubber post or a pop bumper.</summary>
    public sealed class Disc
    {
        public Vector2 C;
        public float R;
        public DiscKind Kind;
        public int Switch = -1;
    }

    /// <summary>
    /// A flipper: a tapered bat turning round its pivot, from its rest angle up to its stroke's end
    /// (radians from +x, counter-clockwise). Pivot to tip <see cref="Length"/>; a 3 in bat is 7.6 cm overall.
    /// </summary>
    public sealed class Flipper
    {
        public Vector2 Pivot;
        public float Length = 0.058f, RBase = 0.0125f, RTip = 0.0064f;
        public float Rest, Up;
        /// <summary>Which button: 0 left, 1 right.</summary>
        public int Side;

        public float Sign => Mathf.Sign(Up - Rest);
    }

    public enum SensorKind : byte { Rollover, Spinner, RampEntry }

    /// <summary>A line the ball rolls over: a rollover switch, a spinner's gate, a ramp's mouth.</summary>
    public sealed class Sensor
    {
        public Vector2 A, B;
        public SensorKind Kind;
        public int Switch = -1;
        /// <summary>A ramp's mouth: the ramp's index, and the way in (the ball must cross along it).</summary>
        public int Ramp = -1;
        public Vector2 Dir;
    }

    /// <summary>A saucer that catches a slow ball and kicks it out; with a subway, out somewhere else.</summary>
    public sealed class Scoop
    {
        public Vector2 C;
        public float R = 0.02f;
        public int Switch = -1;
        /// <summary>Where the ball comes out (the scoop itself, or a subway's exit) and its kick [m/s].</summary>
        public Vector2 Exit, Kick;
        /// <summary>A subway: the ball travels under the playfield to <see cref="Exit"/>.</summary>
        public bool Subway;
        public string Name = "";
    }

    /// <summary>
    /// A ramp or a wire form: a track the ball climbs off the playfield (x, y, h), entered through its
    /// mouth (a <see cref="Sensor"/>), left at its far end back onto the playfield, made when it gets there.
    /// A ball too slow rolls back out of the mouth.
    /// </summary>
    public sealed class Ramp
    {
        public string Name = "";
        public Vector3[] Track = Array.Empty<Vector3>();
        public int Switch = -1;
        /// <summary>Plastic ramp (true) or a wire form (false), for the mesh.</summary>
        public bool Plastic = true;
        public Color Tint = new(0.6f, 0.8f, 1f, 0.5f);
    }

    /// <summary>What a lamp shows.</summary>
    public enum LampKind : byte
    {
        /// <summary>An arrow on a shot: lit when that shot counts for the mission, the jackpot or a combo.</summary>
        Shot,
        /// <summary>A mission's insert (in <see cref="Lamp.Index"/>): lit once done, blinking while running.</summary>
        Mission,
        /// <summary>The bonus multiplier's insert (2x..5x in <see cref="Lamp.Index"/>).</summary>
        BonusX,
        /// <summary>A top lane's light (its switch in <see cref="Lamp.Index"/>).</summary>
        Lane,
        /// <summary>A drop target's or a stand-up's light (its switch in <see cref="Lamp.Index"/>).</summary>
        Target,
        ShootAgain, Lock, ExtraBall, Scoop, Jackpot, Wizard,
        /// <summary>A pop bumper's cap (its switch): flashes when hit.</summary>
        BumperCap,
        /// <summary>Plain decoration: on.</summary>
        General,
    }

    /// <summary>A playfield insert, a round or a triangle (an arrow pointing along <see cref="Dir"/>).</summary>
    public sealed class Lamp
    {
        public Vector2 At;
        public float Size = 0.012f;
        public Vector2 Dir;
        public LampKind Kind;
        public string Shot = "";
        public int Index;
        public Color Color = new(1f, 0.85f, 0.3f);
    }

    /// <summary>A named shot: the switches that make it (any one), for the rules and the lamps.</summary>
    public sealed class Shot
    {
        public string Name = "";
        public int[] Switches = Array.Empty<int>();
        /// <summary>A major shot (a ramp or an orbit): chains into combos.</summary>
        public bool Major;
    }

    /// <summary>A mission: shots to make (each so many times) before the timer runs out (0 = until the ball drains).</summary>
    public sealed class Mission
    {
        public string Name = "";
        public (string Shot, int Count)[] Goals = Array.Empty<(string, int)>();
        public float Seconds = 45f;
        public int Award = 250_000;
    }
}
