namespace ApiPlayground;

/// <summary>
/// 运行期可调参数：全部走环境变量，改完重启即可生效，不用重新编译。
/// 解析失败或非正数时一律退回默认值，避免因为一个拼错的环境变量把服务搞挂。
/// </summary>
public static class AppConfig
{
    private static int Int(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    /// <summary>/api/ratelimit 每分钟额度（API_RATE_LIMIT，默认 20）。</summary>
    public static readonly int RateLimitPerMinute = Int("API_RATE_LIMIT", 20);

    /// <summary>请求日志环形缓冲保留条数（API_LOG_CAPACITY，默认 256）。</summary>
    public static readonly int LogCapacity = Int("API_LOG_CAPACITY", 256);

    /// <summary>天气缓存有效期秒数（API_WEATHER_CACHE_SECONDS，默认 180）。</summary>
    public static readonly int WeatherCacheSeconds = Int("API_WEATHER_CACHE_SECONDS", 180);

    /// <summary>猫咪品种缓存分钟数（API_CAT_CACHE_MINUTES，默认 30）。</summary>
    public static readonly int CatBreedsCacheMinutes = Int("API_CAT_CACHE_MINUTES", 30);

    /// <summary>狗狗索引磁盘缓存有效期天数（API_DOG_CACHE_DAYS，默认 7）。</summary>
    public static readonly int DogCacheDays = Int("API_DOG_CACHE_DAYS", 7);

    /// <summary>SSE 演示端点单次最多推多少条事件（API_SSE_MAX_EVENTS，默认 40），防止被当成常驻连接。</summary>
    public static readonly int SseMaxEvents = Int("API_SSE_MAX_EVENTS", 40);

    /// <summary>给 /api/config 用：把当前生效值原样列出来，方便确认环境变量有没有被读到。</summary>
    public static object Snapshot(string listenUrl) => new
    {
        listenUrl,
        rateLimitPerMinute = RateLimitPerMinute,
        requestLogCapacity = LogCapacity,
        weatherCacheSeconds = WeatherCacheSeconds,
        catBreedsCacheMinutes = CatBreedsCacheMinutes,
        dogCacheDays = DogCacheDays,
        sseMaxEvents = SseMaxEvents,
        env = new
        {
            rateLimit = "API_RATE_LIMIT",
            logCapacity = "API_LOG_CAPACITY",
            weatherCache = "API_WEATHER_CACHE_SECONDS",
            catCache = "API_CAT_CACHE_MINUTES",
            dogCache = "API_DOG_CACHE_DAYS",
            sseEvents = "API_SSE_MAX_EVENTS",
            url = "API_URL"
        },
        note = "以上均为环境变量覆盖，改完重启生效；未设置时使用默认值"
    };
}
