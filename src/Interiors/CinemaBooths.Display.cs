using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's projector setting (#1298, the owner's): how a video is shown, its fit (contain, cover, stretch) and the ratio
/// of the frame it is fitted into (<see cref="CinemaVideo.Fit"/>, <see cref="CinemaVideo.Ratios"/>); contain at the screen's
/// own ratio until someone changes it. The same for everyone in the hall and kept across videos, as the lights are: a
/// peer inside the cinema asks (the projector panel, <c>/cinema fit</c>, <c>/cinema ratio</c>), the server broadcasts, a
/// joiner gets the table. Bots: <see cref="ServeDisplay"/> on the server.
/// </summary>
public partial class CinemaBooths
{
    /// <summary>Server and client: each cinema's halls' displays (<see cref="CinemaVideo.PackDisplay"/>), absent until one is set.</summary>
    private readonly Dictionary<string, int[]> _displays = new();

    /// <summary>A hall's display, packed: 0 (contain, the screen's ratio) until someone changes it.</summary>
    public int DisplayOf(string plan, int hall) =>
        hall >= 0 && hall < MaxHalls && _displays.TryGetValue(plan, out var d) ? d[hall] : 0;

    public void AskDisplay(string plan, int hall, int display)
    {
        if (Online) RpcId(1, MethodName.AskDisplayRpc, plan, hall, display);
        else ServeDisplay(MyId, plan, hall, display);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskDisplayRpc(string plan, int hall, int display)
    {
        if (Multiplayer.IsServer()) ServeDisplay(Multiplayer.GetRemoteSenderId(), plan, hall, display);
    }

    /// <summary>Server (and offline): <paramref name="peer"/> sets a hall's display; refused unless they are inside that cinema.</summary>
    public bool ServeDisplay(long peer, string plan, int hall, int display)
    {
        bool inside = Inside(peer, plan);
        if (!inside || hall < 0 || hall >= MaxHalls) { GD.Print($"[cinema] peer {peer} may not set {plan}'s projector: not inside"); return false; }
        var (fit, ratio) = CinemaVideo.UnpackDisplay(display);
        display = CinemaVideo.PackDisplay(fit, ratio);
        SetDisplay(plan, hall, display);
        if (Online) Rpc(MethodName.SetDisplay, plan, hall, display);
        GD.Print($"[cinema] hall {hall + 1} of {plan}: {fit}, {CinemaVideo.Ratios[ratio].Name} (peer {peer})");
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetDisplay(string plan, int hall, int display)
    {
        if (hall < 0 || hall >= MaxHalls) return;
        if (!_displays.TryGetValue(plan, out var d)) _displays[plan] = d = new int[MaxHalls];
        var (fit, ratio) = CinemaVideo.UnpackDisplay(display);
        d[hall] = CinemaVideo.PackDisplay(fit, ratio);
    }

    /// <summary>Server: a newly joined client gets every hall's display that is not the default.</summary>
    private void SendDisplaysTo(long peer)
    {
        foreach (var (plan, halls) in _displays)
            for (int h = 0; h < halls.Length; h++)
                if (halls[h] != 0) RpcId(peer, MethodName.SetDisplay, plan, h, halls[h]);
    }

    /// <summary>
    /// <c>/cinema fit contain|cover|stretch</c>, <c>/cinema ratio full|2|1.85|16:9|4:3</c>: the hall's display from the chat;
    /// null for any other word.
    /// </summary>
    private string? DisplayCommand(string plan, int hall, string verb, string arg)
    {
        var (fit, ratio) = CinemaVideo.UnpackDisplay(DisplayOf(plan, hall));
        if (verb == "fit")
        {
            if (!Enum.TryParse(arg, true, out CinemaVideo.Fit f) || !Enum.IsDefined(f)) return "Usage: /cinema fit contain|cover|stretch";
            AskDisplay(plan, hall, CinemaVideo.PackDisplay(f, ratio));
            return $"Hall {hall + 1}: {f}";
        }
        if (verb == "ratio")
        {
            int r = Array.FindIndex(CinemaVideo.Ratios, x => x.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
            if (arg.Equals("full", StringComparison.OrdinalIgnoreCase)) r = 0;
            if (r < 0) return $"Usage: /cinema ratio {string.Join("|", CinemaVideo.Ratios.Select(x => x.Name.Split(' ')[0]))} (full: the screen's)";
            AskDisplay(plan, hall, CinemaVideo.PackDisplay(fit, r));
            return $"Hall {hall + 1}: {CinemaVideo.Ratios[r].Name}";
        }
        return null;
    }
}
