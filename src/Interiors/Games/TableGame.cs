namespace UnitSport.Interiors.Games;

/// <summary>The games of a venue's pieces (#1322) that <see cref="TableGames"/> runs; stored on the wire as a byte.</summary>
public enum GameKind : byte
{
    None, Pool, Darts, Foosball, AirHockey, Claw, Cabinet,
}

/// <summary>
/// One piece's game (#1324, #1326), pure: the same class on the server, which runs it, and on every client, which
/// unpacks what the server sends and shows it. Seats hold peer ids in play order (0 empty, bots below
/// <see cref="TableGames.BotBase"/>); <see cref="Up"/> is the seat whose turn it is in a turn game.
///
/// <para>
/// A <b>turn game</b> (pool, darts) resolves an action at once on the server (<see cref="Act"/>) and every peer
/// animates it from the same numbers, ending on the server's result. A <b>live game</b> (<see cref="Live"/>: table
/// football, air hockey, the claw) is stepped by the server (<see cref="Step"/>) from its seats' inputs
/// (<see cref="Input"/>), and its poses go out 20 times a second (<see cref="WritePoses"/>).
/// </para>
/// </summary>
public abstract class TableGame
{
    /// <summary>Bot seats' peer ids: this and below (bowling's partners are -900 000 and down, a few at most).</summary>
    public const long BotBase = -2_000_000;

    public static bool IsBot(long peer) => peer <= BotBase;

    public abstract GameKind Kind { get; }
    /// <summary>How many can play at once.</summary>
    public abstract int MaxSeats { get; }
    /// <summary>A live game: stepped by the server, poses streamed.</summary>
    public virtual bool Live => false;
    /// <summary>A bot may take a seat here (an opponent for a player alone).</summary>
    public virtual bool Bots => true;

    public long[] Seats { get; private set; } = Array.Empty<long>();
    /// <summary>The seat whose turn it is (turn games).</summary>
    public int Up;
    /// <summary>The server's names for the players, for a game's calls ("Vreni pots the 3"); unset, "Player n".</summary>
    public Func<long, string>? Names { get; set; }

    protected string NameAt(int seat) => Names?.Invoke(Seats[seat]) ?? $"Player {seat + 1}";

    protected TableGame() { }

    /// <summary>Sizes the seats: called once by the maker.</summary>
    protected void Init() => Seats = new long[MaxSeats];

    // ---- seats ---------------------------------------------------------------------------------

    public int SeatOf(long peer)
    {
        if (peer == 0) return -1;
        for (int i = 0; i < Seats.Length; i++)
            if (Seats[i] == peer) return i;
        return -1;
    }

    public int Seated
    {
        get
        {
            int n = 0;
            foreach (long s in Seats) if (s != 0) n++;
            return n;
        }
    }

    public bool AnyHuman
    {
        get
        {
            foreach (long s in Seats) if (s > 0) return true;
            return false;
        }
    }

    /// <summary>The first free seat, or <paramref name="wanted"/> when it is free; -1 when full.</summary>
    public int FreeSeat(int wanted = -1)
    {
        if (wanted >= 0 && wanted < Seats.Length && Seats[wanted] == 0) return wanted;
        for (int i = 0; i < Seats.Length; i++)
            if (Seats[i] == 0) return i;
        return -1;
    }

    /// <summary>A peer takes seat <paramref name="seat"/>; a game may refuse while it is under way (null = taken).</summary>
    public virtual string? Sit(long peer, int seat)
    {
        Seats[seat] = peer;
        OnSeats();
        return null;
    }

    /// <summary>A peer leaves their seat; a turn game passes their turn.</summary>
    public virtual void Stand(long peer)
    {
        int seat = SeatOf(peer);
        if (seat < 0) return;
        Seats[seat] = 0;
        OnSeats();
    }

    /// <summary>The seats changed (someone sat or stood): a game starts, ends or moves its turn.</summary>
    protected virtual void OnSeats() { }

    // ---- play ----------------------------------------------------------------------------------

    /// <summary>
    /// Seat <paramref name="seat"/> does <paramref name="action"/> (each game's own numbers) at shared time
    /// <paramref name="now"/>; the server's <paramref name="rng"/> rolls what luck there is. Null when done,
    /// else why not, for the player.
    /// </summary>
    public abstract string? Act(int seat, int action, int[] args, double now, Random rng);

    /// <summary>Live games: one server step of <paramref name="dt"/> seconds at shared time <paramref name="now"/>. True when the state changed (a goal, a prize).</summary>
    public virtual bool Step(double dt, double now) => false;

    /// <summary>Live games: seat <paramref name="seat"/>'s controls, as its client packed them.</summary>
    public virtual void Input(int seat, byte[] input) { }

    /// <summary>Whether something still plays out (balls rolling, a dart in flight) and the next action must wait.</summary>
    public virtual bool Busy(double now) => false;

    /// <summary>Whether this live game needs stepping now (someone at it, or something still moving).</summary>
    public virtual bool Awake => false;

    /// <summary>
    /// A bot in seat <paramref name="seat"/>: what it does at <paramref name="now"/> (a turn game's shot when it is up and the table
    /// is still; a live game's controls through <see cref="Input"/>), or null for nothing yet.
    /// </summary>
    public virtual (int Action, int[] Args)? Bot(int seat, double now, Random rng) => null;

    /// <summary>A short line of the game for the HUD and the chat (score, whose turn).</summary>
    public abstract string Summary(Func<long, string> name);

    // ---- the wire ------------------------------------------------------------------------------

    protected abstract void WriteState(BinaryWriter w);
    protected abstract void ReadState(BinaryReader r);
    public virtual void WritePoses(BinaryWriter w) { }
    public virtual void ReadPoses(BinaryReader r) { }

    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Seats.Length);
        foreach (long s in Seats) w.Write(s);
        w.Write((byte)Up);
        WriteState(w);
        w.Flush();
        return ms.ToArray();
    }

    public void Unpack(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            long s = r.ReadInt64();
            if (i < Seats.Length) Seats[i] = s;
        }
        Up = r.ReadByte();
        ReadState(r);
    }

    public byte[] PackPoses()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WritePoses(w);
        w.Flush();
        return ms.ToArray();
    }

    public void UnpackPoses(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        ReadPoses(r);
    }

    /// <summary>The next seat after <paramref name="seat"/> that holds someone (itself when it is the only one).</summary>
    protected int NextSeated(int seat)
    {
        for (int k = 1; k <= Seats.Length; k++)
        {
            int s = (seat + k) % Seats.Length;
            if (Seats[s] != 0) return s;
        }
        return seat;
    }
}
