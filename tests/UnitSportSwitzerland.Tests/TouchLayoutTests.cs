using Godot;
using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The phone's controls per mode (#1043, docs/notes/core/touch-controls.md): laid out on phones from
/// 16:9 to 22:9 and on a tablet, at every size setting and both hands, no two controls overlap,
/// none leaves the screen, and the right thumb's controls stay out of the left stick's zone.
/// </summary>
public class TouchLayoutTests
{
    /// <summary>Screens in millimetres, landscape: small and large phones, a tablet.</summary>
    private static readonly Vector2[] Screens =
    {
        new(62f * 16 / 9, 62f), new(68f * 16 / 9, 68f), new(68f * 20 / 9, 68f), new(75f * 22 / 9, 75f), new(200f, 150f),
    };

    private static readonly float[] Scales = { 0.75f, 1f, TouchLayout.MaxScale };

    /// <summary>Every mode with each flag that adds controls to it.</summary>
    public static IEnumerable<object[]> Layouts()
    {
        foreach (var mode in Enum.GetValues<TouchMode>())
            foreach (var flags in new[]
                     {
                         TouchFlags.None, TouchFlags.ItemInHand, TouchFlags.ItemInHand | TouchFlags.RunButton,
                         TouchFlags.ItemInHand | TouchFlags.TapToUse, TouchFlags.Gears | TouchFlags.Clutch,
                         TouchFlags.Gears | TouchFlags.WorkMode, TouchFlags.Armed, TouchFlags.Airliner, TouchFlags.Ufo,
                         TouchFlags.Pigeon, TouchFlags.Wingsuit, TouchFlags.RunButton,
                     })
                yield return new object[] { mode, flags };
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ControlsNeverOverlapAndStayOnScreen(TouchMode mode, TouchFlags flags)
    {
        var keys = TouchLayout.For(mode, flags);
        foreach (var screen in Screens)
            foreach (var scale in Scales)
                foreach (bool left in new[] { false, true })
                {
                    var safe = new Rect2(Vector2.Zero, screen);
                    var rects = keys.Select(k => TouchLayout.Place(k, safe, 1f, scale, left)).ToList();
                    string where = $"{mode} {flags} on {screen.X:0}×{screen.Y:0} mm at {scale:0.##}{(left ? ", left-handed" : "")}";
                    for (int i = 0; i < keys.Count; i++)
                    {
                        Assert.True(safe.Encloses(rects[i]), $"{keys[i].Id} leaves the screen: {where}");
                        for (int j = i + 1; j < keys.Count; j++)
                            Assert.False(TouchLayout.Overlap(keys[i], rects[i], keys[j], rects[j], gap: 1f),
                                $"{keys[i].Id} and {keys[j].Id} overlap: {where}");
                    }
                }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void TheRightThumbKeepsOutOfTheStickZone(TouchMode mode, TouchFlags flags)
    {
        // the stick takes the lower 70 % of the left 42 %; at the default size and below a phone's
        // buttons must leave it all to the left thumb
        foreach (var screen in Screens.Take(4))
            foreach (var scale in new[] { 0.75f, 1f })
            {
                var safe = new Rect2(Vector2.Zero, screen);
                var zone = new Rect2(0, screen.Y * 0.3f, screen.X * 0.42f, screen.Y * 0.7f);
                foreach (var key in TouchLayout.For(mode, flags))
                {
                    var r = TouchLayout.Place(key, safe, 1f, scale, leftHanded: false);
                    Assert.False(zone.Intersects(r), $"{key.Id} reaches into the stick zone: {mode} {flags} on {screen.X:0} mm at {scale:0.##}");
                }
            }
    }

    [Theory]
    [InlineData(TouchFlags.None)]
    [InlineData(TouchFlags.ItemInHand)]
    [InlineData(TouchFlags.ItemInHand | TouchFlags.RunButton)]
    public void TheHotbarStaysClearOfTheButtonsOnFoot(TouchFlags flags)
    {
        var keys = TouchLayout.For(TouchMode.Foot, flags);
        foreach (var screen in Screens)
            foreach (var scale in Scales)
                foreach (bool left in new[] { false, true })
                {
                    var safe = new Rect2(Vector2.Zero, screen);
                    var bar = TouchLayout.Hotbar(safe, 1f, scale, left, 6);
                    string where = $"{flags} on {screen.X:0}×{screen.Y:0} mm at {scale:0.##}{(left ? ", left-handed" : "")}";
                    Assert.True(safe.Encloses(bar), $"the hotbar leaves the screen: {where}");
                    foreach (var key in keys)
                        Assert.False(bar.Grow(0.5f).Intersects(TouchLayout.Place(key, safe, 1f, scale, left)), $"the hotbar runs under {key.Id}: {where}");
                }
    }

    [Fact]
    public void EveryModeHasPauseAndMore()
    {
        foreach (var mode in Enum.GetValues<TouchMode>())
        {
            var ids = TouchLayout.For(mode, TouchFlags.None).Select(k => k.Id).ToList();
            Assert.Contains("pause", ids);
            Assert.Contains("more", ids);
        }
    }

    [Fact]
    public void AnItemInHandAddsUseAndAimUnlessTapsUseIt()
    {
        var hand = TouchLayout.For(TouchMode.Foot, TouchFlags.ItemInHand).Select(k => k.Id).ToList();
        Assert.Contains("use", hand);
        Assert.Contains("aim", hand);
        var tap = TouchLayout.For(TouchMode.Foot, TouchFlags.ItemInHand | TouchFlags.TapToUse).Select(k => k.Id).ToList();
        Assert.DoesNotContain("use", tap);
        Assert.Contains("aim", tap);
        Assert.DoesNotContain("use", TouchLayout.For(TouchMode.Foot, TouchFlags.None).Select(k => k.Id));
    }

    [Fact]
    public void ControlsAreFingertipSized()
    {
        // 9 mm is the smallest a fingertip hits reliably; the top bar is a stretch on purpose and
        // gets a 2 mm margin round its 6 mm
        foreach (var mode in Enum.GetValues<TouchMode>())
            foreach (var key in TouchLayout.For(mode, TouchFlags.ItemInHand | TouchFlags.Gears | TouchFlags.Clutch | TouchFlags.Armed))
            {
                float smallest = Math.Min(key.SizeMm.X, key.SizeMm.Y) + 2 * TouchLayout.HitMarginMm;
                Assert.True(smallest >= 9.5f, $"{mode} {key.Id}: {smallest:0.#} mm with its margin");
            }
    }

    [Fact]
    public void LeftHandedMirrorsTheControls()
    {
        var safe = new Rect2(0, 0, 150, 68);
        foreach (var key in TouchLayout.For(TouchMode.Drive, TouchFlags.Gears))
        {
            var right = TouchLayout.Place(key, safe, 1f, 1f, leftHanded: false);
            var left = TouchLayout.Place(key, safe, 1f, 1f, leftHanded: true);
            Assert.Equal(150 - right.GetCenter().X, left.GetCenter().X, 3);
            Assert.Equal(right.GetCenter().Y, left.GetCenter().Y, 3);
        }
    }
}
