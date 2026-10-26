using Godot;

namespace UnitSport.Player;

public partial class FootPlayer
{
    /// <summary>On foot, free climbing a face: <see cref="Anim"/> = (speed along the face, limb phase 0..1, climb-jump 0..1, 0).</summary>
    public const int PoseWallClimb = 8;

    private bool _climbing;

    /// <summary>Free climbing a face (on every peer: a remote copy reads it from the replicated pose).</summary>
    public bool IsClimbing => !Net.NetLink.Dropped(this) && IsMultiplayerAuthority()
        ? _climbing
        : PoseKind == PoseWallClimb && RideKindId == (int)RideKind.OnFoot;
}
