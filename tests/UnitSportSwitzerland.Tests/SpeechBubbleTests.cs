using System;
using Godot;
using UnitSport.Ui.Bubbles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Comic speech bubbles' geometry and placement (src/Ui/Bubbles, linked in, #862).</summary>
public class SpeechBubbleTests
{
    private static readonly Rect2 Screen = new(0, 0, 1920, 1080);

    private static BubblePlaced[] Place(params BubbleWant[] wants)
    {
        var into = new BubblePlaced[wants.Length];
        BubbleLayout.Place(wants, Screen, 30, 15, 75, into);
        return into;
    }

    [Fact]
    public void A_bubble_sits_over_its_speakers_head_with_room_for_the_tail()
    {
        var p = Place(new BubbleWant(new Vector2(960, 600), new Vector2(300, 120)))[0];
        Assert.Equal(new Vector2(960, 600), p.Tip);
        Assert.Equal(600 - 75, p.Body.End.Y, 3);
        Assert.InRange(p.Body.GetCenter().X, 900, 1020);
        Assert.False(p.Off);
    }

    [Fact]
    public void Near_an_edge_the_bubble_leans_inward_and_stays_on_screen()
    {
        var right = Place(new BubbleWant(new Vector2(1880, 500), new Vector2(400, 150)))[0];
        Assert.True(Screen.Grow(-30).Encloses(right.Body));
        Assert.True(right.Body.GetCenter().X < 1880, "a speaker at the right edge gets a bubble to its left");
        var left = Place(new BubbleWant(new Vector2(60, 500), new Vector2(400, 150)))[0];
        Assert.True(left.Body.GetCenter().X > 60);
    }

    [Fact]
    public void A_head_high_in_the_frame_gets_its_bubble_beside_or_under_it()
    {
        var p = Place(new BubbleWant(new Vector2(500, 60), new Vector2(300, 120)))[0];
        Assert.True(Screen.Grow(-30).Encloses(p.Body));
        Assert.False(p.Body.Grow(1).HasPoint(p.Tip), "the bubble must not cover the head it points at");
    }

    [Fact]
    public void A_speaker_off_the_frame_gets_a_tail_to_the_edge()
    {
        var p = Place(new BubbleWant(new Vector2(2500, 400), new Vector2(300, 120)))[0];
        Assert.True(p.Off);
        Assert.Equal(1890, p.Tip.X, 3);
    }

    [Fact]
    public void Bubbles_said_together_never_overlap()
    {
        // four friends side by side, all talking: the same spot over each head would collide
        var wants = new[]
        {
            new BubbleWant(new Vector2(800, 500), new Vector2(360, 140)),
            new BubbleWant(new Vector2(900, 520), new Vector2(360, 140)),
            new BubbleWant(new Vector2(1000, 510), new Vector2(300, 200)),
            new BubbleWant(new Vector2(850, 480), new Vector2(240, 100)),
        };
        var p = Place(wants);
        for (int i = 0; i < p.Length; i++)
        {
            Assert.True(Screen.Encloses(p[i].Body));
            for (int k = 0; k < i; k++)
                Assert.False(p[i].Body.Intersects(p[k].Body), $"bubble {i} covers bubble {k}");
        }
        // the first keeps its spot over its head
        Assert.Equal(p[0].Body, Place(wants[0])[0].Body);
    }

    [Theory]
    [InlineData(BubbleShape.Say)]
    [InlineData(BubbleShape.Whisper)]
    [InlineData(BubbleShape.Shout)]
    [InlineData(BubbleShape.Think)]
    public void Every_body_holds_its_text(BubbleShape shape)
    {
        var text = new Vector2(280, 90);
        var size = BubbleGeometry.BodySize(shape, text, 13);
        var box = new Rect2(Vector2.Zero, size);
        var outline = new Vector2[BubbleGeometry.BodyPoints];
        int n = BubbleGeometry.Body(shape, box, 18, 1, outline);
        Assert.InRange(n, 20, BubbleGeometry.BodyPoints);
        var poly = outline.AsSpan(0, n).ToArray();
        var textBox = new Rect2((size - text) / 2, text);
        foreach (var corner in new[] { textBox.Position, textBox.End, new Vector2(textBox.Position.X, textBox.End.Y), new Vector2(textBox.End.X, textBox.Position.Y) })
            Assert.True(Inside(corner, poly), $"{shape}: the text's corner {corner} is outside the outline");
    }

