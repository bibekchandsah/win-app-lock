using System;
using System.Text.RegularExpressions;

namespace AppLock.WindowIsland.Modules;

/// <summary>Result of parsing a natural language reminder phrase.</summary>
public sealed record ReminderParseResult(bool Success, string Title, DateTime DueTimeUtc, bool HasExplicitTime = true);

/// <summary>
/// Small, robust natural language parser for reminder phrases (island.md Section 9.5).
/// Parses: "Call mum tomorrow at 9", "watch youtube for 5min", "in 20 minutes turn the stove off", "at 23:00", "Friday 5pm".
/// </summary>
public static class WindowIslandReminderParser
{
    public static ReminderParseResult Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ReminderParseResult(false, string.Empty, DateTime.MinValue, HasExplicitTime: false);

        string raw = input.Trim();
        DateTime now = DateTime.Now;

        // 1. "in/for X minute(s)/hour(s)/day(s)/second(s)" or bare "X min/hour"
        var relMatch = Regex.Match(raw, @"\b(?:in|for)\s+(\d+(?:\.\d+)?)\s*(seconds?|secs?|sec|s|minutes?|mins?|min|m|hours?|hrs?|hr|h|days?|d)\b", RegexOptions.IgnoreCase);
        if (!relMatch.Success)
        {
            relMatch = Regex.Match(raw, @"\b(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|min|hours?|hrs?|days?)\b", RegexOptions.IgnoreCase);
        }

        if (relMatch.Success)
        {
            if (double.TryParse(relMatch.Groups[1].Value, out double amount) && amount > 0)
            {
                string unit = relMatch.Groups[2].Value.ToLowerInvariant();
                TimeSpan span = unit switch
                {
                    var u when u.StartsWith("s") => TimeSpan.FromSeconds(amount),
                    var u when u.StartsWith("m") => TimeSpan.FromMinutes(amount),
                    var u when u.StartsWith("h") => TimeSpan.FromHours(amount),
                    var u when u.StartsWith("d") => TimeSpan.FromDays(amount),
                    _ => TimeSpan.Zero
                };

                if (span > TimeSpan.Zero)
                {
                    string title = ExtractTitle(raw, relMatch.Index, relMatch.Length);
                    return new ReminderParseResult(true, title, (now + span).ToUniversalTime(), HasExplicitTime: true);
                }
            }
        }

        // 2. "tomorrow at H(:mm)?\s*(am|pm)?" or "tomorrow"
        var tomMatch = Regex.Match(raw, @"\btomorrow(?:\s+at)?(?:\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?\b", RegexOptions.IgnoreCase);
        if (tomMatch.Success)
        {
            DateTime targetDate = now.Date.AddDays(1);
            int hour = 9; // default 9:00 AM
            int min = 0;

            if (tomMatch.Groups[1].Success && int.TryParse(tomMatch.Groups[1].Value, out int h))
            {
                if (tomMatch.Groups[2].Success && int.TryParse(tomMatch.Groups[2].Value, out int m))
                    min = Math.Clamp(m, 0, 59);

                bool isPm = string.Equals(tomMatch.Groups[3].Value, "pm", StringComparison.OrdinalIgnoreCase);
                bool isAm = string.Equals(tomMatch.Groups[3].Value, "am", StringComparison.OrdinalIgnoreCase);

                if (isPm && h < 12) h += 12;
                else if (isAm && h == 12) h = 0;

                hour = Math.Clamp(h, 0, 23);
            }

            DateTime dueLocal = targetDate.AddHours(hour).AddMinutes(min);
            string title = ExtractTitle(raw, tomMatch.Index, tomMatch.Length);
            return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
        }

        // 3. Month + Day (e.g. "on November 19 at 4", "Nov 19 at 4:30pm", "19th Nov 16:00", "Dec 3")
        const string monthsPattern = "January|Jan|February|Feb|March|Mar|April|Apr|May|June|Jun|July|Jul|August|Aug|September|Sept|Sep|October|Oct|November|Nov|December|Dec";
        var monthDayMatch = Regex.Match(
            raw,
            $@"\b(?:on\s+)?({monthsPattern})\s+(\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s*(\d{{4}}))?(?:(?:\s+at|\s*,)?\s+(\d{{1,2}})(?::(\d{{2}}))?\s*(am|pm)?)?\b",
            RegexOptions.IgnoreCase);
        var dayMonthMatch = !monthDayMatch.Success
            ? Regex.Match(
                raw,
                $@"\b(?:on\s+)?(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?({monthsPattern})(?:,?\s*(\d{{4}}))?(?:(?:\s+at|\s*,)?\s+(\d{{1,2}})(?::(\d{{2}}))?\s*(am|pm)?)?\b",
                RegexOptions.IgnoreCase)
            : Match.Empty;

