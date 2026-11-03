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
            foreach (var flags in TouchLayout.FlagSets)
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
        // buttons must leave it all to the left thumb (a mode without the stick may use it)
        if (!TouchLayout.HasStick(mode)) return;
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
    public void PinballsFlippersKeepTheirSidesForEitherHand()
    {
        // the left flipper is under the left thumb even for the left-handed: the table's sides are fixed
        var keys = TouchLayout.For(TouchMode.Pinball, TouchFlags.None);
        Assert.False(TouchLayout.HasStick(TouchMode.Pinball));
        var safe = new Rect2(Vector2.Zero, Screens[1]);
        foreach (bool left in new[] { false, true })
        {
            var l = TouchLayout.Place(keys.First(k => k.Id == "lflip"), safe, 1f, 1f, left).GetCenter();
            var r = TouchLayout.Place(keys.First(k => k.Id == "rflip"), safe, 1f, 1f, left).GetCenter();
            Assert.True(l.X < safe.Size.X / 2 && r.X > safe.Size.X / 2, $"flippers swapped{(left ? ", left-handed" : "")}");
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
    public void AModesOwnOverrideBeatsTheSharedOneAndHiddenKeysGo()
    {
        var foot = TouchLayout.For(TouchMode.Foot, TouchFlags.None);
        var jump = foot.First(k => k.Id == "jump");
        var o = new Dictionary<string, TouchOverride>
        {
            [TouchLayout.SharedKey(jump)] = new() { X = 20, Y = 20, Size = 1.2f },
            [TouchLayout.ModeKey(TouchMode.Fight, jump)] = new() { X = 30, Y = 30 },
            [TouchLayout.SharedKey(foot.First(k => k.Id == "crouch"))] = new() { Hidden = true },
        };
        var applied = TouchLayout.Apply(foot, TouchMode.Foot, o);
        var moved = applied.First(k => k.Id == "jump");
        Assert.Equal(new Vector2(20, 20), moved.Mm);
        Assert.Equal(jump.SizeMm.X * 1.2f, moved.SizeMm.X, 3);
        Assert.DoesNotContain(applied, k => k.Id == "crouch");
        // the shared move reaches the fist fight's Jump too, unless the fight has its own
        var fight = TouchLayout.For(TouchMode.Fight, TouchFlags.None);
        Assert.Equal(new Vector2(30, 30), TouchLayout.Apply(fight, TouchMode.Fight, o).First(k => k.Id == "jump").Mm);
        // a size out of range is held to it
        o[TouchLayout.SharedKey(jump)].Size = 9;
        Assert.Equal(jump.SizeMm.X * TouchLayout.MaxOverrideSize, TouchLayout.Apply(foot, TouchMode.Foot, o).First(k => k.Id == "jump").SizeMm.X, 3);
    }

    [Fact]
    public void ConflictNamesAMoveThatCoversAControl()
    {
        var safe = new Rect2(0, 0, 151, 68);
        var foot = TouchLayout.For(TouchMode.Foot, TouchFlags.ItemInHand);
        var jump = foot.First(k => k.Id == "jump");
        var none = new Dictionary<string, TouchOverride>();
        Assert.Null(TouchLayout.Conflict(none, safe, 1f, 1f, false));
        // Jump onto Crouch (28, 8)
        var onCrouch = new Dictionary<string, TouchOverride> { [TouchLayout.SharedKey(jump)] = new() { X = 28, Y = 8 } };
        Assert.Contains("Crouch", TouchLayout.Conflict(onCrouch, safe, 1f, 1f, false, "jump"));
        // off the screen
        var off = new Dictionary<string, TouchOverride> { [TouchLayout.SharedKey(jump)] = new() { X = 2, Y = 2 } };
        Assert.Contains("leave the screen", TouchLayout.Conflict(off, safe, 1f, 1f, false, "jump"));
        // a free spot up the right edge is fine in every mode with Jump (foot, fight, snow...)
        var free = new Dictionary<string, TouchOverride> { [TouchLayout.SharedKey(jump)] = new() { X = 60, Y = 40 } };
        Assert.Null(TouchLayout.Conflict(free, safe, 1f, 1f, false, "jump"));
        // hidden, it covers nothing
        var hidden = new Dictionary<string, TouchOverride> { [TouchLayout.SharedKey(jump)] = new() { X = 28, Y = 8, Hidden = true } };
        Assert.Null(TouchLayout.Conflict(hidden, safe, 1f, 1f, false, "jump"));
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

    /// <summary>The phone app's finger (#1253): millimetres turned into pixels on every screen density.</summary>
    [Theory]
    [InlineData(6f)]
    [InlineData(10f)]
    [InlineData(17f)]
    public void ThePhoneAppsFingerCountsInMillimetres(float pxPerMm)
    {
        float slop = TouchLayout.TapSlopMm * pxPerMm, back = TouchLayout.BackSwipeMm * pxPerMm;
        // 1.5 mm of wobble is a tap, 2.5 mm is a drag
        Assert.False(TouchLayout.Beyond(new Vector2(1.5f * pxPerMm, 0f), slop));
        Assert.True(TouchLayout.Beyond(new Vector2(2.5f * pxPerMm, 0f), slop));
        // a back swipe needs 10 mm, mostly sideways
        Assert.Equal(1, TouchLayout.SideSwipe(new Vector2(11f * pxPerMm, 2f * pxPerMm), back));
        Assert.Equal(-1, TouchLayout.SideSwipe(new Vector2(-11f * pxPerMm, 0f), back));
        Assert.Equal(0, TouchLayout.SideSwipe(new Vector2(9f * pxPerMm, 0f), back));
        Assert.Equal(0, TouchLayout.SideSwipe(new Vector2(11f * pxPerMm, 9f * pxPerMm), back));
        // a swipe down (put the folded phone away) likewise
        Assert.Equal(1, TouchLayout.DownSwipe(new Vector2(1f * pxPerMm, 11f * pxPerMm), back));
        Assert.Equal(-1, TouchLayout.DownSwipe(new Vector2(0f, -11f * pxPerMm), back));
        Assert.Equal(0, TouchLayout.DownSwipe(new Vector2(11f * pxPerMm, 11f * pxPerMm), back));
    }

    [Fact]
    public void ThePhoneAppsTargetsAreAtLeastSevenMillimetres()
    {
        Assert.True(TouchLayout.MinTargetMm >= 7f);
        Assert.True(TouchLayout.PrimaryTargetMm >= 9f);
        Assert.True(TouchLayout.PrimaryTargetMm > TouchLayout.MinTargetMm);
        // the slop stays under the back swipe, or a tap could be a swipe
        Assert.True(TouchLayout.TapSlopMm * 2f < TouchLayout.BackSwipeMm);
    }
}
