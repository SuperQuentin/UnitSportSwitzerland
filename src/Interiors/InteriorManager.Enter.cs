using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--enter &lt;venue name | building key&gt;</c> (#1244): starts the session inside a building, no door needed. The
/// target is a venue's name (or part of it, any case and accents: <c>--enter bowlingland</c>), its OSM id, or a
/// building key (<c>2510_1140_26</c>). The world loads round it (it stands for <c>--at</c> and <c>--origin</c> when
/// they are not given); once spawned, its plan is loaded or generated and its interior built, and the local player
/// (or, in a shot run, the free camera: the queue's <c>l</c> lines then work at once) is put just inside its main
/// entrance, facing in. Offline only: online, the server owns who is where.
/// </summary>
public partial class InteriorManager
{
    /// <summary>The <c>--enter</c> target, or null.</summary>
    public static readonly string? EnterTarget = CmdArgs.Value("--enter");

    /// <summary>The plan <c>--enter</c> put us in, for the shot queue's <c>l</c> lines; null until then.</summary>
    public string? Entered { get; private set; }

    private bool _entering;

    /// <summary>An <see cref="Enter"/> on its way in: the shot queue's <c>l</c> lines wait for it.</summary>
    public bool Entering => _entering;

    /// <summary>Where <c>--enter</c>'s building is, LV95, to load the world round it; null without one or if not found.</summary>
    public static (double E, double N)? EnterLv95()
    {
        if (EnterTarget is not { } t) return null;
        if (BuildingKey.TryParse(t, out var key))
        {
            var tile = key.Tile;
            return (tile.MinE + 500, tile.MaxN - 500);
        }
        return FindVenue(t) is { } v ? (Venues.At(v).E, Venues.At(v).N) : null;
    }

    /// <summary>The venue whose OSM id is <paramref name="text"/> or whose name holds it (whole name first, then the biggest kind).</summary>
    public static int? FindVenue(string text)
    {
        string want = Fold(text);
        int best = -1, bestScore = int.MinValue;
        var list = Venues.List;
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v.Osm == text) return i;
            string name = Fold(Venues.NameOf(v));
            if (!name.Contains(want)) continue;
            int score = (name == want ? 1000 : 0) - Venues.Rank(v.Type);
            if (score > bestScore) (best, bestScore) = (i, score);
        }
        return best < 0 ? null : best;
    }

    /// <summary>Lower case, accents off, letters and digits only.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Goes into <paramref name="target"/>'s building (see the class's summary): the player <paramref name="player"/>, or
    /// the free camera when it is null. Returns at once; logs <c>[enter]</c> as it goes, and why if it cannot.
    /// </summary>
    public async void Enter(string target, FootPlayer? player)
    {
        if (_entering || Online || Source == null || Origin == null) return;
        _entering = true;
        try
        {
            if (await BuildingOf(target) is not { } building)
            {
                GD.PrintErr($"[enter] FAILED: no building for \"{target}\" (a venue's name, its OSM id, or a building key)");
                return;
            }
            var layout = await LoadOrGenerate(Source, _storeDir, building.ToString());
            if (layout == null)
            {
                GD.PrintErr($"[enter] FAILED: building {building} has no interior");
                return;
            }
            _cache[layout.Key] = layout;
            _current = layout;
            BuildInterior(layout);
            // a big hall takes a while on a loaded machine (a whole bowling centre: seconds): a minute at most
            double until = RealClock.Now + 60;
            while (!_built.ContainsKey(layout.Key) && RealClock.Now < until)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_built.TryGetValue(layout.Key, out var node))
            {
                GD.PrintErr($"[enter] FAILED: the interior of {layout.Key} was not built");
                return;
            }
            // a frame for its collision (BuildInterior adds it a frame after the node)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var e = layout.AllEntrances()[0];
            var inward = new Vector3(e.InX, 0, e.InZ).Normalized();
            var local = new Vector3(e.X, layout.EntranceY(e) + 0.05f, e.Z) + inward * 1.5f;
            var at = node.ToGlobal(local);
            // facing in: a body looks down its −Z
            var facing = node.GlobalBasis * inward;
            float yaw = Mathf.Atan2(-facing.X, -facing.Z);
            Entered = layout.Key;
            if (player != null && IsInstanceValid(player))
            {
                player.CrossDoor(layout.Key, at, yaw - player.Rotation.Y, Basis.Identity);
                SetPlayerSpace(MyId, layout.Key);
            }
            else if (GetViewport().GetCamera3D() is { } cam)
            {
                cam.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), at + Vector3.Up * 1.6f);
                CameraInside(layout.Key);
            }
            Maintain();
            GD.Print($"[enter] inside {layout.Key} ({layout.Type}), plan point {local.X:F1},{local.Y:F1},{local.Z:F1}");
        }
        catch (Exception ex) { GD.PushError($"[enter] {target}: {ex}"); }
        finally { _entering = false; }
    }

    /// <summary>
    /// The body put down at <paramref name="at"/> with no door walked through (leaving the fly camera, #1244): in the
    /// space of the built interior holding that point, or out of any interior it was in. Offline only.
    /// </summary>
    public void SetBodySpace(FootPlayer player, Vector3 at)
    {
        if (Online) return;
        var node = InInteriorSpace(at) ? InteriorNode.Containing(_built.Values, at) : null;
        string? key = node?.Layout.Key;
        if (player.InteriorKey == key) return;
        player.CrossDoor(key, at, 0f, Basis.Identity);
        SetPlayerSpace(MyId, key ?? "");
        _current = node?.Layout;
        Maintain();
        GD.Print($"[interior] put down {(key != null ? $"inside {key}" : "outside")}");
    }

    /// <summary>
    /// A point 1 m over the lowest floor of the built interior whose footprint holds <paramref name="at"/> when
    /// <paramref name="at"/> is under it, in the void under the interiors (a fly camera through a floor); null otherwise.
    /// </summary>
    public Vector3? LowestFloorAbove(Vector3 at)
    {
        if (!InInteriorSpace(at)) return null;
        foreach (var node in _built.Values)
        {
            if (!node.IsInsideTree()) continue;
            var local = node.ToLocal(at);
            var l = node.Layout;
            if (Math.Abs(local.X) > l.Width / 2 || Math.Abs(local.Z) > l.Depth / 2 || local.Y > l.FloorY(0)) continue;
            return node.ToGlobal(local with { Y = l.FloorY(0) + 1f });
        }
        return null;
    }

    /// <summary>The building a target names: a building key as it is, or the building holding the venue.</summary>
    private async Task<BuildingKey?> BuildingOf(string target)
    {
        if (BuildingKey.TryParse(target, out var key)) return key;
        if (FindVenue(target) is not { } v) return null;
        var venue = Venues.At(v);
        var id = TileId.FromLv95(venue.E, venue.N);
        var tile = await Source!.LoadBuildingsAsync(id);
        if (tile == null) return null;
        foreach (var (venueIndex, building) in Venues.Contained(tile))
            if (venueIndex == v)
            {
                GD.Print($"[enter] {Venues.NameOf(venue)} ({venue.Type}) is building {id.E}_{id.N}_{building}");
                return new BuildingKey(id.E, id.N, building);
            }
        return null;
    }
}
