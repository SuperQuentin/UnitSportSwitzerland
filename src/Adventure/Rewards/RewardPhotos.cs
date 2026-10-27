using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Player;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The camera's side of the photo quests (#1227, rewards plan 7): after a shot the client reports where it looked
/// at every photo spot whose standing disc holds the player (the server judges, <see cref="PhotoJudge"/>), names the
/// spot in the print's sidecar, and records the animals near the frame's middle within 40 m for the fauna page.
/// </summary>
public static class RewardPhotos
{
    public const float FaunaRangeM = 40;
    /// <summary>An animal "near the frame's centre": within this share of the half-angle.</summary>
    public const float FaunaShare = 0.4f;

    private static readonly List<Vector3> Cows = new();

    /// <summary>The camera's compass bearing and pitch (degrees): world X is east, −Z north.</summary>
    public static (float Yaw, float Pitch) Aim(Camera3D cam)
    {
        var f = -cam.GlobalTransform.Basis.Z;
        float yaw = Mathf.RadToDeg(Mathf.Atan2(f.X, -f.Z));
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(f.Y, -1f, 1f)));
        return (yaw, pitch);
    }

    /// <summary>The spot a shot frames, judged here without the terrain (the server judges for real), or "" (the print's sidecar).</summary>
    public static string SpotFor(FootPlayer player, Camera3D cam, float fovDeg)
    {
        if (RewardService.Instance == null) return "";
        var g = player.Global;
        var (yaw, pitch) = Aim(cam);
        foreach (var s in PhotoSpots.At(g.E, g.N, PoiCatalogueHost.Current))
            if (PhotoJudge.Judge(s, g.E, g.N, (float)g.Alt + 1.6f, yaw, pitch, fovDeg, (_, _) => null) == PhotoVerdict.Taken) return s.Id;
        return "";
    }

    /// <summary>After a shot: the spots in reach not yet taken go to the server; the animals in frame go to the fauna page.</summary>
    public static void Report(FootPlayer player, Camera3D cam, float fovDeg)
    {
        if (RewardService.Instance is not { } svc || AdventureService.Instance is not { } adv || !Ui.AdventureLog.Adventuring) return;
        var g = player.Global;
        var (yaw, pitch) = Aim(cam);
        var taken = adv.Mine.Get(PhotosProgress.Section).Taken;
        foreach (var s in PhotoSpots.At(g.E, g.N, PoiCatalogueHost.Current))
            if (!taken.Contains(s.Id)) svc.ReportPhoto(s.Id, yaw, pitch, fovDeg);
        foreach (string name in AnimalsInFrame(cam, fovDeg)) svc.ReportFauna(name);
    }

    /// <summary>The animals near the middle of the frame, within <see cref="FaunaRangeM"/>: cows, birds (by species), the guide marmot.</summary>
    public static List<string> AnimalsInFrame(Camera3D cam, float fovDeg)
    {
        var eye = cam.GlobalPosition;
        var fwd = -cam.GlobalTransform.Basis.Z;
        float maxAngle = Mathf.DegToRad(fovDeg / 2 * FaunaShare);
        var seen = new List<string>();
        bool InFrame(Vector3 p)
        {
            var d = p - eye;
            float len = d.Length();
            return len > 0.5f && len <= FaunaRangeM && fwd.AngleTo(d / len) <= maxAngle;
        }
        if (Farming.Herds.Instance is { } herds)
        {
            herds.CowsNear(eye, FaunaRangeM, Cows);
            foreach (var c in Cows)
                if (InFrame(c + new Vector3(0, 1f, 0))) { seen.Add(Fauna.Cow); break; }
        }
        if (Birds.BirdLife.Instance is { } birds)
            foreach (var b in birds.Birds)
                if (IsInstanceValid(b.Node) && InFrame(b.Node.GlobalPosition) && !seen.Contains(b.Species.Name)) seen.Add(b.Species.Name);
        if (GuideNode.Instance is { Visible: true } guide && guide.Body is { } body && InFrame(body.GlobalPosition + new Vector3(0, 0.2f, 0)))
            seen.Add(guide.IsVulture ? "Bearded Vulture" : Fauna.Marmot);
        return seen;
    }

    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
}
