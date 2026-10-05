using UnitSport.XR;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>VR controller names for prompts (src/XR/XrControlNames.cs, linked in, #435).</summary>
public class XrControlNamesTests
{
    [Theory]
    [InlineData("/interaction_profiles/oculus/touch_controller", XrController.Quest)]
    [InlineData("/interaction_profiles/meta/touch_controller_plus", XrController.Quest)]
    [InlineData("/interaction_profiles/bytedance/pico4_controller", XrController.Quest)]
    [InlineData("/interaction_profiles/hp/mixed_reality_controller", XrController.Quest)]
    [InlineData("/interaction_profiles/htc/vive_cosmos_controller", XrController.Quest)]
    [InlineData("/interaction_profiles/khr/simple_controller", XrController.Quest)]
    [InlineData("/interaction_profiles/valve/index_controller", XrController.Index)]
    [InlineData("/interaction_profiles/htc/vive_controller", XrController.Vive)]
    [InlineData("/interaction_profiles/microsoft/motion_controller", XrController.Wmr)]
    [InlineData("index", XrController.Index)]
    [InlineData("vive", XrController.Vive)]
    [InlineData("wmr", XrController.Wmr)]
    [InlineData("something new", XrController.Quest)]
    public void Profiles_map_to_their_family_and_unknown_is_quest(string path, XrController expected) =>
        Assert.Equal(expected, XrControlNames.FromPath(path));

    [Fact]
    public void Every_control_has_a_name_except_face_buttons_the_hardware_lacks()
    {
        foreach (var controller in Enum.GetValues<XrController>())
            foreach (var control in Enum.GetValues<XrControl>())
            {
                bool face = control is XrControl.A or XrControl.B or XrControl.X or XrControl.Y;
                bool lacks = face && controller is XrController.Vive or XrController.Wmr;
                var name = XrControlNames.Name(control, controller);
                if (lacks) Assert.Null(name);
                else Assert.False(string.IsNullOrWhiteSpace(name), $"{controller} {control}");
            }
    }

    [Fact]
    public void Names_follow_the_hardware()
    {
        Assert.Equal("A", XrControlNames.Name(XrControl.A, XrController.Quest));
        Assert.Equal("Y", XrControlNames.Name(XrControl.Y, XrController.Quest));
        Assert.Equal("L B", XrControlNames.Name(XrControl.Y, XrController.Index));
        Assert.Equal("R trackpad ↑", XrControlNames.Name(XrControl.RightStickUp, XrController.Vive));
        Assert.Equal("R trigger", XrControlNames.Name(XrControl.RightTrigger, XrController.Wmr));
    }

    [Fact]
    public void One_controller_never_gives_two_controls_the_same_name()
    {
        foreach (var controller in Enum.GetValues<XrController>())
        {
            var names = Enum.GetValues<XrControl>().Select(c => XrControlNames.Name(c, controller)).Where(n => n != null).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(0f, 0f, 0)]          // rest: neutral
    [InlineData(-0.06f, 0.08f, 1)]   // left column ahead
    [InlineData(-0.06f, -0.08f, 2)]  // left column back
    [InlineData(0f, 0.08f, 3)]
    [InlineData(0f, -0.08f, 4)]
    [InlineData(0.06f, 0.08f, 5)]
    [InlineData(0.07f, -0.08f, 6)]
    [InlineData(0.2f, 0.08f, 5)]     // far right stays in the last column
    [InlineData(-0.13f, 0.08f, -1)]  // reverse, left of 1, ahead
    [InlineData(-0.13f, -0.08f, 0)]
    [InlineData(0.06f, 0.02f, 0)]    // across the middle
    public void Gear_knob_gates(float across, float ahead, int gate) =>
        Assert.Equal(gate, XrControlNames.GateOf(across, ahead, 0.06f, 0.05f));

    [Fact]
    public void Mirroring_twice_is_the_same_control_and_names_the_other_hand()
    {
        foreach (var control in Enum.GetValues<XrControl>())
            Assert.Equal(control, XrControlNames.Mirror(XrControlNames.Mirror(control)));
        Assert.Equal("L trigger", XrControlNames.Name(XrControlNames.Mirror(XrControl.RightTrigger), XrController.Quest));
        Assert.Equal("X", XrControlNames.Name(XrControlNames.Mirror(XrControl.A), XrController.Quest));
        Assert.Equal("L stick ↑", XrControlNames.Name(XrControlNames.Mirror(XrControl.RightStickUp), XrController.Quest));
    }
}
