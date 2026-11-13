using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// <c>godot --headless --path . -- --spacebandcheck --at E,N --systems terrain,generated --horizon 60</c> (#1480): the band
/// where flat tiles are still drawn in the Space regime (60 to 250 km, #1430 H4 dissolves them). The camera
/// climbs from 55 to 265 km at 1 km/s while it flies 1.5 km/s east and comes back down, so the origin
/// re-anchors every ~13 s as tiles and horizon blocks stream in. In the band the frame must stay flat, and
/// every tile and horizon block must lie where the frame's own tile transform puts it
/// (<see cref="OriginFrame.TileTransform"/>), the ones shifts moved and the ones streamed after alike, to
/// 0.5 m and 10⁻⁴ rad. The frame may turn only above 250 km (<see cref="Curvature.TurnEnterM"/>), and is a
/// flat ground frame again below 50 km. RESULT ok or FAILED.
/// Under <c>--frame earth</c> (#1739) the band is not there (flat tiles in a Space frame, the flat world's alone; the earth
/// world's far view at these heights is the globe: <c>--orbitcheck</c>, <c>--longflightcheck</c>). The same climb then
/// holds the earth side of it: no flat tile or horizon block is drawn at any height, and the frame is the Earth's
/// anchor frame all the way up and down.
/// </summary>
public partial class SpaceBandCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--spacebandcheck");

    private const double StartAlt = 55_000, TopAlt = 265_000, Climb = 1_000, East = 1_500, TimeoutS = 900;
    private const float Slack = 0.5f, Turn = 1e-4f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private readonly HashSet<string> _seen = new();
    private double _t, _flight, _e0, _n0;
    private bool _wasTurned;
    private readonly bool _earth = GameSettings.Current.Frame == WorldFrame.Earth;
    private int _stage, _turns, _shifts, _checked, _late, _tilesSeen, _blocksSeen, _turnedFrames;

    public SpaceBandCheck(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        _origin.Shifted += s =>
        {
            if (OriginShifter.Instance is { InSpace: true, Turned: false }) _shifts++;
            if (!s.Turns) return;
            _turns++;
            // the shift that stops the turning turns back to the grid: the frame was turning a moment ago
            if (OriginShifter.Instance is not { Turned: true } && !_wasTurned) _faults.Add($"a turned shift with the frame not turning (shift {_turns})");
        };
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > TimeoutS) { Finish($"FAILED timeout after {TimeoutS} s at stage {_stage}"); return; }
        if (GetViewport()?.GetCamera3D() is not { } camera) return;
        switch (_stage)
        {
            case 0:
                // the tiles round the start first, so there is a flat world to turn
                if (_t < 5) return;
                (_e0, _n0) = _origin.ToLv95(camera.GlobalPosition);
                _stage = 1;
                return;
            case 1:
            {
                // up through the band and over it, then down again: the path in LV95, placed through the frame
                _flight += delta;
                double up = Math.Min(_flight * Climb, 2 * (TopAlt - StartAlt) - _flight * Climb);
                double alt = StartAlt + up;
                var at = SwissGeodesy.ToEcef(_e0 + East * _flight, _n0, alt);
                camera.GlobalPosition = _origin.Frame.FromEcef(at);
                if (_earth)
                {
                    VerifyEarth(alt);
                    if (alt < StartAlt - 10_000) _stage = 2;
                    return;
                }
                _wasTurned = OriginShifter.Instance?.Turned == true;
                if (OriginShifter.Instance is { InSpace: true } shifter)
                {
                    if (shifter.Turned && alt < Curvature.TurnExitM - 1_000) _faults.Add($"turned at {alt / 1000:F1} km, among the flat tiles");
                    if (!shifter.Turned) Verify();
                    else _turnedFrames++;
                }
                if (alt < StartAlt - 10_000) _stage = 2;
                return;
            }
            default:
            {
                if (_earth)
                {
                    foreach (var fault in _faults.Take(12)) GD.Print($"[spacebandcheck]   {fault}");
                    GD.Print($"[spacebandcheck] under --frame earth: {_checked} frames from {StartAlt / 1000:F0} to {TopAlt / 1000:F0} km and back, "
                        + $"{_shifts} shifts; the band of flat tiles is the flat world's (its earth twins: --orbitcheck, --longflightcheck)");
                    if (_checked == 0) _faults.Add("no frame climbed");
                    Finish(_faults.Count == 0 ? "ok" : $"FAILED {_faults.Count} faults");
                    return;
                }
                var f = _origin.Frame;
                if (f.IsAnchor || f.Alt0 != 0) _faults.Add($"down again: not a flat ground frame (anchor {f.IsAnchor}, alt0 {f.Alt0})");
                foreach (var fault in _faults.Take(12)) GD.Print($"[spacebandcheck]   {fault}");
                GD.Print(FormattableString.Invariant(
                    $"[spacebandcheck] {_checked} frames checked in the band, {_shifts} shifts there, {_tilesSeen} tiles and {_blocksSeen} horizon blocks seen, {_late} first drawn after a shift there; {_turns} turned shifts over {_turnedFrames} frames above 250 km"));
                if (_turns < 1 || _turnedFrames == 0) _faults.Add($"the frame never turned over 250 km ({_turns} turned shifts)");
                if (_late == 0) _faults.Add("nothing was streamed in while the frame followed in the band");
                if (_blocksSeen == 0) _faults.Add("no horizon block drawn (--horizon <km>)");
                Finish(_faults.Count == 0 ? "ok" : $"FAILED {_faults.Count} faults");
                return;
            }
        }
    }

    /// <summary>The earth world in the band (#1739): no flat tile or horizon block drawn, the frame the Earth's anchor.</summary>
    private void VerifyEarth(double alt)
    {
        _checked++;
        if (_faults.Count >= 200) return;
        foreach (var child in _chunks.GetChildren())
            if (child is ChunkNode node)
            {
                _faults.Add($"a flat tile {node.Name} drawn at {alt / 1000:F1} km under --frame earth");
                break;
            }
        if (_chunks.Horizon is { } horizon && horizon.GetChildCount() > 0) _faults.Add($"flat horizon blocks drawn at {alt / 1000:F1} km under --frame earth");
        if (_origin.Frame.Axes == null) _faults.Add($"a flat ground frame at {alt / 1000:F1} km under --frame earth");
    }

    /// <summary>Every tile and horizon block against the frame's transform for its tile.</summary>
    private void Verify()
    {
        _checked++;
        foreach (var child in _chunks.GetChildren())
            if (child is ChunkNode node && ParseId(node.Name, "Chunk_") is { } id)
                Check(node, new TileId(id.E, id.N), "tile", ref _tilesSeen);
        if (_chunks.Horizon is { } horizon)
            foreach (var child in horizon.GetChildren())
                if (child is MeshInstance3D block && ParseId(block.Name, "Horizon_") is { } key)
                    Check(block, new TileId(key.E, key.N + TerrainMeshBuilder.HorizonBlockTiles - 1), "block", ref _blocksSeen);
    }

    private void Check(Node3D node, TileId id, string what, ref int seen)
    {
        if (_seen.Add(node.Name))
        {
            seen++;
            if (_shifts > 0) _late++;
        }
        var want = _origin.TileTransform(id);
        var have = node.Transform;
        float off = have.Origin.DistanceTo(want.Origin);
        float turn = Mathf.Max((have.Basis.X - want.Basis.X).Length(), (have.Basis.Z - want.Basis.Z).Length());
        if ((off > Slack || turn > Turn) && _faults.Count < 200)
            _faults.Add(FormattableString.Invariant($"{what} {node.Name} after {_shifts} shifts in the band: {off:F2} m and {turn:E1} off its frame"));
    }

    private static (int E, int N)? ParseId(StringName name, string prefix)
    {
        var s = name.ToString();
        if (!s.StartsWith(prefix)) return null;
        var parts = s[prefix.Length..].Split('_');
        return parts.Length == 2 && int.TryParse(parts[0], out int e) && int.TryParse(parts[1], out int n) ? (e, n) : null;
    }

    private void Finish(string result)
    {
        GD.Print($"[spacebandcheck] RESULT: {result}");
        SetProcess(false);
        GetTree().Quit(result == "ok" ? 0 : 1);
    }
}
