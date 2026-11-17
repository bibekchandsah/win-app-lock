using System;
using System.Globalization;

namespace AppLock.Services.PcChecker;

/// <summary>Formatting helpers shared by providers and the renderer (sizes, uptime, dates).</summary>
public static class PcFormat
{
    /// <summary>Size in the way Windows shows it (decimal units), e.g. "476 GB".</summary>
    public static string Size(ulong bytes)
    {
        if (bytes >= 1_000_000_000_000UL)
            return (bytes / 1_000_000_000_000d).ToString("0.00", CultureInfo.CurrentCulture) + " TB";
        if (bytes >= 1_000_000_000UL)
            return (bytes / 1_000_000_000d).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
        if (bytes >= 1_000_000UL)
            return (bytes / 1_000_000d).ToString("0", CultureInfo.CurrentCulture) + " MB";
        if (bytes >= 1_000UL)
            return (bytes / 1_000d).ToString("0", CultureInfo.CurrentCulture) + " KB";
        return bytes.ToString(CultureInfo.CurrentCulture) + " B";
    }

    /// <summary>Same size in binary units (GiB), for the units setting.</summary>
    public static string SizeGiB(ulong bytes)
    {
        const double GiB = 1024d * 1024d * 1024d;
        if (bytes >= (ulong)(GiB * 1024))
            return (bytes / (GiB * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " TiB";
        return (bytes / GiB).ToString("0.0", CultureInfo.CurrentCulture) + " GiB";
    }

    public static string Percent(double value)
        => value.ToString("0", CultureInfo.CurrentCulture) + "%";

    public static string Uptime(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")}, {span.Hours} h {span.Minutes} min";
        if (span.TotalHours >= 1)
            return $"{span.Hours} h {span.Minutes} min";
        return $"{span.Minutes} min";
    }

    public static string DateTimeText(DateTime value)
        => value.ToString("g", CultureInfo.CurrentCulture);

    public static string DateText(DateTime value)
        => value.ToString("d", CultureInfo.CurrentCulture);

    public static string Since(DateTime localTime)
    {
        var age = DateTime.Now - localTime;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
    }
}
