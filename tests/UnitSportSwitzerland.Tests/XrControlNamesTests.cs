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
}
