using System.Text.Json;
using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// This player's Battle Royale display choices (#231), in <c>user://br/settings.json</c>: the minimap's
/// size and whether it turns with you, the compass strip, the stings. Set from the row of buttons on
/// the full map (M), so they are changed where they are seen; a file of their own, so nothing else's
/// settings format has to know about them.
/// </summary>
public sealed class BrPrefs
{
    public enum MapSize { Small, Medium, Large }

    public MapSize Minimap { get; set; } = MapSize.Medium;
    /// <summary>The minimap turns so that where you look is up (otherwise north is up).</summary>
    public bool MinimapTurns { get; set; }
    public bool Compass { get; set; } = true;
    /// <summary>The stings for the zone closing, the final circle and the win; the heartbeat outside the zone.</summary>
    public bool Stings { get; set; } = true;

    private const string File = "user://br/settings.json";
    private static BrPrefs? _current;

    public static BrPrefs Current => _current ??= Load();

    /// <summary>Minimap side in pixels.</summary>
    public float MinimapSide => Minimap switch { MapSize.Small => 170f, MapSize.Large => 290f, _ => 220f };

    private static BrPrefs Load()
    {
        try
        {
            if (Godot.FileAccess.FileExists(File) && JsonSerializer.Deserialize<BrPrefs>(Godot.FileAccess.GetFileAsString(File)) is { } p) return p;
        }
        catch (Exception e) { GD.PushWarning($"[br] settings: {e.Message}"); }
        return new BrPrefs();
    }

    public void Save()
    {
        try { Core.JsonStore.Save(File, this); }
        catch (Exception e) { GD.PushWarning($"[br] settings: {e.Message}"); }
    }
}
