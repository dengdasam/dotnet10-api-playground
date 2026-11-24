using System.Collections.Concurrent;
using System.Diagnostics;

namespace ApiPlayground;

/// <summary>
/// 令牌桶限流：按客户端 IP 独立计数，容量 = 每分钟额度，按时间线性补充。
/// 只作用于 /api/ratelimit 这一个演示端点——给全站真实限流会误伤页面初始化的那批并行请求，
/// 也会让压测数字失去意义；想要真限流时把中间件挂到需要的路由上即可。
/// </summary>
public static class RateLimiter
{
    public sealed record Decision(
        bool Allowed, int Limit, int Remaining, double RetryAfterSeconds, double Tokens);

    private sealed class Bucket
    {
        public double Tokens;
        public long At;
    }

    private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();

    public static Decision Take(string key, int perMinute)
    {
        var now = Stopwatch.GetTimestamp();
        var bucket = Buckets.GetOrAdd(key, _ => new Bucket { Tokens = perMinute, At = now });

        lock (bucket)
        {
            var elapsed = (now - bucket.At) / (double)Stopwatch.Frequency;
            bucket.Tokens = Math.Min(perMinute, bucket.Tokens + elapsed * perMinute / 60.0);
            bucket.At = now;

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return new Decision(true, perMinute, (int)Math.Floor(bucket.Tokens), 0, Math.Round(bucket.Tokens, 2));
            }

            // 距离攒够下一个令牌还差多少秒
            var need = 1 - bucket.Tokens;
            var retryAfter = Math.Round(need * 60.0 / perMinute, 2);
            return new Decision(false, perMinute, 0, retryAfter, Math.Round(bucket.Tokens, 2));
        }
    }

    /// <summary>当前有独立计数桶的客户端数量，给 /api/ratelimit 的响应带上。</summary>
    public static int ActiveClients => Buckets.Count;
}
