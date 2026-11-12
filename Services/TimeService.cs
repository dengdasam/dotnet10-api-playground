using System.Globalization;
using System.Text.RegularExpressions;

namespace ApiPlayground.Services;

public sealed record ZoneInfo(string Id, string Label);

public static class TimeService
{
    public static readonly ZoneInfo[] Zones =
    [
        new("Asia/Shanghai", "中国标准时间（北京）"),
        new("Asia/Hong_Kong", "中国香港"),
        new("Asia/Macau", "中国澳门"),
        new("Asia/Taipei", "中国台湾（台北）"),
        new("Asia/Tokyo", "日本东京"),
        new("Asia/Seoul", "韩国首尔"),
        new("Asia/Singapore", "新加坡"),
        new("Asia/Dubai", "阿联酋迪拜"),
        new("Europe/London", "英国伦敦"),
        new("Europe/Paris", "法国巴黎"),
        new("America/New_York", "美国纽约"),
        new("America/Los_Angeles", "美国洛杉矶"),
        new("Australia/Sydney", "澳大利亚悉尼"),
        new("UTC", "协调世界时 UTC"),
    ];

    // Windows 时区 ID 兜底：非 ICU 环境下 IANA 标识可能查不到
    private static readonly Dictionary<string, string> WindowsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Hong_Kong"] = "China Standard Time",
        ["Asia/Macau"] = "China Standard Time",
        ["Asia/Taipei"] = "Taipei Standard Time",
        ["Asia/Tokyo"] = "Tokyo Standard Time",
        ["Asia/Seoul"] = "Korea Standard Time",
        ["Asia/Singapore"] = "Singapore Standard Time",
        ["Asia/Dubai"] = "Arabian Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time",
        ["Australia/Sydney"] = "AUS Eastern Standard Time",
        ["UTC"] = "UTC",
    };

    public static object ListZones() => Zones.Select(z => new { id = z.Id, label = z.Label }).ToArray();

    public static object Now(string? tz)
    {
        var zone = Resolve(tz);
        var utc = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return new
        {
            timeZoneId = zone.Id,
            timeZoneLabel = LabelOf(zone.Id),
            utc = utc.ToString("O"),
            local = local.ToString("O"),
            display = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weekday = Weekday(local.DayOfWeek),
            utcOffset = OffsetText(zone.GetUtcOffset(local)),
            isDaylightSavingTime = zone.IsDaylightSavingTime(local),
            unixMs = utc.ToUnixTimeMilliseconds(),
            dayOfYear = local.DayOfYear
        };
    }

    public static object Convert(string? datetime, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(datetime))
            throw new ApiException("MISSING_DATETIME", "请提供 datetime，例如 2026-11-12 13:30 或 2026-11-12T13:30:00+08:00");

        var fromZone = Resolve(from);
        var toZone = Resolve(to);
        var src = ParseDateTime(datetime, fromZone);
        var dst = TimeZoneInfo.ConvertTime(src, toZone);
        return new
        {
            input = datetime,
            from = fromZone.Id,
            fromLabel = LabelOf(fromZone.Id),
            fromUtcOffset = OffsetText(fromZone.GetUtcOffset(src)),
            to = toZone.Id,
            toLabel = LabelOf(toZone.Id),
            toUtcOffset = OffsetText(toZone.GetUtcOffset(dst)),
            sourceUtc = src.UtcDateTime.ToString("O"),
            result = dst.ToString("O"),
            display = dst.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            weekday = Weekday(dst.DayOfWeek),
            shiftHours = Math.Round((toZone.GetUtcOffset(dst) - fromZone.GetUtcOffset(src)).TotalHours, 2)
        };
    }

    public static object Diff(string? start, string? end, string? tz)
    {
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
            throw new ApiException("MISSING_DATETIME", "请同时提供 start 与 end 两个时间");
        var zone = Resolve(tz);
        var a = ParseDateTime(start, zone);
        var b = ParseDateTime(end, zone);
        var delta = b - a;
        var abs = delta.Duration();
        return new
        {
            timeZoneId = zone.Id,
            start = a.ToString("O"),
            end = b.ToString("O"),
            direction = delta.TotalSeconds >= 0 ? "end 晚于或等于 start" : "end 早于 start",
            signedTotalSeconds = Math.Round(delta.TotalSeconds, 3),
            totalDays = Math.Round(delta.TotalDays, 6),
            totalHours = Math.Round(delta.TotalHours, 4),
            totalMinutes = Math.Round(delta.TotalMinutes, 3),
            totalSeconds = Math.Round(delta.TotalSeconds, 3),
            breakdown = new
            {
                days = abs.Days,
                hours = abs.Hours,
                minutes = abs.Minutes,
                seconds = abs.Seconds,
                milliseconds = abs.Milliseconds
            },
            display = $"{abs.Days} 天 {abs.Hours} 小时 {abs.Minutes} 分 {abs.Seconds} 秒"
        };
    }

    private static TimeZoneInfo Resolve(string? id)
    {
        var key = string.IsNullOrWhiteSpace(id) ? "Asia/Shanghai" : id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(key, out var tz)) return tz;
        if (WindowsIds.TryGetValue(key, out var win)
            && TimeZoneInfo.TryFindSystemTimeZoneById(win, out tz)) return tz;
        var byLabel = Zones.FirstOrDefault(z => z.Label.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null && TimeZoneInfo.TryFindSystemTimeZoneById(byLabel.Id, out tz)) return tz;
        throw new ApiException("INVALID_TIMEZONE",
            $"无法识别时区「{key}」。请用 /api/time/zones 查看可用时区（IANA 标识，如 Asia/Shanghai）。");
    }

    private static DateTimeOffset ParseDateTime(string raw, TimeZoneInfo zone)
    {
        var s = raw.Trim();
        // 显式携带 Z 或 ±HH:mm 偏移时按绝对值解析
        var hasOffset = s.EndsWith('Z')
                        || Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$");
        if (hasOffset
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }
        throw new ApiException("INVALID_DATETIME",
            $"无法解析时间「{s}」。可用格式：2026-11-12 13:30、2026-11-12T13:30:00、带 Z 或 +08:00 偏移的 ISO 8601。");
    }

    private static string LabelOf(string id)
        => Zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;

    private static string OffsetText(TimeSpan o)
        => (o < TimeSpan.Zero ? "-" : "+")
           + Math.Abs(o.Hours).ToString("00", CultureInfo.InvariantCulture)
           + ":" + Math.Abs(o.Minutes).ToString("00", CultureInfo.InvariantCulture);

    private static string Weekday(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        DayOfWeek.Sunday => "星期日",
        _ => d.ToString()
    };
}
