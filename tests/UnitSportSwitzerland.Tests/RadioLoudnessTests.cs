using UnitSport.Items;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A radio's own volume to gain, reach and how hard it moves things (src/Items/RadioLoudness.cs, linked in, #734).</summary>
public class RadioLoudnessTests
{
    [Fact]
    public void The_default_keeps_the_old_loudness_and_about_its_reach()
    {
        Assert.Equal(0f, RadioLoudness.Db(RadioLoudness.Default), 3);
        Assert.InRange(RadioLoudness.Radius(RadioLoudness.Default), 38f, 45f);
    }

    [Fact]
    public void Louder_reaches_farther_and_garbage_is_the_default()
    {
        Assert.Equal(RadioLoudness.MinRadius, RadioLoudness.Radius(0f));
        Assert.Equal(RadioLoudness.MaxRadius, RadioLoudness.Radius(1f));
        Assert.True(RadioLoudness.Db(1f) > 0f && RadioLoudness.Db(0f) < -15f);
        Assert.Equal(RadioLoudness.Default, RadioLoudness.Clamp(float.NaN));
        Assert.Equal(1f, RadioLoudness.Clamp(7f));
    }

    [Fact]
    public void Reach_is_full_near_fades_out_and_is_gone_at_the_edge()
    {
        Assert.Equal(1f, RadioLoudness.Reach(2f, 1f));
        float mid = RadioLoudness.Reach(40f, 1f);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.Equal(0f, RadioLoudness.Reach(60f, 1f));
        Assert.Equal(0f, RadioLoudness.Reach(20f, 0f));   // turned right down: 12 m
        Assert.True(RadioLoudness.Reach(20f, 1f) > RadioLoudness.Reach(20f, 0.5f));
    }
}
