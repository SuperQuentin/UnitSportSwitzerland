using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's projection booth (#1238): each hall's house lights (full, dim, off) and screen (cat photos, on
/// or off), worked at that hall's control panel beside its projector (<see cref="FurnitureType.BoothPanel"/>,
/// one per hall since #1279) and the same for everyone. At <c>World/CinemaBooths</c> on the server and on every
/// client, as <see cref="ChurchRadios"/> is: a player inside asks, the server checks they are in that cinema
/// and broadcasts, a joining client gets the table. State per hall: the plan key and the hall's number
/// (<see cref="HallAt"/>).
///
/// <para>
/// The panel's left half is the lights (three keys), its right half the screen's switch: E at the half
/// you stand at, the pad's interact button, or a VR grip reaching out to it (<c>TryInteract(byHand)</c>).
/// Bots: <see cref="Serve"/> on the server, with the bot's peer id, after the same check.
/// </para>
/// <para>
/// Client: what is applied goes on the cinema the player is in, on change only: the interior material's
/// <c>house_lights</c> (tint-2 lamps, <see cref="RoomLights.Tint"/>; one value for the whole interior, so the
/// level of the hall the camera is in or nearest), each hall's cat slides on its screen and the projector's beam to it (built hidden with the interior: <see cref="InteriorNode"/>'s screens); at High
/// detail the picture thrown by a light in the hall the camera is in (<see cref="InteriorNode.RealProjection"/>).
/// </para>
/// </summary>
public partial class CinemaBooths : Node
{
    public const string NodeName = "CinemaBooths";

    /// <summary>How close to the control panel to work it, m.</summary>
    public const float Reach = 1.6f;

    public enum Lights : byte { Full = 0, Dim = 1, Off = 2 }

    /// <summary>The house lights' brightness at each level [S: 150 lux half, 3-5 lux dimmed, 0 off, against a full house].</summary>
    public static float Level(Lights l) => l switch { Lights.Dim => 0.3f, Lights.Off => 0f, _ => 1f };

    public static CinemaBooths? Instance { get; private set; }

    private static readonly bool ShowAll = Core.CmdArgs.Has("--cinemashow");

    /// <summary>The most halls a cinema has (the generator's MaxHalls).</summary>
    public const int MaxHalls = 10;

    /// <summary>
    /// Server and client: each cinema's halls' states, packed (<see cref="Pack"/>), from their <see cref="Default"/>s
    /// on first need; what players set is written over them and sent. A cinema nobody looked at is absent.
    /// </summary>
    private readonly Dictionary<string, int[]> _states = new();

    /// <summary>The share of halls already showing a film when a cinema is first seen (#1279): lights off, the cat on.</summary>
    public const double ShowingShare = 0.4;

    /// <summary>
    /// A hall's state before anyone works its panel (#1279): some halls are mid-film, lights off and the screen on,
    /// the rest lit and dark. From the hall's own seed, so every peer starts the same with nothing sent.
    /// </summary>
    public static int Default(string plan, int hall) =>
        Core.Fnv.Unit($"{plan}#{hall}#showing") < ShowingShare ? Pack(Lights.Off, true) : Pack(Lights.Full, false);

    /// <summary>A cinema's halls' states, its defaults filled in the first time (once per cinema: no string per frame).</summary>
    private int[] HallsOf(string plan)
    {
        if (_states.TryGetValue(plan, out var halls)) return halls;
        halls = new int[MaxHalls];
        for (int h = 0; h < MaxHalls; h++) halls[h] = Default(plan, h);
        _states[plan] = halls;
        return halls;
    }

    private string _appliedPlan = "";
    private int _applied = -1, _appliedMask = -1;
    private long _appliedReels = -1;
    private int _appliedHere = -2;
    private InteriorNode? _appliedNode;
    private int _slide = -1;
    private bool _real;

    public static CinemaBooths Create(Node world)
    {
        var node = new CinemaBooths { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        CinemaVideo.AllowLocal = LocalFiles;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _stream?.Dispose();
        _stream = null;
        foreach (var tap in _taps.Values) tap.Dispose();
        _taps.Clear();
    }

    private bool Online => NetLink.Online(this);

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    public static int Pack(Lights lights, bool screen) => (int)lights | (screen ? 4 : 0);

    /// <summary>
    /// A hall's showing number (#1279), bits 3-6 of its code: the server counts it up each time the hall's screen comes
    /// on, and the hall's music draws again from it, so a restarted film gets another tune, the same on every peer.
    /// </summary>
    public static int ReelOf(int code) => (code >> 3) & 15;
    public static (Lights Lights, bool Screen) Unpack(int code) => ((Lights)Math.Clamp(code & 3, 0, 2), (code & 4) != 0);

    /// <summary>A hall's lights and screen: full and off until someone works its panel.</summary>
    public (Lights Lights, bool Screen) StateOf(string plan, int hall) =>
        Unpack(hall >= 0 && hall < MaxHalls && plan.Length > 0 ? HallsOf(plan)[hall] : 0);

    /// <summary>
    /// The hall a point of a cinema's plan belongs to: the auditorium whose middle is nearest, numbered as the
    /// ground floor lists them (-1 for none). A panel or a projector by a hall's back wall is that hall's: the
    /// next hall's middle is a hall's width further off.
    /// </summary>
    public static int HallAt(InteriorLayout l, float x, float z)
    {
        if (l.Floors.Count == 0) return -1;
        int best = -1, k = 0;
        float bestD = float.MaxValue;
        foreach (var r in l.Floors[0].Rooms)
        {
            if (r.Type != RoomType.Auditorium) continue;
            float dx = (r.X0 + r.X1) / 2 - x, dz = (r.Z0 + r.Z1) / 2 - z, d = dx * dx + dz * dz;
            if (d < bestD) (best, bestD) = (k, d);
            k++;
        }
        return best;
    }

    // ---- the player at the panel -----------------------------------------------------------------

    /// <summary>Which control of which hall's panel <paramref name="p"/> stands at: the plan key, the hall (#1279), and whether it is the screen's half.</summary>
    public static (string Plan, int Hall, bool Screen)? At(FootPlayer p)
    {
        if (PanelAt(p) is not { } at) return null;
        var panel = at.Node.Layout.Furniture[at.Panel];
        int hall = HallAt(at.Node.Layout, panel.X, panel.Z);
        return hall < 0 ? null : (at.Node.Layout.Key, hall, at.Screen);
    }

    /// <summary>The prompt at the panel, or null: what E would do there.</summary>
    public static string? Prompt(FootPlayer p)
    {
        if (At(p) is not { } at || Instance is not { } booths) return null;
        var (lights, screen) = booths.StateOf(at.Plan, at.Hall);
        return at.Screen
            ? "Projector: screen, slides or a video"
            : lights switch { Lights.Full => "Dim the lights", Lights.Dim => "Lights off", _ => "Lights up" };
    }

    /// <summary>E (or the pad's interact, or a VR grip) at the panel: the next light level, or the screen on or off.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (At(p) is not { } at || Instance is not { } booths) return false;
        // the screen's half opens the hall's projector panel (#1298): off, the slides, or a video
        if (at.Screen) { CinemaPanelUi.Open(p, at.Plan, at.Hall); return true; }
        var (lights, screen) = booths.StateOf(at.Plan, at.Hall);
        booths.Ask(at.Plan, at.Hall, Pack((Lights)(((int)lights + 1) % 3), screen));
        return true;
    }

    // ---- client: asking --------------------------------------------------------------------------

    public void Ask(string plan, int hall, int code)
    {
        if (Online) RpcId(1, MethodName.AskSet, plan, hall, code);
        else Serve(MyId, plan, hall, code);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskSet(string plan, int hall, int code)
    {
        if (Multiplayer.IsServer()) Serve(Multiplayer.GetRemoteSenderId(), plan, hall, code);
    }

    // ---- server ----------------------------------------------------------------------------------

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> sets hall <paramref name="hall"/> of cinema <paramref name="plan"/>'s
    /// lights and screen. Refused unless they are inside it. What a bot calls, with its own peer id.
    /// </summary>
    public bool Serve(long peer, string plan, int hall, int code)
    {
        bool inside = plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan : !Online);
        if (!inside) { GD.Print($"[cinema] peer {peer} is not in {plan}: refused"); return false; }
        code &= 7;
        if ((code & 3) > 2 || hall < 0 || hall >= MaxHalls) return false;
        // the showing number counts up as the screen comes on, and stays with it
        int was = HallsOf(plan)[hall];
        int reel = ReelOf(was);
        if (Unpack(code).Screen && !Unpack(was).Screen) reel = (reel + 1) & 15;
        code |= reel << 3;
        Set(plan, hall, code);
        if (Online) Rpc(MethodName.Set, plan, hall, code);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Set(string plan, int hall, int code)
    {
        if (hall < 0 || hall >= MaxHalls) return;
        HallsOf(plan)[hall] = code;
    }

    /// <summary>Server: a newly joined client gets every hall's lights and screen.</summary>
    public void SendTo(long peer)
    {
        foreach (var (plan, halls) in _states)
            for (int h = 0; h < halls.Length; h++)
                if (halls[h] != Default(plan, h)) RpcId(peer, MethodName.Set, plan, h, halls[h]);
        SendVideosTo(peer);
    }

    // ---- client: showing it ----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // the server's copy of each hall someone watches through it (#1298 phase 2)
        if (ServerSide) PumpRelay(delta);
        if (NetworkManager.DedicatedServer) return;
        var im = InteriorManager.Instance;
        if (im?.CurrentNode is not { } node || node.Layout.Type != BuildingType.Cinema) { _appliedNode = null; PumpVideo(null, -1, 0); return; }
        // --cinemashow: every cinema dimmed with the cat on, for a shot (no panel to press in one)
        var halls = ShowAll ? null : HallsOf(node.Layout.Key);
        // the halls whose screens are on, a bit each; the house lights of the hall the camera is in or nearest
        int mask = 0;
        long reels = 0;
        for (int h = 0; h < MaxHalls; h++)
        {
            if (ShowAll || halls != null && Unpack(halls[h]).Screen) mask |= 1 << h;
            if (halls != null) reels |= (long)ReelOf(halls[h]) << (h * 4);
        }
        var cam = GetViewport().GetCamera3D();
        int here = cam != null ? node.HallNear(node.ToLocal(cam.GlobalPosition)) : 0;
        int code = ShowAll ? Pack(Lights.Dim, true) : halls != null && here >= 0 ? halls[here] : 0;
        // the slide on the screens, from the shared clock: a texture swap every few seconds, on change only
        if (mask != 0)
        {
            int slide = InteriorNode.SlideAt(ClockSync.ServerNow);
            if (slide != _slide)
            {
                InteriorNode.ShowSlide(_slide = slide);
                if (node == _appliedNode) node.ShowGlow(true);
            }
        }
        // High detail throws the picture with a light in the hall the camera is in (InteriorNode's screens)
        bool real = InteriorNode.RealProjection;
        if (node != _appliedNode || code != _applied || mask != _appliedMask || reels != _appliedReels || here != _appliedHere
            || node.Layout.Key != _appliedPlan || real != _real)
        {
            _applied = code;
            _appliedMask = mask;
            _appliedReels = reels;
            // the same hall's lights switched fade; walking into another hall shows its level at once
            bool snap = node != _appliedNode || here != _appliedHere;
            _appliedHere = here;
            _appliedNode = node;
            _appliedPlan = node.Layout.Key;
            _real = real;
            var (lights, screen) = Unpack(code);
            node.ShowCinema(Level(lights), mask, halls, screen, real, snap);
        }
        if (real && cam != null) node.ProjectFor(node.ToLocal(cam.GlobalPosition));
        // the video of the hall the camera is in (#1298), decoded here; every hall playing one keeps its film music off
        int shown = PumpVideo(node, here, mask);
        int muted = shown;
        if (halls != null)
            for (int h = 0; h < MaxHalls; h++)
                if ((mask >> h & 1) != 0 && h != here && VideoOf(node.Layout.Key, h).Link.Length > 0) muted |= 1 << h;
        node.MuteFilmMusic(muted);
        // the seats let down under whoever sits, the house lights fading to their level (#1279)
        node.UpdateSeats();
        node.FadeHouse(delta);
    }
}
