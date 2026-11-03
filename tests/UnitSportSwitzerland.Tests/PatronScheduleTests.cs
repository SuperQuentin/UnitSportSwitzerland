using UnitSport.Interiors.Patrons;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The venues' patrons' schedule (#1244): busy-ness by hour, windows, parties, queues; all pure.</summary>
public class PatronScheduleTests
{
    [Fact]
    public void BusyFollowsTheVenuesHours()
    {
        Assert.True(PatronSchedule.Busy(PatronVenue.Beiz, 12.3) > 0.9f);
        Assert.True(PatronSchedule.Busy(PatronVenue.Beiz, 19.5) > 0.9f);
        Assert.True(PatronSchedule.Busy(PatronVenue.Beiz, 15.5) < 0.4f);
        Assert.Equal(0f, PatronSchedule.Busy(PatronVenue.Beiz, 4));
        Assert.True(PatronSchedule.Busy(PatronVenue.Cafe, 9) > 0.9f);
        Assert.Equal(0f, PatronSchedule.Busy(PatronVenue.Cafe, 21));
        // a bar and a club run on past midnight
        Assert.True(PatronSchedule.Busy(PatronVenue.Bar, 0.5) > 0.3f);
        Assert.True(PatronSchedule.Busy(PatronVenue.Club, 1.5) > 0.9f);
        Assert.Equal(0f, PatronSchedule.Busy(PatronVenue.Club, 15));
        Assert.Equal(0f, PatronSchedule.Busy(PatronVenue.Club, 5));
        // every curve stays in 0..1 all day
        foreach (PatronVenue v in Enum.GetValues<PatronVenue>())
            for (double h = 0; h < 24; h += 0.25)
            {
                float b = PatronSchedule.Busy(v, h);
                Assert.InRange(b, 0f, 1f);
            }
    }

    [Fact]
    public void ASpotIsFullAboutAsOftenAsItIsBusy()
    {
        foreach (float busy in new[] { 0.2f, 0.5f, 0.9f })
        {
            int full = 0, samples = 0;
            for (uint key = 1; key <= 200; key++)
                for (double t = 0; t < 36000; t += 97)
                {
                    samples++;
                    if (PatronSchedule.Party(key * 7919u, 4, PatronActivity.Dine, t, _ => busy).Party > 0) full++;
                }
            double share = full / (double)samples;
            Assert.InRange(share, busy - 0.08, busy + 0.08);
        }
    }

    [Fact]
    public void AClosedVenueIsEmptyAndAPartyStaysTheSameThroughItsStay()
    {
        for (double t = 0; t < 20000; t += 31)
            Assert.Equal(0, PatronSchedule.Party(42, 4, PatronActivity.Dine, t, _ => 0f).Party);
        // within one window the party is one size, and never more than the seats
        for (uint key = 1; key < 50; key++)
        {
            var seen = new Dictionary<long, int>();
            for (double t = 0; t < 12000; t += 13)
            {
                var (n, w) = PatronSchedule.Party(key, 3, PatronActivity.Coffee, t, _ => 1f);
                Assert.InRange(n, 0, 3);
                if (n == 0) continue;
                if (seen.TryGetValue(w, out int was)) Assert.Equal(was, n);
                else seen[w] = n;
            }
        }
    }

    [Fact]
    public void ThePartyIsAPureFunctionOfTheClock()
    {
        for (double t = 0; t < 5000; t += 11.3)
            Assert.Equal(PatronSchedule.Party(1234, 4, PatronActivity.Drink, t, _ => 0.6f),
                PatronSchedule.Party(1234, 4, PatronActivity.Drink, t, _ => 0.6f));
    }

    [Fact]
    public void PartiesAreMostlyTwo()
    {
        var count = new int[5];
        for (uint k = 0; k < 4000; k++) count[PatronSchedule.PartySize(PatronSchedule.Mix(k, 1, 2), 4)]++;
        Assert.True(count[2] > count[1] && count[2] > count[3] && count[2] > count[4]);
        Assert.Equal(0, count[0]);
        for (uint k = 0; k < 100; k++) Assert.Equal(1, PatronSchedule.PartySize(k, 1));
    }

    [Fact]
    public void FirstSeatIsEvenAndOnTheTable()
    {
        for (uint k = 0; k < 500; k++)
            foreach (int seats in new[] { 1, 2, 4, 6 })
            {
                int s = PatronSchedule.FirstSeat(k, k * 3, seats);
                Assert.InRange(s, 0, seats - 1);
                Assert.Equal(0, s % 2);
            }
    }

    [Fact]
    public void AQueueServesOneAtATimeAndFollowsBusyness()
    {
        long last = long.MinValue;
        for (double t = 0; t < 600; t += 1)
        {
            var (front, len) = PatronSchedule.Queue(77, t, 20, 5, _ => 1f);
            Assert.InRange(len, 0, 5);
            if (last != long.MinValue) Assert.InRange(front - last, 0, 1);
            last = front;
        }
        Assert.Equal(0, PatronSchedule.Queue(77, 100, 20, 5, _ => 0f).Length);
        double sumBusy = 0, sumQuiet = 0;
        for (double t = 0; t < 4000; t += 20)
        {
            sumBusy += PatronSchedule.Queue(5, t, 20, 5, _ => 1f).Length;
            sumQuiet += PatronSchedule.Queue(5, t, 20, 5, _ => 0.3f).Length;
        }
        Assert.True(sumBusy > 2 * sumQuiet);
        for (double t = 0; t < 100; t += 0.7) Assert.InRange(PatronSchedule.QueuePhase(5, t, 20), 0, 1);
    }
}
