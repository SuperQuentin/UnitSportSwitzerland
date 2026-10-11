using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The movie studio's sound (#656): the beat found in music, the ghost beats and kept markers on
/// the timeline, snapping to them, sound clips and the file that keeps them.
/// </summary>
public class MovieAudioTests
{
    private const int Rate = 11025;

    /// <summary>A click track: a short decaying noise burst on every beat, over quiet noise.</summary>
    private static float[] Clicks(double bpm, double seconds, double offset = 0.25, int seed = 1)
    {
        var random = new Random(seed);
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(random.NextDouble() - 0.5) * 0.02f;
        for (double t = offset; t < seconds; t += 60 / bpm)
        {
            int at = (int)(t * Rate);
            for (int k = 0; k < Rate / 40 && at + k < s.Length; k++)
                s[at + k] += (float)((random.NextDouble() - 0.5) * 1.6 * Math.Exp(-k / (Rate / 200.0)));
        }
        return s;
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(95.0)]
    [InlineData(140.0)]
    public void TheBeatOfAClickTrackIsFound(double bpm)
    {
        var grid = BeatDetector.Detect(Clicks(bpm, 20), Rate);
        Assert.InRange(grid.Bpm, bpm - 1.5, bpm + 1.5);
        double period = 60 / bpm;
        Assert.InRange(grid.Beats.Length, (int)(19 / period) - 2, (int)(20 / period) + 2);
        // every beat found lies on a click, within 25 ms
        foreach (double b in grid.Beats)
        {
            double phase = (b - 0.25) / period;
            double off = Math.Abs(phase - Math.Round(phase)) * period;
            Assert.True(off < 0.025, $"beat at {b:F3} s is {off * 1000:F0} ms off the clicks");
        }
    }

