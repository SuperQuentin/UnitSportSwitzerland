using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Verification helper: raycasts straight down onto carved and uncarved terrain near a
/// tunnel portal and reports whether physics agrees with the visual mesh. Confirms that
/// Jolt honours NaN heightfield cells as holes, so tunnels are actually enterable.
///
///   godot --path . -- --probe lv95E,lv95N,seconds
/// </summary>
public partial class TunnelProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly double _e, _n, _settle;
    private double _elapsed;
    private bool _done;

    public TunnelProbe(ChunkManager chunks, WorldOrigin origin, double e, double n, double settle)
    {
        _chunks = chunks;
        _origin = origin;
        _e = e;
        _n = n;
        _settle = settle;
    }

    public static string[]? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--probe")
            {
                var parts = args[i + 1].Split(',');
                return parts.Length == 3 ? parts : null;
            }
        return null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _elapsed += delta;
        if (_elapsed < _settle) return;
        _done = true;

        var space = GetViewport().World3D.DirectSpaceState;
        int carvedOpen = 0, carvedBlocked = 0, solidHit = 0, solidMiss = 0;

        // sample a grid around the portal and compare physics against the carve mask
        var tile = TileId.FromLv95(_e, _n);
        var holes = LoadHoles(tile);
        if (holes == null)
        {
            GD.Print($"[probe] no hole file for tile {tile} — nothing to verify");
            GetTree().Quit(1);
            return;
        }

        for (int dr = -30; dr <= 30; dr++)
            for (int dc = -30; dc <= 30; dc++)
            {
                double e = _e + dc * ChunkFormat.SpacingM;
                double n = _n + dr * ChunkFormat.SpacingM;
                if (TileId.FromLv95(e, n) != tile) continue;

                int col = (int)((e - tile.MinE) / ChunkFormat.SpacingM);
                int row = (int)((tile.MaxN - n) / ChunkFormat.SpacingM);
                if ((uint)col >= HoleFormat.QuadsPerSide || (uint)row >= HoleFormat.QuadsPerSide) continue;
                bool carved = holes.Contains(HoleFormat.CellIndex(col, row));

                var top = _origin.ToWorld(e, n, 3000);
                var bottom = _origin.ToWorld(e, n, 0);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, bottom));

                if (carved)
                {
                    if (hit.Count == 0) carvedOpen++; else carvedBlocked++;
                }
                else
                {
                    if (hit.Count > 0) solidHit++; else solidMiss++;
                }
            }

        GD.Print($"[probe] carved cells: {carvedOpen} open, {carvedBlocked} still blocked");
        GD.Print($"[probe] solid  cells: {solidHit} hit,  {solidMiss} unexpectedly open");
        GD.Print(carvedOpen > 0 && carvedBlocked == 0
            ? "[probe] RESULT: Jolt honours NaN heightfield holes — tunnels are enterable"
            : "[probe] RESULT: NaN holes NOT honoured — collision still seals the portal");
        GetTree().Quit(0);
    }

    private static HashSet<int>? LoadHoles(TileId tile)
    {
        string path = System.IO.Path.Combine(TerrainPaths.FindChunkDir(), HoleFormat.FileName(tile));
        if (!System.IO.File.Exists(path)) return null;
        using var fs = System.IO.File.OpenRead(path);
        return HoleFormat.Decode(fs);
    }
}
