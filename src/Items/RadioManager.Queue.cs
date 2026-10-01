using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// The CD changer of a radio in the world (#211): its <see cref="RadioMode"/>, set like play and
/// stop (asked of the server, which writes <see cref="RadioBody.Mode"/>), and what the server
/// puts on when a CD runs out. The server only knows the shared CDs, so after someone's personal
/// CD "the list" goes on with the shared ones; repeat replays the personal CD, whose length the
/// radio already holds.
/// </summary>
public partial class RadioManager
{
    private readonly Random _shuffle = new();

    /// <summary>Sets what the radio does when its CD ends, for everyone.</summary>
    public void SetMode(RadioBody radio, RadioMode mode)
    {
        if (!Online) { radio.Mode = (int)mode; return; }
        RpcId(1, MethodName.RequestMode, radio.Name, (int)mode);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestMode(string name, int mode)
    {
        if (!Multiplayer.IsServer()) return;
        if (GetNodeOrNull<RadioBody>(name) is { } radio) radio.Mode = (int)RadioQueue.Clamp(mode);
    }

    /// <summary>The owner of the list: a CD ran out. The next starts now (this runs once a second: a CD changer's pause).</summary>
    private void Ended(RadioBody r)
    {
        int next = RadioQueue.Following(r.CdId, RadioQueue.Clamp(r.Mode), RadioQueue.Order(CdLibrary.Instance, withPersonal: !Online), _shuffle);
        float length = next == 0 ? -1f : next == r.CdId ? r.Length : TrustedLength(next, CdLibrary.Instance?.Find(next)?.Duration ?? 0f);
        if (length <= 0)
        {
            r.Playing = false;
            return;
        }
        StartOn(r, next, length);
        GD.Print($"[radio] {r.Name} goes on with CD {next} ({RadioQueue.Clamp(r.Mode)})");
    }
}
