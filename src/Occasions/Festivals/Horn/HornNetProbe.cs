using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>
/// <c>--hornnet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/hornnetcheck.sh</c>, #1269): the alphorn and yodel game over a
/// dedicated server whose calendar reads 11 March 2027 (day 2 of the check-only festival), every assertion about what reached the REMOTE
/// peer. Lea (A) and Tom (B) walk to the alphorn row; each sits down on the server's word, A as the melody and B as the second voice of the
/// same song (same seed); a song sent too early is refused; both play it with the real <see cref="HornGame"/>; the server grades both against
/// the chart, scores their harmony, and each remote peer is told its own result and holds its own Batzen and party points.
/// </summary>
public partial class HornNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--hornnet");

    public HornNetProbe(ItemController items) : base(items, "hornnet", "HN", "hornnet_") { }
    public HornNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("test-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && HornGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        Expect(await Until(() => Svc.RunningOf(FestivalCatalog.TestId) is { Day: 2 }, 30), "the server runs the test festival on day 2");
        var game = HornGame.Instance!;
        var site = Svc.RunningOf(FestivalCatalog.TestId)!.Site!;
        var refused = new List<string>();
        Svc.Refused += why => refused.Add(why);

        // at the row
        var (rowE, rowN) = HornGround.SpotOf(site);
        var g0 = Me!.Global;
        Me.PlaceAt(Me.GlobalPosition + new Vector3((float)(rowE - g0.E), 0.3f, (float)(g0.N - rowN)), Me.Rotation.Y);
        Me.Velocity = Vector3.Zero;
        Expect(await Until(() => HornGround.At(Me) != null && HornGround.InteractionFor(Me) != null, 10), "at the row, with the prompt");
        Expect(await Handshake("ready", _role == "A" ? "B" : "A", "ready", 120), "the other player is at the row too");

        // A sits down first, then B: the same song, the next voice
        if (_role == "B") Expect(await Heard("A", "seated", 60), "A sat down first");
        GD.Print($"{Log} asking at {Time.GetTicksMsec() / 1000.0:F1} s");
        // the server learns where the body stands a moment after the teleport: ask again if it still says "go to the row"
        bool asked = false;
        for (int i = 0; i < 8 && game.State != HornGame.Phase.Playing; i++)
        {
            if (game.Idle) asked |= HornGround.TryInteract(Me);
            await Until(() => game.State == HornGame.Phase.Playing || game.Idle, 6);
            if (game.State != HornGame.Phase.Playing) await Seconds(1.5);
        }
        Expect(asked, "E at the row asks for a seat");
        Expect(game.State == HornGame.Phase.Playing, "the server seated this player");
        Expect(game.Part == (_role == "A" ? HornPart.Melody : HornPart.Third), $"as voice {(_role == "A" ? "I" : "II")} ({game.Part})");
        GD.Print($"{Log} seated at {Time.GetTicksMsec() / 1000.0:F1} s");
        Say("seated");
        Expect(await Heard(_role == "A" ? "B" : "A", "seated", 60), "both are seated in the same song");
        var seed = game.Chart.Seed;
        GD.Print($"{Log} song {seed} part {game.Part}");

        // blows sent before the song is over are refused on the server's word
        Svc.AskHornSong([new HornPlay(8, 1, 2)]);
        Expect(await Until(() => refused.Any(r => r.Contains("not over")), 10), $"a song sent at once is refused ({refused.LastOrDefault()})");

        // the song, played frame by frame
        game.Scripted = true;
        var chart = game.Chart;
        double spb = chart.SecondsPerBeat;
        foreach (var n in chart.Of(game.Part))
        {
            await Frames(() => game.SongTime >= n.Start * spb, 3000);
            game.SetLane(Array.IndexOf(game.Lanes, n.Partial) - game.Lane);
            game.Blow(true);
            await Frames(() => game.SongTime >= n.End * spb, 3000);
            game.Blow(false);
        }
        GD.Print($"{Log} song played at {Time.GetTicksMsec() / 1000.0:F1} s, {game.Plays.Count} blows worth {AlphornRules.Grade(chart, game.Part, game.Plays).Points} here, fps {Engine.GetFramesPerSecond()}");
        Expect(await Until(() => game.Result != null, 60), "this remote peer was told its result");
        var r = game.Result;
        if (r != null)
        {
            Expect(r.Value.Voices == 2 && r.Value.Part == game.Part, $"two voices were scored ({r.Value.Voices}, {r.Value.Part})");
            Expect(r.Value.Points >= r.Value.Max * 0.9, $"{r.Value.Points} of {r.Value.Max}");
            Expect(r.Value.Harmony100 >= 115, $"the two voices earned a harmony bonus (x{r.Value.Harmony100 / 100.0:F2})");
            Expect(r.Value.Batzen > AlphornRules.BatzenPerSong * 0.5, $"{r.Value.Batzen} Batzen");
            Expect(await Until(() => Mine.Earned == r.Value.Batzen && Mine.Points == r.Value.BoardPoints, 10),
                $"this peer's own book holds it ({Mine.Earned} earned of {Mine.Batzen} Batzen, {Mine.Points} points)");
            Expect(await Until(() => Svc.BoardOf(FestivalCatalog.TestId) is { Points: > 0 }, 10), "and the board moved");
        }
        Say("done");
        Expect(await Heard(_role == "A" ? "B" : "A", "done", 120), "the other player finished");
        await Finish(5.0);
    }

    /// <summary>Says <paramref name="say"/> again every few seconds until <paramref name="role"/> has said <paramref name="expected"/> (a line sent before the other peer joined chat is lost).</summary>
    private async Task<bool> Handshake(string say, string role, string expected, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end)
        {
            Say(say);
            if (await Heard(role, expected, 3)) return true;
        }
        return false;
    }

    private async Task Frames(Func<bool> done, int max)
    {
        for (int i = 0; i < max && !done(); i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
