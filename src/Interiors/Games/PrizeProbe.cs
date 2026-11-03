using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Interiors.Games;

/// <summary>
/// <c>godot --headless --path . -- --prizecheck --systems ui</c> (#1326): the arcade's money on a scratch pack. The change
/// machine swaps francs for Batzen (and says no without them); a machine's coin is paid from the pack; the prize
/// counter's panel shows every prize, greys what the tickets do not reach, swaps tickets for a prize, refuses a full
/// pack; a game's tickets reach the winner and none a bot; every new item has a name and an icon.
/// </summary>
public partial class PrizeProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--prizecheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[prizes] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override async void _Ready()
    {
        var said = new List<string>();
        void Say(string s) => said.Add(s);
        var inv = Inventory.Scratch();

        Check(!ArcadeCounters.Swap(inv, Say) && said[^1].Contains("takes 5 CHF"), $"no francs, no Batzen ({said[^1]})");
        inv.Add(ItemId.Francs, 12);
        Check(ArcadeCounters.Swap(inv, Say) && inv.CountPlain(ItemId.ArcadeCoin) == 5 && inv.Cash == 7, $"5 CHF for 5 Batzen (cash {inv.Cash})");

        foreach (var id in new[] { ItemId.ArcadeCoin, ItemId.PrizeTicket, ItemId.PlushCow, ItemId.PlushMarmot, ItemId.PlushBernese, ItemId.Keyring, ItemId.BouncyBall, ItemId.SweetsBag })
            Check(ItemDefs.Get(id) is { } d && d.Name.Length > 0 && ItemIcons.Get(id) != null, $"{id}: a name and an icon");
        // (the list also names older items with no grid yet: only the arcade's are this check's)
        var mine = new[] { "ArcadeCoin", "PrizeTicket", "PlushCow", "PlushMarmot", "PlushBernese", "Keyring", "BouncyBall", "SweetsBag" };
        var bad = ItemIcons.Validate().Where(b => mine.Any(m => b.StartsWith(m + ":") || b.StartsWith(m + " "))).ToList();
        Check(bad.Count == 0, $"the arcade's icon grids are well formed ({string.Join("; ", bad)})");

        var ui = new PrizeCounterUi { Name = "PrizeCounterUi" };
        AddChild(ui);
        inv.Add(ItemId.PrizeTicket, 45);
        ui.Open(inv, Say);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var swaps = ui.Swaps.ToList();
        Check(swaps.Count == ArcadeCounters.Prizes.Length, $"a row per prize ({swaps.Count})");
        int open = swaps.Count(b => !b.Disabled);
        Check(open == 3, $"45 tickets reach the three cheap prizes, not the plush ({open} open)");
        Check(swaps.Last().Text.Contains("355"), $"a greyed prize says how many more ({swaps.Last().Text})");
        swaps[2].EmitSignal(BaseButton.SignalName.Pressed);
        Check(inv.CountPlain(ItemId.SweetsBag) == 1 && inv.CountPlain(ItemId.PrizeTicket) == 5, $"40 tickets for the sweets ({inv.CountPlain(ItemId.PrizeTicket)} left)");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(ui.Swaps.Count(b => !b.Disabled) == 0, "the panel followed the pack: nothing left to swap");
        Check(!ArcadeCounters.Buy(inv, ItemId.PlushBernese, Say) && said[^1].Contains("400"), "the Bernese takes 400 tickets");
        ui.Close();
        Check(!ui.IsOpen, "the panel closes");

        // a game's tickets: air hockey pays the human, never the bot
        var game = new AirHockey.AirHockeyGame();
        game.Sit(7, 0);
        game.Sit(TableGame.BotBase, 1);
        game.Act(0, AirHockey.AirHockeyGame.ActCoin, Array.Empty<int>(), 0, new Random(1));
        game.Goals[1] = AirHockey.AirHockeyGame.Win - 1;
        game.Goals[0] = 3;
        double now = 0;
        while (!game.InPlay) game.Step(TableGames.LiveStep, now += TableGames.LiveStep);
        game.PuckAt = new Vector2(0, -0.8f);
        game.PuckV = new Vector2(0, -4f);
        game.MalletAt[0] = new Vector2(0.4f, -0.5f);
        for (int i = 0; i < 120 && game.Winner < 0; i++) game.Step(TableGames.LiveStep, now += TableGames.LiveStep);
        Check(game.Winner == 1, "the bot won the last goal");
        Check(game.Payouts.Any(p => p.Peer == 7 && p.Count == 6 && p.Item == ItemId.PrizeTicket) && game.Payouts.Any(p => p.Peer == TableGame.BotBase),
            $"the game paid two a goal and the winner more ({string.Join(", ", game.Payouts.Select(p => $"{p.Peer}:{p.Count}"))})");
        Check(!game.Running && game.Act(0, AirHockey.AirHockeyGame.ActCoin, Array.Empty<int>(), now, new Random(1)) == null, "another coin, another game");

        GD.Print($"[prizes] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
