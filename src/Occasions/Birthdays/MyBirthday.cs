using System;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net.Accounts;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// This player's own birthday (#1423, docs/notes/occasions/birthdays.md): the Account page's day, month and optional year, whether
/// others may see it, the party hat, and the year of the last gift. Kept in <see cref="GameSettings"/> (the offline copy); signed in,
/// the account's own storage object <c>profile/birthday</c> wins when the account is read (<see cref="Pull"/>), and every change is
/// written back (<see cref="Push"/>), so the date follows the account to another machine.
/// <c>--birthday DD-MM[-YYYY]</c> pins it for one run without saving, and <c>--birthday-public</c> shows it (the checks).
/// "Today" is <see cref="Calendar.Today"/>, so <c>--date</c> moves it too.
/// </summary>
public static class MyBirthday
{
    private const string Collection = "profile", Key = "birthday";

    private static readonly Lazy<Birthday?> Pinned = new(() => Parse(CmdArgs.Value("--birthday")));

    /// <summary>Something about the birthday changed (the date, the switches, the account's copy arrived).</summary>
    public static event Action? Changed;

    private static GameSettings S => GameSettings.Current;

    public static Birthday Date => Pinned.Value ?? new Birthday(S.BirthdayDay, S.BirthdayMonth, S.BirthdayYear);
    public static bool Public => S.BirthdayPublic || CmdArgs.Has("--birthday-public");
    public static bool Hat => S.BirthdayHat;

    /// <summary>Today is this player's birthday.</summary>
    public static bool IsToday => BirthdayRules.IsToday(Date, Calendar.Today);

    /// <summary>The age turned today, or null with no year (or not the birthday).</summary>
    public static int? Age => IsToday ? BirthdayRules.AgeIn(Date, Calendar.Today) : null;

    /// <summary>The title screen's greeting on the birthday, else null.</summary>
    public static string? Greeting(string name) => IsToday ? BirthdayRules.Greeting(name, Age) : null;

    /// <summary>"DD-MM" or "DD-MM-YYYY" as <c>--birthday</c> takes it; null when not a valid birthday.</summary>
    public static Birthday? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('-');
        if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], out int d) || !int.TryParse(parts[1], out int m)) return null;
        int y = parts.Length == 3 && int.TryParse(parts[2], out int yy) ? yy : 0;
        return BirthdayRules.Valid(d, m, y, Calendar.Today) ? new Birthday(d, m, y) : null;
    }

    /// <summary>Sets the date and the switches from the Account page, saves, and writes them to the account when signed in.</summary>
    public static void Set(Birthday date, bool shown, bool hat)
    {
        S.BirthdayDay = date.Day;
        S.BirthdayMonth = date.Month;
        S.BirthdayYear = date.Year;
        S.BirthdayPublic = shown;
        S.BirthdayHat = hat;
        S.Commit();
        Changed?.Invoke();
        _ = Push();
    }

    /// <summary>
    /// The gift is due today (<see cref="BirthdayRules.GiftDue"/>): marks this year's as given, saves, and says true once. The
    /// caller puts the box in the pack.
    /// </summary>
    public static bool TakeGift()
    {
        if (!BirthdayRules.GiftDue(Date, S.BirthdayGiftYear, Calendar.Today)) return false;
        S.BirthdayGiftYear = Calendar.Today.Year;
        S.Commit();
        _ = Push();
        return true;
    }

    private static bool _hooked;

    /// <summary>Follows the account: reads its copy each time it signs in. Called once by the title screen and the client world.</summary>
    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Account.Changed += () => { if (Account.SignedIn) _ = Pull(); };
        if (Account.SignedIn) _ = Pull();
    }

    private sealed record Stored(int Day, int Month, int Year, bool Public, bool Hat, int GiftYear);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Reads the account's copy and takes it; an account with none gets this machine's, if one was entered.</summary>
    public static async Task Pull()
    {
        try
        {
            string url = Account.Url, token = await Account.TokenFor(url);
            if (token.Length == 0) return;
            string? json = await NakamaRest.ReadOwn(url, token, Collection, Key);
            if (json == null)
            {
                if (S.BirthdayMonth != 0) await Push();
                return;
            }
            if (JsonSerializer.Deserialize<Stored>(json, Json) is not { } s) return;
            Callable.From(() =>
            {
                S.BirthdayDay = s.Day;
                S.BirthdayMonth = s.Month;
                S.BirthdayYear = s.Year;
                S.BirthdayPublic = s.Public;
                S.BirthdayHat = s.Hat;
                // the later of the two: a gift taken offline on this machine still counts
                S.BirthdayGiftYear = Math.Max(S.BirthdayGiftYear, s.GiftYear);
                S.Commit();
                Changed?.Invoke();
            }).CallDeferred();
        }
        catch (Exception e) when (e is NakamaException or JsonException)
        {
            GD.Print($"[birthday] could not read the account's birthday: {e.Message}");
        }
    }

    /// <summary>Writes this machine's copy to the account, when signed in.</summary>
    public static async Task Push()
    {
        if (!Account.SignedIn) return;
        try
        {
            string url = Account.Url, token = await Account.TokenFor(url);
            if (token.Length == 0) return;
            var s = new Stored(S.BirthdayDay, S.BirthdayMonth, S.BirthdayYear, S.BirthdayPublic, S.BirthdayHat, S.BirthdayGiftYear);
            await NakamaRest.WriteOwn(url, token, Collection, Key, JsonSerializer.Serialize(s, Json));
        }
        catch (NakamaException e)
        {
            GD.Print($"[birthday] could not save the birthday to the account: {e.Message}");
        }
    }
}
