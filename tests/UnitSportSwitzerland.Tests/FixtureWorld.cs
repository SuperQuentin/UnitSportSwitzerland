namespace UnitSport.Tests;

/// <summary>
/// What a fixture course sets for the whole process: a <c>FixtureChunkSource</c> writes <c>CoursePlace.Active</c> and its
/// <c>Fleet</c> as it is built, the fun world its venues and stadium (<c>Venues.Override</c>, <c>StadiumCatalog.UseWorld</c>),
/// <c>ForgetWorld</c> undoes them. xUnit runs test classes in parallel, so a class building one course read another's
/// place half the time (<c>FunCourseTests.On_earth_the_fleet_stands_where_the_leaves_have_its_course_points</c> failed in the
/// full run and passed alone). Every class that builds a course or reads what one sets joins <see cref="Collection"/>.
/// </summary>
public static class FixtureWorld
{
    public const string Collection = "FixtureWorld";
}
