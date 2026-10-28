using static UnitSport.Interiors.Pinball.PinballTable;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A table's rules, "modern-lite" (#1325): pure and deterministic, fed the solver's events with their
/// table time. The player's client runs it to drive its table (it asks for balls, scoop kicks and
/// drop resets through <see cref="Commands"/>); the server runs a mirror from the same events alone and
/// owns the score that everyone sees.
///
/// <para>
/// Per player: three balls; every switch scores and adds to the bonus; the top lanes (moved by the
/// flippers) raise the bonus multiplier to 5x; the scoop starts the next mission when lit (lit at the
/// start and again by each drop bank cleared), collects a lit extra ball, and starts the wizard mode
/// once every mission is done; a mission is its goals before its timer; the lock targets light the
/// lock, two locks start a three-ball multiball with the jackpot lit on its shot; the ball save covers
/// the first seconds of each ball and of a multiball; major shots within 4 s chain into combos; tilt
/// after two warnings; match at the end.
/// </para>
/// </summary>
public sealed class PinballRules
{
    public const int BallsPerGame = 3, MaxPlayers = 4, TiltWarnings = 2;
    public const double BallSaveSeconds = 8, MultiballSaveSeconds = 12, ComboWindow = 4, SkillWindow = 5, ScoopHold = 1.2, MessageSeconds = 2.5;

    public enum Phase : byte { Attract, Playing, GameOver }

    public enum Command : byte { ServeBall, LaunchBall, ReleaseScoop, ResetBank, FlippersOff, FlippersOn }

    /// <summary>What the rules ask of the table: a command, its argument (a scoop, a bank), and when.</summary>
    public readonly record struct Order(Command Command, int Arg, double At);

    public sealed class Player
    {
        public long Score;
        public int Ball = 1;
        public int ExtraBalls;
        public int BonusX = 1;
        public long Bonus;
        /// <summary>Missions done, by index; the next to start.</summary>
        public bool[] Done = Array.Empty<bool>();
        public int Next;
        public bool MissionLit = true, LockLit, ExtraLit, ExtraGiven, WizardDone;
        public int Locks, Ramps;
        public bool[] Lanes = new bool[3];
        public bool[] LockTargets = Array.Empty<bool>();
        public int BanksCleared;
        public bool Matched;
    }

    public readonly PinballTable Table;
    public readonly List<Player> Players = new();
    public readonly List<Order> Commands = new();
    public Phase State = Phase.Attract;
    public int Up;
    public double Now;
    public int BallsInPlay, BallsServing;
    /// <summary>The running mission (-1 none), its progress per goal and its end time.</summary>
    public int Mission = -1;
    public int[] Progress = Array.Empty<int>();
    public double MissionEnds;
    public bool Wizard, Multiball, Tilted;
    public int Warnings;
    public double SaveUntil = double.NaN, JackpotValue = 50_000;
    public int Combo;
    public double LastMajor = double.NegativeInfinity;
    public string LastMajorShot = "";
    /// <summary>The skill shot lane (0-2) while the ball is on the plunger and just after.</summary>
    public int SkillLane = -1;
    public double SkillUntil;
    public string Message = "";
    public double MessageUntil;
    /// <summary>The match draw at the end: 00-90.</summary>
    public int MatchDigits = -1;
    /// <summary>A lamp flashing because its switch just closed: switch → until.</summary>
    public readonly Dictionary<int, double> Flash = new();
    /// <summary>Bumped on every change a display shows.</summary>
    public int Version;
    /// <summary>Things worth a sound beyond the switches': "knock" (a free game, an extra ball), "save", "tilt", "jackpot", "mission".</summary>
    public readonly List<string> Cues = new();
    private readonly int _seed;
    private double _ballStarted;

    public PinballRules(PinballTable table, int seed)
    {
        Table = table;
        _seed = seed;
    }

    public Player? Current => State == Phase.Playing && Up < Players.Count ? Players[Up] : null;

    // ---- the game ------------------------------------------------------------------------------

    /// <summary>Start pressed: a new game for one player, or (during the first ball) one more player.</summary>
    public bool Start(double at)
    {
        Advance(at);
        if (State == Phase.Playing)
        {
            if (Players.Count >= MaxPlayers || Players.Exists(p => p.Ball > 1)) return false;
            Players.Add(NewPlayer());
            Say($"PLAYER {Players.Count}");
            return true;
        }
        Players.Clear();
        Players.Add(NewPlayer());
        State = Phase.Playing;
        Up = 0;
        MatchDigits = -1;
        BallsInPlay = 0;
        StartBall();
        return true;
    }

