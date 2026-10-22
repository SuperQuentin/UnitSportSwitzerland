using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;

namespace UnitSport.Ui.Skins;

/// <summary>
/// The menu skins (#1135, docs/notes/ui/menu-skins.md): every <c>*.json</c> in <c>res://assets/skins/</c>, then
/// <c>user://skins/</c> (a player's own, which may replace a built-in one by id), and the one a menu page gets now.
/// <list type="bullet">
/// <item>Settings › Video › Menu look: Auto (the default) or a skin. Auto takes the day skin from 07:00 to 19:00 and the
/// night skin otherwise: the player's clock on the title screen, the world's clock in a world.</item>
/// <item><c>--menuskin &lt;id&gt;</c> forces one for a run (checks, screenshots).</item>
/// </list>
/// A page takes the skin active when it is built; pages are built on every push, so the next one shows a change.
/// </summary>
public static class MenuSkin
{
    public const string ClassicId = "classic", DayDefault = "alpine", NightDefault = "night-arcade";

    private static Dictionary<string, SkinData>? _skins;
    private static readonly Dictionary<string, Theme> Themes = new();

    /// <summary>Every skin found, built-in first, by id.</summary>
    public static IReadOnlyDictionary<string, SkinData> All => _skins ??= Load();

    /// <summary>The skin a page built now gets.</summary>
    public static SkinData Current => All[CurrentId];

    public static string CurrentId
    {
        get
        {
            var s = GameSettings.Current;
            string? setting = CmdArgs.Value("--menuskin") ?? s.MenuSkin;
            double hour = World.WorldClock.Active ? World.WorldClock.Hour : DateTime.Now.TimeOfDay.TotalHours;
            return SkinRules.Pick(setting, hour, s.MenuSkinDay, s.MenuSkinNight, All.Keys.ToList(), ClassicId);
        }
    }

    /// <summary>The theme of the current skin, built once per skin.</summary>
    public static Theme Theme() => ThemeOf(CurrentId);

    public static Theme ThemeOf(string id)
    {
        if (Themes.TryGetValue(id, out var t)) return t;
        return Themes[id] = SkinTheme.Build(All.TryGetValue(id, out var s) ? s : All[ClassicId]);
    }

    /// <summary>A colour role of the current skin, for the few things drawn by hand (backdrops, bars).</summary>
    public static Color Role(string role) => SkinTheme.C(Current, role);

    private static Dictionary<string, SkinData> Load()
    {
        var skins = new Dictionary<string, SkinData>(StringComparer.Ordinal);
        foreach (string dir in new[] { "res://assets/skins", "user://skins" })
        {
            if (!DirAccess.DirExistsAbsolute(dir)) continue;
            foreach (string file in DirAccess.GetFilesAt(dir).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                string path = $"{dir}/{file}";
                if (SkinData.Parse(Godot.FileAccess.GetFileAsString(path), out string? error) is { } skin) skins[skin.Id] = skin;
                else GD.PushWarning($"[skins] {path} skipped: {error}");
            }
        }
        // the classic look is the fallback of every lookup: never without it
        if (!skins.ContainsKey(ClassicId)) skins[ClassicId] = Classic();
        GD.Print($"[skins] {string.Join(", ", skins.Keys)}");
        return skins;
    }

    /// <summary>The look the menus had before the skins (and the HUDs keep), in code: always there.</summary>
    public static SkinData Classic()
    {
        static string Hex(Color c) => "#" + c.ToHtml(true).ToUpperInvariant();
        return new SkinData
        {
            Id = ClassicId, Name = "Classic", Night = true, Font = "system",
            Colors = new()
            {
                ["ground"] = "#05060AFF", ["card"] = Hex(UiTheme.Glass), ["cardEdge"] = "#FFFFFF12", ["ink"] = Hex(UiTheme.Text),
                ["inkDim"] = Hex(UiTheme.TextDim), ["inkFaint"] = Hex(UiTheme.TextFaint), ["accent"] = Hex(UiTheme.Amber),
                ["onAccent"] = "#121214FF", ["accent2"] = Hex(UiTheme.Amber), ["control"] = "#FFFFFF0B", ["controlHover"] = "#FFFFFF17",
                ["controlPressed"] = "#FFFFFF21", ["good"] = Hex(UiTheme.Good), ["warn"] = Hex(UiTheme.Warn), ["bad"] = Hex(UiTheme.Bad),
                ["hairline"] = Hex(UiTheme.Hairline), ["shadow"] = "#00000061",
            },
            Shape = new SkinShape { Radius = 16, ControlRadius = 8, Outline = 1, ShadowSize = 22 },
        };
    }

    /// <summary>For tests and a settings change: the next lookup re-reads the files and rebuilds the themes.</summary>
    public static void Reload()
    {
        _skins = null;
        Themes.Clear();
    }
}
