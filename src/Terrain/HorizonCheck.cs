using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// <c>godot --path . -- --horizoncheck [--at E,N]</c> (#1429): the far horizon is where it belongs,
/// before and after the origin moves. Waits until every block within <see cref="HorizonLayer.DistanceM"/>
/// is drawn, then checks that each block sits at its LV95 corner (<see cref="WorldOrigin.ToWorld(double, double, double)"/>
/// to the millimetre), that no block is there twice, that none is farther than the unload slack and
/// that the coverage texture's corner is the cover's LV95 corner. Then shifts the origin three
/// times (whole km, a fraction of a metre, and back) and checks the same in each new frame.
/// RESULT ok or FAILED with the first faults. Runs on the generated world (no map), with a horizon the test
/// profile leaves off (#1411):
/// <c>--horizon 20 --systems terrain,generated</c>.
/// Under <c>--frame earth</c> (#1739) there is no <see cref="HorizonLayer"/>: the globe is the far view
/// (<c>terrain/earth-far-view</c>; its check is <c>--longflightcheck</c>'s far view "present all the way"). The check
/// then holds that no flat horizon is built to double it, and ends.
/// </summary>
public partial class HorizonCheck : Node
{
    private const double TimeoutS = 240, SettleS = 1.0, BlockM = TerrainMeshBuilder.HorizonBlockTiles * ChunkFormat.TileSizeM;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private double _t, _settled;
    private int _step;
    private Vector2 _coverBefore;

    public HorizonCheck(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => CmdArgs.Has("--horizoncheck");

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > TimeoutS)
        {
            GD.Print($"[horizoncheck] RESULT: FAILED timeout after {TimeoutS} s (blocks {_chunks.Horizon?.Progress()})");
            GetTree().Quit(2);
            return;
        }
        var horizon = _chunks.Horizon;
        if (GameSettings.Current.Frame == WorldFrame.Earth)
        {
            // long enough for a flat horizon to have been asked for and built, as it is within seconds on flat
            if (_t < 10) return;
            bool none = horizon == null || horizon.GetChildCount() == 0;
            GD.Print(none
                ? "[horizoncheck] under --frame earth: no flat horizon built (the globe is the far view: --longflightcheck)"
                : $"[horizoncheck] under --frame earth a flat horizon is built ({horizon!.GetChildCount()} blocks)");
            GD.Print(none ? "[horizoncheck] RESULT: ok" : "[horizoncheck] RESULT: FAILED a flat horizon under --frame earth");
            SetProcess(false);
            GetTree().Quit(none ? 0 : 1);
            return;
        }
        if (horizon == null || !horizon.HasData) return;
        // the test profile has no horizon (#1411): this check asks for one with --horizon
        if (horizon.DistanceM <= 0)
        {
            GD.Print("[horizoncheck] RESULT: FAILED the horizon is off (0 km): run with --horizon N");
            GetTree().Quit(2);
            return;
        }
        if (horizon.Progress() is not { } p || p.Total == 0 || p.Done < p.Total) { _settled = 0; return; }
        _settled += delta;
        if (_settled < SettleS) return;

        switch (_step++)
        {
            case 0:
                GD.Print($"[horizoncheck] {p.Total} blocks to {horizon.DistanceM / 1000:F0} km at origin LV95 {_origin.E:F0}/{_origin.N:F0}");
                Verify(horizon, "start");
                Shift(horizon, 7000, -3000, exact: false);
                break;
            case 1:
                Shift(horizon, 0.375, -0.25, exact: true);
                break;
            case 2:
                Shift(horizon, -7000.375, 3000.25, exact: true);
                break;
            default:
                foreach (var f in _faults.Take(12)) GD.Print($"[horizoncheck]   {f}");
                GD.Print(_faults.Count == 0
                    ? "[horizoncheck] RESULT: ok"
                    : $"[horizoncheck] RESULT: FAILED {_faults.Count} faults");
                SetProcess(false);
                GetTree().Quit(_faults.Count == 0 ? 0 : 1);
                break;
        }
    }

    /// <summary>Moves the origin by (dE, dN) metres now and checks the horizon in the new frame at once.</summary>
    private void Shift(HorizonLayer horizon, double dE, double dN, bool exact)
    {
        _coverBefore = CoverOrigin(horizon);
        double e = _origin.E + dE, n = _origin.N + dN;
        var before = _origin.Frame;
        if (OriginShifter.Instance is not { } shifter || !shifter.ShiftTo(e, n, exact))
        {
            _faults.Add($"shift to {e:F3}/{n:F3} did not happen");
            return;
        }
        var shift = _origin.Since(before);
        string what = $"after shift {shift}";
        Verify(horizon, what);
        var expected = shift.Point(new Vector3(_coverBefore.X, 0, _coverBefore.Y));
        var cover = CoverOrigin(horizon);
        if (cover.DistanceTo(new Vector2(expected.X, expected.Z)) > 0.001f)
            _faults.Add($"{what}: cover_origin {cover} not moved with the shift (want {expected.X:F3},{expected.Z:F3})");
        _settled = 0;
    }

    private static Vector2 CoverOrigin(HorizonLayer horizon) =>
        horizon.Material?.GetShaderParameter("cover_origin").AsVector2() ?? Vector2.Zero;

    private void Verify(HorizonLayer horizon, string when)
    {
        var seen = new HashSet<(int E, int N)>();
        int blocks = 0;
        var (ce, cn) = _origin.ToLv95(Camera());
        foreach (var child in horizon.GetChildren())
        {
            // every block, also one Godot renamed past a sibling of the same name (@Horizon_E_N@2), which this used to skip
            if (child is not MeshInstance3D block) continue;
            var parts = block.Name.ToString().Split('_');
            if (parts.Length != 3 || parts[0] != "Horizon" || !int.TryParse(parts[1], out int be) || !int.TryParse(parts[2], out int bn))
            {
                _faults.Add($"{when}: unexpected block name {block.Name}");
                continue;
            }
            blocks++;
            if (!seen.Add((be, bn))) _faults.Add($"{when}: block {be}/{bn} twice");
            var want = _origin.ToWorld(be * ChunkFormat.TileSizeM, (bn + TerrainMeshBuilder.HorizonBlockTiles) * ChunkFormat.TileSizeM, 0);
            if (block.Position.DistanceTo(want) > 0.001f)
                _faults.Add($"{when}: block {be}/{bn} at {block.Position}, its corner is {want}");
            if (!block.Transform.Basis.IsEqualApprox(Basis.Identity))
                _faults.Add($"{when}: block {be}/{bn} turned or scaled: {block.Transform.Basis}");
            double de = Math.Max(0, Math.Max(be * ChunkFormat.TileSizeM - ce, ce - (be * ChunkFormat.TileSizeM + BlockM)));
            double dn = Math.Max(0, Math.Max(bn * ChunkFormat.TileSizeM - cn, cn - (bn * ChunkFormat.TileSizeM + BlockM)));
            if (Math.Max(de, dn) > horizon.DistanceM + 10_000 + BlockM)
                _faults.Add($"{when}: block {be}/{bn} is {Math.Max(de, dn) / 1000:F1} km out, past the unload slack");
        }
        if (horizon.Transform != Transform3D.Identity)
            _faults.Add($"{when}: the horizon container left the identity: {horizon.Transform}");
        GD.Print($"[horizoncheck] {when}: {blocks} blocks drawn, {_faults.Count} faults so far");
        if (blocks == 0) _faults.Add($"{when}: no block drawn");
    }

    private Vector3 Camera() => GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
}