    private Player NewPlayer() => new()
    {
        Done = new bool[Table.Missions.Count],
        LockTargets = new bool[Table.LockTargets.Length],
    };

    private void StartBall()
    {
        var p = Current!;
        Tilted = false;
        Warnings = 0;
        Mission = -1;
        Wizard = false;
        Multiball = false;
        Combo = 0;
        p.Bonus = 0;
        SaveUntil = double.NaN;
        _ballStarted = Now;
        SkillLane = (int)((uint)(_seed * 31 + p.Ball * 7 + Up * 13) % 3);
        SkillUntil = double.PositiveInfinity;
        Ask(Command.FlippersOn);
        _down.Clear();
        for (int i = 0; i < Table.DropBanks.Count; i++) Ask(Command.ResetBank, i);
        Serve();
        Say(Players.Count > 1 ? $"PLAYER {Up + 1}  BALL {p.Ball}" : $"BALL {p.Ball}");
    }

    private void Serve()
    {
        BallsInPlay++;
        Ask(Command.ServeBall, 0, Players.Count == 1 && Players[0].Ball == 1 && Players[0].Score == 0 ? 0.3 : 1.5);
    }

    private void Launch()
    {
        BallsInPlay++;
        Ask(Command.LaunchBall);
    }

    private void Ask(Command c, int arg = 0, double delay = 0) => Commands.Add(new Order(c, arg, Now + delay));

    private void Say(string text, double seconds = MessageSeconds)
    {
        Message = text;
        MessageUntil = Now + seconds;
        Version++;
    }

    /// <summary>The clock moves on: timers (the mission's, the ball save's, a message's) run out in order.</summary>
    public void Advance(double t)
    {
        if (t < Now) return;
        Now = t;
        if (Mission >= 0 && !Wizard && Table.Missions[Mission].Seconds > 0 && Now >= MissionEnds)
        {
            Say($"{Table.Missions[Mission].Name.ToUpperInvariant()} OVER");
            Mission = -1;
        }
        if (!double.IsNaN(SaveUntil) && Now >= SaveUntil) { SaveUntil = double.NaN; Version++; }
        if (SkillLane >= 0 && Now >= SkillUntil) { SkillLane = -1; Version++; }
        if (Message.Length > 0 && Now >= MessageUntil) { Message = ""; Version++; }
    }

    // ---- what the table reports ----------------------------------------------------------------

    public void Apply(PinballSolver.Event e)
    {
        Advance(e.Time);
        if (State != Phase.Playing) return;
        switch (e.Kind)
        {
            case PinballSolver.EventKind.Launch:
                // the ball save starts with the plunge; a skill shot counts a few seconds from it
                if (double.IsNaN(SaveUntil) && Now - _ballStarted < 60 && !Multiball) SaveUntil = Now + BallSaveSeconds;
                if (SkillLane >= 0) SkillUntil = Now + SkillWindow;
                Version++;
                break;
            case PinballSolver.EventKind.Drain:
                Drained();
                break;
            case PinballSolver.EventKind.TiltWarn:
                if (Tilted) break;
                Warnings++;
                Cues.Add("warn");
                if (Warnings > TiltWarnings)
                {
                    Tilted = true;
                    Mission = -1;
                    Ask(Command.FlippersOff);
                    Cues.Add("tilt");
                    Say("TILT", 6);
                }
                else Say("DANGER");
                break;
            case PinballSolver.EventKind.Switch:
                if (!Tilted) Hit(e.Id);
                break;
        }
    }

    /// <summary>A flipper button: the lit top lanes move along (lane change).</summary>
    public void Flipped(int side, double at)
    {
        Advance(at);
        if (Current is not { } p || Tilted) return;
        var l = p.Lanes;
        if (side == 0) (l[0], l[1], l[2]) = (l[1], l[2], l[0]);
        else (l[0], l[1], l[2]) = (l[2], l[0], l[1]);
        Version++;
    }

