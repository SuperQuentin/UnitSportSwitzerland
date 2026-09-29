using Godot;

namespace UnitSport.Occasions;

/// <summary>
/// <c>godot --path . -- --occasioncheck</c>
///
/// <para>
/// Walks <see cref="OccasionSchedule"/> across the dates it has to get right — both ends of every
/// default window and the day either side, a window that wraps over new year, a one-off with a
/// year, a leap day — and the config merge. Prints every case and exits non-zero on any mismatch.
/// Pure logic: no world is loaded.
/// </para>
/// </summary>
public static class OccasionProbe
{
    public static bool Requested => OS.GetCmdlineUserArgs().Contains("--occasioncheck");

    public static int Run()
    {
        int failures = 0;

        void Expect(string label, IEnumerable<OccasionEntry> entries, string date, params string[] expected)
        {
            var d = DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var got = OccasionSchedule.Evaluate(entries, d).Select(r => r.Instance).ToArray();
            bool ok = got.SequenceEqual(expected);
            if (!ok) failures++;
            GD.Print($"[occasioncheck] {(ok ? "ok  " : "FAIL")} {label,-28} {date}  -> "
                + $"{(got.Length == 0 ? "none" : string.Join(", ", got))}"
                + (ok ? "" : $"   expected {(expected.Length == 0 ? "none" : string.Join(", ", expected))}"));
        }

        var defaults = OccasionConfig.Defaults();
        Expect("before halloween", defaults, "2026-08-31");
        Expect("halloween opens", defaults, "2026-09-01", "halloween-2026");
        Expect("halloween closes", defaults, "2026-10-31", "halloween-2026");
        Expect("christmas opens", defaults, "2026-11-01", "christmas-2026");
        Expect("christmas closes", defaults, "2026-12-31", "christmas-2026");
        Expect("new year", defaults, "2027-01-01");
        Expect("summer", defaults, "2027-07-14");

        var wrap = new List<OccasionEntry>
        {
            new() { Id = "fetes", Schedule = { new DateWindow("12-20", "01-06") } },
        };
        Expect("wrap: day before", wrap, "2026-12-19");
        Expect("wrap: opens", wrap, "2026-12-20", "fetes-2026");
        Expect("wrap: after new year", wrap, "2027-01-01", "fetes-2026");
        Expect("wrap: closes", wrap, "2027-01-06", "fetes-2026");
        Expect("wrap: day after", wrap, "2027-01-07");

        var oneOff = new List<OccasionEntry>
        {
            new() { Id = "olympics", Schedule = { new DateWindow("2026-02-06", "2026-02-22") } },
        };
        Expect("one-off: opens", oneOff, "2026-02-06", "olympics-2026");
        Expect("one-off: closes", oneOff, "2026-02-22", "olympics-2026");
        Expect("one-off: next year", oneOff, "2027-02-10");

        var leap = new List<OccasionEntry>
        {
            new() { Id = "leap", Schedule = { new DateWindow("02-29", "03-01") } },
            new() { Id = "broken", Schedule = { new DateWindow("13-01", "13-02") } },
            new() { Id = "off", Enabled = false, Schedule = { new DateWindow("01-01", "12-31") } },
        };
        Expect("leap day", leap, "2028-02-29", "leap-2028");
        Expect("no leap day", leap, "2027-03-01", "leap-2027");
        Expect("invalid + disabled", leap, "2027-06-01");

        var stacked = OccasionConfig.Defaults();
        stacked.Add(new OccasionEntry { Id = "games", Schedule = { new DateWindow("10-15", "11-15") } });
        Expect("stacking", stacked, "2026-10-20", "halloween-2026", "games-2026");

        // the file wins per id; a default the file does not mention is still there
        var merged = OccasionConfig.Merge(OccasionConfig.Defaults(), new List<OccasionEntry>
        {
            new() { Id = "Halloween", Enabled = false },
            new() { Id = "community-day", Schedule = { new DateWindow("2026-10-03", "2026-10-03") } },
        });
        Expect("merge: file disables", merged, "2026-10-03", "community-day-2026");
        Expect("merge: default kept", merged, "2026-12-01", "christmas-2026");

        GD.Print(failures == 0 ? "[occasioncheck] all passed" : $"[occasioncheck] {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
