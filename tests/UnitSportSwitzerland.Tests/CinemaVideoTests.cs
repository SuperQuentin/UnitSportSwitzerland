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
    public void A_live_link_names_its_owner_and_never_comes_from_a_player()
    {
        string link = CinemaVideo.LiveLink(1762081815, "Monitor 1 (1920x1080)");
        Assert.True(CinemaVideo.IsLive(link));
        Assert.Equal((1762081815L, "Monitor 1 (1920x1080)"), CinemaVideo.LiveOf(link));
        Assert.Equal("Monitor 1 (1920x1080)", CinemaVideo.Title(link));
        // a player typing one is refused: only the server writes it
        Assert.False(CinemaVideo.Allowed(link));
        Assert.Equal((0L, ""), CinemaVideo.LiveOf("https://example.org/live:1:x"));
        Assert.Equal((0L, ""), CinemaVideo.LiveOf("live:notanumber:x"));
    }

    [Fact]
    public void A_live_label_is_cut_to_printable_and_short()
    {
        var (_, label) = CinemaVideo.LiveOf(CinemaVideo.LiveLink(5, "a\nb" + new string('x', 200)));
        Assert.DoesNotContain('\n', label);
        Assert.True(label.Length <= 80);
    }

    [Theory]
    [InlineData("https://mirrors.mit.edu/kodi/demo-files/BBB/bbb_sunflower_1080p_60fps_stereo_abl.mp4", CinemaVideo.Stereo.TopBottom)]
    [InlineData("https://example.org/films/Avatar.2009.HSBS.1080p.mkv", CinemaVideo.Stereo.SideBySide)]
    [InlineData(@"D:\films\trip [3D] (SBS).mp4", CinemaVideo.Stereo.SideBySide)]
    [InlineData("C:/films/concert_HOU.mp4", CinemaVideo.Stereo.TopBottom)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", CinemaVideo.Stereo.Mono)]
    [InlineData("https://example.org/sbsx/lr-kit.mp4", CinemaVideo.Stereo.SideBySide)]
    public void Guess_reads_a_3D_film_from_its_name(string link, CinemaVideo.Stereo expected) =>
        Assert.Equal(expected, CinemaVideo.Guess(link));

    [Fact]
    public void A_3D_film_decodes_both_eyes_side_by_side()
    {
        Assert.Equal((854, 358), CinemaVideo.PackedSize(CinemaVideo.Stereo.Mono, 854, 358));
        Assert.Equal((1708, 358), CinemaVideo.PackedSize(CinemaVideo.Stereo.TopBottom, 854, 358));
        Assert.DoesNotContain("hstack", CinemaVideo.StereoFit(CinemaVideo.Stereo.Mono, 854, 358));
        string sbs = CinemaVideo.StereoFit(CinemaVideo.Stereo.SideBySide, 854, 358);
        Assert.Contains("crop=iw/2:ih:0:0", sbs);
        Assert.Contains("crop=iw/2:ih:iw/2:0", sbs);
        Assert.EndsWith("hstack", sbs);
        Assert.Contains("crop=iw:ih/2:0:ih/2", CinemaVideo.StereoFit(CinemaVideo.Stereo.TopBottom, 854, 358));
    }

    [Fact]
    public void A_restart_leads_further_but_not_forever()
    {
        Assert.True(CinemaVideo.RestartLead(1) > CinemaVideo.StartLead);
        Assert.Equal(CinemaVideo.RestartLead(4), CinemaVideo.RestartLead(40));
    }
}
