using System.Text.Json;

namespace ApiPlayground.Services;

/// <summary>汇率快照：Rates[X] 表示 1 USD 可兑换多少个 X。</summary>
public sealed record RateSnapshot(
    Dictionary<string, double> Rates,
    string Source,
    string UpdatedAt,
    string Provider,
    string Note);

/// <summary>
/// 汇率数据源：open.er-api.com 公开免 Key 接口（每日更新，以 USD 为基准），
/// 上游不可用或超时时降级为内置快照。成功后缓存 30 分钟，避免每次换算都打上游。
/// </summary>
public static class ExchangeRateService
{
    private const string Endpoint = "https://open.er-api.com/v6/latest/USD";
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    // 单航班：并发首次换算不会同时打 N 次上游
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static readonly string[] Codes =
        ["CNY", "USD", "EUR", "JPY", "GBP", "HKD", "TWD", "MOP", "KRW", "SGD", "AUD", "CAD", "CHF", "THB"];

    public static readonly (string Code, string Label)[] Labels =
    [
        ("CNY", "人民币 (CNY)"),
        ("USD", "美元 (USD)"),
        ("EUR", "欧元 (EUR)"),
        ("JPY", "日元 (JPY)"),
        ("GBP", "英镑 (GBP)"),
        ("HKD", "港元 (HKD)"),
        ("TWD", "新台币 (TWD)"),
        ("MOP", "澳门元 (MOP)"),
        ("KRW", "韩元 (KRW)"),
        ("SGD", "新加坡元 (SGD)"),
        ("AUD", "澳元 (AUD)"),
        ("CAD", "加元 (CAD)"),
        ("CHF", "瑞士法郎 (CHF)"),
        ("THB", "泰铢 (THB)"),
    ];

    // 2026-11-12 上游实际返回值，作为断网时的参考快照
    private static readonly Dictionary<string, double> FallbackRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CNY"] = 6.685797,
        ["USD"] = 1,
        ["EUR"] = 0.899865,
        ["JPY"] = 159.51378,
        ["GBP"] = 0.752421,
        ["HKD"] = 7.84507,
        ["TWD"] = 32.880042,
        ["MOP"] = 8.080353,
        ["KRW"] = 1336.604237,
        ["SGD"] = 1.283658,
        ["AUD"] = 1.45935,
        ["CAD"] = 1.423964,
        ["CHF"] = 0.831838,
        ["THB"] = 33.722639,
    };

    private static volatile RateSnapshot? _cache;
    private static DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

    public static async Task<RateSnapshot> GetAsync()
    {
        if (TryGetCached(out var cached)) return cached;

        if (DateTimeOffset.UtcNow - _lastFailure < TimeSpan.FromMinutes(2))
            return Fallback("上游近期不可用，暂用内置快照");

        await Gate.WaitAsync();
        try
        {
            // 等锁期间可能已被别的请求填好
            if (TryGetCached(out cached)) return cached;
            if (DateTimeOffset.UtcNow - _lastFailure < TimeSpan.FromMinutes(2))
                return Fallback("上游近期不可用，暂用内置快照");

            try
            {
                var snapshot = await FetchLiveAsync();
                _cache = snapshot;
                _cacheAt = DateTimeOffset.UtcNow;
                return snapshot;
            }
            catch
            {
                _lastFailure = DateTimeOffset.UtcNow;
                return Fallback("上游汇率服务不可用或响应异常");
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>启动时预热一次，把上游 1 秒级的往返开销挪到用户请求之外。</summary>
    public static async Task WarmupAsync()
    {
        try { await GetAsync(); } catch { /* 预热失败不影响服务 */ }
    }

    private static bool TryGetCached(out RateSnapshot snapshot)
    {
        var hit = _cache;
        if (hit is not null && DateTimeOffset.UtcNow - _cacheAt < CacheTtl)
        {
            snapshot = hit;
            return true;
        }
        snapshot = null!;
        return false;
    }

    private static async Task<RateSnapshot> FetchLiveAsync()
    {
        using var resp = await Http.GetAsync(Endpoint);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var result) || result.GetString() != "success")
            throw new InvalidOperationException("上游返回非 success 结果");
        if (!root.TryGetProperty("rates", out var ratesNode) || ratesNode.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("上游缺少 rates 字段");

        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in ratesNode.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.GetDouble() > 0)
                rates[prop.Name.ToUpperInvariant()] = prop.Value.GetDouble();
        }
        foreach (var code in Codes)
        {
            if (!rates.ContainsKey(code))
                throw new InvalidOperationException($"上游缺少币种 {code}");
        }

        var updated = root.TryGetProperty("time_last_update_utc", out var u)
            ? u.GetString() ?? DateTime.UtcNow.ToString("R")
            : DateTime.UtcNow.ToString("R");
        var provider = root.TryGetProperty("provider", out var p)
            ? p.GetString() ?? "exchangerate-api.com open access"
            : "exchangerate-api.com open access";

        return new RateSnapshot(rates, "live", updated, provider, "上游每日更新的参考汇率，以 USD 为基准做交叉换算");
    }

    private static RateSnapshot Fallback(string reason)
        => new(
            // 兜底快照是只读的，直接复用同一份字典，省掉每次降级时的整表复制
            FallbackRates,
            "fallback",
            "内置快照 2026-11-12（非实时）",
            "本地内置参考快照",
            reason + "，已使用内置快照汇率");
}
