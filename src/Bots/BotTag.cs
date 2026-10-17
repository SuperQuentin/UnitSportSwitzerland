using Godot;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// The mark over a bot's head on every client (#837): its name and "BOT", drawn to 60 m like a
/// sleeper's tag (<c>Net/Sleepers</c>), hidden by walls (depth-tested), so it marks a bot without
/// showing where one hides.
/// </summary>
public partial class BotTag : Label3D
{
    /// <summary>Over the head of a standing figure.</summary>
    private const float Height = 2.25f;

    public BotTag(FootPlayer body)
    {
        Name = "BotTag";
        Text = string.IsNullOrEmpty(body.BotName) ? "BOT" : $"{body.BotName}\nBOT";
        FontSize = 36;
        PixelSize = 0.006f;
        OutlineSize = 8;
        Modulate = new Color(1f, 0.85f, 0.35f);
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        Shaded = false;
        VisibilityRangeEnd = 60f;
        CastShadow = ShadowCastingSetting.Off;
        Position = new Vector3(0, Height, 0);
    }
}
