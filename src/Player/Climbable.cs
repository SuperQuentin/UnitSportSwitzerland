using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>What one climbing ray hit, as far as climbing cares (#1225).</summary>
public enum ClimbBody
{
    /// <summary>The terrain's ground: its land cover decides.</summary>
    Terrain,
    /// <summary>A static body: a wall, a bridge pier, a dam face, a fixture block.</summary>
    Static,
    /// <summary>A vehicle, an animated or moving body (the steamer), a rigid body.</summary>
    Moving,
    /// <summary>Another player or a figure.</summary>
    Player,
    /// <summary>A tree trunk: a thin capsule reads as a pole.</summary>
    Tree,
    /// <summary>A placed object (a built piece, a crate) or anything in the <c>no_climb</c> group.</summary>
    NoClimb,
}

/// <summary>
/// Which faces can be free-climbed (#1225, <c>docs/plans/adventure-stamina.md</c> 5): pure, given what
/// one ray found, linked into the unit tests (<c>ClimbableTests</c>).
/// </summary>
public static class Climbable
{
    /// <summary>
    /// A face whose normal's Y is above this is walked or slid, not climbed (53°). Shared with
    /// <c>XrClimb</c>'s hand climbing.
    /// </summary>
    public const float SteepNormalY = 0.6f;
    /// <summary>A face leaning out past this (an overhang) is let go.</summary>
    public const float OverhangNormalY = -0.15f;

    /// <summary>The face's steepness can be climbed.</summary>
    public static bool Steep(float normalY) => normalY <= SteepNormalY && normalY >= OverhangNormalY;

    /// <summary>
    /// The ground's cover can be climbed: anything but ice, firn and water (they need tools).
    /// Unknown cover (null: a server-run bot, a far tile) counts as rock.
    /// </summary>
    public static bool Cover(CoverClass? cover) =>
        cover is not (CoverClass.Glacier or CoverClass.Snowfield or CoverClass.Water);

    /// <summary>Crumbling rock: climbing it costs <see cref="Stamina.LooseFactor"/> more.</summary>
    public static bool Loose(CoverClass? cover) =>
        cover is CoverClass.LooseRock or CoverClass.Scree or CoverClass.LooseScree;

    /// <summary>Whether this kind of body can be climbed at all.</summary>
    public static bool Body(ClimbBody body) => body is ClimbBody.Terrain or ClimbBody.Static;

    /// <summary>The whole rule for one hit.</summary>
    public static bool Face(ClimbBody body, float normalY, CoverClass? cover) =>
        Body(body) && Steep(normalY) && (body != ClimbBody.Terrain || Cover(cover));
}
