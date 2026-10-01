using System.Globalization;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>/time</c> chat command parsing (src/World/TimeCommand.cs, linked in).</summary>
public class TimeCommandTests
{
    [Theory]
    [InlineData("noon", 12.0)]
    [InlineData("midnight", 0.0)]
    [InlineData("21:30", 21.5)]
    [InlineData("24:00", 0.0)]
    [InlineData("14.5", 14.5)]
    [InlineData(" NIGHT ", 21.0)]
    public void TryParseHour_accepts(string text, double hour)
    {
        Assert.True(TimeCommand.TryParseHour(text, out double h));
        Assert.Equal(hour, h, 9);
    }

    [Theory]
    [InlineData("24:30")]
    [InlineData("12:60")]
    [InlineData("-1")]
    [InlineData("25")]
    [InlineData("+3:00")]
    [InlineData("tea time")]
    public void TryParseHour_rejects(string text) => Assert.False(TimeCommand.TryParseHour(text, out _));

    [Fact]
    public void Numbers_parse_the_same_on_a_French_locale()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.True(TimeCommand.TryParse(["add", "1.5"], out var op, out double v, out _));
            Assert.Equal((TimeOp.Add, 1.5), (op, v));
            Assert.Equal("1.5 min a day", TimeCommand.DescribeSpeed(1.5f));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData(new string[0], TimeOp.Query, 0.0)]
    [InlineData(new[] { "set", "sunset" }, TimeOp.Set, 18.0)]
    [InlineData(new[] { "14:00" }, TimeOp.Set, 14.0)]
    [InlineData(new[] { "add", "-2" }, TimeOp.Add, -2.0)]
    [InlineData(new[] { "speed", "0" }, TimeOp.Speed, 0.0)]
    [InlineData(new[] { "SPEED", "240" }, TimeOp.Speed, 240.0)]
    public void TryParse_valid_lines(string[] args, TimeOp op, double value)
    {
        Assert.True(TimeCommand.TryParse(args, out var o, out double v, out string err), err);
        Assert.Equal((op, value), (o, v));
    }

    [Theory]
    [InlineData("speed", "241")]
    [InlineData("speed", "-1")]
    [InlineData("add", "NaN")]
    [InlineData("set", "25:00")]
    [InlineData("query", "now")]
    public void TryParse_rejects_with_a_message(string verb, string arg)
    {
        Assert.False(TimeCommand.TryParse([verb, arg], out _, out _, out string err));
        Assert.NotEmpty(err);
    }

    [Theory]
    [InlineData(23.0, 3600.0, 24f, 23.0 + 60.0)] // an hour of real time at 24 min a day = 60 game hours
    [InlineData(10.0, 600.0, 0f, 10.0)]          // a stopped clock
    public void Advance_wraps_into_a_day(double hour, double seconds, float minutesPerDay, double raw)
        => Assert.Equal(((raw % 24) + 24) % 24, TimeCommand.Advance(hour, seconds, minutesPerDay), 9);

    [Theory]
    [InlineData(14.0833333333, "14:05")]
    [InlineData(-1.0, "23:00")]
    [InlineData(24.0, "00:00")]
    public void Format_wraps_to_the_minute(double hour, string text)
        => Assert.Equal(text, TimeCommand.Format(hour));
}
