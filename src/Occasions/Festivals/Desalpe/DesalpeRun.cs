// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

namespace UnitSport.Occasions.Festivals.Desalpe;

public enum DesalpePhase : byte { Grazing, Driving, Arrived }

/// <summary>What a drive came to: cows that arrived (and how many of them are dressed), the whole herd, the cows lost, seconds, the points.</summary>
public sealed record DesalpeResult(int Score, int Arrived, int ArrivedDressed, int Total, int Lost, int Seconds, int Dressed);

/// <summary>
/// One herd of the Désalpe from the alp to the fair (#1269 part 7, pure): grazing at the alp while it is dressed, driven once someone starts
/// it, and set down at the fair when it is done. The server steps it at 10 Hz with the drivers' bodies; every client only draws what the
/// server snapshots (<see cref="DesalpeWire"/>). The drive is over when the Leitkuh and every cow that is not lost stand at the fair, 45 s
/// after the Leitkuh arrived, or at three times par, whichever is first.
/// </summary>
public sealed class DesalpeRun
{
    public readonly DesalpeRoute Route;
    public readonly HerdFlock Flock;
    public readonly byte[] Dress;
    public readonly double Par;

    public DesalpePhase Phase { get; private set; }

    /// <summary>Seconds since the drive started (while driving), or what it took.</summary>
    public double Elapsed { get; private set; }

    /// <summary>Seconds the herd has stood at the fair.</summary>
    public double AtFair { get; private set; }

    public DesalpeResult? Result { get; private set; }

    /// <summary>The run's number in this festival instance (the seed's other half).</summary>
    public int Number { get; }

    public DesalpeRun(int routeSeed, int number, double routeMeters, byte[]? dress = null)
    {
        Number = number;
        Route = DesalpeRoute.Plan(routeSeed, routeMeters);
        Flock = new HerdFlock(Route, DesalpeRules.Cows, routeSeed * 31 + number);
        Dress = dress is { Length: DesalpeRules.Cows } ? (byte[])dress.Clone() : new byte[DesalpeRules.Cows];
        Par = DesalpeRules.ParSeconds(Route.Length);
    }

    /// <summary>Dresses (or undresses) a cow while the herd is still on the alp. False for anything else.</summary>
    public bool TryDress(int cow, byte dress)
    {
        if (Phase != DesalpePhase.Grazing || cow < 0 || cow >= Dress.Length || !DesalpeRules.Valid(dress)) return false;
        Dress[cow] = dress;
        return true;
    }

    public bool Start()
    {
        if (Phase != DesalpePhase.Grazing) return false;
        Phase = DesalpePhase.Driving;
        return true;
    }

    /// <summary>Steps the herd by <paramref name="dt"/> seconds. Returns true on the step the drive ended.</summary>
    public bool Tick(float dt, IReadOnlyList<Driver> drivers)
    {
        if (Phase == DesalpePhase.Grazing) return false;
        Flock.Step(dt, Phase == DesalpePhase.Driving ? drivers : Array.Empty<Driver>());
        if (Phase == DesalpePhase.Arrived) { AtFair += dt; return false; }
        Elapsed += dt;
        bool home = Flock.LeaderArrived;
        bool gathered = home && Enumerable.Range(0, Flock.Count).All(i => Flock.State[i] == CowState.Lost || Flock.Arrived(i));
        if (!(gathered && Flock.SinceArrival >= 4 || home && Flock.SinceArrival >= DesalpeRules.StragglerSeconds || Elapsed >= Par * DesalpeRules.TimeoutOfPar)) return false;
        int arrived = Flock.ArrivedCount(), dressed = 0, lost = 0;
        for (int i = 0; i < Flock.Count; i++)
        {
            if (Flock.Arrived(i) && Dress[i] != 0) dressed++;
            if (!Flock.Arrived(i)) lost++;
        }
        Result = new DesalpeResult(DesalpeRules.Score(arrived, dressed, Elapsed, Par), arrived, dressed, Flock.Count, lost, (int)Elapsed, DesalpeRules.Decorated(Dress));
        Phase = DesalpePhase.Arrived;
        return true;
    }
}
