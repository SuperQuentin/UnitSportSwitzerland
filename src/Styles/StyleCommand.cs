using System.Linq;

namespace UnitSport.Styles;

/// <summary>
/// <c>/style</c>: which visual style this screen shows, <c>/style cartoon</c> to switch live,
/// <c>/style rebuild</c> to rebuild the world's meshes in place. Client-only, like the style: the
/// chat answers it here and never sends it to a server. Not saved (<see cref="StyleKit.Choose"/>).
/// </summary>
public static class StyleCommand
{
    public const string Usage = "Usage: /style [ps1 | cartoon | real- | real+ | rebuild]";

    /// <summary>What Tab offers after <c>/style</c>.</summary>
    public static readonly string[] Words = ["ps1", "cartoon", "real-", "real+", "rebuild"];

    /// <summary>
    /// Raised by <c>/style rebuild</c>; the client world rebuilds every tile's meshes in place
    /// (<c>Terrain.ChunkManager.RebuildVisuals</c>). Main thread.
    /// </summary>
    public static event System.Action? RebuildRequested;

    /// <summary>The reply to a <c>/style</c> line, or null when the line is another command.</summary>
    public static string? Run(string text)
    {
        if (!text.StartsWith('/')) return null;
        string[] parts = text[1..].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("style", System.StringComparison.OrdinalIgnoreCase)) return null;

        if (parts.Length == 1)
            return $"Visual style: {StyleKit.NameOf(StyleKit.Applied)}. Styles: "
                + string.Join(", ", StyleKit.Names.Select(n => n.Name).Distinct().Take(4)) + ".";
        if (parts.Length != 2) return Usage;

        if (parts[1].Equals("rebuild", System.StringComparison.OrdinalIgnoreCase))
        {
            if (RebuildRequested == null) return "No world to rebuild.";
            RebuildRequested.Invoke();
            return "Rebuilding the world's meshes.";
        }
        if (!StyleKit.TryParse(parts[1], out var style)) return $"No style '{parts[1]}'. {Usage}";
        if (style == StyleKit.Applied) return $"Already {StyleKit.NameOf(style)}.";

        if (!StyleKit.HasWorld) return "No world to restyle.";
        StyleKit.Choose(style);
        var borrowed = System.Enum.GetValues<MaterialRole>().Count(r => StyleKit.Resolve(style, r).From != style);
        return $"Visual style: {StyleKit.NameOf(style)}, for this session"
            + (borrowed == 0 ? "." : $" ({borrowed} of its materials still borrowed, see --style-report).");
    }
}
