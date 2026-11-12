using System.Diagnostics;

namespace ServiceController;

/// <summary>
/// 主服务进程的生命周期管理：启动 dotnet ApiPlayground.dll，优雅停止（内部端点），
/// 超时未退则强制结束；同时消费子进程 stdout/stderr，避免管道缓冲区写满阻塞。
/// </summary>
public sealed class BackendProcess(string dll, string workDir, string backendUrl, string controllerUrl)
{
    // 探测与停机都直连本机，避免系统代理把 127.0.0.1 请求转发成 502
    private readonly HttpClient _probe = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly HttpClient _shutdown = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _log = [];
    private long _seq;          // 累计写入行数，只增不减；环形缓冲裁掉老行后仍能按序号增量读取

    private Process? _proc;
    private DateTimeOffset? _startedAt;
    private string _message = "就绪";
    private volatile bool _stopping;
    private volatile bool _crashed;

    private bool Alive => _proc is { HasExited: false };

    /// <summary>供反向代理快速判定，避免向已停止的端口发起注定失败的连接。</summary>
    public bool IsRunning => Alive;

    /// <summary>主服务是否是非预期退出（崩溃 / 端口被占），供窗口与页面区分「手动停止」和「异常退出」。</summary>
    public bool Crashed => _crashed && !Alive;

    // 以下是给控制台窗口用的只读视图
    public bool Running => Alive;
    public int? Pid => Alive ? _proc!.Id : null;
    public string Message => _message;
    public DateTimeOffset? StartedAt => _startedAt;
    public string BackendUrl => backendUrl;

    public int LogCount
    {
        get { lock (_log) return _log.Count; }
    }

    /// <summary>当前日志绝对序号，配合 <see cref="LogsSince"/> 做增量读取（窗口实时流水用）。</summary>
    public long LogSeq
    {
        get { lock (_log) return _seq; }
    }

    /// <summary>取序号大于 since 的新行；缓冲已被裁掉的部分自然跳过，max 限制单次返回量防止刷屏。</summary>
    public IReadOnlyList<string> LogsSince(long since, int max = 40)
    {
        lock (_log)
        {
            var oldest = _seq - _log.Count;                 // 缓冲里最早一行的绝对序号
            var skip = (int)Math.Clamp(since - oldest, 0, _log.Count);
            var take = _log.Count - skip;
            if (take <= 0) return Array.Empty<string>();
            return _log.Skip(skip).Take(Math.Min(take, max)).ToArray();
        }
    }

    /// <summary>取最近 count 行主服务输出，供控制台 logs 命令查看。</summary>
    public IReadOnlyList<string> RecentLogs(int count)
    {
        lock (_log)
        {
            var skip = Math.Max(0, _log.Count - count);
            return _log.Skip(skip).ToArray();
        }
    }

    public object Status() => new
    {
        running = Alive,
        pid = Alive ? _proc!.Id : (int?)null,
        startedAt = _startedAt?.ToString("O"),
        uptimeSeconds = _startedAt is { } t ? Math.Round((DateTimeOffset.Now - t).TotalSeconds, 1) : 0.0,
        backendUrl,
        controllerUrl,
        crashed = Crashed,
        message = _message
    };

    public async Task<object> StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Alive) { _message = "服务已在运行"; return Status(); }
            if (!File.Exists(dll))
            {
                _message = "未找到已编译的 ApiPlayground.dll，请先执行 dotnet build";
                return Status();
            }

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "\"" + dll + "\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["API_URL"] = backendUrl;
            // 从控制台窗口拉起时让主服务写完整日志（Info），否则窗口里只能看到错误，看不到启动与请求流水。
            // 想临时静音：启动控制器前设置环境变量 API_CHILD_LOG_LEVEL=Warning。
            psi.Environment["API_LOG_LEVEL"] =
                Environment.GetEnvironmentVariable("API_CHILD_LOG_LEVEL") ?? "Information";
            // 让主服务盯着控制器：控制器一退出（含被强杀），主服务自行停止，不留后台常驻进程
            psi.Environment["API_PARENT_PID"] = Environment.ProcessId.ToString();

            var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dotnet 进程");
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            _proc = p;
            _startedAt = DateTimeOffset.Now;
            _stopping = false;
            _crashed = false;
            _message = "正在启动…";

            // 非预期退出（崩溃、端口被占）时记下原因，别让窗口只显示一句「已离线」却不知道为啥
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                if (_stopping || !ReferenceEquals(_proc, p)) return;
                var code = -1;
                try { code = p.ExitCode; } catch { /* 读不到退出码 */ }
                _crashed = true;
                _message = $"主服务异常退出（退出码 {code}）" + Suspicion();
            };

            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                if (!Alive) break;
                if (await ProbeAsync()) { ready = true; break; }
                await Task.Delay(100);   // 100ms 粒度：冷启动约 0.7~0.9 秒，比 250ms 少等最多 150ms
            }
            if (ready) _message = "服务已启动";
            else if (Alive) _message = "进程已拉起，但端口尚未就绪";
            else if (!_crashed) _message = "进程启动后立即退出，请查看编译输出";  // 崩溃时保留 Exited 里写好的原因
            return Status();
        }
        catch (Exception ex)
        {
            _message = "启动失败：" + ex.Message;
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<object> StopAsync()
    {
        await _gate.WaitAsync();
        _stopping = true;   // 标记为主动停止，Exited 回调不再记成「异常退出」
        try
        {
            if (!Alive)
            {
                _proc = null;
                _startedAt = null;
                if (!_crashed) _message = "服务已停止";
                return Status();
            }

            var p = _proc!;
            // 先请求主服务自己优雅停机，让在途请求处理完
            try { await _shutdown.PostAsync(backendUrl + "/_internal/shutdown", null); }
            catch { /* 端口已不通时直接走下面的等待/强杀 */ }

            var exited = await Task.Run(() => p.WaitForExit(5000));
            if (!exited && !p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* 进程可能刚好退出 */ }
                _message = "优雅停止超时，已强制结束进程";
            }
            else
            {
                _message = "服务已优雅停止";
            }

            _proc = null;
            _startedAt = null;
            _crashed = false;
            return Status();
        }
        finally { _stopping = false; _gate.Release(); }
    }

    /// <summary>控制器自身退出时清理子进程，避免留下孤儿进程。</summary>
    public void KillChild()
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        try { p.Kill(entireProcessTree: true); }
        catch { /* 忽略 */ }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            using var r = await _probe.GetAsync(backendUrl + "/api/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>从最近输出里判断崩溃原因，给窗口和页面一句人话。</summary>
    private string Suspicion()
    {
        var logs = RecentLogs(60);
        if (logs.Any(l => l.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                       || l.Contains("10048", StringComparison.OrdinalIgnoreCase)))
            return "：端口 5180 已被其它进程占用，先关掉占用程序的再输入 start。";

        var lastError = logs.LastOrDefault(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase));
        return lastError is null ? "，输入 logs 查看主服务输出。" : "：" + lastError.Trim();
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_log)
        {
            _log.Add(line);
            _seq++;
            if (_log.Count > 400) _log.RemoveAt(0);
        }
    }
}
