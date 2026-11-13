using System;
using System.IO;
using System.Linq;
using UnitSport.Styles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Zoom-aware detail (#1433, src/Styles/ZoomRule.cs, linked in).</summary>
public class ZoomRuleTests
{
    /// <summary>The camera item's vertical field of view at a focal length (docs/notes/items/camera-zoom.md).</summary>
    private static float Lens(float mm) => 2f * System.MathF.Atan(12f / mm) * 180f / System.MathF.PI;

    [Fact]
    public void The_walking_view_is_zoom_one_and_wider_views_never_lose_detail()
    {
        Assert.Equal(1f, ZoomRule.Of(ZoomRule.ReferenceFov), 4);
        Assert.Equal(1f, ZoomRule.Of(75f));
        Assert.Equal(1f, ZoomRule.Of(0f));
        Assert.Equal(1f, ZoomRule.Of(float.NaN));
    }

    [Fact]
    public void The_camera_lens_zooms_by_the_ratio_of_tangents()
    {
        Assert.InRange(ZoomRule.Of(Lens(35f)), 1.95f, 1.98f);   // 38 degrees
        Assert.InRange(ZoomRule.Of(Lens(200f)), 11.1f, 11.3f);  // 6.87 degrees
        Assert.InRange(ZoomRule.Of(9f), 8.5f, 8.7f);            // the binoculars
        Assert.InRange(ZoomRule.Of(0.0688f), 1000f, ZoomRule.MaxZoom);   // the telescope at 20,000 mm still follows
        Assert.Equal(ZoomRule.MaxZoom, ZoomRule.Of(0.00275f));  // at 500,000 mm (#1813) the detail stops there
    }

    [Fact]
    public void A_step_never_exceeds_the_zoom_and_holds_on_a_boundary()
    {
        for (float z = 1f; z < 500f; z *= 1.07f)
        {
            int k = ZoomRule.StepFor(z, 0);
            Assert.True(ZoomRule.Step(k) <= z * 1.0001f, $"step {ZoomRule.Step(k)} over zoom {z}");
            Assert.True(ZoomRule.Step(k + 1) > z, $"step {k} too low for {z}");
        }
        Assert.Equal(0, ZoomRule.StepFor(1f, 0));
        Assert.Equal(1, ZoomRule.StepFor(1.25f, 0));
        // just under step 1 coming from it: held; 5 % under: dropped
        Assert.Equal(1, ZoomRule.StepFor(1.22f, 1));
        Assert.Equal(0, ZoomRule.StepFor(1.18f, 1));
    }

    [Fact]
    public void A_zoomed_tile_takes_the_ring_it_looks_like_but_never_ring_zero()
    {
        Assert.Equal(0, ZoomRule.Ring(0, 11f));
        Assert.Equal(1, ZoomRule.Ring(1, 11f));
        Assert.Equal(5, ZoomRule.Ring(5, 1f));
        Assert.Equal(2, ZoomRule.Ring(4, 1.95f));
        Assert.Equal(1, ZoomRule.Ring(15, 11f));
        Assert.Equal(3, ZoomRule.Ring(15, 4.77f));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Files that set a visibility range Godot measures by distance alone, and must hand the node to
    /// <c>ZoomRanges</c> (src/Styles/LodScale.cs) so a zoomed camera rescales it. Exempt: the
    /// network-interest fade (#689) and the movie recorder, which only reads ranges.
    /// </summary>
    [Fact]
    public void Every_visibility_range_is_tracked_for_the_zoom()
    {
        string[] exempt = { "VehicleBody.cs", "WorldRecorder.cs", "LodScale.cs" };
        string src = Path.Combine(Root(), "src");
        var untracked = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !exempt.Contains(Path.GetFileName(f)))
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                return System.Text.RegularExpressions.Regex.IsMatch(text, @"VisibilityRange(Begin|End)\s*=")
                    && !text.Contains("ZoomRanges.Track");
            })
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.True(untracked.Count == 0, "set a visibility range without ZoomRanges.Track/TrackTree: " + string.Join(", ", untracked));
    }
}
