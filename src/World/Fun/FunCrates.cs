using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Terrain.Fixture;

namespace UnitSport.World.Fun;

/// <summary>
/// Server: the fun world's weapon crates (#1396), military crates of the Battle Royale's (<see cref="BrCrates"/>) standing
/// round the PvP block, which anyone standing in the block may loot. An emptied or cleared crate (a match's end clears
/// them all) is put back full a while later, so the block always has something to fight with.
/// </summary>
public partial class FunCrates : Node
{
    /// <summary>Where the crates stand, metres from the block's middle.</summary>
    private static readonly (double X, double Y)[] Spots = [(-12, 6), (12, -6), (-70, 70), (70, -70), (-120, -10), (120, 10)];

    /// <summary>How long an emptied crate stays empty, s.</summary>
    public const double Restock = 90;

    private readonly BrCrates _crates;
    private readonly Crate?[] _placed = new Crate?[Spots.Length];
    private readonly double[] _emptySince = new double[Spots.Length];
    private Func<long, bool>? _wrapped;
    private double _clock, _nextCheck;
    private readonly Random _rng = new(1396);

    public FunCrates(BrCrates crates)
    {
        Name = "FunCrates";
        _crates = crates;
    }

    public FunCrates() : this(null!) { }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_clock < _nextCheck) return;
        _nextCheck = _clock + 2;
        OpenToBlock();
        for (int i = 0; i < Spots.Length; i++)
        {
            var c = _placed[i];
            bool there = c != null && _crates.All.Any(x => x.Id == c.Id && x.Ids.Length > 0);
            if (there) { _emptySince[i] = -1; continue; }
            // the first stocking at once, an emptied crate after a while
            if (c != null && _emptySince[i] < 0) _emptySince[i] = _clock;
            if (c != null && _clock - _emptySince[i] < Restock) continue;
            _placed[i] = Stock(i);
            _emptySince[i] = -1;
        }
    }

    /// <summary>Anyone in the PvP block may loot, besides a match's entrants: the match sets its own rule after this node is made.</summary>
    private void OpenToBlock()
    {
        if (_crates.MayLoot == _wrapped) return;
        var match = _crates.MayLoot;
        _wrapped = peer => match(peer) || FunWorld.Players?.Invoke(peer) is { } p && FunWorld.InPvp(p.E, p.N);
        _crates.MayLoot = _wrapped;
    }

    private Crate Stock(int i)
    {
        var (x0, y0, x1, y1) = FunCourse.PvpBox;
        var crate = new Crate
        {
            Style = CrateStyle.Military, Label = "a weapons crate",
            E = FunWorld.Start.E + (x0 + x1) / 2 + Spots[i].X, N = FunWorld.Start.N + (y0 + y1) / 2 + Spots[i].Y,
        };
        crate.SetStacks(Loot.MatchLoot.Roll(Loot.MatchTable.Military, _rng));
        _crates.Spawn([crate]);
        return crate;
    }
}
