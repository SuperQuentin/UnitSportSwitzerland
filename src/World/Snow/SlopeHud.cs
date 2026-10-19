using System;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// While the local player skis a slope (#887): its name or number in its colour and what the snow
/// is like there, top left; and the ski map, top right (<see cref="SlopeMap"/>). The text is
/// rebuilt only when the slope or its snow changes (perf-no-per-frame-allocations), looked at
/// twice a second.
/// </summary>
public partial class SlopeHud : CanvasLayer
{
    private readonly Func<FootPlayer?> _player;
    private readonly Label _label;
    private double _since;
    private ulong _slope;
    private SnowCondition _condition = (SnowCondition)255;

    /// <summary>
    /// The skier this HUD follows: the local player on skis, else a body this peer drives on skis
    /// (a probe's own rider); looked up twice a second.
    /// </summary>
    public FootPlayer? Skier { get; private set; }

    public SlopeHud(Func<FootPlayer?> player, Terrain.ChunkManager chunks)
    {
        _player = player;
        Layer = 5;
        Name = "SlopeHud";
        _label = new Label { Position = new Vector2(24, 96), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeConstantOverride("outline_size", 6);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        AddChild(_label);
        AddChild(new SlopeMap(() => Skier, chunks));
    }

    public override void _Process(double delta)
    {
        _since += delta;
        if (_since < 0.5) return;
        _since = 0;
        Skier = FindSkier();
        var p = Skier;
        var slope = p?.Slope;
        if (slope == null)
        {
            _label.Visible = false;
            _slope = 0;
            return;
        }
        var condition = p!.SlopeSnow.Condition;
        _label.Visible = true;
        if (slope.Id == _slope && condition == _condition) return;
        _slope = slope.Id;
        _condition = condition;
        _label.Text = Describe(slope, condition);
        _label.AddThemeColorOverride("font_color", ColourOf(slope.Difficulty));
    }

    private FootPlayer? FindSkier()
    {
        if (_player() is { } local && IsInstanceValid(local) && SnowRide.Is(local.RideKindId)) return local;
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && !p.Bot && SnowRide.Is(p.RideKindId) && p.IsMultiplayerAuthority())
                return p;
        return null;
    }

    public static string Describe(PisteRecord slope, SnowCondition condition)
    {
        string name = slope.Name.Length > 0 && slope.Ref.Length > 0 ? $"{slope.Ref} {slope.Name}"
            : slope.Name.Length > 0 ? slope.Name : slope.Ref.Length > 0 ? $"Slope {slope.Ref}" : KindOf(slope);
        return $"{name} · {LevelOf(slope.Difficulty)} · {ConditionName(condition)}";
    }

    private static string KindOf(PisteRecord s) => s.Type switch
    {
        PisteType.Nordic => "Cross-country trail",
        PisteType.Sled => "Sledge run",
        PisteType.SnowPark => "Snow park",
        PisteType.Connection => "Link",
        _ => "Slope",
    };

    /// <summary>The Swiss markings: blue, red, black; yellow for an itinerary (marked, not groomed).</summary>
    public static string LevelOf(PisteDifficulty d) => d switch
    {
        PisteDifficulty.Novice or PisteDifficulty.Easy => "blue",
        PisteDifficulty.Intermediate => "red",
        PisteDifficulty.Advanced or PisteDifficulty.Expert => "black",
        PisteDifficulty.Freeride or PisteDifficulty.Extreme => "itinerary",
        _ => "unrated",
    };

    private static Color ColourOf(PisteDifficulty d) => d switch
    {
        PisteDifficulty.Novice or PisteDifficulty.Easy => new Color(0.45f, 0.65f, 1f),
        PisteDifficulty.Intermediate => new Color(1f, 0.42f, 0.38f),
        PisteDifficulty.Advanced or PisteDifficulty.Expert => new Color(0.85f, 0.85f, 0.85f),
        PisteDifficulty.Freeride or PisteDifficulty.Extreme => new Color(1f, 0.85f, 0.3f),
        _ => Colors.White,
    };

    public static string ConditionName(SnowCondition c) => c switch
    {
        SnowCondition.Corduroy => "fresh corduroy",
        SnowCondition.Packed => "packed snow",
        SnowCondition.HardPack => "hard-packed",
        SnowCondition.Icy => "icy",
        SnowCondition.Powder => "powder",
        SnowCondition.Chopped => "chopped powder",
        SnowCondition.Slush => "slush",
        SnowCondition.Moguls => "moguls",
        _ => "",
    };
}
