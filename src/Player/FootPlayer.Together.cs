using Godot;
using UnitSport.Adventure.Together;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Playing together on the body (#1266, <c>docs/notes/adventure/links.md</c>): the hooks <see cref="TogetherService"/>
/// sets on a body it simulates (the tether on the walk intent, the rope that catches a fall, the company for the
/// stamina regen), the held hand every peer poses, and the held E at a friend (hold hands) or at the rope (haul).
/// </summary>
public partial class FootPlayer
{
    /// <summary>The soft follow constraint (<see cref="Tether"/>) on the walk's wish, world space; set by the together service on a linked body it simulates.</summary>
    public Func<(Vector3 Wish, bool Run), (Vector3 Wish, bool Run)>? Tethered { get; set; }

    /// <summary>The rope round a partner who stands (in the air on foot): the velocity kept, and whether the rope holds the body now.</summary>
    public Func<Vector3, float, (Vector3 Velocity, bool Held)>? RopeHold { get; set; }

    /// <summary>Who the body is with, for the regen (<see cref="StaminaCompany"/>).</summary>
    public Func<StaminaCompany>? CompanyNow { get; set; }

    /// <summary>A jump off the floor beside a linked friend who offers a step: true makes it a leg-up (2.6 m, free).</summary>
    public Func<bool>? LegUp { get; set; }

    /// <summary>A friend's hand to hold (every peer, from the link book): its world point, and whether the figure's left hand reaches.</summary>
    public (Vector3 World, bool Left)? HandHold { get; set; }

    /// <summary>The held hand as the pose builder takes it, in the figure's frame, to 2 cm; the item hand never lets go of its item.</summary>
    private Avatar.HumanMeshBuilder.HandHold? HoldPose(bool armed)
    {
        // a body lying down or tipped over (getting up, a crash) holds nothing
        if (HandHold is not { } h || _walker == null || BodyPose.Basis.Y.Y < 0.9f) return null;
        var local = (_walker.ToLocal(h.World) / 0.02f).Round() * 0.02f;
        return new Avatar.HumanMeshBuilder.HandHold(local, h.Left || armed);
    }

    /// <summary>Where the holding wrist is now (world), from the pose's own mounts: the checks compare it with the friend's.</summary>
    public Vector3? HoldWrist => HandHold is { } h && _walker != null && _mountsKey.Hold is { } k
        ? _walker.ToGlobal(k.Left ? _poseMounts.HandR : _poseMounts.HandL) : null;

    // ---- a fall into a hidden crevasse (#1372) ---------------------------------------------------

    /// <summary>
    /// Down a hidden crevasse (the owner's, <see cref="CrevasseNode"/>): replicated in <see cref="PoseKind"/> while the body
    /// is in its slot, <c>Anim.W</c> saying what the figure does (0 stands or hangs, 1 climbs the ice, 2 falls). The server
    /// sees a body 10 m under the ground: <c>StaminaWatch</c> skips it.
    /// </summary>
    public const int PoseCrevasse = 9;
    /// <summary>The collision layer of a crevasse's slot (walls, ends, floor): only a body in the slot has it in its mask.</summary>
    public const uint CrevasseLayer = 1u << 20;

    /// <summary>The body is inside a crevasse's slot (owner): the ground is not solid for it, the slot is.</summary>
    public bool InCrevasse { get; private set; }

    /// <summary>The bridge broke: the ground is let go for this body and the slot's walls and floor taken instead.</summary>
    public void EnterCrevasse()
    {
        InCrevasse = true;
        CollisionMask = (CollisionMask & ~1u) | CrevasseLayer;
    }

    /// <summary>Out of the slot (or let out of it): the ground again.</summary>
    public void LeaveCrevasse()
    {
        if (!InCrevasse) return;
        InCrevasse = false;
        CollisionMask = (CollisionMask & ~CrevasseLayer) | (_walkMask & 1u);
    }

    /// <summary>The owner's pose in a slot, over what the walk or the climb published: PoseKind <see cref="PoseCrevasse"/> and what the figure does.</summary>
    private void PublishCrevassePose()
    {
        float inner = PoseKind == PoseWallClimb ? 1f : PoseKind is PoseFall or PoseAir ? 2f : 0f;
        PoseKind = PoseCrevasse;
        Anim = Anim with { W = inner };
    }

    /// <summary>The pose a peer draws: a crevasse's figure is the climb, the flail or the stand its <c>Anim.W</c> names.</summary>
    private int ShownPose => PoseKind != PoseCrevasse ? PoseKind : Anim.W > 1.5f ? PoseFall : Anim.W > 0.5f ? PoseWallClimb : PoseStride;

    // ---- the held E ---------------------------------------------------------------------------------

    /// <summary>Seconds E is held at a party member to ask to hold hands (a tap stays the fight's).</summary>
    public const float LinkHoldSeconds = 0.5f;
    /// <summary>Seconds E is held at the rope to haul a hanging partner up.</summary>
    public const float HaulHoldSeconds = 2f;

    private float _togetherHeld = -1f;
    private long _togetherPeer;
    private bool _hauling, _haulAsked;
    private Action? _togetherTap;

    /// <summary>What the held E is doing now: 0..1 toward the ask, -1 when nothing is held (the prompt's bar).</summary>
    public float TogetherHoldProgress => _togetherHeld < 0f ? -1f
        : Mathf.Clamp(_togetherHeld / (_hauling ? HaulHoldSeconds : LinkHoldSeconds), 0f, 1f);

    /// <summary>
    /// E pressed at <paramref name="peer"/>: when they are a party member who may be linked, the press waits to see
    /// whether it is held (<see cref="LinkHoldSeconds"/>: ask to hold hands) or a tap (<paramref name="tap"/>, the
    /// fight's challenge as before). False when they are not, and E does what it always did.
    /// </summary>
    private bool BeginTogetherHold(long peer, Action tap)
    {
        if (TogetherService.Instance?.MayAskLink(peer) != true) return false;
        _togetherPeer = peer;
        _togetherHeld = 0f;
        _togetherTap = tap;
        _hauling = false;
        return true;
    }

    /// <summary>
    /// E with nothing else to do while linked (#1266): at the rope above a hanging partner a hold hauls them up;
    /// otherwise a tap lets go. False when not linked.
    /// </summary>
    private bool TryTogetherInteract()
    {
        if (Bot || Npc || TogetherService.Instance is not { } together) return false;
        // a shared pot offered here, or a hut or a restaurant to start one at
        if (together.TryPotInteract(this)) return true;
        if (!together.LinkedHere) return false;
        if (together.HaulTarget(this) is long hanging and not 0)
        {
            _togetherPeer = hanging;
            _togetherHeld = 0f;
            _togetherTap = null;
            _hauling = true;
            _haulAsked = false;
            return true;
        }
        together.AskUnlink();
        return true;
    }

    /// <summary>Per physics tick: a held E running toward its ask, or let go.</summary>
    private void TickTogether(float dt)
    {
        if (_togetherHeld < 0f || Bot || Npc) return;
        bool held = PlayerInput.Held(PlayerInput.InteractMount);
        if (_hauling)
        {
            if (!held)
            {
                if (_haulAsked) TogetherService.Instance?.AskHaul(false);
                _togetherHeld = -1f;
                return;
            }
            _togetherHeld += dt;
            if (!_haulAsked && _togetherHeld >= HaulHoldSeconds)
            {
                _haulAsked = true;
                TogetherService.Instance?.AskHaul(true);
            }
            return;
        }
        if (!held)
        {
            // a tap: what E at a player always did
            _togetherHeld = -1f;
            _togetherTap?.Invoke();
            _togetherTap = null;
            return;
        }
        if ((_togetherHeld += dt) < LinkHoldSeconds) return;
        _togetherHeld = -1f;
        _togetherTap = null;
        TogetherService.Instance?.AskLink(_togetherPeer, LinkMode.Hand);
    }
}