    /// <summary>Even-odd point in polygon (Geometry2D needs the engine).</summary>
    private static bool Inside(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    [Fact]
    public void A_shout_spikes_past_its_box_and_two_shouts_differ()
    {
        var box = new Rect2(0, 0, 400, 160);
        var a = new Vector2[BubbleGeometry.BodyPoints];
        var b = new Vector2[BubbleGeometry.BodyPoints];
        int n = BubbleGeometry.Body(BubbleShape.Shout, box, 20, 1, a);
        BubbleGeometry.Body(BubbleShape.Shout, box, 20, 2, b);
        Assert.Equal(0, n % 2);
        float reach = 0;
        for (int i = 0; i < n; i++) reach = Math.Max(reach, Math.Max(-a[i].Y, a[i].Y - 160));
        Assert.InRange(reach, 10, 40);
        Assert.NotEqual(a[1], b[1]);
    }

    [Fact]
    public void The_tail_runs_from_inside_the_body_to_the_head()
    {
        var box = new Rect2(100, 100, 300, 120);
        var tail = new Vector2[BubbleGeometry.TailPoints];
        var head = new Vector2(260, 330);
        Assert.True(BubbleGeometry.Tail(BubbleShape.Say, box, head, 30, tail));
        Assert.Equal(head, tail[BubbleGeometry.TailPoints / 2]);
        // both ends of its base are inside the body's ellipse
        foreach (var p in new[] { tail[0], tail[^1] })
        {
            var d = (p - box.GetCenter()) / (box.Size / 2);
            Assert.True(d.LengthSquared() < 1f, $"tail base {p} outside the body");
        }
        // no tail to a head inside the bubble
        Assert.False(BubbleGeometry.Tail(BubbleShape.Say, box, box.GetCenter(), 30, tail));
    }

    [Fact]
    public void A_thought_trails_smaller_circles_toward_the_head()
    {
        var box = new Rect2(100, 100, 300, 120);
        var trail = new (Vector2 At, float R)[3];
        Assert.Equal(3, BubbleGeometry.Trail(box, new Vector2(250, 400), 20, trail));
        Assert.True(trail[0].R > trail[1].R && trail[1].R > trail[2].R);
        Assert.True(trail[2].At.Y > trail[0].At.Y);
    }

    [Fact]
    public void A_whisper_is_dashed_all_round()
    {
        var square = new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100) };
        var into = new Vector2[256];
        int n = BubbleGeometry.Dashes(square, 10, 5, into);
        Assert.Equal(0, n % 2);
        float drawn = 0;
        for (int i = 0; i < n; i += 2) drawn += into[i].DistanceTo(into[i + 1]);
        // two thirds of the 400 px rim, give or take the last dash
        Assert.InRange(drawn, 255, 280);
    }

    [Fact]
    public void Text_types_in_at_a_readable_pace_and_the_bubble_pops()
    {
        Assert.Equal(0, BubbleGeometry.Typed(20, -0.1, BubbleShape.Say));
        Assert.Equal(1, BubbleGeometry.Typed(20, 0, BubbleShape.Say));
        Assert.Equal(20, BubbleGeometry.Typed(20, 1.0, BubbleShape.Say));
        Assert.True(BubbleGeometry.Typed(30, 0.3, BubbleShape.Shout) > BubbleGeometry.Typed(30, 0.3, BubbleShape.Whisper));
        Assert.True(BubbleGeometry.Pop(0.16) > 1.05f);
        Assert.Equal(1f, BubbleGeometry.Pop(0.5));
        Assert.Equal(0f, BubbleGeometry.Fade(0, 3));
        Assert.Equal(1f, BubbleGeometry.Fade(1, 3));
        Assert.InRange(BubbleGeometry.ReadingTime(40, BubbleShape.Say), 4, 6);
        Assert.Equal(8, BubbleGeometry.ReadingTime(500, BubbleShape.Say));
    }
}