    [Fact]
    public void MusicIsRhythmicNoiseAndADroneAreNot()
    {
        Assert.True(BeatDetector.Detect(Clicks(120, 20), Rate).Confidence > BeatDetector.Rhythmic);
        var random = new Random(4);
        var noise = new float[20 * Rate];
        var drone = new float[20 * Rate];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(random.NextDouble() - 0.5);
            // an engine: a tone whose loudness swells slowly, a little hiss on it
            drone[i] = (float)(Math.Sin(i * 0.3) * (0.6 + 0.4 * Math.Sin(i * 0.0009)) + (random.NextDouble() - 0.5) * 0.1);
        }
        Assert.True(BeatDetector.Detect(noise, Rate).Confidence < BeatDetector.Rhythmic);
        Assert.True(BeatDetector.Detect(drone, Rate).Confidence < BeatDetector.Rhythmic);
    }

    [Fact]
    public void SilenceHasNoBeat()
    {
        Assert.Empty(BeatDetector.Detect(new float[Rate * 5], Rate).Beats);
        Assert.Empty(BeatDetector.Detect(new float[100], Rate).Beats);
    }

    [Fact]
    public void SamplesComeDownPeaksAndAWavFile()
    {
        var s = new float[44100];
        s[1000] = 0.5f;
        var low = Pcm.Decimate(s, 44100, 11025, out int rate);
        Assert.Equal(11025, rate);
        Assert.Equal(11025, low.Length);
        var peaks = Pcm.Peaks(s, 44100, 50);
        Assert.Equal(50, peaks.Length);
        Assert.Equal(127, peaks[1]);
        var wav = new MemoryStream();
        Pcm.WriteWav(wav, new short[] { 1, -1, 300 }, 22050);
        Assert.Equal(44 + 6, wav.Length);
    }

    [Fact]
    public void TheGameSoundRingKeepsTheLastSecondsInTime()
    {
        var ring = new SoundRing(2, 100);   // 2 s at 100 Hz
        var block = new float[50];
        for (int k = 0; k < 6; k++)
        {
            Array.Fill(block, k / 10f);
            ring.Append(block, 0.5 * (k + 1));   // half a second each, the last at 3 s
        }
        Assert.Equal(200, ring.Count);
        Assert.Equal(1.0, ring.Oldest, 9);
        var all = ring.Slice(double.NegativeInfinity, out double start);
        Assert.Equal(1.0, start, 9);
        Assert.Equal(200, all.Length);
        Assert.Equal((short)(0.2f * 32767f), all[0]);
        var late = ring.Slice(2.5, out start);
        Assert.Equal(2.5, start, 9);
        Assert.Equal(50, late.Length);
        Assert.Equal((short)(0.5f * 32767f), late[0]);
        ring.Clear();
        Assert.Empty(ring.Slice(0, out _));
    }

    private static MovieProject WithSong(out Clip song)
    {
        var p = new MovieProject(new[] { "RideKindId" });
        var asset = new AudioAsset { Name = "Song", File = "song.ogg", Duration = 10, Beat = new BeatGrid(120, new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 }) };
        song = p.AddAudio(p.AudioLaneFor("Music"), asset, 4);
        return p;
    }

    [Fact]
    public void SoundClipsAreClipsButNoActorPlaysThem()
    {
        var p = WithSong(out var song);
        Assert.True(song.Audio);
        Assert.Null(p.ActiveClip(0, 5));
        var right = p.Split(song.Id, 6)!;
        Assert.True(right.Audio);
        Assert.Equal(2, right.In, 9);
        p.TrimEnd(right.Id, 99);
        Assert.Equal(10, right.Out, 9);   // no further than the song
        Assert.Equal(0, p.AudioLaneFor("Music"));
    }

    [Fact]
    public void BeatsLandWhereTheClipPlaysThem()
    {
        var p = WithSong(out var song);
        p.TrimStart(song.Id, 5);   // the clip now plays the song from 1 s, at timeline 5
        var beats = p.Beats();
        Assert.Equal(new[] { 5.0, 5.5, 6.0, 6.5, 7.0 }, beats.Select(b => b.T).ToArray());
        // every fourth of the song's own beats (0.5 and 2.5 s) is a downbeat, wherever the clip starts
        Assert.False(beats[1].Downbeat);
        Assert.True(beats[3].Downbeat);
        Assert.Equal(5.5, p.NearestBeat(5.62, 0.2));
        Assert.Null(p.NearestBeat(5.8, 0.1));
    }

    [Fact]
    public void MarkersAreKeptOnceAndSnappedTo()
    {
        var p = WithSong(out var song);
        Assert.True(p.AddMarker(9));
        Assert.True(p.AddMarker(4.5));
        Assert.False(p.AddMarker(9.0004));
        Assert.Equal(new[] { 4.5, 9.0 }, p.Markers);
        // a dragged edge snaps to a marker, a beat or another clip's edge nearby, else stays
        Assert.Equal(9, p.Snap(9.05, 0.1));
        Assert.Equal(5.0, p.Snap(5.04, 0.1));
        Assert.Equal(7.3, p.Snap(7.3, 0.1));
        Assert.True(p.RemoveMarker(9.02, 0.05));
        Assert.False(p.RemoveMarker(9.02, 0.05));
        Assert.Equal(new[] { 4.5 }, p.Markers);
    }

    [Fact]
    public void SoundAndMarkersSurviveTheFileAndOldFilesStillLoad()
    {
        var p = WithSong(out var song);
        p.Audio[0].Peaks = new byte[] { 1, 2, 3 };
        p.AddMarker(4.5);
        p.Split(song.Id, 6);
        var bytes = new MemoryStream();
        MovieFile.Write(p, bytes);
        bytes.Position = 0;
        var q = MovieFile.Read(bytes, p.Discrete);
        Assert.Equal(new[] { "Music" }, q.AudioLanes);
        Assert.Equal("song.ogg", q.Audio[0].File);
        Assert.Equal(p.Audio[0].Beat.Beats, q.Audio[0].Beat.Beats);
        Assert.Equal(new byte[] { 1, 2, 3 }, q.Audio[0].Peaks);
        Assert.Equal(2, q.Clips.Count(c => c.Audio));
        Assert.Equal(new[] { 4.5 }, q.Markers);

        // a version 1 file: the same movie without its sound
        var old = new MemoryStream();
        MovieFile.Write(p, old, 1);
        old.Position = 0;
        var o = MovieFile.Read(old, p.Discrete);
        Assert.Empty(o.Clips);
        Assert.Empty(o.Audio);
    }

    [Fact]
    public void CompactKeepsTheSoundsInUse()
    {
        var p = WithSong(out var song);
        var other = p.AddAudio(p.AudioLaneFor("Game sound"), new AudioAsset { Name = "Game", File = "g.wav", Duration = 3, Game = true }, 0);
        p.Delete(song.Id);
        p.Compact();
        Assert.Single(p.Audio);
        Assert.Equal(new[] { "Game sound" }, p.AudioLanes);
        Assert.Equal(0, p.Clips[0].Lane);
        Assert.Equal(0, p.Clips[0].Track);
        Assert.Equal(other.Id, p.Clips[0].Id);
    }
}
