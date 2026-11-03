using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace UnitSport.Ui.Skins;

/// <summary>
/// One menu skin as data (#1135, docs/notes/ui/menu-skins.md): colour roles, shapes and a font, read from a JSON file in
/// <c>assets/skins/</c> (or <c>user://skins/</c>), so a new look is a new file and never new code. No Godot here, so
/// it is unit tested; <see cref="SkinTheme"/> turns it into a Godot <c>Theme</c>.
/// </summary>
public sealed class SkinData
{
    /// <summary>The colour roles every skin names, as <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public static readonly string[] Roles =
    {
        "ground",          // behind the pages
        "card",            // a page or card's fill
        "cardEdge",        // its outline
        "ink",             // text
        "inkDim",          // secondary text
        "inkFaint",        // hints, placeholders
        "accent",          // the primary button, focus, the chosen option
        "onAccent",        // text on the accent
        "accent2",         // a second accent: badges, the progress
        "control",         // a button or field at rest
        "controlHover",
        "controlPressed",
        "good", "warn", "bad",
        "hairline",        // separators
        "shadow",          // the card's shadow (or glow)
    };

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>For Auto: a skin meant for the night (only a hint for the settings list).</summary>
    public bool Night { get; set; }
    public Dictionary<string, string> Colors { get; set; } = new();
    public SkinShape Shape { get; set; } = new();
    /// <summary><c>system</c> (the machine's sans) or <c>saira</c> (bundled: Saira, and Saira Condensed for titles).</summary>
    public string Font { get; set; } = "system";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a skin; null with <paramref name="error"/> set when it is not one.</summary>
    public static SkinData? Parse(string json, out string? error)
    {
        SkinData? s;
        try { s = JsonSerializer.Deserialize<SkinData>(json, Json); }
        catch (JsonException e) { error = $"not JSON: {e.Message}"; return null; }
        if (s == null) { error = "empty"; return null; }
        error = s.Problem();
        return error == null ? s : null;
    }

    /// <summary>What is wrong with this skin, or null: an id, a name, and every role as a colour.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            return "id: letters, digits and -";
        if (string.IsNullOrWhiteSpace(Name)) return "name missing";
        foreach (string role in Roles)
        {
            if (!Colors.TryGetValue(role, out var hex)) return $"colour '{role}' missing";
            if (Rgba(hex) == null) return $"colour '{role}': '{hex}' is not #RRGGBB or #RRGGBBAA";
        }
        foreach (string role in OptionalRoles.Keys)
            if (Colors.TryGetValue(role, out var hex) && Rgba(hex) == null) return $"colour '{role}': '{hex}' is not #RRGGBB or #RRGGBBAA";
        if (Font is not ("system" or "saira")) return $"font '{Font}': system or saira";
        return Shape.Problem();
    }

    /// <summary>A role's colour as r, g, b, a in 0..1.</summary>
    public (float R, float G, float B, float A) Color(string role) =>
        Rgba(Colors.TryGetValue(role, out var hex) ? hex : Colors[Fallback(role)]) ?? (1, 0, 1, 1);

    /// <summary>Optional roles a skin may leave out, and the role each falls back to.</summary>
    public static readonly Dictionary<string, string> OptionalRoles = new() { ["controlEdge"] = "cardEdge" };

    private static string Fallback(string role) => OptionalRoles.TryGetValue(role, out var f) ? f : role;

    /// <summary><c>#RRGGBB</c> or <c>#RRGGBBAA</c> to 0..1 channels; null for anything else.</summary>
    public static (float R, float G, float B, float A)? Rgba(string? hex)
    {
        if (hex is not { Length: 7 or 9 } || hex[0] != '#') return null;
        if (!uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        if (hex.Length == 7) v = (v << 8) | 0xFF;
        return (((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }
}

/// <summary>A skin's shapes: the same few numbers make a glass panel, a sticker or a glowing card.</summary>
public sealed class SkinShape
{
    /// <summary>Corner radius of pages and cards, px.</summary>
    public int Radius { get; set; } = 12;
    /// <summary>Corner radius of buttons and fields, px.</summary>
    public int ControlRadius { get; set; } = 8;
    /// <summary>Card outline, px (a sticker's is thick).</summary>
    public int Outline { get; set; } = 1;
    /// <summary>A button's outline, px (0: none).</summary>
    public int ControlOutline { get; set; }
    /// <summary>The shadow's offset, px: a hard sticker shadow when <see cref="ShadowSize"/> is 0.</summary>
    public int ShadowX { get; set; }
    public int ShadowY { get; set; }
    /// <summary>The shadow's blur, px: a soft shadow, or a glow in the shadow colour.</summary>
    public int ShadowSize { get; set; } = 22;
    /// <summary>Cards, the main button and a hovered menu entry cast the shadow too (a sticker skin); else only pages
    /// and popups do. Plain controls (fields, dropdowns, buttons) never do.</summary>
    public bool ControlShadow { get; set; }
    /// <summary>On hover a control grows by this much (0.05 = 5 %); 0 keeps it still.</summary>
    public float HoverScale { get; set; }
    /// <summary>On hover a control tilts by this much, degrees.</summary>
    public float HoverTilt { get; set; }

    /// <summary>Buttons, fields, tabs and menu entries lean by this much (0.22: a slanted sports-game look); 0 keeps them square.</summary>
    public float Skew { get; set; }
    /// <summary>A menu entry under the pointer fills with the accent (its text in <c>onAccent</c>); else a light tint.</summary>
    public bool MenuFill { get; set; }
    /// <summary>Big titles (the logo, a page's name) in capitals.</summary>
    public bool DisplayUppercase { get; set; }

    public string? Problem() =>
        Skew is < 0 or > 0.5f ? "skew: 0 to 0.5"
        : Radius is < 0 or > 40 || ControlRadius is < 0 or > 40 ? "radius: 0 to 40"
        : Outline is < 0 or > 8 || ControlOutline is < 0 or > 8 ? "outline: 0 to 8"
        : Math.Abs(ShadowX) > 16 || Math.Abs(ShadowY) > 16 || ShadowSize is < 0 or > 60 ? "shadow: offsets to 16, size to 60"
        : HoverScale is < 0 or > 0.2f || Math.Abs(HoverTilt) > 8 ? "hover: scale 0 to 0.2, tilt to 8 degrees"
        : null;
}

/// <summary>Which skin a page gets (#1135): the setting, and Auto's day/night rule.</summary>
public static class SkinRules
{
    public const string Auto = "auto";

    /// <summary>Daytime for Auto: 07:00 to 19:00.</summary>
    public static bool IsDay(double hour) => hour >= 7 && hour < 19;

    /// <summary>
    /// The skin id for <paramref name="setting"/> (an id, or <see cref="Auto"/>) at <paramref name="hour"/>; a skin that
    /// does not exist falls back to <paramref name="fallback"/>.
    /// </summary>
    public static string Pick(string? setting, double hour, string day, string night, IReadOnlyCollection<string> known, string fallback)
    {
        string id = string.IsNullOrEmpty(setting) || setting == Auto ? (IsDay(hour) ? day : night) : setting;
        return known.Contains(id) ? id : fallback;
    }
}
