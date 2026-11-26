using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ApiPlayground.Services;

public sealed class Job
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int DurationMs { get; init; }
    public required bool WillFail { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public string State { get; set; } = "queued";
    public int Progress { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public object? Result { get; set; }
}

/// <summary>
/// 异步任务演示：提交后立刻返回 jobId，后台按 durationMs 分 20 步推进进度。
/// 内存队列，最多 4 个并发、保留最近 60 个任务；进程重启即清空。
/// </summary>
public sealed class JobService
{
    private const int MaxConcurrent = 4;
    private const int MaxJobs = 60;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private long _seq;

    public object Submit(string? name, int durationMs, bool fail)
    {
        var job = new Job
        {
            Id = "j" + Interlocked.Increment(ref _seq).ToString("x4") + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = string.IsNullOrWhiteSpace(name) ? "模拟任务" : name.Trim(),
            DurationMs = Math.Clamp(durationMs, 500, 15_000),
            WillFail = fail,
            CreatedAt = DateTimeOffset.Now
        };

        _jobs[job.Id] = job;
        Trim();
        _ = Task.Run(() => RunAsync(job));
        return Snapshot(job);
    }

    public object Get(string id)
        => _jobs.TryGetValue(id, out var job)
            ? Snapshot(job)
            : throw new ApiException("JOB_NOT_FOUND", $"没有 id 为「{id}」的任务");

    public object List(int limit)
    {
        var take = Math.Clamp(limit, 1, MaxJobs);
        var rows = _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Take(take)
            .Select(Snapshot)
            .ToArray();

        return new
        {
            total = _jobs.Count,
            returned = rows.Length,
            concurrency = new { max = MaxConcurrent, running = MaxConcurrent - _slots.CurrentCount },
            items = rows,
            note = "按提交时间倒序；内存存储，重启清空"
        };
    }

    private async Task RunAsync(Job job)
    {
        await _slots.WaitAsync();
        try
        {
            lock (job) { job.State = "running"; job.StartedAt = DateTimeOffset.Now; }

            const int steps = 20;
            var stepDelay = Math.Max(25, job.DurationMs / steps);
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(stepDelay);
                lock (job) { job.Progress = (int)Math.Round(i * 100.0 / steps); }

                if (job.WillFail && i >= steps / 2)
                {
                    lock (job)
                    {
                        job.State = "failed";
                        job.Error = "任务按 fail=true 主动失败（演示用）";
                        job.FinishedAt = DateTimeOffset.Now;
                    }
                    return;
                }
            }

            lock (job)
            {
                job.State = "succeeded";
                job.Progress = 100;
                job.FinishedAt = DateTimeOffset.Now;
                job.Result = new { message = "任务完成", durationMs = job.DurationMs };
            }
        }
        catch (Exception ex)
        {
            lock (job)
            {
                job.State = "failed";
                job.Error = ex.Message;
                job.FinishedAt = DateTimeOffset.Now;
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private static object Snapshot(Job job)
    {
        lock (job)
        {
            var from = job.StartedAt ?? job.CreatedAt;
            var to = job.FinishedAt ?? DateTimeOffset.Now;
            return new
            {
                id = job.Id,
                name = job.Name,
                state = job.State,
                progress = job.Progress,
                durationMs = job.DurationMs,
                willFail = job.WillFail,
                createdAt = job.CreatedAt.ToString("O"),
                startedAt = job.StartedAt?.ToString("O"),
                finishedAt = job.FinishedAt?.ToString("O"),
                elapsedMs = Math.Round((to - from).TotalMilliseconds),
                error = job.Error,
                result = job.Result
            };
        }
    }

    private void Trim()
    {
        if (_jobs.Count <= MaxJobs) return;
        foreach (var old in _jobs.Values.OrderBy(j => j.CreatedAt).Take(_jobs.Count - MaxJobs))
            _jobs.TryRemove(old.Id, out _);
    }
}

/// <summary>
/// 批量调用：一次 POST 提交多条内部 GET，服务端并发执行后一起返回。
/// 只允许 /api/ 下的 GET，避免被当成任意请求转发器。
/// </summary>
public sealed class BatchService(HttpClient http)
{
    private const int MaxCalls = 8;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(8);

    public static List<(string Method, string Path)> Parse(Dictionary<string, JsonElement> map)
    {
        if (!map.TryGetValue("requests", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ApiException("INVALID_BODY",
                "requests 必须是数组，例如 [{ \"path\": \"/api/time/now?tz=UTC\" }]");

        var list = new List<(string, string)>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ApiException("INVALID_BODY", "requests 的每一项必须是对象");

            var path = item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var method = item.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "GET";
            if (string.IsNullOrWhiteSpace(path))
                throw new ApiException("INVALID_BODY", "每一项都需要 path");

            list.Add(((method ?? "GET").Trim().ToUpperInvariant(), path!));
        }
        return list;
    }

    public async Task<object?> RunAsync(List<(string Method, string Path)> calls)
    {
        if (calls.Count == 0)
            throw new ApiException("EMPTY_BATCH", "requests 不能为空");
        if (calls.Count > MaxCalls)
            throw new ApiException("TOO_MANY_CALLS", $"最多 {MaxCalls} 条，当前 {calls.Count} 条");

        foreach (var (method, path) in calls)
        {
            if (method != "GET")
                throw new ApiException("INVALID_METHOD", $"batch 目前只支持 GET，收到 {method}");
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                throw new ApiException("INVALID_PATH", $"只允许调用 /api/ 下的内部路径：{path}");
            if (path.StartsWith("/api/batch", StringComparison.OrdinalIgnoreCase))
                throw new ApiException("INVALID_PATH", "batch 里不能再调 batch");
        }

        using var total = new CancellationTokenSource(TotalTimeout);
        var startedAt = Stopwatch.GetTimestamp();
        var results = await Task.WhenAll(calls.Select((call, index) => CallAsync(call, index, total.Token)));

        return new
        {
            count = results.Length,
            totalMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
            results,
            note = $"只允许 GET 内部路径，最多 {MaxCalls} 条；单条 3 秒、整体 8 秒超时"
        };
    }

    private async Task<object> CallAsync((string Method, string Path) call, int index, CancellationToken totalToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var perCall = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
            perCall.CancelAfter(PerCallTimeout);
            using var response = await http.GetAsync(call.Path, perCall.Token);
            var body = await response.Content.ReadAsStringAsync(perCall.Token);

            return new
            {
                index,
                method = call.Method,
                path = call.Path,
                status = (int)response.StatusCode,
                ok = response.IsSuccessStatusCode,
                elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                body = body.Length <= 1200 ? body : body[..1200] + "…（已截断）"
            };
        }
        catch (OperationCanceledException)
        {
            return new
            {
                index,
                method = call.Method,
                path = call.Path,
                status = 0,
                ok = false,
                elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                error = "调用超时"
            };
        }
        catch (Exception ex)
        {
            return new
            {
                index,
                method = call.Method,
                path = call.Path,
                status = 0,
                ok = false,
                elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                error = ex.Message
            };
        }
    }
}
