using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Two cars meeting on a narrow two-way road (#1180, <see cref="NarrowPass"/>): both crawl and move over to their right edge, past
/// the tarmac by what it takes, pass with room between, and go back to their line; one waits only on a road under 3 m.
/// </summary>
public class NarrowPassTests
{
    private const float Keep = 1f;   // a 4 m road's lane middle, right of the centreline

    /// <summary>
    /// Two cars driving at each other on a <paramref name="width"/> m road as <c>Traffic.StepCar</c> steers them: each tick the
    /// meeting ahead (until the tails are past), the centre and crawl it asks for, the sideways move only while rolling (0.8 m/s).
    /// Returns the least room between the bodies while side by side, the fastest either went then, the most either moved right.
    /// </summary>
    private static (float Room, float Speed, float Right, float EndA, float EndB) Meet(float width, float speedB = 13.9f)
    {
        float xa = 0f, xb = 200f, va = 13.9f, vb = speedB, pa = 0f, pb = 0f;
        float room = float.MaxValue, fastest = 0f, right = 0f;
        const float dt = 1f / 60f;
        for (int step = 0; step < 60 * 60; step++)
        {
            float ahead = xb - xa;
            bool meeting = ahead > -6f && ahead < NarrowPass.Look;
            float ca = Keep + pa, cb = Keep + pb;
            float wantA = 0f, wantB = 0f, ta = 13.9f, tb = speedB;
            if (meeting)
            {
                float centreA = NarrowPass.Centre(width, Keep, cb), centreB = NarrowPass.Centre(width, Keep, ca);
                (wantA, wantB) = (centreA - Keep, centreB - Keep);
                ta = Math.Min(ta, NarrowPass.Approach(width, centreA, ahead));
                tb = Math.Min(tb, NarrowPass.Approach(width, centreB, ahead));
            }
            va = MoveToward(va, ta, (ta > va ? 2.2f : 6f) * dt);
            vb = MoveToward(vb, tb, (tb > vb ? 2.2f : 6f) * dt);
            pa = MoveToward(pa, wantA, 0.8f * Math.Clamp(va / 3f, 0f, 1f) * dt);
            pb = MoveToward(pb, wantB, 0.8f * Math.Clamp(vb / 3f, 0f, 1f) * dt);
            xa += va * dt;
            xb -= vb * dt;
            if (Math.Abs(xb - xa) < 5f)
            {
                room = Math.Min(room, Keep + pa + Keep + pb - 2f * NarrowPass.HalfBody);
                fastest = Math.Max(fastest, Math.Max(va, vb));
            }
            right = Math.Max(right, Keep + Math.Max(pa, pb));
            if (xa > 260f) break;
        }
        return (room, fastest, right, pa, pb);
    }

    private static float MoveToward(float from, float to, float delta) =>
        Math.Abs(to - from) <= delta ? to : from + Math.Sign(to - from) * delta;

    [Fact]
    public void TwoOncomingCars_OnA4mRoad_CrawlPastEachOther_MovedOver()
    {
        Assert.True(NarrowPass.Fits(4f));
        var (room, speed, right, endA, endB) = Meet(4f);
        // the bodies pass with room between (Gap 0.5 m, less what the moving over had not reached yet: never touching)
        Assert.True(room > 0.3f, $"room between the bodies {room:F2} m");
        // at a crawl: 15 km/h at most, beside each other
        Assert.True(speed <= NarrowPass.FastCrawl + 0.05f, $"speed side by side {speed * 3.6f:F1} km/h");
        // each middle 1.2 m right of the centreline: its side 0.15 m past the 4 m road's edge, within the verge allowance
        Assert.InRange(right, 1.15f, NarrowPass.MostRight(4f));
        Assert.True(right + NarrowPass.HalfBody - 2f <= NarrowPass.Overhang);
        // and back to their lines once past
        Assert.Equal(0f, endA, 2);
        Assert.Equal(0f, endB, 2);
    }

    [Fact]
    public void PassingACarStandingAtARed_MovesOverFurtherAlone()
    {
        // the standing car keeps its line (a car slides over only while it rolls): the passing one goes 1.4 m over
        Assert.Equal(1.4f, NarrowPass.Centre(4f, Keep, Keep), 2);
        var (room, speed, _, _, _) = Meet(4f, speedB: 0f);
        Assert.True(room > 0.3f, $"room {room:F2} m");
        Assert.True(speed <= NarrowPass.FastCrawl + 0.05f);
    }

    [Fact]
    public void LessRoom_SlowerCrawl_AndUnder3m_OneAtATime()
    {
        Assert.True(NarrowPass.Crawl(3.2f, NarrowPass.Centre(3.2f, 0.8f, 0.8f)) < NarrowPass.Crawl(4f, NarrowPass.Centre(4f, Keep, Keep)));
        Assert.True(NarrowPass.Crawl(4f, 1.2f) >= NarrowPass.SlowCrawl);
        Assert.False(NarrowPass.Fits(2.8f));
    }
}
