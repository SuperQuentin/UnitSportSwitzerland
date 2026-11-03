using UnitSport.Player;

namespace UnitSport.Interiors.Games;

// /games (#1324): list the venue's playable pieces, step up to one, add a bot, do a game's action by numbers, step
// back: what a check or a shot queue needs to play without hands.
public partial class TableGames
{
    public string Command(string rest, FootPlayer? player)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        if (InteriorManager.Instance?.CurrentNode is not { } node) return "Go into a venue first.";
        var l = node.Layout;
        string key = l.Key;
        int At() => TableGamePlay.Current is { } p && p.Key == key ? p.Index : -1;
        switch (verb)
        {
            case "play" when parts.Length > 1 && int.TryParse(parts[1], out int index):
            {
                if (index < 0 || index >= l.Furniture.Count || GameCatalog.Of(l.Furniture[index].Type) is not { } spec) return $"No game at #{index} here.";
                TableGamePlay.Current?.QueueFree();
                TableGamePlay.Begin(player, node, index, spec);
                return $"At the {spec.Title.ToLowerInvariant()} (#{index}).";
            }
            case "bot":
                if (At() < 0) return "Step up to a game first (/games play <n>).";
                AskBot(key, At());
                return "A bot is asked for.";
            case "act" when parts.Length > 1 && int.TryParse(parts[1], out int action):
            {
                if (At() < 0) return "Step up to a game first.";
                var args = parts.Skip(2).Select(a => int.TryParse(a, out int v) ? v : 0).ToArray();
                AskAct(key, At(), action, args);
                return $"Action {action} ({string.Join(" ", args)}).";
            }
            case "leave":
                TableGamePlay.Current?.QueueFree();
                return "Stepped back.";
            default:
            {
                var lines = new List<string>();
                for (int i = 0; i < l.Furniture.Count; i++)
                {
                    if (GameCatalog.Of(l.Furniture[i].Type) is not { } spec) continue;
                    var g = GameAt(key, i);
                    lines.Add($"#{i} {spec.Title}: {(g == null ? "idle" : g.Summary(NameOf).Replace('\n', ' '))}");
                }
                return lines.Count == 0 ? "No games here." : string.Join("\n", lines);
            }
        }
    }
}
