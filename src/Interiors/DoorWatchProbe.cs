using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --connect 127.0.0.1 --doorwatch[,out.png]</c>: the other side of an online
/// <c>--interiorcheck</c>, run by a second client on the same server.
///
/// <para>
/// Stands 8 m in front of the house door that check will use (the door nearest the spawn), just
/// outside the distance that keeps a door open, and watches the other player go through it. It
/// must see, from the server's tables alone: the door open; the interior behind it built here and
/// shown through the doorway; the other player visible while inside that house with its door open
/// (and hidden whenever they are in a building with every door shut); them back outside; the door
/// shut again. Screenshots of the open door and of the other player through it. Ends when the
/// other client leaves; non-zero exit on any failure.
/// </para>
/// </summary>
public partial class DoorWatchProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private FootPlayer? _player;
    private DoorIndex.Entry? _door;
    private string _key = "";
    private double _t, _seen;
    private bool _sawOpen, _sawPortal, _sawInside, _sawOutsideAgain, _sawShut, _hiddenOk = true, _sawHidden;
    private bool _shotOpen, _shotRemote;
    private int _remoteSeen;

    public DoorWatchProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--doorwatch"))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > 400) { Finish("timed out"); return; }
        var interiors = InteriorManager.Instance;
        if (interiors == null) return;

        if (_player == null)
        {
            _player = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p.IsMultiplayerAuthority());
            if (_player == null) return;
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            if (!_chunks.TryGetHeight(at, out float g)) { _player = null; return; }
            _player.Camera.Current = true;
            _player.LeaveInterior(new Vector3(at.X, g + 1f, at.Z), 0);
            var me = _player;
            interiors.LocalPlayer = () => me;
            return;
        }
        if (_door == null)
        {
            if (!_player.IsOnFloor()) return;
            // the same choice the check makes: the door nearest the spawn
            _door = DoorIndex.Nearest(_player.GlobalPosition, 400f);
            if (_door is not { } d) return;
            _key = d.Key.ToString();
            var stand = d.World + d.Outward * 8f;
            if (_chunks.TryGetHeight(stand, out float g)) stand.Y = g + 1f;
            var face = -d.Outward;
            _player.LeaveInterior(stand, Mathf.Atan2(-face.X, -face.Z));
            GD.Print($"[watch] as peer {Multiplayer.GetUniqueId()}, watching the door of {_key} from 8 m");
            return;
        }

        var remote = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
            .FirstOrDefault(p => !p.IsMultiplayerAuthority() && p.GetMultiplayerAuthority() != 1);
        if (remote != null) _remoteSeen++;
        else if (_remoteSeen > 0) { Finish(null); return; } // the check is done and gone

        bool open = interiors.IsOpen(_key);
        interiors.Links.TryGetValue(_key, out var link);
        string plan = link?.Plan ?? "";
        if (open && !_sawOpen) { _sawOpen = true; GD.Print($"[watch] the door opened ({_t:F1} s)"); }
        if (open && (_log -= delta) <= 0)
        {
            _log = 1;
            var cam = GetViewport().GetCamera3D();
            GD.Print($"[watch]   link {(link != null ? $"swing {link.Swing:F2}" : "none")}, portal on "
                + $"{interiors.Portals?.Active?.Door ?? "nothing"}, camera {cam?.Name} at {cam?.GlobalPosition:F0}");
        }
        if (_sawOpen && link is { Swing: >= 1f } && interiors.Portals?.Active == link && !_sawPortal)
        {
            _sawPortal = true;
            GD.Print("[watch] the interior is built here and shown through the doorway");
        }
        if (_sawPortal && !_shotOpen && link is { Swing: >= 1f }) { _shotOpen = true; Save("_watch_open"); }

        if (remote != null)
        {
            long peer = remote.GetMultiplayerAuthority();
            string space = interiors.SpaceOf(peer);
            if (space.Length > 0 && space == plan && open)
            {
                if (!_sawInside) GD.Print($"[watch] the other player is inside {space}, door open: visible {remote.Visible}");
                _sawInside |= remote.Visible;
                _seen += delta;
                if (!_shotRemote && _seen > 0.3 && remote.Visible && interiors.Portals?.Active == link)
                {
                    _shotRemote = true;
                    Save("_watch_remote");
                }
            }
            // in a building with every door shut: out of sight, and not sent to us
            if (space.Length > 0 && !interiors.Linked(space, ""))
            {
                if (!_sawHidden) GD.Print($"[watch] the other player is in {space} with its doors shut: visible {remote.Visible}");
                _sawHidden = true;
                // a frame of grace for the table and the flag to meet
                if (remote.Visible && _t - _lastShut > 0.5) _hiddenOk = false;
            }
            else _lastShut = _t;
            if (_sawInside && space.Length == 0 && remote.Visible && !_sawOutsideAgain)
            {
                _sawOutsideAgain = true;
                GD.Print("[watch] the other player is back outside, visible");
            }
        }
        if (_sawOutsideAgain && !open && !_sawShut)
        {
            _sawShut = true;
            GD.Print($"[watch] the door shut again ({_t:F1} s)");
        }
    }

    private double _lastShut, _log;

    private void Save(string suffix)
    {
        if (_shot == null) return;
        var path = _shot.Replace(".png", suffix + ".png");
        if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) GD.Print($"[watch] wrote {path}");
    }

    private void Finish(string? failure)
    {
        bool ok = failure == null;
        void Check(bool c, string what) { GD.Print($"[watch] {(c ? "ok  " : "FAIL")} {what}"); ok &= c; }
        if (failure != null) Check(false, failure);
        Check(_sawOpen, "saw the door open, from the server's table");
        Check(_sawPortal, "built the interior and showed it through the doorway");
        Check(_sawInside, "saw the other player inside while the door was open");
        Check(_sawOutsideAgain, "saw them back outside");
        Check(_sawShut, "saw the door shut again");
        if (_sawHidden) Check(_hiddenOk, "the other player was hidden while every door of their building was shut");
        else GD.Print("[watch] (never saw them in a building with every door shut)");
        GD.Print(ok ? "[watch] RESULT: ok" : "[watch] RESULT: FAILED");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
