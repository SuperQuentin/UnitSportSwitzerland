using System;
using System.Collections.Generic;
using Godot;

namespace UnitSport.Ui.Bubbles;

/// <summary>How a comic speech bubble is drawn (#862).</summary>
public enum BubbleShape
{
    /// <summary>Spoken: a round white bubble, a curved tail to the speaker.</summary>
    Say,
    /// <summary>Shouted: a spiky outline, a zigzag tail, bigger lettering.</summary>
    Shout,
    /// <summary>Whispered: the round bubble with a dashed outline, smaller lettering.</summary>
    Whisper,
    /// <summary>Thought: a cloud, little circles trailing to the head instead of a tail.</summary>
    Think,
}

/// <summary>
/// The pure geometry of a comic speech bubble (#862): its body's outline for each shape, the tail
/// to the speaker, the thought bubble's trail of circles, a dashed outline, and the timing of its
/// pop and of the text typing in. Plain C# over Godot's value types, tier-0 tested
/// (<c>SpeechBubbleTests</c>); <see cref="SpeechBubble"/> draws it, the films' captions and the
/// in-game chat bubbles use it.
/// </summary>
public static class BubbleGeometry
{
    /// <summary>Points on a body's outline.</summary>
    public const int BodyPoints = 96;
    /// <summary>Points of a tail's polygon: base left, the curve to the tip and back, base right.</summary>
    public const int TailPoints = 2 * TailSide + 1;
    private const int TailSide = 8;

    /// <summary>
    /// The body's box round a text block of <paramref name="text"/> pixels: the round shapes need
    /// room in their corners (a superellipse of power 3 holds a box 1/1.26 of its size, an ellipse
    /// 1/1.41), plus a margin of <paramref name="pad"/>.
    /// </summary>
    public static Vector2 BodySize(BubbleShape shape, Vector2 text, float pad) => shape switch
    {
        BubbleShape.Shout => text * 1.30f + new Vector2(pad * 2.6f, pad * 2.0f),
        BubbleShape.Think => text * 1.32f + new Vector2(pad * 1.2f, pad * 1.0f),
        _ => text * 1.12f + new Vector2(pad * 2.4f, pad * 1.8f),
    };

