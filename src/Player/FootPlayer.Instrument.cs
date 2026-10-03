using Godot;

namespace UnitSport.Player;

/// <summary>
/// Sat at a house instrument (#433, <see cref="Interiors.HouseProps"/>): the body is held on the
/// piano's bench or the drummer's stool, facing the keys, and drawn seated
/// (<see cref="PoseSeat"/>, replicated like any pose, so everyone sees who plays). At a keyboard
/// the player stands. Only the owner knows <see cref="PlayingAt"/>; the notes go through
/// <see cref="Interiors.HouseProps.Strike"/>.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Sat on a stool or bench, hands forward over the keys (#433).</summary>
    public const int PoseSeat = 6;

    /// <summary>Hip height on a piano bench or a drum stool, m above the floor.</summary>
    private const float SeatHip = 0.5f;

    private static readonly Avatar.SeatAnchor Stool = new(0, new Vector3(0, SeatHip, 0), 0.05f, 0f);

    /// <summary>Owner: the instrument played, an index into the interior's furniture, or -1.</summary>
    public int PlayingAt { get; private set; } = -1;

    private Node3D? _playNode;
    private Vector3 _playLocal;
    private float _playYaw;
    private bool _playSit;
    private ArrayMesh? _seatMesh;
    private (Avatar.HumanPalette, Avatar.Headwear) _seatKey;

    /// <summary>
    /// Sits (or stands) at instrument <paramref name="index"/> of <paramref name="node"/>: on the
    /// spot <paramref name="local"/> of the interior, facing <paramref name="yaw"/> (interior frame).
    /// </summary>
    public void PlayAt(Node3D node, int index, Vector3 local, float yaw, bool sit)
    {
        _playNode = node;
        _playLocal = local;
        _playYaw = yaw;
        _playSit = sit;
        PlayingAt = index;
        DanceId = 0;
        Velocity = Vector3.Zero;
        _viewYaw = node.GlobalRotation.Y + yaw;
        _pitch = -0.25f;
        HoldAtInstrument();
    }

    /// <summary>Stands up from the instrument where one sat.</summary>
    public void StopPlaying()
    {
        if (PlayingAt < 0) return;
        PlayingAt = -1;
        _playNode = null;
        Velocity = Vector3.Zero;
        if (Interiors.HouseProps.Instance?.Ui is { IsOpen: true } ui) ui.Close();
    }

    /// <summary>
    /// Called early in <c>_PhysicsProcess</c>: true while at an instrument (the body stays put,
    /// facing it). Anything that takes the player away (a door, a crash, a ride) stands them up.
    /// </summary>
    private bool HoldAtInstrument()
    {
        if (PlayingAt < 0) return false;
        if (_playNode == null || !IsInstanceValid(_playNode) || !_playNode.IsInsideTree() || !Indoors
            || _ride != null || _ragdoll != null || KnockedOut || _deadTimer > 0)
        {
            StopPlaying();
            return false;
        }
        GlobalPosition = _playNode.ToGlobal(_playLocal);
        Velocity = Vector3.Zero;
        float yaw = _playNode.GlobalRotation.Y + _playYaw;
        Rotation = new Vector3(0, yaw, 0);
        _viewYaw = yaw;
        return true;
    }

    /// <summary>The owner's pose while at an instrument: seated, or stood still at the keyboard. False when not playing.</summary>
    private bool PublishPlayingPose()
    {
        if (PlayingAt < 0) return false;
        PoseKind = _playSit ? PoseSeat : PoseStride;
        Anim = Vector4.Zero;
        BodyPose = Transform3D.Identity;
        return true;
    }

    /// <summary>The seated figure, on this peer and every other: built once per look, the hip on the stool.</summary>
    private void ApplySeatFigure()
    {
        if (_walker == null) return;
        var key = (_walkPalette, Hat);
        if (_seatMesh == null || key != _seatKey)
        {
            _seatMesh = Avatar.SeatedFigure.Build(_walkPalette, Stool, Hat);
            _seatKey = key;
        }
        if (_walker.Mesh != _seatMesh) _walker.Mesh = _seatMesh;
        // the stride figure is built again on standing up
        _poseKey = default;
        _walker.Transform = new Transform3D(Basis.Identity, new Vector3(0, SeatHip, 0));
    }
}
