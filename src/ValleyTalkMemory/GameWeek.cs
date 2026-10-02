using System;
using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// In-game calendar math. Stardew Valley: 28 days per season, 4 seasons per year,
/// which divides exactly into four 7-day weeks per season.
/// </summary>
internal static class GameWeek
{
    public const int DaysPerSeason = 28;
    public const int SeasonsPerYear = 4;

    private static readonly string[] SeasonNames = { "Spring", "Summer", "Fall", "Winter" };

    /// <summary>Absolute day index since Spring 1, Year 1 (that day is 0).</summary>
    public static long ToDay(int year, int seasonIndex, int dayOfMonth)
        => ((long)(year - 1) * SeasonsPerYear + seasonIndex) * DaysPerSeason + (dayOfMonth - 1);

    public static long ToDay(WorldDate date)
        => ToDay(date.Year, SeasonIndexOf(date.Season), date.DayOfMonth);

    public static long Today => ToDay(Game1.Date);

    public static int SeasonIndexOf(Season season)
    {
        int i = (int)season;
        return (i >= 0 && i < SeasonsPerYear) ? i : 0;
    }

    public static int SeasonIndexFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        for (int i = 0; i < SeasonNames.Length; i++)
        {
            if (string.Equals(SeasonNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.TryParse(name, out int parsed) ? Math.Max(0, Math.Min(3, parsed)) : 0;
    }

    public static long WeekOf(long day, int daysPerWeek)
        => daysPerWeek <= 0 ? day / 7 : day / daysPerWeek;

    public static long CurrentWeek(int daysPerWeek) => WeekOf(Today, daysPerWeek);

    /// <summary>Renders an absolute day as "Y1 Fall D19".</summary>
    public static string Describe(long day)
    {
        if (day < 0)
            day = 0;
        long yearLen = SeasonsPerYear * DaysPerSeason;
        int year = (int)(day / yearLen) + 1;
        int rem = (int)(day % yearLen);
        return $"Y{year} {SeasonNames[rem / DaysPerSeason]} D{rem % DaysPerSeason + 1}";
    }

    /// <summary>Renders an absolute day plus a Stardew clock value as "Y1 Fall D19 18:20".</summary>
    public static string Describe(long day, int timeOfDay)
    {
        int h = timeOfDay / 100;
        int m = timeOfDay % 100;
        return $"{Describe(day)} {h:00}:{m:00}";
    }

    public static string DescribeWeek(long weekIndex, int daysPerWeek)
    {
        long start = weekIndex * daysPerWeek;
        long end = start + daysPerWeek - 1;
        return daysPerWeek == DaysPerSeason
            ? Describe(start)
            : $"{Describe(start)} ~ {Describe(end)}";
    }

    /// <summary>Human readable "3 days ago" style delta, used in injected text.</summary>
    public static string Relative(long day, long today)
    {
        long diff = today - day;
        if (diff <= 0) return "今天";
        if (diff == 1) return "昨天";
        if (diff < 7) return $"{diff} 天前";
        if (diff < 28) return $"{diff / 7} 周前";
        return Describe(day);
    }
}
