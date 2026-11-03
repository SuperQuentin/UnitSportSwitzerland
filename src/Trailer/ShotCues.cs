using System;
using System.Collections.Generic;

namespace UnitSport.Trailer;

/// <summary>
/// A shot's English lines as subtitle cues (#862): its captions (and their second lines), its
/// speech bubbles and its chat lines, each with its id and the frames it shows on at
/// <c>fps</c>, from the shot's first frame. The director writes them beside each filmed shot
/// (<c>shotNN.cues.json</c>); <c>--trailer-lines</c> lists them for the translations file.
/// </summary>
public static class ShotCues
{
    /// <summary>Frames the shot has at <paramref name="fps"/>: from the song's clock, as the recorder films it.</summary>
    public static int Frames(Shot shot, int fps) =>
        (int)Math.Round(shot.End * fps) - (int)Math.Round(shot.Start * fps);

    public static List<Cue> Of(Shot shot, int fps)
    {
        int frames = Frames(shot, fps);
        int F(double t) => Math.Clamp((int)Math.Round(t * fps), 0, frames);
        var cues = new List<Cue>();
        foreach (var c in shot.Captions)
        {
            if (c.Text.Length > 0)
                cues.Add(new Cue(c.Id ?? SubtitleTrack.Slug(c.Text), Cue.Caption, c.Text, F(c.T), F(c.T + c.Seconds)));
            if (!string.IsNullOrEmpty(c.Sub))
                cues.Add(new Cue(c.SubId ?? SubtitleTrack.Slug(c.Sub), Cue.Caption, c.Sub, F(c.T), F(c.T + c.Seconds)));
        }
        foreach (var b in shot.Bubbles)
            cues.Add(new Cue(b.Id ?? SubtitleTrack.Slug(b.Text), Cue.Bubble, b.Text, F(b.T), F(b.Until(shot.Length))));
        // a chat line stays up to the end of its shot
        foreach (var l in shot.Chat)
            cues.Add(new Cue(l.Id ?? SubtitleTrack.Slug(l.Text), Cue.Speech, l.Text, F(l.T), frames));
        cues.RemoveAll(c => c.To <= c.From);
        return cues;
    }
}