        if (monthDayMatch.Success || dayMonthMatch.Success)
        {
            Match m = monthDayMatch.Success ? monthDayMatch : dayMonthMatch;
            string monthStr = monthDayMatch.Success ? m.Groups[1].Value : m.Groups[2].Value;
            string dayStr   = monthDayMatch.Success ? m.Groups[2].Value : m.Groups[1].Value;

            if (TryParseMonth(monthStr, out int month) && int.TryParse(dayStr, out int day) && day >= 1 && day <= 31)
            {
                int year = now.Year;
                int parsedYear = 0;
                bool explicitYear = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out parsedYear);
                if (explicitYear) year = Math.Clamp(parsedYear, now.Year, 2100);

                int hour = 9;
                int min = 0;
                if (m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int mh))
                {
                    if (m.Groups[5].Success && int.TryParse(m.Groups[5].Value, out int mm))
                        min = Math.Clamp(mm, 0, 59);

                    bool isPm = string.Equals(m.Groups[6].Value, "pm", StringComparison.OrdinalIgnoreCase);
                    bool isAm = string.Equals(m.Groups[6].Value, "am", StringComparison.OrdinalIgnoreCase);

                    if (isPm && mh < 12) mh += 12;
                    else if (isAm && mh == 12) mh = 0;

                    hour = Math.Clamp(mh, 0, 23);
                }

                int maxDay = DateTime.DaysInMonth(year, month);
                int clampedDay = Math.Min(day, maxDay);
                DateTime dueLocal = new DateTime(year, month, clampedDay, hour, min, 0, DateTimeKind.Local);
                if (!explicitYear && dueLocal <= now)
                {
                    int nextYear = year + 1;
                    dueLocal = new DateTime(nextYear, month, Math.Min(day, DateTime.DaysInMonth(nextYear, month)), hour, min, 0, DateTimeKind.Local);
                }

