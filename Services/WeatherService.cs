using System.Globalization;
using System.Text.Json;

namespace ApiPlayground.Services;

public sealed record CityInfo(string Name, string Region, double Latitude, double Longitude);

/// <summary>
/// 天气：优先 Open-Meteo 实时观测（免 Key），上游不可用时降级为确定性本地模拟数据。
/// 内置一张中文城市表，常用城市无需走地理编码，响应更快也更稳。
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // 上游连续失败时短暂抑制重试，避免每次请求都白等一个超时
    private static DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private static bool Suppressed => DateTimeOffset.UtcNow - _lastFailure < TimeSpan.FromMinutes(2);

    private static readonly CityInfo[] CityTable =
    [
        new("北京", "北京", 39.9042, 116.4074),
        new("上海", "上海", 31.2304, 121.4737),
        new("广州", "广东", 23.1291, 113.2644),
        new("深圳", "广东", 22.5431, 114.0579),
        new("宁波", "浙江", 29.8683, 121.5440),
        new("杭州", "浙江", 30.2741, 120.1551),
        new("来宾", "广西", 23.7500, 109.2333),
        new("南宁", "广西", 22.8170, 108.3669),
        new("北海", "广西", 21.4813, 109.1201),
        new("防城港", "广西", 21.6144, 108.3298),
        new("湛江", "广东", 21.2707, 110.3594),
        new("厦门", "福建", 24.4798, 118.0894),
        new("成都", "四川", 30.5728, 104.0668),
        new("重庆", "重庆", 29.5630, 106.5516),
        new("武汉", "湖北", 30.5928, 114.3055),
        new("西安", "陕西", 34.3416, 108.9398),
        new("南京", "江苏", 32.0603, 118.7969),
        new("青岛", "山东", 36.0671, 120.3826),
        new("三亚", "海南", 18.2528, 109.5119),
        new("海口", "海南", 20.0444, 110.1999),
        new("哈尔滨", "黑龙江", 45.8038, 126.5349),
        new("拉萨", "西藏", 29.6525, 91.1721),
        new("中国香港", "中国香港", 22.3193, 114.1694),
        new("中国澳门", "中国澳门", 22.1987, 113.5439),
        new("中国台北", "中国台湾", 25.0330, 121.5654),
    ];

    public static object ListCities() => CityTable.Select(c => new
    {
        name = c.Name,
        region = c.Region,
        latitude = c.Latitude,
        longitude = c.Longitude
    }).ToArray();

    public async Task<object> GetAsync(string? cityInput)
    {
        var query = (cityInput ?? "").Trim();
        if (query.Length == 0)
            throw new ApiException("MISSING_CITY", "请提供城市名，例如 city=宁波");
        if (query.Length > 40)
            throw new ApiException("INVALID_CITY", "城市名过长（上限 40 字符）");

        string name = query, region = "";
        double? lat = null, lon = null;

        var builtIn = FindBuiltIn(query);
        if (builtIn is not null)
        {
            name = builtIn.Name;
            region = builtIn.Region;
            lat = builtIn.Latitude;
            lon = builtIn.Longitude;
        }

        if (lat is null)
        {
            if (Suppressed) return Fallback(query, "", null, null, "上游近期不可用，暂走降级");
            try
            {
                var geo = await GeocodeAsync(query);
                name = geo.Name;
                region = geo.Region;
                lat = geo.Latitude;
                lon = geo.Longitude;
            }
            catch
            {
                _lastFailure = DateTimeOffset.UtcNow;
                return Fallback(query, "", null, null, "城市解析服务不可用");
            }
        }

        // 显式收窄可空性：内置城市或地理编码必须给出完整经纬度
        if (lat is not double latitude || lon is not double longitude)
            return Fallback(name, region, null, null, "无法确定城市经纬度");

        if (Suppressed)
            return Fallback(name, region, latitude, longitude, "上游近期不可用，暂走降级");

        try
        {
            return await FetchLiveAsync(name, region, latitude, longitude);
        }
        catch
        {
            _lastFailure = DateTimeOffset.UtcNow;
            return Fallback(name, region, lat, lon, "上游天气服务不可用或响应异常");
        }
    }

    private static CityInfo? FindBuiltIn(string query)
    {
        var key = Normalize(query);
        return CityTable.FirstOrDefault(c =>
            Normalize(c.Name) == key || c.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string s)
        => s.Trim().Replace("市", "").Replace("中国", "").ToLowerInvariant();

    private static async Task<(string Name, string Region, double Latitude, double Longitude)> GeocodeAsync(string query)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?name="
                  + Uri.EscapeDataString(query) + "&count=1&language=zh&format=json";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("results", out var arr)
            || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new InvalidOperationException("地理编码无匹配城市");

        var first = arr[0];
        var name = first.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
        var region = first.TryGetProperty("admin1", out var r)
            ? r.GetString() ?? ""
            : first.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
        return (name, region, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    private static async Task<object> FetchLiveAsync(string name, string region, double lat, double lon)
    {
        var url = "https://api.open-meteo.com/v1/forecast"
                  + "?latitude=" + lat.ToString(CultureInfo.InvariantCulture)
                  + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                  + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m"
                  + "&timezone=auto";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("current", out var cur))
            throw new InvalidOperationException("上游响应缺少 current 字段");

        var code = Number(cur, "weather_code") ?? -1;
        return new
        {
            city = name,
            region,
            latitude = lat,
            longitude = lon,
            observedAt = Text(cur, "time"),
            timezone = Text(doc.RootElement, "timezone"),
            temperatureC = Number(cur, "temperature_2m"),
            feelsLikeC = Number(cur, "apparent_temperature"),
            humidityPercent = Number(cur, "relative_humidity_2m"),
            precipitationMm = Number(cur, "precipitation"),
            windSpeedKmh = Number(cur, "wind_speed_10m"),
            weatherCode = (int)code,
            weatherText = Wmo((int)code),
            source = "live",
            provider = "Open-Meteo /v1/forecast",
            note = "上游实时观测数据"
        };
    }

    private static double? Number(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? Math.Round(v.GetDouble(), 1)
            : null;

    private static string? Text(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static object Fallback(string name, string region, double? lat, double? lon, string reason)
    {
        // 以「城市名 + UTC 小时」生成稳定种子：同一小时内多次调用结果一致，跨进程也一致
        var stamp = name + "|" + DateTime.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        var seed = 17;
        foreach (var ch in stamp) seed = unchecked(seed * 31 + ch);
        var rnd = new Random(seed);

        int[] codes = [0, 1, 2, 3, 61, 80];
        var code = codes[rnd.Next(codes.Length)];
        var temp = Math.Round(6 + rnd.NextDouble() * 26, 1);
        return new
        {
            city = name,
            region,
            latitude = lat,
            longitude = lon,
            observedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm"),
            timezone = "Asia/Shanghai",
            temperatureC = temp,
            feelsLikeC = Math.Round(temp - 2 + rnd.NextDouble() * 4, 1),
            humidityPercent = rnd.Next(35, 95),
            precipitationMm = code is 61 or 80 ? Math.Round(rnd.NextDouble() * 3, 1) : 0.0,
            windSpeedKmh = Math.Round(2 + rnd.NextDouble() * 22, 1),
            weatherCode = code,
            weatherText = Wmo(code),
            source = "fallback",
            provider = "本地确定性模拟",
            note = reason + "，已返回本地模拟数据（按城市名 + UTC 小时生成，同一小时内结果稳定）"
        };
    }

    private static string Wmo(int c) => c switch
    {
        0 => "晴",
        1 => "大部晴朗",
        2 => "局部多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 => "阵雨",
        81 => "中阵雨",
        82 => "强阵雨",
        85 => "小阵雪",
        86 => "大阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => "未知天气码"
    };
}
