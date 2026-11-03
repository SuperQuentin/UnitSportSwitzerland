using Godot;
using UnitSport.Player;

namespace UnitSport.Interiors.Pinball;

// /pinball (#1325), offline: list the venue's machines, step up to one, let a robot play the table, the
// top view: what a shot queue needs to play and look at a table without hands.
public partial class PinballTables
{
    public string Command(string rest, string? key, FootPlayer? player)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        if (key == null || InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Key != key) return "Go into a venue with pinball machines first.";
        var l = node.Layout;
        switch (verb)
        {
            case "play" when parts.Length > 1 && int.TryParse(parts[1], out int index):
                if (index < 0 || index >= l.Furniture.Count || l.Furniture[index].Type != FurnitureType.Pinball) return $"No pinball machine #{index} here.";
                PinballPlay.Begin(player, key, index);
                return $"At {PinballLayouts.Get(TableOf(l, index)).Title} (#{index}).";
            case "robot":
                if (PinballPlay.Current is not { } play) return "Step up to a machine first (/pinball play <n>).";
                play.Robot = !play.Robot;
                return play.Robot ? "A robot plays the flippers and the plunger." : "Your hands again.";
            case "view":
                if (PinballPlay.Current is not { } p2) return "Step up to a machine first.";
                p2.TopView = !p2.TopView;
                return p2.TopView ? "Straight down on the playfield." : "The player's view.";
            case "start":
                if (PinballPlay.Current is not { } p3) return "Step up to a machine first.";
                p3.PressStart();
                return "Start.";
            default:
            {
                var lines = new List<string>();
                for (int i = 0; i < l.Furniture.Count; i++)
                {
                    if (l.Furniture[i].Type != FurnitureType.Pinball) continue;
                    var m = Peek(key, i);
                    string state = m?.Rules is { } r ? $"{r.State}, {string.Join(" / ", r.Players.Select(x => PinballRules.Fmt(x.Score)))}"
                        : ScreenOf(key, i) is { } s ? $"{s.Phase}, {string.Join(" / ", s.Scores.Select(PinballRules.Fmt))}" : "idle";
                    lines.Add($"#{i} {PinballLayouts.Get(TableOf(l, i)).Title}: {state}");
                }
                return lines.Count == 0 ? "No pinball machines here." : string.Join("\n", lines);
            }
        }
    }
}