                string title = ExtractTitle(raw, m.Index, m.Length);
                return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
            }
        }

        // 4. Day of week e.g. "Friday 5pm" or "Monday at 10"
        var dowMatch = Regex.Match(raw, @"\b(?:on\s+)?(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)(?:\s+at)?(?:\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?\b", RegexOptions.IgnoreCase);
        if (dowMatch.Success && Enum.TryParse<DayOfWeek>(dowMatch.Groups[1].Value, true, out var targetDow))
        {
            int daysToAdd = ((int)targetDow - (int)now.DayOfWeek + 7) % 7;
            if (daysToAdd == 0) daysToAdd = 7; // next occurrence

            int hour = 9;
            int min = 0;
            if (dowMatch.Groups[2].Success && int.TryParse(dowMatch.Groups[2].Value, out int dh))
            {
                if (dowMatch.Groups[3].Success && int.TryParse(dowMatch.Groups[3].Value, out int dm))
                    min = Math.Clamp(dm, 0, 59);

                bool isPm = string.Equals(dowMatch.Groups[4].Value, "pm", StringComparison.OrdinalIgnoreCase);
                bool isAm = string.Equals(dowMatch.Groups[4].Value, "am", StringComparison.OrdinalIgnoreCase);

                if (isPm && dh < 12) dh += 12;
                else if (isAm && dh == 12) dh = 0;

                hour = Math.Clamp(dh, 0, 23);
            }

            DateTime dueLocal = now.Date.AddDays(daysToAdd).AddHours(hour).AddMinutes(min);
            string title = ExtractTitle(raw, dowMatch.Index, dowMatch.Length);
            return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
        }

        // 5. "at HH:mm" (24-hour e.g. at 23:00, at 21:30)
        var at24Match = Regex.Match(raw, @"\bat\s+(\d{1,2}):(\d{2})\b", RegexOptions.IgnoreCase);
        if (at24Match.Success && int.TryParse(at24Match.Groups[1].Value, out int h24) && int.TryParse(at24Match.Groups[2].Value, out int m24))
        {
            DateTime dueLocal = now.Date.AddHours(Math.Clamp(h24, 0, 23)).AddMinutes(Math.Clamp(m24, 0, 59));
            if (dueLocal <= now) dueLocal = dueLocal.AddDays(1); // if earlier today, schedule for tomorrow

            string title = ExtractTitle(raw, at24Match.Index, at24Match.Length);
            return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
        }

        // 6. "at H\s*(am|pm)?" or "at H:mm\s*(am|pm)"
        var at12Match = Regex.Match(raw, @"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", RegexOptions.IgnoreCase);
        if (at12Match.Success && int.TryParse(at12Match.Groups[1].Value, out int h12))
        {
            int min12 = 0;
            if (at12Match.Groups[2].Success && int.TryParse(at12Match.Groups[2].Value, out int m12))
                min12 = Math.Clamp(m12, 0, 59);

            bool isPm = string.Equals(at12Match.Groups[3].Value, "pm", StringComparison.OrdinalIgnoreCase);
            bool isAm = string.Equals(at12Match.Groups[3].Value, "am", StringComparison.OrdinalIgnoreCase);

            if (isPm && h12 < 12) h12 += 12;
            else if (isAm && h12 == 12) h12 = 0;
            else if (!isPm && !isAm)
            {
                if (h12 < 12 && now.Hour >= h12 && now.Hour < h12 + 12)
                    h12 += 12;
            }

            DateTime dueLocal = now.Date.AddHours(Math.Clamp(h12, 0, 23)).AddMinutes(min12);
            if (dueLocal <= now) dueLocal = dueLocal.AddDays(1);

            string title = ExtractTitle(raw, at12Match.Index, at12Match.Length);
            return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
        }

        // 7. "tonight", "this evening", "this afternoon", "next week"
        var phraseMatch = Regex.Match(raw, @"\b(tonight|this\s+evening|this\s+afternoon|next\s+week)\b", RegexOptions.IgnoreCase);
        if (phraseMatch.Success)
        {
            string p = Regex.Replace(phraseMatch.Groups[1].Value.ToLowerInvariant(), @"\s+", " ");
            DateTime dueLocal = p switch
            {
                "tonight" => now.Date.AddHours(20),
                "this evening" => now.Date.AddHours(18),
                "this afternoon" => now.Date.AddHours(14),
                _ => now.Date.AddDays(7).AddHours(9)
            };
            if (dueLocal <= now) dueLocal = dueLocal.AddDays(1);
            string title = ExtractTitle(raw, phraseMatch.Index, phraseMatch.Length);
            return new ReminderParseResult(true, title, dueLocal.ToUniversalTime(), HasExplicitTime: true);
        }

        // Fallback: no explicit time specified
        return new ReminderParseResult(true, raw, now.AddMinutes(10).ToUniversalTime(), HasExplicitTime: false);
    }

    private static bool TryParseMonth(string text, out int month)
    {
        month = text.Trim().ToLowerInvariant() switch
        {
            "january" or "jan" => 1,
            "february" or "feb" => 2,
            "march" or "mar" => 3,
            "april" or "apr" => 4,
            "may" => 5,
            "june" or "jun" => 6,
            "july" or "jul" => 7,
            "august" or "aug" => 8,
            "september" or "sept" or "sep" => 9,
            "october" or "oct" => 10,
            "november" or "nov" => 11,
            "december" or "dec" => 12,
            _ => 0
        };
        return month > 0;
    }

    public static string FormatPill(DateTime dueTimeUtc)
    {
        DateTime dueLocal = dueTimeUtc.ToLocalTime();
        DateTime now = DateTime.Now;
        TimeSpan diff = dueLocal - now;

        if (diff <= TimeSpan.Zero) return "in 1 min";

        if (diff.TotalMinutes < 60 && dueLocal.Date == now.Date)
        {
            int mins = Math.Max(1, (int)Math.Round(diff.TotalMinutes));
            return $"in {mins} min";
        }

        if (dueLocal.Date == now.Date)
        {
            return $"today {dueLocal:h:mm tt}";
        }

        if (dueLocal.Date == now.Date.AddDays(1))
        {
            return $"tomorrow {dueLocal:h:mm tt}";
        }

        if (diff.TotalDays < 6)
        {
            return $"{dueLocal:dddd} {dueLocal:h:mm tt}";
        }

        return dueLocal.ToString("MMM d, h:mm tt");
    }

    private static string ExtractTitle(string raw, int matchIndex, int matchLength)
    {
        string before = raw[..matchIndex].Trim();
        string after  = raw[(matchIndex + matchLength)..].Trim();
        string title  = $"{before} {after}".Trim(' ', '-', ',', ':');
        return string.IsNullOrEmpty(title) ? "Reminder" : title;
    }
}
