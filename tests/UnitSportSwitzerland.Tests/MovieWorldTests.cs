using Godot;
using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The recorded world (#775): the ring of world events that always records, the track a grab
/// slices from it, and how the studio samples that track.
/// </summary>
public class MovieWorldTests
{
    private const double Dt = 1.0 / Channels.Rate;
    private static readonly WorldLook[] Looks = { new(), new() };

    private static WorldPose At(double e, double n = 1_200_000, double alt = 500) =>
        new() { X = e, Y = n, Z = alt, Rot = Quaternion.Identity, Scale = Vector3.One };

    private static WorldPose Local(float x) =>
        new() { X = x, Rot = Quaternion.Identity, Scale = Vector3.One };

    [Fact]
    public void ACarThatDrivesIsSampledBetweenTicksAndAParkedOneHolds()
    {
        var ring = new WorldRing(300);
        ring.Spawn(0, 1, -1, -1, At(2_600_000), true);         // a car
        ring.Spawn(0, 2, 1, 0, Local(0), true);                // its body
        ring.Spawn(0, 3, -1, -1, At(2_600_100), true);         // a parked car
        ring.Spawn(0, 4, 3, 1, Local(0), true);
        for (int k = 1; k <= 30; k++) ring.Move(k * Dt, 1, At(2_600_000 + k));
        var track = ring.Slice(double.NegativeInfinity, 2, Looks, out double start)!;

        Assert.Equal(0, start);
        Assert.Equal(2, track.Duration, 6);
        Assert.Equal(4, track.Entities.Length);
        var car = track.Entities[0];
        Assert.Equal(2_600_010.5, car.PoseAt(10.5 * Dt).X, 3);    // between two ticks: interpolated
        Assert.Equal(2_600_030, car.PoseAt(1.8).X, 3);            // stopped: holds where it stopped
        var parked = track.Entities[2];
        Assert.Single(parked.Times);                               // never moved: one frame
        Assert.Equal(2_600_100, parked.PoseAt(1.5).X, 3);
        Assert.Equal(0, track.Entities[1].Parent);                 // the body hangs from the car
        Assert.Equal(2, track.Entities[3].Parent);
    }

    [Fact]
    public void AnObjectThatRestsThenMovesHoldsUntilItMoves()
    {
        var ring = new WorldRing(300);
        ring.Spawn(0, 1, -1, -1, At(0), true);
        ring.Move(1.0, 1, At(5));            // a second of rest, then a jump of 5 m in one tick
        ring.Move(1.0 + Dt, 1, At(6));
        var e = ring.Slice(double.NegativeInfinity, 2, Looks, out _)!.Entities[0];
        Assert.Equal(0, e.PoseAt(0.5).X, 6);         // not dragged across the rest
        Assert.Equal(5.5, e.PoseAt(1.0 + Dt / 2).X, 6);
    }

    [Fact]
    public void WhatFallsOutOfTheWindowFoldsIntoTheStartingState()
    {
        var ring = new WorldRing(10);
        ring.Spawn(0, 1, -1, -1, At(0), true);
        ring.Spawn(0, 2, 1, 0, Local(0), true);
        ring.Spawn(1, 5, -1, -1, At(50), true);
        ring.Show(2, 2, false);                      // the body hidden at 2 s
        ring.Gone(3, 5);                             // the second object gone at 3 s
        for (int k = 0; k <= 20; k++) ring.Move(4 + k, 1, At(100 + k));   // moving to 24 s
        Assert.Equal(2, ring.BaseCount);             // the car and its body; the gone one dropped out

        var track = ring.Slice(double.NegativeInfinity, 24, Looks, out double start)!;
        Assert.Equal(14, start, 6);                  // the window, not the first event
        Assert.Equal(10, track.Duration, 6);
        Assert.Equal(2, track.Entities.Length);
        Assert.False(track.Entities[1].ShownAt(0));  // it was hidden before the window: still hidden
        Assert.Equal(110, track.Entities[0].PoseAt(0).X, 6);
        Assert.Equal(120, track.Entities[0].PoseAt(10).X, 6);
    }

    [Fact]
    public void AGrabTakesWhatCameAfterTheLastOneAndAGoneObjectEnds()
    {
        var ring = new WorldRing(300);
        ring.Spawn(0, 1, -1, -1, At(0), true);
        ring.Spawn(0, 2, 1, 1, Local(0), true);
        ring.Spawn(0, 3, -1, -1, At(9), true);
        ring.Look(4, 2, 0);
        ring.Gone(6, 3);
        var first = ring.Slice(double.NegativeInfinity, 5, Looks, out _)!;
        Assert.Equal(3, first.Entities.Length);

        var second = ring.Slice(5, 8, Looks, out double start)!;
        Assert.Equal(5, start, 6);
        Assert.Equal(3, second.Duration, 6);
        Assert.Equal(1, second.Entities[2].Died, 6);          // gone at 6 s = 1 s into this grab
        Assert.False(second.Entities[2].AliveAt(2));
        // only the look still used at the start is kept, renumbered from 0
        Assert.Single(second.Looks);
        Assert.Same(Looks[0], second.Looks[second.Entities[1].LookAt(1)]);
    }

    [Fact]
    public void TheEnvironmentKeepsItsKindAndItsHourGoesTheShortWayRoundMidnight()
    {
        var ring = new WorldRing(300);
        ring.Spawn(0, 1, WorldEntity.Environment, -1, WorldPose.Identity with { X = 23.99, Y = 0.2 }, false);
        ring.Move(Dt, 1, WorldPose.Identity with { X = 0.01, Y = 0.4 });
        ring.Spawn(0, 2, -1, -1, At(0), true);
        var track = ring.Slice(double.NegativeInfinity, 1, Looks, out _)!;
        var env = track.Entities[0];
        Assert.True(env.IsEnvironment);
        Assert.False(env.IsGroup);
        Assert.True(track.Entities[1].IsGroup);
        var (hour, sea, _) = env.EnvironmentAt(Dt / 2);
        Assert.True(hour > 23.99 || hour < 0.01, $"midnight is crossed the short way, got {hour}");
        Assert.Equal(0.3f, sea, 3);
        Assert.Equal(0.01, env.EnvironmentAt(0.5).Hour, 6);   // after the last change: holds
    }

    [Fact]
    public void NothingRecordedGivesNoTrack()
    {
        var ring = new WorldRing(300);
        Assert.Null(ring.Slice(double.NegativeInfinity, 10, Looks, out _));
        ring.Spawn(10, 1, -1, -1, At(0), true);
        Assert.Null(ring.Slice(double.NegativeInfinity, 10.1, Looks, out _));   // shorter than a clip may be
    }

    [Fact]
    public void WorldClipsPlayOnTheirOwnRowAndSurviveCompact()
    {
        var p = new MovieProject(Array.Empty<string>());
        var ring = new WorldRing(300);
        ring.Spawn(0, 1, -1, -1, At(0), true);
        var track = ring.Slice(double.NegativeInfinity, 4, Looks, out _)!;
        var clip = p.AddWorld(track, 2);
        Assert.True(p.HasWorld);
        Assert.Same(clip, p.ActiveWorld(3));
        Assert.Null(p.ActiveClip(0, 3));                       // never an actor's clip
        var right = p.Split(clip.Id, 4)!;
        Assert.True(right.World);
        Assert.Equal(6, p.Duration, 6);
        p.Compact();
        Assert.Single(p.Worlds);
        Assert.All(p.Clips, c => Assert.True(c.World && c.Track == 0));
    }
}
