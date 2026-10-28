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

    [Theory]
    [InlineData(CinemaVideo.Quality.Low, 480)]
    [InlineData(CinemaVideo.Quality.Medium, 720)]
    [InlineData(CinemaVideo.Quality.High, 1080)]
    public void DecodeSize_keeps_the_screen_shape_and_asks_sources_that_tall(CinemaVideo.Quality q, int lines)
    {
        var (w, h, tallest) = CinemaVideo.DecodeSize(q);
        Assert.Equal(lines, tallest);
        Assert.InRange(w / (float)h, 2.37f, 2.41f);
        Assert.True(w % 2 == 0 && h % 2 == 0);
        Assert.Contains($"height<={lines}]", CinemaVideo.SiteFormat(tallest));
    }

    [Fact]
    public void DecodeSize_Low_is_the_screen_size_the_relay_scales_to() =>
        Assert.Equal((CinemaVideo.Width, CinemaVideo.Height), (CinemaVideo.DecodeSize(CinemaVideo.Quality.Low).Width, CinemaVideo.DecodeSize(CinemaVideo.Quality.Low).Height));

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
    public void A_showing_hall_draws_a_film_or_the_slides_among_them()
    {
        int films = CinemaVideo.OpenFilms.Length;
        var drawn = Enumerable.Range(0, 2000).Select(i => CinemaVideo.PickShowing((uint)(i * 2654435761u), films)).ToList();
        Assert.All(drawn, p => Assert.InRange(p, -1, films - 1));
        // the slides one entry among the films: about one draw in films + 1
        Assert.InRange(drawn.Count(p => p < 0), 2000 / (films + 1) / 2, 2000 / (films + 1) * 2);
        Assert.Equal(films, drawn.Where(p => p >= 0).Distinct().Count());
    }

    [Fact]
    public void A_showing_film_loops_on_the_shared_clock()
    {
        // a 600 s film phased at 100 s: started at 100, 700, 1300 ...
        Assert.Equal(700, CinemaVideo.LoopStart(1000, 600, 100));
        Assert.Equal(1300, CinemaVideo.LoopStart(1300, 600, 100));
        Assert.Equal(100, CinemaVideo.LoopStart(699.9, 600, 100));
        // before its first start: the loop before
        Assert.Equal(-500, CinemaVideo.LoopStart(50, 600, 100));
        // two peers at any moments of one loop share its start
        Assert.Equal(CinemaVideo.LoopStart(800, 600, 100), CinemaVideo.LoopStart(1250, 600, 100));
    }

    [Fact]
    public void The_open_films_are_web_links_that_loop()
    {
        Assert.NotEmpty(CinemaVideo.OpenFilms);
        Assert.All(CinemaVideo.OpenFilms, f =>
        {
            Assert.True(CinemaVideo.Allowed(f.Link), f.Link);
            Assert.True(f.Seconds > 60, f.Title);
            Assert.False(string.IsNullOrWhiteSpace(f.Title));
        });
        Assert.Equal(CinemaVideo.OpenFilms.Length, CinemaVideo.OpenFilms.Select(f => f.Link).Distinct().Count());
    }

    [Fact]
    public void The_curtains_measure_a_picture_between_its_black_bars()
    {
        // a 16:9 picture padded into 854x358: 636 columns lit in the middle
        int lit0 = (854 - 636) / 2;
        Assert.Equal(636 / 854f, CinemaVideo.ContentWidth(854, 358, (x, _) => x >= lit0 && x < lit0 + 636), 3);
        // a scope picture fills it all; a black frame says nothing
        Assert.Equal(1f, CinemaVideo.ContentWidth(854, 358, (_, _) => true));
        Assert.Equal(-1f, CinemaVideo.ContentWidth(854, 358, (_, _) => false));
        // a dark scene lit only in its middle row still counts its widest lit column
        Assert.Equal(0.5f, CinemaVideo.ContentWidth(100, 90, (x, y) => y == 45 && x >= 25 && x < 75), 3);
    }

    [Fact]
    public void A_picture_shape_waits_and_only_widens()
    {
        var shape = new CinemaVideo.Shape(6);
        // open until 1.5 s of measures: a black start says nothing
        for (int i = 0; i < 8; i++) shape.Add(0.74f);
        Assert.Equal(1f, shape.Width);
        shape.Add(0.74f);
        Assert.Equal(0.74f, shape.Width, 3);
        // a dark scene never narrows it; a wider shot widens it; a black frame is no measure
        shape.Add(0.3f);
        shape.Add(-1f);
        Assert.Equal(0.74f, shape.Width, 3);
        shape.Add(0.9f);
        Assert.Equal(0.9f, shape.Width, 3);
    }

    [Fact]
    public void A_title_card_says_nothing_of_the_picture_shape()
    {
        var shape = new CinemaVideo.Shape(6);
        // a film first seen on its end card: a line of lit letters on black, 40 % wide and 10 % tall
        for (int i = 0; i < 20; i++) shape.Add(0.4f, 0.1f);
        Assert.Equal((1f, 1f), (shape.Width, shape.Height));
        // a 16:9 picture fitted at the frame's full height, and one with bars burned in both ways, do count
        for (int i = 0; i < 9; i++) shape.Add(0.74f, 1f);
        Assert.Equal(0.74f, shape.Width, 3);
        var burned = new CinemaVideo.Shape(6);
        for (int i = 0; i < 9; i++) burned.Add(0.74f, 0.74f);
        Assert.Equal((0.74f, 0.74f), (burned.Width, burned.Height));
    }

    [Fact]
    public void A_display_packs_its_fit_and_ratio_and_defaults_to_contain_at_the_screens_ratio()
    {
        Assert.Equal((CinemaVideo.Fit.Contain, 0), CinemaVideo.UnpackDisplay(0));
        Assert.Equal("Full screen (2.39:1)", CinemaVideo.Ratios[0].Name);
        foreach (var fit in Enum.GetValues<CinemaVideo.Fit>())
            for (int r = 0; r < CinemaVideo.Ratios.Length; r++)
                Assert.Equal((fit, r), CinemaVideo.UnpackDisplay(CinemaVideo.PackDisplay(fit, r)));
        // out of range from the wire: clamped
        Assert.Equal((CinemaVideo.Fit.Stretch, CinemaVideo.Ratios.Length - 1), CinemaVideo.UnpackDisplay(15 | 15 << 4));
    }

    [Fact]
    public void The_curtains_frame_what_the_display_shows()
    {
        float sixteenNine = 16f / 9f;
        int full = 0, r169 = Array.FindIndex(CinemaVideo.Ratios, r => r.Name == "16:9"), r43 = Array.FindIndex(CinemaVideo.Ratios, r => r.Name == "4:3");
        // contain at the screen's ratio: a 16:9 picture shows 74 % of the width, a scope one all of it
        Assert.Equal(sixteenNine / 2.39f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Contain, full, sixteenNine), 3);
        Assert.Equal(1f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Contain, full, 2.39f), 3);
        // cover and stretch fill the frame: at the screen's ratio, all of it; at 4:3, the 4:3 frame
        Assert.Equal(1f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Cover, full, sixteenNine), 3);
        Assert.Equal(1f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Stretch, full, sixteenNine), 3);
        Assert.Equal(4f / 3f / 2.39f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Cover, r43, sixteenNine), 3);
        // contain a scope picture in a 16:9 frame: as wide as the frame (letterboxed inside it)
        Assert.Equal(sixteenNine / 2.39f, CinemaVideo.VisibleWidth(CinemaVideo.Fit.Contain, r169, 2.39f), 3);
    }

    [Fact]
    public void The_screen_maps_a_picture_contained_covered_or_stretched()
    {
        float sixteenNine = 16f / 9f, w = sixteenNine / 2.39f;
        // contain, full ratio: black bars at the sides, the picture's middle at the screen's middle
        Assert.Null(CinemaVideo.MapScreen(0.05f, 0.5f, CinemaVideo.Fit.Contain, 0, sixteenNine));
        var mid = CinemaVideo.MapScreen(0.5f, 0.5f, CinemaVideo.Fit.Contain, 0, sixteenNine)!.Value;
        Assert.Equal(0.5f, mid.X, 3);
        var edge = CinemaVideo.MapScreen(0.5f - w / 2 + 0.0001f, 0.2f, CinemaVideo.Fit.Contain, 0, sixteenNine)!.Value;
        Assert.Equal(0f, edge.X, 2);
        Assert.Equal(0.2f, edge.Y, 3);
        // cover, full ratio: the screen's left edge shows the picture's left, its top a crop of it
        var cover = CinemaVideo.MapScreen(0f, 0f, CinemaVideo.Fit.Cover, 0, sixteenNine)!.Value;
        Assert.Equal(0f, cover.X, 3);
        Assert.True(cover.Y > 0.1f);
        // stretch: corner to corner
        var stretch = CinemaVideo.MapScreen(1f, 1f, CinemaVideo.Fit.Stretch, 0, sixteenNine)!.Value;
        Assert.Equal((1f, 1f), (stretch.X, stretch.Y));
        // outside a 4:3 frame: black, whatever the fit
        int r43 = Array.FindIndex(CinemaVideo.Ratios, r => r.Name == "4:3");
        Assert.Null(CinemaVideo.MapScreen(0.1f, 0.5f, CinemaVideo.Fit.Stretch, r43, sixteenNine));
    }

    [Fact]
    public void A_picture_is_measured_both_ways()
    {
        // a 4:3 picture in 854x358: 477 columns and all rows lit; a 2.76 one: all columns, 309 rows
        int c0 = (854 - 477) / 2;
        Assert.Equal((477 / 854f, 1f), CinemaVideo.ContentSize(854, 358, (x, _) => x >= c0 && x < c0 + 477), new Tol());
        int r0 = (358 - 309) / 2;
        Assert.Equal((1f, 309 / 358f), CinemaVideo.ContentSize(854, 358, (_, y) => y >= r0 && y < r0 + 309), new Tol());
    }

    private sealed class Tol : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 0.01f && Math.Abs(a.Item2 - b.Item2) < 0.01f;
        public int GetHashCode((float, float) o) => 0;
    }

    [Fact]
    public void A_restart_leads_further_but_not_forever()
    {
        Assert.True(CinemaVideo.RestartLead(1) > CinemaVideo.StartLead);
        Assert.Equal(CinemaVideo.RestartLead(4), CinemaVideo.RestartLead(40));
    }
}
