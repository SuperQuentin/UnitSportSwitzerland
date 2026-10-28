using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>
/// <c>--hornscheck --festival test --date 2027-03-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c> (quick
/// tier, #1269): offline, this client plays the server's part. Away from the alphorn row there is no prompt and no seat; at the row E sits
/// the player down as the melody, three bots take the other voices through the server's own methods, the song is played with the real
/// <see cref="HornGame"/> (driven frame by frame as a player's hands would), the server grades it, scores the harmony of four voices and pays
/// Batzen and party points; a forged song, an early one and a seat too far away are refused; the valley (a ring of walls a kilometre out)
/// answers the blows about six seconds later. Its progress lives in <c>test_output/hornscheck</c>.
/// </summary>
public partial class HornCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--hornscheck");

    private readonly Func<FootPlayer?> _me;

    public HornCheck(ItemController items, Func<FootPlayer?> me) : base(items, "hornscheck", "", "horn_") => _me = me;
    public HornCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && HornGame.Instance != null && Me is { } me && me.IsOnFloor() && Svc.Running.Count > 0, 90))
        {
            Fail($"no festival running (service {FestivalService.Instance != null}, game {HornGame.Instance != null}, running {FestivalService.Instance?.Running.Count})");
            return;
        }
        var svc = Svc;
        var game = HornGame.Instance!;
        var run = svc.RunningOf(FestivalCatalog.TestId);
        if (run?.Site is not { } site) { Fail("the test festival has no site"); return; }
        Expect(run.Day == 2 && FestivalCatalog.Get(FestivalCatalog.TestId)!.IsOpen(HornGround.GameId, run.Day), "the alphorn game is open on day 2");
        var refused = new List<string>();
        svc.Refused += why => refused.Add(why);

        // ---- away from the row: nothing to press -------------------------------------------------
        Me!.PlaceAt(Me.GlobalPosition + new Vector3(-40f, 0.3f, 0f), Me.Rotation.Y);
        Me.Velocity = Vector3.Zero;
        await Seconds(0.5);
        Expect(HornGround.At(Me) == null && HornGround.InteractionFor(Me) == null && !HornGround.TryInteract(Me), "away from the row there is no prompt");
        svc.ServeHornStart(AdventureService.LocalPeer, out var none);
        Expect(none == null && refused.Count > 0 && refused[^1].Contains("alphorn row"), $"a seat is refused away from the row ({refused.LastOrDefault()})");

        // ---- at the row ------------------------------------------------------------------------------
        var (rowE, rowN) = HornGround.SpotOf(site);
        var g0 = Me.Global;
        Me.PlaceAt(Me.GlobalPosition + new Vector3((float)(rowE - g0.E), 0.3f, (float)(g0.N - rowN)), Me.Rotation.Y);
        Me.Velocity = Vector3.Zero;
        await Seconds(0.5);
        Expect(HornGround.At(Me) != null && HornGround.InteractionFor(Me) is { } hint && hint.Text.Contains("alphorn"), $"at the row the prompt says so ({HornGround.InteractionFor(Me)?.Text})");

        // bots may sit down: their position is the row's
        var adv = AdventureService.Instance!;
        var position = adv.PositionOf;
        adv.PositionOf = peer => peer < 0 && peer != -9 ? position?.Invoke(AdventureService.LocalPeer) : peer == -9 ? new GlobalPos(site.E + 4000, site.N, 0) : position?.Invoke(peer);
        Expect(svc.ServeHornBot(-9, 1, new Random(1)) is { } far && far.Contains("alphorn row"), "a bot too far from the row gets no seat");

        Expect(HornGround.TryInteract(Me), "E at the row asks for a seat");
        Expect(await Until(() => game.State == HornGame.Phase.Playing, 10), "the server seats this player");
        Expect(game.Part == HornPart.Melody, $"as the melody ({game.Part})");
        var chart = game.Chart;
        Expect(chart.Melody.Length is >= 16 and <= 32 && chart.Bpm is >= 60 and <= 76, $"a song of {chart.Melody.Length} notes at {chart.Bpm:F0} BPM");
        Expect(!HornGround.TryInteract(Me), "no second seat while a song is on");

        // three bots take the other voices through the server's own methods
        var rng = new Random(7);
        Expect(svc.ServeHornBot(-1, 0.95, rng) == null && svc.ServeHornBot(-2, 0.95, rng) == null && svc.ServeHornBot(-3, 0.95, rng) == null, "three bots take the second voice, the drone and the yodel");
        Expect(svc.ServeHornBot(-4, 0.95, rng) is { } full && full.Contains("full"), "a fifth voice finds the row full");
        Expect(svc.HornVoices == 4, $"four voices sit in the song ({svc.HornVoices})");

        // the valley: a ring of walls a kilometre out answers every blow
        game.Ground = (e, n) => Math.Sqrt((e - site.E) * (e - site.E) + (n - site.N) * (n - site.N)) > 1000 ? 300 : 0;
        game.Scripted = true;

        // a forged song and an early one are refused
        var paidBefore = svc.HornPaid.Count;
        refused.Clear();
        Expect(svc.ServeHornSong(-77, [new HornPlay(8, 1, 2)]) is { } nobody && nobody.Contains("no seat"), "blows with no seat are refused");
        svc.AskHornSong([new HornPlay(8, 1, 2)]);
        Expect(refused.Count > 0 && refused[^1].Contains("not over") && svc.HornPaid.Count == paidBefore, $"blows sent before the song is over are refused ({refused.LastOrDefault()})");

        // ---- the song, played frame by frame -----------------------------------------------------------
        double spb = chart.SecondsPerBeat;
        var lanes = game.Lanes;
        bool shot = false;
        foreach (var n in chart.Melody)
        {
            await Frames(() => game.SongTime >= n.Start * spb, 2000);
            game.SetLane(Array.IndexOf(lanes, n.Partial) - game.Lane);
            game.Blow(true);
            if (!shot && DisplayServer.GetName() != "headless" && n.Start > 14)
            {
                shot = true;
                await Seconds(0.3);
                GD.Print($"[hornscheck] shot {Shot("song")}");
            }
            await Frames(() => game.SongTime >= n.End * spb, 2000);
            game.Blow(false);
        }
        Expect(game.Plays.Count == chart.Melody.Length, $"{game.Plays.Count} blows for {chart.Melody.Length} notes");
        Expect(game.EchoesHeard > 0 && game.LastEcho is { } echo && echo.DelaySeconds is > 5.4 and < 6.4,
            $"the valley answered ({game.EchoesHeard} echoes, the last {game.LastEcho?.DelaySeconds:F2} s after the note)");
        Expect(await Until(() => game.Result != null, 30), "the server graded the song and told this player");
        var r = game.Result;
        if (r == null) { await Finish(0); return; }
        Expect(r.Value.Voices == 4 && r.Value.Part == HornPart.Melody, $"four voices were scored ({r.Value.Voices})");
        Expect(r.Value.Points >= r.Value.Max * 0.9, $"the melody scored {r.Value.Points} of {r.Value.Max}");
        Expect(r.Value.Harmony100 >= 130, $"four voices in tune earn a harmony bonus (x{r.Value.Harmony100 / 100.0:F2})");
        Expect(r.Value.Batzen > AlphornRules.BatzenPerSong && r.Value.BoardPoints > r.Value.Points, $"paid {r.Value.Batzen} Batzen and {r.Value.BoardPoints} party points");
        var inst = Svc.Mine.Instances.GetValueOrDefault("test-2027") ?? new FestivalInstance();
        Expect(inst.Earned == r.Value.Batzen && inst.Batzen == r.Value.Batzen + 60 && inst.Points == r.Value.BoardPoints && inst.Best.GetValueOrDefault(HornGround.GameId) == r.Value.BoardPoints, $"the book holds it: the song's Batzen, the Warm-up and Crowd milestones' 60 on top ({inst.Earned}/{inst.Batzen} Batzen, {inst.Points} points)");
        Expect(svc.BoardOf(FestivalCatalog.TestId) is { } board && board.Points == r.Value.BoardPoints, "the party board moved");
        Expect(svc.HornPaid.Count(p => p.Peer < 0) == 3 && svc.HornPaid.All(p => p.Peer > 0 || p.Result.Batzen == 0), "the bots made up the field and earned nothing");
        Expect(svc.HornVoices == 0, "the song is over");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(0.3);
            GD.Print($"[hornscheck] shot {Shot("result")}");
        }

        // ---- the yodel: three bots sit down first, so this player is the fourth voice and taps ------------------
        game.Scripted = false;
        Expect(await Until(() => game.Idle, 5), "the screen is ready for another song");
        game.Scripted = true;
        refused.Clear();
        Expect(svc.ServeHornBot(-1, 0.95, rng) == null && svc.ServeHornBot(-2, 0.95, rng) == null && svc.ServeHornBot(-3, 0.95, rng) == null, "three bots sit down");
        Expect(HornGround.TryInteract(Me) && await Until(() => game.State == HornGame.Phase.Playing, 10) && game.Part == HornPart.Yodel, $"this player is the yodel ({game.Part})");
        chart = game.Chart;
        spb = chart.SecondsPerBeat;
        foreach (var n in chart.Of(HornPart.Yodel))
        {
            await Frames(() => game.SongTime >= n.Start * spb, 2000);
            game.Blow(true);
        }
        Expect(await Until(() => game.Result != null && game.Result.Value.Part == HornPart.Yodel, 30), "the yodel was graded");
        r = game.Result!;
        Expect(r.Value.Points >= r.Value.Max * 0.9 && r.Value.Voices == 4, $"the taps scored {r.Value.Points} of {r.Value.Max} with {r.Value.Voices} voices");
        Expect(r.Value.Batzen > 0 && Svc.Mine.Instances["test-2027"].Earned <= FestivalCatalog.Get(FestivalCatalog.TestId)!.Game(HornGround.GameId)!.DayCap, "paid within the game's daily cap");

        game.Scripted = false;
        adv.PositionOf = position;
        await Finish(0);
    }

    /// <summary>Waits for <paramref name="done"/> frame by frame (a song's beat window is shorter than a poll).</summary>
    private async Task Frames(Func<bool> done, int max)
    {
        for (int i = 0; i < max && !done(); i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