    private void Hit(int sw)
    {
        var p = Current!;
        var kind = Table.Switches[sw].Kind;
        Flash[sw] = Now + 0.15;
        long points = kind switch
        {
            SwitchKind.Bumper => 1_000, SwitchKind.Sling => 110, SwitchKind.Target => 2_500, SwitchKind.Drop => 3_000,
            SwitchKind.Rollover => 500, SwitchKind.TopLane => 1_000, SwitchKind.Inlane => 1_000, SwitchKind.Outlane => 5_000,
            SwitchKind.Spinner => 250, SwitchKind.Scoop => 5_000, SwitchKind.Ramp => 10_000, SwitchKind.Orbit => 7_500, _ => 0,
        };
        p.Bonus += kind switch
        {
            SwitchKind.Ramp or SwitchKind.Orbit => 2_000, SwitchKind.Drop or SwitchKind.Target => 500, SwitchKind.Scoop => 1_000, _ => 50,
        };
        if (Wizard && ShotOf(sw) is { } ws && (ws.Major || ws.Name == Table.ScoopShot)) points += 100_000;
        Score(points);

        if (kind == SwitchKind.TopLane) TopLane(p, Array.IndexOf(Table.TopLanes, sw));
        if (kind == SwitchKind.Drop) Drop(p, sw);
        int lt = Array.IndexOf(Table.LockTargets, sw);
        if (lt >= 0 && !p.LockLit)
        {
            p.LockTargets[lt] = true;
            if (Array.TrueForAll(p.LockTargets, x => x))
            {
                p.LockLit = true;
                Array.Fill(p.LockTargets, false);
                Say("LOCK IS LIT");
            }
        }
        if (kind == SwitchKind.Ramp) p.Ramps++;
        foreach (var shot in Table.Shots)
            if (Array.IndexOf(shot.Switches, sw) >= 0) Made(p, shot);
        if (kind == SwitchKind.Scoop) Scoop(p, sw);
        Version++;
    }

    private Shot? ShotOf(int sw) => Table.Shots.Find(s => Array.IndexOf(s.Switches, sw) >= 0);

    private void Score(long points)
    {
        if (Current is not { } p || points == 0) return;
        p.Score += points;
    }

    private void TopLane(Player p, int lane)
    {
        if (lane < 0) return;
        if (lane == SkillLane && Now < SkillUntil)
        {
            Score(25_000);
            Say("SKILL SHOT 25,000");
        }
        SkillLane = -1;
        p.Lanes[lane] = true;
        if (!Array.TrueForAll(p.Lanes, x => x)) return;
        Array.Fill(p.Lanes, false);
        if (p.BonusX < 5)
        {
            p.BonusX++;
            Say($"BONUS {p.BonusX}X");
        }
        else Score(50_000);
    }

    private void Drop(Player p, int sw)
    {
        _down.Add(sw);
        for (int bank = 0; bank < Table.DropBanks.Count; bank++)
        {
            var walls = Table.DropBanks[bank];
            bool mine = false, all = true;
            foreach (int w in walls)
            {
                mine |= Table.Walls[w].Switch == sw;
                all &= _down.Contains(Table.Walls[w].Switch);
            }
            if (!mine || !all) continue;
            // the bank cleared: points, the scoop lit for a mission, and the bank back up
            Score(25_000);
            p.BanksCleared++;
            if (Mission < 0 && !Wizard) p.MissionLit = true;
            Say("BANK CLEARED");
            foreach (int w in walls) _down.Remove(Table.Walls[w].Switch);
            Ask(Command.ResetBank, bank, 1.0);
        }
    }

    /// <summary>The drop targets down (their switches), as the rules know from the events.</summary>
    private readonly HashSet<int> _down = new();

