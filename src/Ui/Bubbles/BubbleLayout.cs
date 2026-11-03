using System;
using Godot;

namespace UnitSport.Ui.Bubbles;

/// <summary>One bubble to place: the speaker's head on screen and the bubble's size (its pop excluded).</summary>
/// <param name="Head">The speaker's head projected on screen, px; may be off the screen.</param>
/// <param name="Size">The body's size, px.</param>
public readonly record struct BubbleWant(Vector2 Head, Vector2 Size);

/// <summary>
/// Where a bubble goes: its body's box and the point its tail ends at (the head, or the frame's
/// edge toward it when <paramref name="Off"/>: the speaker is off the frame).
/// </summary>
public readonly record struct BubblePlaced(Rect2 Body, Vector2 Tip, bool Off = false)
{
    /// <summary>How far the tail reaches out of the body, px: longer tails are drawn under shorter ones.</summary>
    public float Reach => Mathf.Max(0f, Body.GetCenter().DistanceTo(Tip) - Body.Size.Length() * 0.5f);
}

/// <summary>
/// Places speech bubbles on screen (#862): each over its speaker's head with room for a tail,
/// kept wholly on screen, leaning away from the edge near one (so the tail comes out of the side
/// toward the speaker); with no room over the head, against the top with a shorter tail, else
/// beside the head, else under it; and never over another:
/// a bubble that would cover one placed before it climbs over it, else goes beside or under it.
/// Plain C#, tier-0 tested (<c>SpeechBubbleTests</c>).
/// </summary>
public static class BubbleLayout
{
    /// <summary>
    /// Places <paramref name="wants"/> in order (the first keeps its spot: pass the oldest first)
    /// inside <paramref name="screen"/>, <paramref name="margin"/> from its edges,
    /// <paramref name="gap"/> between bubbles, the tail <paramref name="tail"/> long when the
    /// bubble sits where it wants.
    /// </summary>
    public static void Place(ReadOnlySpan<BubbleWant> wants, Rect2 screen, float margin, float gap, float tail, Span<BubblePlaced> into)
    {
        var inner = screen.Grow(-margin);
        for (int i = 0; i < wants.Length; i++)
        {
            var w = wants[i];
            var tip = Clamp(w.Head, inner);
            // lean away from the nearer edge: at the left third the bubble grows to the right of the head
            float f = inner.Size.X > 0 ? (tip.X - inner.Position.X) / inner.Size.X : 0.5f;
            float lean = Mathf.Clamp((0.5f - f) * 1.6f, -0.42f, 0.42f);
            float x = tip.X - w.Size.X * 0.5f + w.Size.X * lean, over = tip.Y - tail - w.Size.Y;
            Vector2 at;
            if (over >= inner.Position.Y) at = new Vector2(x, over);
            // a speaker high in the frame: a shorter tail, the bubble against the top
            else if (tip.Y - tail * 0.35f - w.Size.Y >= inner.Position.Y) at = new Vector2(x, inner.Position.Y);
            else
            {
                // beside the head, on the side with more room, else under it, the tail up
                bool right = tip.X < inner.GetCenter().X;
                var beside = new Vector2(right ? tip.X + tail * 0.6f : tip.X - tail * 0.6f - w.Size.X,
                    Mathf.Max(inner.Position.Y, tip.Y - w.Size.Y * 0.6f));
                if (inner.Encloses(new Rect2(beside, w.Size))) at = beside;
                else if (tip.Y + tail + w.Size.Y <= inner.End.Y) at = new Vector2(x, tip.Y + tail);
                else at = new Vector2(x, over);
            }
            var box = Fit(new Rect2(at, w.Size), inner);
            box = Clear(box, into[..i], wants, inner, gap);
            into[i] = new BubblePlaced(box, tip, tip != w.Head);
        }
    }

    /// <summary>
    /// Moves <paramref name="box"/> off every bubble in <paramref name="placed"/>: over the one it
    /// hits, else beside it (right, then left), else under it; the move that stays on screen and
    /// clear of all of them and of every speaker's head, else the first that stays on screen.
    /// </summary>
    private static Rect2 Clear(Rect2 box, ReadOnlySpan<BubblePlaced> placed, ReadOnlySpan<BubbleWant> heads, Rect2 inner, float gap)
    {
        Span<Rect2> tries = stackalloc Rect2[4];
        for (int round = 0; round < 8; round++)
        {
            int hit = Hit(box, placed, gap);
            if (hit < 0) return box;
            var o = placed[hit].Body;
            tries[0] = box with { Position = new Vector2(box.Position.X, o.Position.Y - gap - box.Size.Y) };
            tries[1] = box with { Position = new Vector2(o.End.X + gap, box.Position.Y) };
            tries[2] = box with { Position = new Vector2(o.Position.X - gap - box.Size.X, box.Position.Y) };
            tries[3] = box with { Position = new Vector2(box.Position.X, o.End.Y + gap) };
            int pick = -1;
            for (int k = 0; k < tries.Length && pick < 0; k++)
                if (inner.Encloses(tries[k]) && Hit(tries[k], placed, gap) < 0 && !Covers(tries[k], heads, gap)) pick = k;
            for (int k = 0; k < tries.Length && pick < 0; k++)
                if (inner.Encloses(tries[k]) && Hit(tries[k], placed, gap) < 0) pick = k;
            // nothing clean: the first that stays on screen, and the next round moves on from there
            for (int k = 0; k < tries.Length && pick < 0; k++)
                if (inner.Encloses(tries[k])) pick = k;
            if (pick < 0) return box;
            box = tries[pick];
        }
        return box;
    }

    private static int Hit(Rect2 box, ReadOnlySpan<BubblePlaced> placed, float gap)
    {
        var grown = box.Grow(gap * 0.5f - 0.01f);
        for (int k = 0; k < placed.Length; k++)
            if (grown.Intersects(placed[k].Body.Grow(gap * 0.5f - 0.01f))) return k;
        return -1;
    }

    /// <summary>Whether <paramref name="box"/> hides a speaker's head.</summary>
    private static bool Covers(Rect2 box, ReadOnlySpan<BubbleWant> heads, float gap)
    {
        var grown = box.Grow(gap);
        foreach (var w in heads)
            if (grown.HasPoint(w.Head)) return true;
        return false;
    }

    /// <summary><paramref name="box"/> moved (not shrunk) inside <paramref name="inner"/>; a box larger than it is pinned to its top left.</summary>
    public static Rect2 Fit(Rect2 box, Rect2 inner)
    {
        var p = box.Position;
        p.X = Mathf.Max(inner.Position.X, Mathf.Min(p.X, inner.End.X - box.Size.X));
        p.Y = Mathf.Max(inner.Position.Y, Mathf.Min(p.Y, inner.End.Y - box.Size.Y));
        return box with { Position = p };
    }

    private static Vector2 Clamp(Vector2 p, Rect2 r) =>
        new(Mathf.Clamp(p.X, r.Position.X, r.End.X), Mathf.Clamp(p.Y, r.Position.Y, r.End.Y));
}
