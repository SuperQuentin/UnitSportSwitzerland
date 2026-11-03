using UnitSport.Player;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Which faces can be free-climbed (#1225, src/Player/Climbable.cs).</summary>
public class ClimbableTests
{
    [Theory]
    [InlineData(0.9f, false)]    // walkable
    [InlineData(0.61f, false)]   // still a steep slope, slid
    [InlineData(0.6f, true)]     // 53°
    [InlineData(0.17f, true)]    // 80°
    [InlineData(0f, true)]       // vertical
    [InlineData(-0.15f, true)]   // a slight overhang
    [InlineData(-0.2f, false)]   // leaning out: let go
    public void Steepness(float normalY, bool climbable) => Assert.Equal(climbable, Climbable.Steep(normalY));

    [Theory]
    [InlineData(CoverClass.Rock, true)]
    [InlineData(CoverClass.Scree, true)]
    [InlineData(CoverClass.Forest, true)]
    [InlineData(CoverClass.Glacier, false)]
    [InlineData(CoverClass.Snowfield, false)]
    [InlineData(CoverClass.Water, false)]
    public void Covers(CoverClass cover, bool climbable) => Assert.Equal(climbable, Climbable.Cover(cover));

    [Fact]
    public void Unknown_cover_counts_as_rock() => Assert.True(Climbable.Face(ClimbBody.Terrain, 0.2f, null));

    [Theory]
    [InlineData(CoverClass.LooseRock, true)]
    [InlineData(CoverClass.LooseScree, true)]
    [InlineData(CoverClass.Scree, true)]
    [InlineData(CoverClass.Rock, false)]
    public void Loose_rock(CoverClass cover, bool loose) => Assert.Equal(loose, Climbable.Loose(cover));

    [Theory]
    [InlineData(ClimbBody.Terrain, true)]
    [InlineData(ClimbBody.Static, true)]
    [InlineData(ClimbBody.Moving, false)]
    [InlineData(ClimbBody.Player, false)]
    [InlineData(ClimbBody.Tree, false)]
    [InlineData(ClimbBody.NoClimb, false)]
    public void Bodies(ClimbBody body, bool climbable) => Assert.Equal(climbable, Climbable.Face(body, 0f, CoverClass.Rock));

    [Fact]
    public void A_wall_ignores_the_ground_cover() => Assert.True(Climbable.Face(ClimbBody.Static, 0f, CoverClass.Glacier));
}
