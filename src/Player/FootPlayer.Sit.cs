using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Sitting down on a stadium's stand (#885). E (pad Y; VR: Y, or reach out and grip) on a row of a
/// seat block sits on the seat there (<see cref="global::UnitSport.Terrain.Stadiums.StadiumSeats"/>), facing the
/// pitch; E again, or walking off, stands up. Nothing new on the wire: the seat is the body's
/// position and turn, the pose <see cref="PoseSit"/> with the feet's drop in <c>Anim.X</c>, and every
/// peer draws the seated figure from that (<see cref="ApplyFootPose"/>). The owner decides, as for a
/// dance: a server-run bot calls <see cref="TrySit"/> and <see cref="StandUpFromSeat"/> on its body,
/// and stands up by wishing to walk (its <c>WalkControls</c>), as a player does with the stick.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Seated on a stand: <c>Anim.X</c> is how far under the hip the feet rest, <c>BodyPose</c> lifts the hip.</summary>
    public const int PoseSit = 6;

    /// <summary>A seated eye over the hip, m.</summary>
    private const float SitEyeOverHip = 0.78f;
    /// <summary>The walk's wish that stands a seated body up, once the stick has been let go since sitting.</summary>
    private const float SitStandWish = 0.3f;
    private const float SitRecline = 0.05f;

    public bool Sitting { get; private set; }
    private float _sitDrop, _sitYaw;
    private Vector3 _sitStand;
    // moving stands up only once the move that walked you there has been let go
    private bool _sitArmed;

    /// <summary>The seat a press of E would sit on, here and now; null when there is none.</summary>
    public global::UnitSport.Terrain.Stadiums.StadiumSeat? SeatInReach()
    {
        if (Sitting || !CanEmote || _ride != null || RidingWith != 0 || Aboard || Indoors || !IsOnFloor() || Origin is not { } origin) return null;
        var (e, n) = origin.ToLv95(GlobalPosition);
        return global::UnitSport.Terrain.Stadiums.StadiumSeats.At(e, n);
    }

    /// <summary>Sits on the seat in reach, if any: the body still, turned to the pitch, drawn seated.</summary>
    public bool TrySit()
    {
        if (SeatInReach() is not { } seat || Origin is not { } origin) return false;
        var at = origin.ToWorld(seat.E, seat.N, 0);
        float hipY = GlobalPosition.Y + seat.Hip;
        _sitStand = new Vector3(at.X, hipY - seat.Hip + 0.05f, at.Z);
        _sitDrop = seat.Drop;
        _sitYaw = seat.Yaw;
        GlobalPosition = new Vector3(at.X, hipY - seat.Drop, at.Z);
        Velocity = Vector3.Zero;
        _viewYaw = _sitYaw;
        Rotation = new Vector3(0, _sitYaw, 0);
        _body.Disabled = true;
        DanceId = 0;
        _sitArmed = false;
        Sitting = true;
        PublishSitPose();
        return true;
    }

    /// <summary>Stands up where the seat is, on the row's step.</summary>
    public void StandUpFromSeat()
    {
        if (!Sitting) return;
        Sitting = false;
        GlobalPosition = _sitStand;
        Velocity = Vector3.Zero;
        _body.Disabled = false;
        PoseKind = PoseStride;
        Anim = Vector4.Zero;
        BodyPose = Transform3D.Identity;
    }

    /// <summary>The owner's pose while seated, written together as the pose kind changes (<c>replicated-value-whose-meaning-depends</c>).</summary>
    private void PublishSitPose()
    {
        PoseKind = PoseSit;
        Anim = new Vector4(_sitDrop, SitRecline, 0f, 0f);
        BodyPose = new Transform3D(Basis.Identity, new Vector3(0, _sitDrop, 0));
    }

    /// <summary>
    /// A physics step seated: the body stays put; anything that takes the body (a blow, a knock-out,
    /// a fight, a vehicle) stands it up first, and so does a wish to walk once the stick was let go.
    /// True while still seated.
    /// </summary>
    private bool SitPhysics()
    {
        if (!Sitting) return false;
        if (!CanEmote || _ride != null || RidingWith != 0)
        {
            StandUpFromSeat();
            return false;
        }
        float wish = WalkControls?.Invoke() is { } walk ? walk.Wish.Length() : Npc ? 0f : PlayerInput.Move.Length();
        if (wish < 0.1f) _sitArmed = true;
        else if (_sitArmed && wish > SitStandWish)
        {
            StandUpFromSeat();
            return false;
        }
        Velocity = Vector3.Zero;
        return true;
    }

    /// <summary>The seated figure for <see cref="ApplyFootPose"/>: built around the hip, the feet <c>Anim.X</c> under it.</summary>
    private ArrayMesh SeatedWalker(Avatar.HumanPalette palette)
    {
        var key = (palette, Hat, Mathf.Round(Anim.X * 100f), Mathf.Round(Anim.Y * 100f));
        if (_sitMesh != null && key.Equals(_sitKey)) return _sitMesh;
        _sitKey = key;
        return _sitMesh = Avatar.SeatedFigure.Build(palette, new Avatar.SeatAnchor(0, new Vector3(0, Anim.X, 0), Anim.Y, 0f), Hat);
    }

    private ArrayMesh? _sitMesh;
    private (Avatar.HumanPalette, Avatar.Headwear, float, float) _sitKey;
}
