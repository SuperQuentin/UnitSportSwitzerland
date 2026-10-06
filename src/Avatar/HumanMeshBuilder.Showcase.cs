using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

// The figure's entries in the model viewer (--models, docs/notes/avatar/model-viewer.md): one per
// enum value, so a new pose, arm pose or hat shows without touching this file.
public static partial class HumanMeshBuilder
{
    [Showcase("Figures", "Pose")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcasePoses() =>
        Enum.GetValues<HumanPose>().Select((pose, i) => (pose.ToString(), (Func<Node3D>)(() =>
            ModelViewer.Shaded(Build(HumanPalette.ForRider(i), pose), figure: true))));

    [Showcase("Figures", "Holding")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseArmPoses() =>
        Enum.GetValues<ItemArmPose>().Where(arm => arm != ItemArmPose.None).Select((arm, i) => (arm.ToString(), (Func<Node3D>)(() =>
            ModelViewer.Shaded(BuildPosed(HumanPalette.ForRider(i), HumanPose.Standing, arm, 1f), figure: true))));

    [Showcase("Occasions", "Hat")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseHats() =>
        Enum.GetValues<Headwear>().Where(hat => hat != Headwear.None).Select((hat, i) => (hat.ToString(), (Func<Node3D>)(() =>
            ModelViewer.Shaded(Build(HumanPalette.ForRider(i + 1), hat: hat), figure: true))));
}
