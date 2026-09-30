using Godot;

namespace UnitSport.Net;

/// <summary>
/// Stands in for <c>Occasions.OccasionManager</c> on a <see cref="Swarm"/> bot's branch: the server
/// sends every joining peer the running occasions, and the real manager is a per-process singleton.
/// Its RPC list must match the real one (Godot addresses RPCs by index into it).
/// </summary>
public partial class SwarmOccasions : Node
{
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetActive(string json) { }
}