    /// <summary>
    /// The body's outline, <see cref="BodyPoints"/> points round <paramref name="box"/>, into
    /// <paramref name="into"/> (cleared): a superellipse for a word said or whispered, a burst of
    /// spikes for a shout (its spikes reach past the box by about <paramref name="spike"/>), a cloud
    /// of bumps for a thought (each bulging about 0.7 of its half width past the box). <paramref name="seed"/> varies the spikes so two shouts differ.
    /// Returns how many points were written (<see cref="BodyPoints"/>, fewer for a small shout:
    /// a spike about every 2.2 spike lengths of rim). <paramref name="into"/> holds at least
    /// <see cref="BodyPoints"/>.
    /// </summary>
    public static int Body(BubbleShape shape, Rect2 box, float spike, int seed, Span<Vector2> into)
    {
        var c = box.GetCenter();
        float a = box.Size.X * 0.5f, b = box.Size.Y * 0.5f;
        int n = BodyPoints;
        switch (shape)
        {
            case BubbleShape.Shout:
            {
                // spikes in pairs of points: on the ellipse, then out along its normal by about a spike
                float rim = Mathf.Pi * (3f * (a + b) - Mathf.Sqrt((3f * a + b) * (a + 3f * b)));
                n = 2 * Math.Clamp((int)(rim / Mathf.Max(spike * 2.4f, 1f)), 10, BodyPoints / 2);
                uint s = (uint)seed * 2654435761u + 12345u;
                for (int i = 0; i < n; i++)
                {
                    float t = Mathf.Tau * (i + 0.5f) / n;
                    float cs = Mathf.Cos(t), sn = Mathf.Sin(t);
                    s = s * 1664525u + 1013904223u;
                    float jitter = (s >> 8) / 16777216f;
                    var onRim = c + new Vector2(cs * a, sn * b);
                    var normal = new Vector2(cs / a, sn / b).Normalized();
                    into[i] = i % 2 == 1
                        ? onRim + normal * spike * (0.75f + 0.9f * jitter)
                        : onRim - normal * spike * 0.15f * jitter;
                }
                break;
            }
            case BubbleShape.Think:
            {
                // lobes: each a flattened half circle bulging out over a chord of the ellipse,
                // about two and a half spike lengths across
                float rim = Mathf.Pi * (3f * (a + b) - Mathf.Sqrt((3f * a + b) * (a + 3f * b)));
                int lobes = Math.Clamp((int)(rim / Mathf.Max(spike * 4.5f, 1f)), 7, 16);
                int per = BodyPoints / lobes;
                n = lobes * per;
                for (int l = 0; l < lobes; l++)
                {
                    float t0 = Mathf.Tau * l / lobes, t1 = Mathf.Tau * (l + 1) / lobes;
                    var p0 = c + new Vector2(Mathf.Cos(t0) * a, Mathf.Sin(t0) * b);
                    var p1 = c + new Vector2(Mathf.Cos(t1) * a, Mathf.Sin(t1) * b);
                    var mid = (p0 + p1) * 0.5f;
                    var half = p0 - mid;
                    // the normal that points away from the centre
                    var outward = new Vector2(-half.Y, half.X);
                    if (outward.Dot(mid - c) < 0) outward = -outward;
                    for (int k = 0; k < per; k++)
                    {
                        float phi = Mathf.Pi * k / per;
                        into[l * per + k] = mid + half * Mathf.Cos(phi) + outward * (Mathf.Sin(phi) * 0.7f);
                    }
                }
                break;
            }
            default:
            {
                // a superellipse of power 3: rounder than a box, fuller than an ellipse
                for (int i = 0; i < n; i++)
                {
                    float t = Mathf.Tau * i / n;
                    float cs = Mathf.Cos(t), sn = Mathf.Sin(t);
                    into[i] = c + new Vector2(Mathf.Sign(cs) * Mathf.Pow(Mathf.Abs(cs), 2f / 3f) * a,
                        Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), 2f / 3f) * b);
                }
                break;
            }
        }
        return n;
    }

    /// <summary>
    /// Where the body's outline meets the ray from its centre toward <paramref name="toward"/>
    /// (the ellipse of <paramref name="box"/>), pulled in by <paramref name="inset"/> of the way so
    /// a tail's base sits inside the body and its fill covers the body's outline there.
    /// </summary>
    public static Vector2 Rim(Rect2 box, Vector2 toward, float inset = 0.15f)
    {
        var c = box.GetCenter();
        var d = toward - c;
        if (d.LengthSquared() < 1e-6f) return c;
        float a = box.Size.X * 0.5f, b = box.Size.Y * 0.5f;
        // the ellipse (x/a)² + (y/b)² = 1 along d
        float k = 1f / Mathf.Sqrt(d.X * d.X / (a * a) + d.Y * d.Y / (b * b));
        return c + d * k * (1f - inset);
    }

    /// <summary>
    /// A tail from the body round <paramref name="box"/> to <paramref name="tip"/>,
    /// <see cref="TailPoints"/> points into <paramref name="into"/>: a curved wedge
    /// <paramref name="width"/> wide at its base (a zigzag for a shout). False when the tip is
    /// inside the body (nothing to point at).
    /// </summary>
    public static bool Tail(BubbleShape shape, Rect2 box, Vector2 tip, float width, Span<Vector2> into)
    {
        var c = box.GetCenter();
        float a = box.Size.X * 0.5f, b = box.Size.Y * 0.5f;
        var rel = tip - c;
        if (rel.X * rel.X / (a * a) + rel.Y * rel.Y / (b * b) <= 1.05f) return false;
        // the base: on the rim toward the tip, but leaning toward the body's bottom (or top) middle,
        // where a comic artist puts it, so a tip far to the side still gets a tail that hangs
        var hang = new Vector2(c.X + Mathf.Clamp(rel.X, -a * 0.45f, a * 0.45f), tip.Y);
        var root = Rim(box, hang, 0.18f);
        var along = tip - root;
        float len = along.Length();
        var dir = along / len;
        var side = new Vector2(-dir.Y, dir.X);
        // the narrow end bends a little toward the body's middle, like a brush stroke
        float bend = Mathf.Sign(side.Dot(c - root)) * len * 0.12f;
        float w = Mathf.Min(width, len * 0.8f);
        var left = root + side * (w * 0.5f);
        var right = root - side * (w * 0.5f);
        into[0] = left;
        for (int k = 1; k <= TailSide; k++)
        {
            float t = (float)k / TailSide;
            into[k] = Curve(left, tip, side * bend, t, shape == BubbleShape.Shout ? k : 0, side * w * 0.35f);
        }
        for (int k = 1; k <= TailSide; k++)
        {
            float t = 1f - (float)k / TailSide;
            into[TailSide + k] = Curve(right, tip, side * bend, t, shape == BubbleShape.Shout ? TailSide - k : 0, side * w * 0.35f);
        }
        return true;
    }

    /// <summary>A point of one side of a tail: a bowed line from base to tip, zigzagged for a shout.</summary>
    private static Vector2 Curve(Vector2 from, Vector2 tip, Vector2 bow, float t, int zig, Vector2 zag)
    {
        var p = from.Lerp(tip, t) + bow * (4f * t * (1f - t));
        if (zig > 0 && zig < TailSide) p += zag * ((zig % 2 == 0 ? 1f : -1f) * (1f - t));
        return p;
    }

    /// <summary>
    /// A thought's trail: three circles from the body's rim to <paramref name="tip"/>, smaller and
    /// smaller (centre and radius); empty when the tip is inside the body.
    /// </summary>
    public static int Trail(Rect2 box, Vector2 tip, float size, Span<(Vector2 At, float R)> into)
    {
        var c = box.GetCenter();
        float a = box.Size.X * 0.5f, b = box.Size.Y * 0.5f;
        var rel = tip - c;
        if (rel.X * rel.X / (a * a) + rel.Y * rel.Y / (b * b) <= 1.05f) return 0;
        var rim = Rim(box, tip, -0.12f);
        int n = Math.Min(3, into.Length);
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.6f) / (n + 0.4f);
            into[i] = (rim.Lerp(tip, t), size * (0.62f - 0.17f * i));
        }
        return n;
    }

    /// <summary>
    /// A dashed closed outline: segments (pairs of points, for <c>DrawMultiline</c>) along the loop
    /// <paramref name="loop"/>, <paramref name="dash"/> drawn and <paramref name="gap"/> left out,
    /// carried on from one edge to the next. Returns how many points were written.
    /// </summary>
    public static int Dashes(ReadOnlySpan<Vector2> loop, float dash, float gap, Span<Vector2> into)
    {
        int w = 0;
        float phase = 0f;   // distance into the current dash (+) or gap (−)
        bool on = true;
        for (int i = 0; i < loop.Length && w + 1 < into.Length; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Length];
            float edge = p.DistanceTo(q), at = 0f;
            while (at < edge && w + 1 < into.Length)
            {
                float left = (on ? dash : gap) - phase;
                float step = Mathf.Min(left, edge - at);
                if (on)
                {
                    into[w++] = p.Lerp(q, at / edge);
                    into[w++] = p.Lerp(q, (at + step) / edge);
                }
                at += step;
                phase += step;
                if (phase >= (on ? dash : gap) - 1e-4f)
                {
                    phase = 0f;
                    on = !on;
                }
            }
        }
        return w;
    }

    /// <summary>
    /// The pop as a bubble appears: from 60 % to 108 % of its size in 0.16 s, settling to 100 %
    /// by 0.3 s.
    /// </summary>
    public static float Pop(double age)
    {
        if (age <= 0) return 0.6f;
        if (age < 0.16) return Mathf.Lerp(0.6f, 1.08f, Mathf.SmoothStep(0f, 1f, (float)(age / 0.16)));
        if (age < 0.3) return Mathf.Lerp(1.08f, 1f, Mathf.SmoothStep(0f, 1f, (float)((age - 0.16) / 0.14)));
        return 1f;
    }

    /// <summary>How opaque a bubble is: in over 0.1 s, out over its last 0.2 s.</summary>
    public static float Fade(double age, double seconds) =>
        Mathf.Clamp((float)Math.Min(age / 0.1, (seconds - age) / 0.2), 0f, 1f);

    /// <summary>Letters a second the text types in at: readable, a shout faster, a whisper slower.</summary>
    public static double LettersPerSecond(BubbleShape shape) => shape switch
    {
        BubbleShape.Shout => 45,
        BubbleShape.Whisper => 24,
        BubbleShape.Think => 28,
        _ => 32,
    };

    /// <summary>Letters of <paramref name="letters"/> shown <paramref name="age"/> s after the bubble appeared.</summary>
    public static int Typed(int letters, double age, BubbleShape shape) =>
        age < 0 ? 0 : Math.Min(letters, (int)(age * LettersPerSecond(shape)) + 1);

    /// <summary>
    /// How long a line stays up when nobody says: long enough to type and then read it
    /// (2 s, plus a second per 15 letters), at most 8 s.
    /// </summary>
    public static double ReadingTime(int letters, BubbleShape shape) =>
        Math.Min(8.0, letters / LettersPerSecond(shape) + 2.0 + letters / 15.0);
}
