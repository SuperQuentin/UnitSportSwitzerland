using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Moves whatever the player is currently controlling to an LV95 position, or to a place on any body (#2039).
///
/// The thing to move is resolved at the moment of the jump, not when this node was built.
/// The earlier teleport captured the spectator camera once at startup, which is why it
/// silently did nothing in multiplayer: there you are an on-foot networked `FootPlayer`, so
/// the jump moved a camera that was not even current. Asking `ClientWorld` each time is what
/// makes the same Tab search work when flying, when walking, and when connected to a server.
/// </summary>
public sealed partial class Teleporter : Node
{
    /// <summary>Height above ground a flying camera arrives at, in metres.</summary>
    private const float FlyingArrivalHeight = 220f;

    /// <summary>Height above ground a walking body arrives at — just enough to land.</summary>
    private const float WalkingArrivalHeight = 2.0f;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;

    /// <summary>The world's teleporter, for the VR hand map (#439).</summary>
    public static Teleporter? Instance { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public Teleporter(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "Teleporter";
    }

    /// <summary>
    /// Supplies the node to move. Set by <see cref="ClientWorld"/> so it can hand back the
    /// spectator camera, the local on-foot player, or the networked player as appropriate.
    /// </summary>
    public Func<Node3D?> ActiveTarget { get; set; } = () => null;

    /// <summary>Raised after a jump is requested, for the chat log and the HUD.</summary>
    public event Action<string>? Teleported;

    /// <summary>
    /// Jumps to an LV95 easting/northing. The height is not known until the terrain under
    /// the destination has streamed in, so this arrives high and lets <see cref="SpawnPoint"/>
    /// settle it.
    /// </summary>
    /// <returns>False when there is nothing to move — no camera and no player yet.</returns>
    public bool TeleportTo(GlobalPos to, string? label = null)
    {
        // a place on another body, or the Earth from another body: the handover first (#2039)
        if (to.Body != BodyId.Earth || _origin.Body != BodyId.Earth)
        {
            var g = to.Geodetic;
            return TeleportTo(to.Body, g.LatDeg, g.LonDeg, label);
        }
        var l = to.Lv95;
        return TeleportTo(l.E, l.N, label);
    }

    /// <summary>The same, given in LV95 notation (a typed coordinate, a Swiss source).</summary>
    public bool TeleportTo(double lv95E, double lv95N, string? label = null)
    {
        // a Swiss place picked on the Moon (#2039): the anchor goes back to the Earth first, as /goto does
        if (_origin.Body != BodyId.Earth)
        {
            var (lat, lon) = SwissGeodesy.ToLatLon(lv95E, lv95N);
            return TeleportTo(BodyId.Earth, lat, lon, label ?? $"{lv95E:F0}/{lv95N:F0}", (0, 0));
        }
        var target = ActiveTarget();
        if (target is null)
        {
            GD.PushWarning("[teleport] nothing to move: no active camera or player");
            return false;
        }
        // the phone out (#889): the jump waits until its screen covers the view
        string where = label ?? $"{lv95E:F0}/{lv95N:F0}";
        if (Curtain?.Invoke(where, () => Jump(lv95E, lv95N, label)) == true) return true;
        return Jump(lv95E, lv95N, label);
    }

    /// <summary>
    /// Jumps to a place on any body by its latitude and east longitude (#2039): the place search's sites on the Moon and
    /// the planets, a Swiss town from another body. On the body the anchor is already on, and on the Earth from the
    /// Earth, it is the ordinary jump; otherwise the anchor is handed over first (<see cref="BodyHandover"/>, what
    /// <c>/goto</c> does) and the camera or body set down on the place's ground. <paramref name="land"/> is metres east
    /// and north of the place to arrive at (beside a lander, not on it). Refused (<see cref="Refused"/>, false) online,
    /// whose play on other bodies waits for C3b (#1975), on the flat frame, which has no other body, and for a walker
    /// onto a body with no ground (a giant, the Sun).
    /// </summary>
    public bool TeleportTo(BodyId body, double latDeg, double lonDeg, string? label = null, (double East, double North) land = default)
    {
        string where = label ?? System.FormattableString.Invariant($"{CelestialBody.Of(body).Name} {latDeg:F2}, {lonDeg:F2}");
        // the Earth from the Earth: the LV95 jump as ever
        if (body == BodyId.Earth && _origin.Body == BodyId.Earth && land == default)
        {
            var (e, n) = SwissGeodesy.ToLv95(latDeg, lonDeg);
            return TeleportTo(e, n, label);
        }
        var target = ActiveTarget();
        if (target is null)
        {
            GD.PushWarning("[teleport] nothing to move: no active camera or player");
            return false;
        }
        if (RefusalFor(body, target) is { } why)
        {
            GD.Print($"[teleport] refused: {why}");
            Refused?.Invoke(why);
            return false;
        }
        // the place moved by the landing offset, along its own east and north (a few tens of metres: flat enough)
        double r = CelestialBody.Of(body).MeanRadius;
        double lat = latDeg + land.North / r * (180 / Math.PI);
        double lon = lonDeg + land.East / (r * Math.Max(0.01, Math.Cos(latDeg * Math.PI / 180))) * (180 / Math.PI);
        if (Curtain?.Invoke(where, () => JumpBody(body, lat, lon, where)) == true) return true;
        return JumpBody(body, lat, lon, where);
    }

    /// <summary>Why a jump to <paramref name="body"/> cannot be made now, or null.</summary>
    public string? RefusalFor(BodyId body, Node3D? target)
    {
        if (body == _origin.Body && body != BodyId.Earth) return null;
        if (body != _origin.Body && Networked())
            return $"Travel to {CelestialBody.Of(body).Name} waits for online play on other bodies: offline only for now.";
        if (_origin.Frame.Axes == null) return "Other bodies need the earth world (--frame earth).";
        if (target is CharacterBody3D && !CelestialBody.Of(body).Landable)
            return $"{CelestialBody.Of(body).Name} has no ground to stand on: take the fly camera (Shift+T) to go there.";
        return null;
    }

    /// <summary>Whether this game is online (where travel between bodies waits for C3b); set by <see cref="ClientWorld"/>.</summary>
    public Func<bool> Networked { get; set; } = () => false;

    /// <summary>Whether the ground is still being made round an arrival (the earth world's leaves); set by <see cref="ClientWorld"/>.</summary>
    public Func<bool>? StillLoading { get; set; }

    /// <summary>The body the world is on now.</summary>
    public BodyId Body => _origin.Body;

    /// <summary>Where the camera or body a jump would move is now, or null.</summary>
    public GlobalPos? Here() => ActiveTarget() is { } t ? _origin.ToGlobal(t.GlobalPosition) : null;

    /// <summary>Raised with the reason when a jump is refused, for a toast.</summary>
    public event Action<string>? Refused;

    private bool JumpBody(BodyId body, double latDeg, double lonDeg, string where)
    {
        var target = ActiveTarget();
        if (target is null || OriginShifter.Instance is not { } shifter) return false;
        if (target is Player.FootPlayer { Indoors: true } inside)
            Interiors.InteriorManager.Instance?.Leave(inside);
        float arrival = target is CharacterBody3D ? WalkingArrivalHeight : FlyingArrivalHeight;
        foreach (var node in GetParent().GetChildren())
            if (node is SpawnPoint pending) pending.QueueFree();

        // the anchor onto the body at the place (nothing moves, then the world shifts to the place's own frame): the
        // place is world (0, 0, 0) on the body's reference surface, +Y its up
        if (!BodyHandover.To(_origin, shifter, body, BodyHandover.At(body, latDeg, lonDeg))) return false;
        // level after the turn of the frame: a camera looking along the new horizon, a body upright
        target.Rotation = new Vector3(0, target.Rotation.Y, 0);

        GetParent().AddChild(new SpawnPoint(target, _ground, Vector3.Zero, arrival) { StillLoading = StillLoading });
        GD.Print(System.FormattableString.Invariant($"[teleport] {target.Name} -> {where} ({body} {latDeg:F4}, {lonDeg:F4})"));
        Teleported?.Invoke(where);
        return true;
    }

    /// <summary>
    /// Something that can hide a jump (the phone's screen, #889): given where to and the jump, it
    /// returns true when it will run the jump itself once the view is covered, false to let it go now.
    /// </summary>
    public static Func<string, Action, bool>? Curtain { get; set; }

    /// <summary>Raised when a body or camera that was moved has been set down on streamed ground (or given up on).</summary>
    public static event Action? Arrived;

    internal static void RaiseArrived() => Arrived?.Invoke();

    private bool Jump(double lv95E, double lv95N, string? label)
    {
        var target = ActiveTarget();
        if (target is null) return false;

        // a teleport out of a house is a way out of it: drop the interior before moving
        if (target is Player.FootPlayer { Indoors: true } inside)
            Interiors.InteriorManager.Instance?.Leave(inside);

        // A body arrives at walking height; a free camera arrives high enough to see where
        // it landed. Dropping a CharacterBody3D from 220 m would be a long fall.
        float arrival = target is CharacterBody3D ? WalkingArrivalHeight : FlyingArrivalHeight;

        // Any spawn still settling would fight this one for the height.
        foreach (var node in GetParent().GetChildren())
            if (node is SpawnPoint pending) pending.QueueFree();

        // the origin goes first (#185), so the destination is placed near it rather than far from
        // the old one, and the tiles there are built in precise coordinates from the start
        OriginShifter.Instance?.ShiftTo(lv95E, lv95N);

        GetParent().AddChild(new SpawnPoint(target, _ground, _origin, lv95E, lv95N, arrival));

        string where = label ?? $"{lv95E:F0}/{lv95N:F0}";
        GD.Print($"[teleport] {target.Name} -> {where}");
        Teleported?.Invoke(where);
        return true;
    }
}
