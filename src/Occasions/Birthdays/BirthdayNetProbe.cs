using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// <c>--birthdaynet guest|celebrant</c> with <c>--connect</c> (driven by <c>tools/birthdaynetcheck.sh</c>, #1423): a server on a fixed
/// <c>--date</c>, Tom (<c>guest</c>, in first) and Lea (<c>celebrant</c>, <c>--birthday</c> today with a year, shown). What each reads
/// is what reached it from the other, the remote peer:
/// <list type="number">
/// <item>Tom hears the server's chat line with Lea's age, and Lea is in his list of celebrants;</item>
/// <item>Tom stands by Lea and wishes her a happy birthday: Lea reads "Tom wished you a happy birthday", both see the paper
/// (the item event over Lea's head), Tom is offered no second wish, and Lea's <c>/birthday</c> counts one wish;</item>
/// <item>Lea lights birthday fireworks: Tom's sky plays the same show (the seed from her name and the day).</item>
/// </list>
/// </summary>
public partial class BirthdayNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--birthdaynet");

    public BirthdayNetProbe(ItemController items) : base(items, "birthdaynet", "BD") { }
    public BirthdayNetProbe() : this(null!) { }

    private static BirthdayService Svc => BirthdayService.Instance!;
    private readonly List<string> _lines = new(), _toasts = new();

    public override async void _Ready()
    {
        _role = Role ?? "GUEST";
        // the chat from the start: the birthday's line comes as Lea joins, maybe before our player is on the ground
        await Until(() => Chat != null && BirthdayService.Instance != null, 120);
        Chat!.LineReceived += (line, _) => _lines.Add(line);
        var toast = Svc.Toast;
        Svc.Toast = t => { _toasts.Add(t); toast?.Invoke(t); };
        GD.Print($"{Log} listening");
        if (!await Joined(150, () => BirthdayService.Instance != null)) return;
        if (_role == "GUEST") await Guest();
        else await Celebrant();
        await Finish(6.0);
    }

    private (long Peer, FootPlayer Body)? Other()
    {
        var other = PlayerSnapshot.Of(GetTree()).Select(s => s.Player).FirstOrDefault(p => p != Me && !p.Npc);
        return other != null && FootPlayer.NetId(other.Name) is { } id ? (id, other) : null;
    }

    private async Task Guest()
    {
        Expect(await Until(() => _lines.Any(l => l.Contains("Lea turns 30 today")), 120), "the chat line: Lea turns 30 today");
        Expect(await Until(() => Other() != null, 60), "Lea's body is here");
        var (lea, body) = Other()!.Value;
        Expect(await Until(() => Svc.Celebrants.ContainsKey(lea), 10), "Lea is a celebrant here");
        Expect(Svc.CanWish(lea), "a wish is offered at Lea");
        // stand by her: the server checks the distance from what we publish
        Me!.GlobalPosition = body.GlobalPosition + new Vector3(1.5f, 0.3f, 0);
        await Seconds(1.0);
        int paper = BirthdayFx.Count;
        Svc.Wish(lea);
        Expect(!Svc.CanWish(lea), "no second wish offered");
        Expect(await Until(() => _toasts.Any(t => t.Contains("You wished Lea")), 10), "the server confirmed the wish");
        Expect(await Until(() => BirthdayFx.Count > paper, 10), "the paper over Lea's head, here");
        Say("wished");
        Expect(await Until(() => Festivals.Fireworks.Instance?.PlayingOwn == true, 30), "Lea's birthday fireworks play here");
        Expect(Festivals.Fireworks.Instance?.ShowSeed == BirthdayRules.ShowSeed("Lea", Calendar.Today, 1),
            $"the same show as Lea's (seed {Festivals.Fireworks.Instance?.ShowSeed})");
    }

    private async Task Celebrant()
    {
        Expect(MyBirthday.IsToday && MyBirthday.Age == 30, "it is Lea's 30th birthday");
        Expect(await Until(() => Svc.Celebrants.ContainsKey(Svc.Self), 20), "the server lists Lea as a celebrant");
        Expect(await Heard("GUEST", "wished", 120), "Tom wished");
        Expect(await Until(() => _toasts.Any(t => t.Contains("Tom wished you a happy birthday")), 10), "Lea read Tom's wish");
        Expect(BirthdayFx.Count > 0, "the paper over Lea's head, here too");
        Chat!.Send("/birthday");
        Expect(await Until(() => _lines.Any(l => l.Contains("1 person wished you a happy birthday today")), 10), "/birthday counts one wish");
        Expect(Svc.TryLight(Me!, out string? why), $"Lea lights her fireworks ({why})");
        Expect(await Until(() => Festivals.Fireworks.Instance?.PlayingOwn == true, 30), "they play here");
    }
}
