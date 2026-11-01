using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Occasions.Birthdays;

namespace UnitSport.Ui;

/// <summary>
/// The Account page's Birthday section (#1423, docs/notes/occasions/birthdays.md): day and month, the year only if the player wants
/// their age in the chat, whether other players may see it, and the party hat. Saved by <see cref="MyBirthday.Set"/>, which also
/// writes it to the account when signed in. Optional: "Not set" everywhere is the default, and nothing happens.
/// </summary>
public static class BirthdayPage
{
    public static void Fill(VBoxContainer rows)
    {
        rows.AddChild(UiKit.Spacer(14));
        rows.AddChild(UiKit.Section("Birthday"));
        rows.AddChild(UiKit.Text("On your birthday you get a party hat, a gift and fireworks to light. Optional, and the year only shows your age in the chat.",
            UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));

        var today = UnitSport.Core.Calendar.Today;
        var date = MyBirthday.Date;
        var line = UiKit.HBox(8);
        rows.AddChild(line);

        OptionButton Pick(string[] items, int selected, float width)
        {
            var o = new OptionButton { CustomMinimumSize = new Vector2(width, 40), FocusMode = Control.FocusModeEnum.All, FitToLongestItem = false };
            o.ClipText = true;
            foreach (string item in items) o.AddItem(item);
            o.Selected = selected;
            line.AddChild(o);
            return o;
        }

        var days = new string[32];
        days[0] = "Day";
        for (int d = 1; d <= 31; d++) days[d] = d.ToString(CultureInfo.InvariantCulture);
        var months = new string[13];
        months[0] = "Month";
        for (int m = 1; m <= 12; m++) months[m] = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m);
        // newest first; index 0 is "no year"
        int years = today.Year - BirthdayRules.OldestYear + 1;
        var yearItems = new string[years + 1];
        yearItems[0] = "Year (optional)";
        for (int i = 1; i <= years; i++) yearItems[i] = (today.Year - i + 1).ToString(CultureInfo.InvariantCulture);
        int YearIndex(int y) => y <= 0 ? 0 : today.Year - y + 1;

        var day = Pick(days, date.IsSet ? date.Day : 0, 110);
        var month = Pick(months, date.IsSet ? date.Month : 0, 170);
        var year = Pick(yearItems, date.IsSet ? YearIndex(date.Year) : 0, 170);
        var clear = UiKit.Button("Clear");
        clear.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        line.AddChild(clear);

        var note = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
        rows.AddChild(note);

        bool shown = MyBirthday.Public, hat = MyBirthday.Hat;

        void Save()
        {
            int d = day.Selected, m = month.Selected, y = year.Selected == 0 ? 0 : today.Year - year.Selected + 1;
            note.Text = "";
            if (d == 0 || m == 0)
            {
                // half entered: keep nothing until both are chosen
                if (d != 0 || m != 0) note.Text = "Choose both the day and the month.";
                else MyBirthday.Set(Birthday.None, shown, hat);
                return;
            }
            if (!BirthdayRules.Valid(d, m, y, today))
            {
                note.Text = d > BirthdayRules.DaysIn(m) ? $"{months[m]} has no {d}th." : "That date is not a birthday that has come yet.";
                return;
            }
            MyBirthday.Set(new Birthday(d, m, y), shown, hat);
            if (MyBirthday.IsToday) note.Text = BirthdayRules.Greeting("", MyBirthday.Age);
        }

        day.ItemSelected += _ => Save();
        month.ItemSelected += _ => Save();
        year.ItemSelected += _ => Save();
        clear.Pressed += () => { day.Selected = 0; month.Selected = 0; year.Selected = 0; Save(); };

        UiKit.ToggleRow(rows, "Let other players see it's my birthday", shown, on => { shown = on; Save(); },
            "On your birthday, a line in the chat tells everyone (with your age if you gave the year), and they can wish you a happy birthday.");
        UiKit.ToggleRow(rows, "Wear a party hat on my birthday", hat, on => { hat = on; Save(); },
            "Only while nothing else is on your head.");
        if (MyBirthday.IsToday) note.Text = BirthdayRules.Greeting("", MyBirthday.Age);
    }
}
