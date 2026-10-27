using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's projection booth (#1238): the house lights (full, dim, off) and the screen (cat photos, on or
/// off), worked at the booth's control panel (<see cref="FurnitureType.BoothPanel"/>) and the same for
/// everyone. At <c>World/CinemaBooths</c> on the server and on every client, as <see cref="ChurchRadios"/>
/// is: a player inside asks, the server checks they are in that cinema and broadcasts, a joining client
/// gets the table. State per cinema (plan key), for all its halls: one projectionist runs them all.
///
/// <para>
/// The panel's left half is the lights (three keys), its right half the screen's switch: E at the half
/// you stand at, the pad's interact button, or a VR grip reaching out to it (<c>TryInteract(byHand)</c>).
/// Bots: <see cref="Serve"/> on the server, with the bot's peer id, after the same check.
/// </para>
/// <para>
/// Client: what is applied goes on the cinema the player is in, on change only: the interior material's
/// <c>house_lights</c> (tint-2 lamps, <see cref="RoomLights.Tint"/>), the cat slides on every screen and the
/// projector's beam to it (built hidden with the interior: <see cref="InteriorNode"/>'s screens); at High
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

    /// <summary>Server and client: each cinema's state, packed (<see cref="Pack"/>); a cinema nobody touched is absent.</summary>
    private readonly Dictionary<string, int> _states = new();

    private string _appliedPlan = "";
    private int _applied = -1;
    private InteriorNode? _appliedNode;
    private int _slide = -1;
    private bool _real;

    public static CinemaBooths Create(Node world)
    {
        var node = new CinemaBooths { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    public static int Pack(Lights lights, bool screen) => (int)lights | (screen ? 4 : 0);
    public static (Lights Lights, bool Screen) Unpack(int code) => ((Lights)Math.Clamp(code & 3, 0, 2), (code & 4) != 0);

    /// <summary>A cinema's lights and screen: full and off until someone works the panel.</summary>
    public (Lights Lights, bool Screen) StateOf(string plan) => Unpack(_states.GetValueOrDefault(plan));

    // ---- the player at the panel -----------------------------------------------------------------

    /// <summary>Which control of a cinema's panel <paramref name="p"/> stands at: the plan key and whether it is the screen's half.</summary>
    public static (string Plan, bool Screen)? At(FootPlayer p)
        => PanelAt(p) is { } at ? (at.Node.Layout.Key, at.Screen) : null;

    /// <summary>The prompt at the panel, or null: what E would do there.</summary>
    public static string? Prompt(FootPlayer p)
    {
        if (At(p) is not { } at || Instance is not { } booths) return null;
        var (lights, screen) = booths.StateOf(at.Plan);
        return at.Screen
            ? screen ? "Screen off" : "Screen on"
            : lights switch { Lights.Full => "Dim the lights", Lights.Dim => "Lights off", _ => "Lights up" };
    }

    /// <summary>E (or the pad's interact, or a VR grip) at the panel: the next light level, or the screen on or off.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (At(p) is not { } at || Instance is not { } booths) return false;
        var (lights, screen) = booths.StateOf(at.Plan);
        int next = at.Screen ? Pack(lights, !screen) : Pack((Lights)(((int)lights + 1) % 3), screen);
        booths.Ask(at.Plan, next);
        return true;
    }

    // ---- client: asking --------------------------------------------------------------------------

    public void Ask(string plan, int code)
    {
        if (Online) RpcId(1, MethodName.AskSet, plan, code);
        else Serve(MyId, plan, code);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskSet(string plan, int code)
    {
        if (Multiplayer.IsServer()) Serve(Multiplayer.GetRemoteSenderId(), plan, code);
    }

    // ---- server ----------------------------------------------------------------------------------

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> sets cinema <paramref name="plan"/>'s lights and screen.
    /// Refused unless they are inside it. What a bot calls, with its own peer id.
    /// </summary>
    public bool Serve(long peer, string plan, int code)
    {
        bool inside = plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan : !Online);
        if (!inside) { GD.Print($"[cinema] peer {peer} is not in {plan}: refused"); return false; }
        code &= 7;
        if ((code & 3) > 2) return false;
        Set(plan, code);
        if (Online) Rpc(MethodName.Set, plan, code);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Set(string plan, int code)
    {
        if (code == 0) _states.Remove(plan);
        else _states[plan] = code;
    }

    /// <summary>Server: a newly joined client gets every cinema's lights and screen.</summary>
    public void SendTo(long peer)
    {
        foreach (var (plan, code) in _states) RpcId(peer, MethodName.Set, plan, code);
    }

    // ---- client: showing it ----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (NetworkManager.DedicatedServer) return;
        var im = InteriorManager.Instance;
        if (im?.CurrentNode is not { } node || node.Layout.Type != BuildingType.Cinema) { _appliedNode = null; return; }
        // --cinemashow: every cinema dimmed with the cat on, for a shot (no panel to press in one)
        int code = ShowAll ? Pack(Lights.Dim, true) : _states.GetValueOrDefault(node.Layout.Key);
        // the slide on the screens, from the shared clock: a texture swap every few seconds, on change only
        if (Unpack(code).Screen)
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
        if (node != _appliedNode || code != _applied || node.Layout.Key != _appliedPlan || real != _real)
        {
            _appliedNode = node;
            _applied = code;
            _appliedPlan = node.Layout.Key;
            _real = real;
            var (lights, screen) = Unpack(code);
            node.ShowCinema(Level(lights), screen, real);
        }
        if (real && GetViewport().GetCamera3D() is { } cam) node.ProjectFor(node.ToLocal(cam.GlobalPosition));
    }
}
