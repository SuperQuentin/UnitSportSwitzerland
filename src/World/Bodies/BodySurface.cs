using System.Collections.Generic;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World.Bodies;

/// <summary>
/// One body's terrain, every body the same way, the Earth included (#1848, H7b, docs/plans/planets.md section 4): its
/// pyramid chain, its leaves, the profile that makes what is finer than its data, its datum, its sea and its leaf level.
/// The Earth's entries are the objects its world has always used (the globe's chain <see cref="GlobeData.Earth"/>, its
/// real and generated leaves, the fill on the pyramid, LHN95's geoid, the sea); another body's are its chain
/// (<see cref="BodyData"/>), no leaves yet, its relief family over its craters and stamps (<see cref="BodyGround.HeightsOf"/>),
/// its ellipsoid, no sea. What reads a body's terrain asks here (<see cref="Of"/>): the globes (<see cref="BodyViews"/>,
/// <see cref="Globe.DataSource"/>), the ground under one's feet.
///
/// <para>
/// <b>The Earth's profile is its fill</b> (<see cref="Profile"/> <see cref="ProfileKind.EarthFill"/>): the generator's
/// leaf moved onto the pyramid (<see cref="PyramidFill"/>, <see cref="GeneratedLeaves"/>), which makes whole leaves
/// (heights, cover, water, trees, villages) rather than a height at a point; another body's profile is a height at a point
/// (<see cref="SurfaceProfile"/>), its craters and boulders round it. One interface over both would hide that difference,
/// not remove it: the plan's section 5 says so since #1848.
/// </para>
/// </summary>
public sealed class BodySurface
{
    /// <summary>What makes a body's ground finer than its data.</summary>
    public enum ProfileKind
    {
        /// <summary>The data alone (the Sun, the giants' cloud decks: nothing to stand on).</summary>
        None,
        /// <summary>The Earth's fill: generated leaves on the pyramid (<see cref="PyramidFill"/>).</summary>
        EarthFill,
        /// <summary>A height at a point: craters, stamps and a relief family (<see cref="BodyHeights"/>, <see cref="SurfaceProfile"/>).</summary>
        Relief,
    }

    /// <summary>Where a body's heights are counted from.</summary>
    public enum DatumKind
    {
        /// <summary>The Earth: LHN95's geoid (heights above sea level, <c>SwissGeodesy.Lhn95Height</c>).</summary>
        Geoid,
        /// <summary>Its reference ellipsoid (<see cref="CelestialBody.Shape"/>).</summary>
        Ellipsoid,
    }

    public CelestialBody Body { get; }

    /// <summary>Its pyramid chain (finest copy first: the local install, the server's, the shipped base, generated); null for the Sun.</summary>
    public PyramidSource? Pyramid => Body.Id == BodyId.Earth ? GlobeData.Earth : BodyData.For(Body);

    /// <summary>Whether it has leaves (the Earth's regions and fill; another body's ground is its near square for now).</summary>
    public bool HasLeaves => Body.Id == BodyId.Earth;

    /// <summary>What makes its ground finer than its data.</summary>
    public ProfileKind Profile => Body.Id == BodyId.Earth ? ProfileKind.EarthFill : Body.Landable ? ProfileKind.Relief : ProfileKind.None;

    /// <summary>Its relief family (dunes, ridges, grooves, lava flows), or null (the Earth's fill, the craters alone, nothing).</summary>
    public SurfaceProfile? Relief => Profile == ProfileKind.Relief ? SurfaceProfile.For(Body.Id, Body.Cube.Shape.A) : null;

    public DatumKind Datum => Body.Id == BodyId.Earth ? DatumKind.Geoid : DatumKind.Ellipsoid;

    /// <summary>Whether it has a sea at its datum (the Earth; Titan's lakes are not modelled).</summary>
    public bool HasSea => Body.Id == BodyId.Earth;

    /// <summary>The cube level of its leaves (<see cref="CelestialBody.LeafLevel"/>; the Earth's is <see cref="LeafFrames.LeafLevel"/>).</summary>
    public int LeafLevel => Body.LeafLevel;

    /// <summary>
    /// Its heights at a point, for the ground under one's feet on a body that has no leaves (<see cref="BodyGround.HeightsOf"/>,
    /// the pyramid's finest level under the profile); null for the Earth (its leaves) and a body one cannot stand on.
    /// </summary>
    public BodyHeights? PointHeights()
    {
        if (Profile != ProfileKind.Relief || Pyramid is not { } pyramid) return null;
        return BodyGround.HeightsOf(Body, pyramid, System.Math.Max(pyramid.MaxLevel, 0));
    }

    private BodySurface(CelestialBody body) => Body = body;

    private static readonly Dictionary<BodyId, BodySurface> All = new();
    private static readonly object Gate = new();

    /// <summary>A body's surface (one per body for the process).</summary>
    public static BodySurface Of(BodyId id)
    {
        lock (Gate)
        {
            if (!All.TryGetValue(id, out var s)) All[id] = s = new BodySurface(CelestialBody.Of(id));
            return s;
        }
    }

    /// <summary>The Earth's.</summary>
    public static BodySurface Earth => Of(BodyId.Earth);
}
