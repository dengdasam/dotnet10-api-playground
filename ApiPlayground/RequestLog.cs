using System.Diagnostics;

namespace ApiPlayground;

/// <summary>
/// 请求日志环形缓冲：保留最近 N 条（默认 256），供 /api/logs 查询。
/// 只记「一行摘要」，不存请求体/响应体——页面要的是"刚才调了什么、多快、成没成"，
/// 而不是把内存变成日志仓库。
/// </summary>
public static class RequestLog
{
    public readonly record struct Entry(
        long Seq, string At, string Method, string Path, int Status, double Ms);

    private static readonly object Gate = new();
    private static readonly Entry[] Items = new Entry[AppConfig.LogCapacity];
    private static int _next;
    private static long _seq;
    private static long _total;

    public static long Total => Interlocked.Read(ref _total);

    public static void Add(string method, string path, int status, double ms)
    {
        var entry = new Entry(
            ++_seq,
            DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
            method,
            path.Length > 120 ? path[..120] + "…" : path,
            status,
            Math.Round(ms, 2));

        lock (Gate)
        {
            Items[_next] = entry;
            _next = (_next + 1) % Items.Length;
        }
        Interlocked.Increment(ref _total);
    }

    /// <summary>最新在前返回至多 limit 条。</summary>
    public static object Snapshot(int limit)
    {
        limit = Math.Clamp(limit, 1, Items.Length);
        var items = new Entry[limit];

        lock (Gate)
        {
            var count = 0;
            for (var i = 1; i <= Items.Length && count < limit; i++)
            {
                var idx = (_next - i + Items.Length) % Items.Length;
                var e = Items[idx];
                if (e.Seq == 0) break;          // 还没写满，后面都是空槽
                items[count++] = e;
            }
            if (count < limit) Array.Resize(ref items, count);
        }

        return new
        {
            total = Total,
            capacity = Items.Length,
            returned = items.Length,
            items = items.Select(e => new
            {
                seq = e.Seq,
                at = e.At,
                method = e.Method,
                path = e.Path,
                status = e.Status,
                ok = e.Status < 400,
                ms = e.Ms
            }),
            note = "环形缓冲，只保留最近 N 条摘要；条数可用 API_LOG_CAPACITY 调整"
        };
    }
}

/// <summary>把每个 /api 请求记进环形缓冲的中间件。</summary>
public static class RequestLogMiddleware
{
    public static IApplicationBuilder UseRequestLog(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            // 静态资源与页面本身不记，只关心 API 调用
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                RequestLog.Add(
                    ctx.Request.Method,
                    ctx.Request.Path + ctx.Request.QueryString,
                    ctx.Response.StatusCode,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
        });
}