    /// <summary>A score with commas, whatever the machine's language.</summary>
    public static string Fmt(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private void Made(Player p, Shot shot)
    {
        if (shot.Major)
        {
            // a combo: another major shot soon after the last
            if (Now - LastMajor < ComboWindow && shot.Name != LastMajorShot)
            {
                Combo++;
                Score(10_000L * (Combo + 1));
                Say($"{Combo + 1}-WAY COMBO");
            }
            else Combo = 0;
            LastMajor = Now;
            LastMajorShot = shot.Name;
        }
        if (Mission >= 0 && !Wizard)
        {
            var m = Table.Missions[Mission];
            for (int g = 0; g < m.Goals.Length; g++)
                if (m.Goals[g].Shot == shot.Name && Progress[g] < m.Goals[g].Count)
                {
                    Progress[g]++;
                    Score(5_000);
                }
            bool done = true;
            for (int g = 0; g < m.Goals.Length; g++) done &= Progress[g] >= m.Goals[g].Count;
            if (done) MissionDone(p);
        }
        if (Multiball && shot.Name == Table.JackpotShot)
        {
            Score((long)JackpotValue);
            Say($"JACKPOT {Fmt((long)JackpotValue)}");
            JackpotValue += 25_000;
            Cues.Add("jackpot");
        }
        else if (!Multiball && p.LockLit && shot.Name == Table.LockShot)
        {
            p.LockLit = false;
            p.Locks++;
            if (p.Locks >= 2)
            {
                p.Locks = 0;
                StartMultiball(3);
                Say("MULTIBALL");
            }
            else Say($"BALL {p.Locks} LOCKED");
        }
    }

    private void MissionDone(Player p)
    {
        var m = Table.Missions[Mission];
        Score(m.Award);
        p.Done[Mission] = true;
        Say($"{m.Name.ToUpperInvariant()} COMPLETE", 3.5);
        Cues.Add("mission");
        int done = Array.FindAll(p.Done, x => x).Length;
        if (done >= 2 && !p.ExtraGiven)
        {
            p.ExtraLit = true;
            p.ExtraGiven = true;
        }
        Mission = -1;
        if (Array.TrueForAll(p.Done, x => x)) p.MissionLit = true;
    }

    private void Scoop(Player p, int sw)
    {
        int scoop = Table.Scoops.FindIndex(s => s.Switch == sw);
        double hold = 0.6;
        if (Table.Scoops[scoop].Name == Table.ScoopShot)
        {
            if (p.ExtraLit)
            {
                p.ExtraLit = false;
                p.ExtraBalls++;
                Say("EXTRA BALL", 3);
                Cues.Add("knock");
                hold = ScoopHold;
            }
            if (p.MissionLit && Mission < 0 && !Wizard && !Multiball)
            {
                p.MissionLit = false;
                hold = ScoopHold + 0.8;
                if (Array.TrueForAll(p.Done, x => x)) StartWizard(p);
                else StartMission(p);
            }
        }
        Ask(Command.ReleaseScoop, scoop, hold);
    }

    private void StartMission(Player p)
    {
        int n = Table.Missions.Count;
        for (int k = 0; k < n; k++)
        {
            int i = (p.Next + k) % n;
            if (p.Done[i]) continue;
            Mission = i;
            p.Next = (i + 1) % n;
            Progress = new int[Table.Missions[i].Goals.Length];
            MissionEnds = Now + Table.Missions[i].Seconds;
            Say(Table.Missions[i].Name.ToUpperInvariant(), 3);
            Cues.Add("mission");
            return;
        }
    }

    private void StartWizard(Player p)
    {
        Wizard = true;
        Mission = -1;
        Say(Table.Wizard.ToUpperInvariant(), 4);
        Cues.Add("mission");
        StartMultiball(3);
    }

    private void StartMultiball(int balls)
    {
        Multiball = true;
        JackpotValue = 50_000;
        SaveUntil = Now + MultiballSaveSeconds;
        Cues.Add("multiball");
        for (int add = balls - BallsInPlay; add > 0; add--) Launch();
    }

    private void Drained()
    {
        BallsInPlay = Math.Max(0, BallsInPlay - 1);
        if (!Tilted && !double.IsNaN(SaveUntil) && Now < SaveUntil)
        {
            // saved: back out through the plunger at once
            Launch();
            if (!Multiball) SaveUntil = double.NaN;
            Say("BALL SAVED");
            Cues.Add("save");
            return;
        }
        if (Multiball && BallsInPlay <= 1)
        {
            Multiball = false;
            if (Wizard)
            {
                Wizard = false;
                var p = Current!;
                Array.Fill(p.Done, false);
                p.WizardDone = true;
                p.MissionLit = true;
            }
            Version++;
        }
        if (BallsInPlay > 0) return;
        EndBall();
    }

    private void EndBall()
    {
        var p = Current!;
        if (!Tilted)
        {
            long bonus = p.Bonus * p.BonusX;
            Score(bonus);
            Say($"BONUS {Fmt(bonus)}", 2);
        }
        p.BonusX = 1;
        Mission = -1;
        Wizard = false;
        Multiball = false;
        if (p.ExtraBalls > 0 && !Tilted)
        {
            p.ExtraBalls--;
            StartBall();
            Say("SHOOT AGAIN");
            return;
        }
        p.Ball++;
        // the next player still with balls, or the game is over
        for (int k = 1; k <= Players.Count; k++)
        {
            int i = (Up + k) % Players.Count;
            if (Players[i].Ball <= BallsPerGame)
            {
                Up = i;
                StartBall();
                return;
            }
        }
        GameOver();
    }

    private void GameOver()
    {
        State = Phase.GameOver;
        Ask(Command.FlippersOff);
        MatchDigits = (int)((uint)(_seed * 2654435761u + (uint)Players.Count * 97u + (uint)(Players[0].Score % 1000)) % 10) * 10;
        foreach (var p in Players)
            if (p.Score % 100 == MatchDigits)
            {
                p.Matched = true;
                Cues.Add("knock");
            }
        Say("GAME OVER", 6);
    }

    /// <summary>The player up quits (left the cabinet, gave up): their game ends where it stands.</summary>
    public void Abandon(double at)
    {
        Advance(at);
        if (State != Phase.Playing) return;
        GameOver();
    }

    // ---- what the lamps show -------------------------------------------------------------------

    public enum Lit : byte { Off, On, Blink }

    /// <summary>One playfield insert's state now.</summary>
    public Lit LampState(Lamp lamp)
    {
        var p = Current;
        if (p == null)
            return lamp.Kind is LampKind.General or LampKind.BumperCap ? Lit.On : lamp.Kind == LampKind.Shot ? Lit.Blink : Lit.Off;
        if (Flash.TryGetValue(lamp.Index, out double until) && Now < until
            && lamp.Kind is LampKind.BumperCap or LampKind.Target or LampKind.Lane) return Lit.On;
        switch (lamp.Kind)
        {
            case LampKind.General: return Lit.On;
            case LampKind.BumperCap: return Lit.Off;
            case LampKind.ShootAgain: return p.ExtraBalls > 0 ? Lit.On : !double.IsNaN(SaveUntil) ? Lit.Blink : Lit.Off;
            case LampKind.BonusX: return p.BonusX >= lamp.Index ? Lit.On : Lit.Off;
            case LampKind.Lane:
            {
                int lane = Array.IndexOf(Table.TopLanes, lamp.Index);
                return lane >= 0 && p.Lanes[lane] ? Lit.On : lane == SkillLane ? Lit.Blink : Lit.Off;
            }
            case LampKind.Target:
            {
                int lt = Array.IndexOf(Table.LockTargets, lamp.Index);
                if (lt >= 0) return p.LockTargets[lt] ? Lit.On : p.LockLit ? Lit.Off : Lit.Blink;
                return _down.Contains(lamp.Index) ? Lit.On : Lit.Off;
            }
            case LampKind.Mission:
                return lamp.Index == Mission ? Lit.Blink : lamp.Index < p.Done.Length && p.Done[lamp.Index] ? Lit.On : Lit.Off;
            case LampKind.Wizard: return Wizard ? Lit.Blink : p.WizardDone ? Lit.On : Lit.Off;
            case LampKind.Lock: return p.LockLit ? Lit.Blink : p.Locks > 0 ? Lit.On : Lit.Off;
            case LampKind.ExtraBall: return p.ExtraLit ? Lit.Blink : Lit.Off;
            case LampKind.Scoop: return p.MissionLit && Mission < 0 && !Multiball ? Lit.Blink : Lit.Off;
            case LampKind.Jackpot: return Multiball ? Lit.Blink : Lit.Off;
            case LampKind.Shot:
                if (Wizard) return Lit.Blink;
                if (Multiball && lamp.Shot == Table.JackpotShot) return Lit.Blink;
                if (Mission >= 0)
                {
                    var m = Table.Missions[Mission];
                    for (int g = 0; g < m.Goals.Length; g++)
                        if (m.Goals[g].Shot == lamp.Shot && Progress[g] < m.Goals[g].Count) return Lit.Blink;
                }
                if (lamp.Shot == Table.ScoopShot && (p.MissionLit || p.ExtraLit) && !Multiball) return Lit.On;
                if (lamp.Shot == Table.LockShot && p.LockLit) return Lit.On;
                if (Now - LastMajor < ComboWindow && lamp.Shot != LastMajorShot && Table.ShotNamed(lamp.Shot)?.Major == true) return Lit.On;
                return Lit.Off;
        }
        return Lit.Off;
    }

    /// <summary>The mission's state as a line ("ESCAPEMENT 1/3  32s"), or "".</summary>
    public string MissionLine()
    {
        if (Wizard) return Table.Wizard.ToUpperInvariant();
        if (Mission < 0) return "";
        var m = Table.Missions[Mission];
        int have = 0, need = 0;
        for (int g = 0; g < m.Goals.Length; g++) { have += Math.Min(Progress[g], m.Goals[g].Count); need += m.Goals[g].Count; }
        int left = Math.Max(0, (int)Math.Ceiling(MissionEnds - Now));
        return $"{m.Name.ToUpperInvariant()} {have}/{need}  {left}s";
    }
}
