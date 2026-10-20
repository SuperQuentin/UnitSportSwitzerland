using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Ropeways;

/// <summary>
/// The lifts (#912) on every peer: whole lines joined from the road tiles' ropeway pieces
/// (<see cref="RopewayJoin"/>), loaded in blocks of <see cref="Block"/> km round whoever asks (the
/// camera, a rider, a bot), each block with a 2 km margin so a line crossing its edge comes whole.
/// The carriers are <see cref="RopewayMotion"/> on the shared sim clock: nothing is sent.
/// docs/notes/world/ski-lifts.md.
/// </summary>
public partial class RopewayService : Node
{
    public const string NodeName = "Ropeways";
    /// <summary>Tiles a block's side.</summary>
    private const int Block = 4;
    private const int Margin = 2;
    /// <summary>How near a bottom station a body boards, flat metres.</summary>
    public const float BoardReach = 8f;

    public static RopewayService? Instance { get; private set; }

    private Func<IChunkSource?> _source = () => null;
    private ChunkManager? _chunks;
    private readonly ConcurrentDictionary<(int, int), IReadOnlyList<RopewayLine>> _blocks = new();
    private readonly HashSet<(int, int)> _loading = new();

    public ChunkManager? Chunks => _chunks;

    public static RopewayService Create(Node world, Func<IChunkSource?> source, ChunkManager chunks)
    {
        var s = new RopewayService { Name = NodeName, _source = source, _chunks = chunks };
        world.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>The shared clock the carriers run on.</summary>
    public static double Sim => Net.ClockSync.SimNow;

    private static (int, int) BlockOf(double e, double n) =>
        ((int)Math.Floor(Math.Floor(e / 1000) / Block), (int)Math.Floor(Math.Floor(n / 1000) / Block));

    /// <summary>The lines round an LV95 point (its block); empty while it loads. Main thread.</summary>
    public IReadOnlyList<RopewayLine> Near(double e, double n)
    {
        var key = BlockOf(e, n);
        if (_blocks.TryGetValue(key, out var lines)) return lines;
        if (_source() is { } source && _loading.Add(key))
            Task.Run(async () =>
            {
                var found = Array.Empty<RopewayLine>() as IReadOnlyList<RopewayLine>;
                try { found = await Load(source, key); }
                catch (Exception ex) { GD.PushWarning($"[ropeways] block {key}: {ex.Message}"); }
                _blocks[key] = found;
            });
        return Array.Empty<RopewayLine>();
    }

    private static async Task<IReadOnlyList<RopewayLine>> Load(IChunkSource source, (int E, int N) block)
    {
        var pieces = new List<RopewayJoin.Piece>();
        for (int te = block.E * Block - Margin; te < (block.E + 1) * Block + Margin; te++)
            for (int tn = block.N * Block - Margin; tn < (block.N + 1) * Block + Margin; tn++)
            {
                var id = new TileId(te, tn);
                RoadTile? tile;
                try { tile = await source.LoadRoadsAsync(id).ConfigureAwait(false); }
                catch (Exception) { continue; }
                if (tile == null) continue;
                foreach (var seg in tile.Segments)
                {
                    if (!RoadFormat.IsAerial(seg.Class) || seg.PointCount < 2) continue;
                    var kind = seg.Class switch
                    {
                        RoadClass.Chairlift => RopewayKind.Chair,
                        RoadClass.SkiLift => RopewayKind.TBar,
                        RoadClass.RopeTow => RopewayKind.RopeTow,
                        _ => RopewayKind.Gondola,
                    };
                    var pts = new double[seg.PointCount * 3];
                    for (int i = 0; i < seg.PointCount; i++)
                    {
                        var (e, n) = seg.Lv95(id, i);
                        pts[i * 3] = e; pts[i * 3 + 1] = n; pts[i * 3 + 2] = seg.Points[i * 3 + 1];
                    }
                    pieces.Add(new RopewayJoin.Piece(kind, pts));
                }
            }
        return RopewayJoin.Join(pieces);
    }

    /// <summary>The bottom station within <see cref="BoardReach"/> m of a world point, if any (its block loaded).</summary>
    public RopewayLine? StationNear(Vector3 world)
    {
        if (_chunks?.Origin is not { } origin) return null;
        var (e, n) = origin.ToLv95(world);
        RopewayLine? best = null;
        double bestD = BoardReach * BoardReach;
        foreach (var line in Near(e, n))
        {
            double d = (line.E[0] - e) * (line.E[0] - e) + (line.N[0] - n) * (line.N[0] - n);
            if (d < bestD) { bestD = d; best = line; }
        }
        return best;
    }

    /// <summary>The line with this key round an LV95 point, if loaded.</summary>
    public RopewayLine? Find(long key, double e, double n)
    {
        foreach (var line in Near(e, n))
            if (line.Key == key) return line;
        return null;
    }

    /// <summary>A rope tow's or a T-bar's line, or a chair's: what to call it in a prompt.</summary>
    public static string KindName(RopewayKind k) => k switch
    {
        RopewayKind.Chair => "chairlift",
        RopewayKind.TBar => "T-bar",
        RopewayKind.RopeTow => "rope tow",
        _ => "gondola",
    };
}
