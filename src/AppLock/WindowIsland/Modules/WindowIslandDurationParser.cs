using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AppLock.WindowIsland.Modules;

/// <summary>
/// Robust duration parser for custom timer inputs (e.g. "45m", "1h 30m", "90s", "15:00", "25").
/// </summary>
public static class WindowIslandDurationParser
{
    public static bool TryParse(string? input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = input.Trim().ToLowerInvariant();
        if (s.Contains('-')) return false;

        // 1. Colon format: mm:ss or hh:mm:ss
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int p1) && int.TryParse(parts[1], out int p2))
            {
                duration = TimeSpan.FromMinutes(p1) + TimeSpan.FromSeconds(p2);
                return duration > TimeSpan.Zero;
            }
            if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int sec))
            {
                duration = new TimeSpan(h, m, sec);
                return duration > TimeSpan.Zero;
            }
        }

        // 2. Pure number: e.g. "25" -> default to minutes
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawVal))
        {
            if (rawVal > 0)
            {
                duration = TimeSpan.FromMinutes(rawVal);
                return true;
            }
            return false;
        }

        // 3. Regex matching tokens: (\d+(?:\.\d+)?)\s*(h|hr|hour|hours|m|min|minute|minutes|s|sec|second|seconds)
        var matches = Regex.Matches(s, @"(?<val>\d+(?:\.\d+)?)\s*(?<unit>[a-z]+)");
        if (matches.Count > 0)
        {
            double totalSeconds = 0;
            bool matchedAny = false;
            foreach (Match match in matches)
            {
                if (double.TryParse(match.Groups["val"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    string u = match.Groups["unit"].Value;
                    if (u.StartsWith("h"))
                    {
                        totalSeconds += val * 3600.0;
                        matchedAny = true;
                    }
                    else if (u.StartsWith("m"))
                    {
                        totalSeconds += val * 60.0;
                        matchedAny = true;
                    }
                    else if (u.StartsWith("s"))
                    {
                        totalSeconds += val;
                        matchedAny = true;
                    }
                }
            }
            if (matchedAny && totalSeconds > 0)
            {
                duration = TimeSpan.FromSeconds(totalSeconds);
                return true;
            }
        }

        return false;
    }
}
