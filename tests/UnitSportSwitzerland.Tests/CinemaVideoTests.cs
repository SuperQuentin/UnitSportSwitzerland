using UnitSport.Interiors;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A cinema hall's video (#1298): which links may play, and the frame and sample due on the shared clock (src/Interiors/CinemaVideo.cs, linked in).</summary>
public class CinemaVideoTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://www.twitch.tv/somechannel")]
    [InlineData("http://example.org/films/trailer.mp4")]
    [InlineData("https://cdn.example.org/live/index.m3u8?token=a%20b")]
    public void Allowed_takes_web_links(string link) => Assert.True(CinemaVideo.Allowed(link));

    [Theory]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("concat:a.mp4|b.mp4")]
    [InlineData("rtmp://example.org/live")]
    [InlineData("https://example.org/a\nb")]
    [InlineData("not a link")]
    public void Allowed_refuses_the_rest(string link) => Assert.False(CinemaVideo.Allowed(link));

    [Fact]
    public void Allowed_refuses_a_link_too_long() =>
        Assert.False(CinemaVideo.Allowed("https://example.org/" + new string('a', CinemaVideo.MaxLink)));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=x", true)]
    [InlineData("https://youtu.be/x", true)]
    [InlineData("https://m.youtube.com/watch?v=x", true)]
    [InlineData("https://www.twitch.tv/x", true)]
    [InlineData("https://vimeo.com/123", true)]
    [InlineData("https://example.org/x.mp4", false)]
    [InlineData("https://notyoutube.com/x", false)]
    public void IsSite_knows_the_video_sites(string link, bool site) => Assert.Equal(site, CinemaVideo.IsSite(link));

    [Fact]
    public void Frames_follow_the_clock_from_where_the_decoder_started()
    {
        // a decoder started 12.5 s into the video: nothing before, frame k at 12.5 + k / Fps
        Assert.Equal(-1, CinemaVideo.FrameAt(12.4, 12.5));
        Assert.Equal(0, CinemaVideo.FrameAt(12.5, 12.5));
        Assert.Equal(CinemaVideo.Fps, CinemaVideo.FrameAt(13.5, 12.5));
        Assert.Equal(CinemaVideo.Fps * 10 + CinemaVideo.Fps / 2, CinemaVideo.FrameAt(23.0, 12.5));
    }

    [Fact]
    public void Two_clients_on_one_clock_show_the_same_moment_whatever_their_start()
    {
        // client A started at 3 s, client B (late, slow) at 7.2 s: at 9 s on the shared clock both show 9 s
        double now = 1000 + 9, t0 = 1000;
        double pos = CinemaVideo.PositionAt(now, t0);
        double a = 3 + CinemaVideo.FrameAt(pos, 3) / (double)CinemaVideo.Fps;
        double b = 7.2 + CinemaVideo.FrameAt(pos, 7.2) / (double)CinemaVideo.Fps;
        Assert.InRange(Math.Abs(a - b), 0, 1.0 / CinemaVideo.Fps);
        Assert.InRange(pos - a, 0, 1.0 / CinemaVideo.Fps);
        Assert.Equal((long)(2 * CinemaVideo.AudioRate), CinemaVideo.SampleAt(pos, 7));
    }

    [Fact]
    public void The_position_never_goes_before_the_start() => Assert.Equal(0, CinemaVideo.PositionAt(5, 10));

    [Fact]
    public void A_restart_leads_further_but_not_forever()
    {
        Assert.True(CinemaVideo.RestartLead(1) > CinemaVideo.StartLead);
        Assert.Equal(CinemaVideo.RestartLead(4), CinemaVideo.RestartLead(40));
    }
}
