using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>The medic armband (#218, <see cref="Combat.Medic"/>): a bit of the replicated outfit, so every figure draws it.</summary>
public partial class FootPlayer
{
    /// <summary>
    /// Wears the medic armband: hurts no player and is hurt by none. Rides in <see cref="OutfitBits"/>
    /// (<see cref="Outfit.MedicBit"/>, replicated on change, redrawn with the clothes); set on the
    /// owner, only when the server says so (<see cref="MedicState"/>).
    /// </summary>
    public bool Medic
    {
        get => (OutfitBits & Outfit.MedicBit) != 0;
        private set => OutfitBits = value ? OutfitBits | Outfit.MedicBit : OutfitBits & ~Outfit.MedicBit;
    }

    /// <summary>Server: tells this body's owner its armband state, cooldown and pending seconds.</summary>
    public void SendMedicState(long owner, bool on, double cooldownLeft, double pendingLeft) =>
        RpcId(owner, MethodName.MedicState, on, (float)cooldownLeft, (float)pendingLeft);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MedicState(bool on, float cooldownLeft, float pendingLeft)
    {
        if (Multiplayer.GetRemoteSenderId() != 1 || !IsMultiplayerAuthority()) return;
        Medic = on;
        Combat.Medic.CooldownEnds = Combat.Medic.Now + cooldownLeft;
        Combat.Medic.PendingEnds = Combat.Medic.Now + pendingLeft;
    }
}
