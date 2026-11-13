using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ServiceController;

/// <summary>
/// 控制器窗口里的交互式命令行：status / start / stop / restart / logs / open / help / exit。
/// 窗口关闭、Ctrl+C 或输入 exit 时控制器一并退出并结束主服务，不留孤儿进程。
/// 若标准输入被重定向（当后台服务跑），命令循环自动退出，控制器继续无界面运行。
/// </summary>
public sealed class ConsoleShell(
    BackendProcess backend, string controllerUrl, Action requestShutdown, string projectDir, string dll)
{
    private volatile bool _closing;
    private long _logCursor;
    private volatile bool _streamOn = true;
    private volatile bool _promptShown;
    private readonly object _liveGate = new();

    public async Task RunAsync()
    {
        Banner();
        UpdateTitle();

        _ = Task.Run(WatchAsync);

        while (!_closing && Console.In.ReadLine() is { } raw)
        {
            _promptShown = false;
            var (cmd, arg) = Split(raw);
            try
            {
                switch (cmd)
                {
                    case "" or "s" or "status" or "st":
                        ShowStatus();
                        break;
                    case "start" or "up":
                        await DoStartAsync();
                        break;
                    case "stop" or "down":
                        await DoStopAsync();
                        break;
                    case "r" or "restart":
                        await DoRestartAsync();
                        break;
                    case "b" or "build":
                        await DoBuildAsync();
                        break;
                    case "l" or "log" or "logs":
                        ShowLogs(arg);
                        break;
                    case "stream" or "slog":
                        ToggleStream(arg);
                        break;
                    case "o" or "open":
                        OpenBrowser();
                        break;
                    case "c" or "cls" or "clear":
                        try { Console.Clear(); } catch { /* 无控制台时忽略 */ }
                        Banner();
                        break;
                    case "h" or "help" or "?":
                        Help();
                        break;
                    case "exit" or "quit" or "q":
                        await ExitAsync();
                        break;
                    default:
                        Warn($"未知命令「{cmd}」，输入 help 查看可用命令");
                        break;
                }
            }
            catch (Exception ex)
            {
                Warn("命令执行出错：" + ex.Message);
            }

            if (!_closing) Prompt();
        }

        if (!_closing)
        {
            _promptShown = false;   // 之后不会再有提示符，日志直接顶格输出
            Console.WriteLine();
            Info("标准输入已关闭，控制器继续在后台运行；主服务日志仍会继续打印在这一窗口。");
        }
    }

    // ---------- 命令实现 ----------

    private void ShowStatus()
    {
        var running = backend.Running;
        Note("服务状态");
        Write("  运行状态  ", running ? "运行中" : "已停止", running);
        Write("  主服务    ", backend.BackendUrl + "（" + (running ? "pid " + backend.Pid : "无进程") + "）");
        if (running && backend.StartedAt is { } t)
            Write("  已运行    ", Duration(DateTimeOffset.Now - t));
        Write("  页面地址  ", controllerUrl + "/");
        Write("  最近消息  ", backend.Message);
    }

    private async Task DoStartAsync()
    {
        if (backend.Running) { Info("服务已在运行，无需重复启动"); ShowStatus(); return; }
        Info("正在启动主服务…");
        _logCursor = backend.LogSeq;      // 只显示本次启动产生的新输出
        var sw = Stopwatch.StartNew();
        await backend.StartAsync();
        sw.Stop();
        FlushLogs();                      // 立刻把启动日志（监听地址、环境）摊出来
        if (backend.Running)
            Info($"启动完成，耗时 {sw.ElapsedMilliseconds} ms（pid {backend.Pid}）");
        else
            Warn("启动未成功：" + backend.Message);
        ShowStatus();
    }

    private async Task DoStopAsync()
    {
        if (!backend.Running) { Info("服务当前已是停止状态"); return; }
        Info("正在优雅停止主服务…");
        await backend.StopAsync();
        FlushLogs();                      // 停机过程的日志也一并显示
        Info(backend.Message);
        ShowStatus();
    }

    private async Task DoRestartAsync()
    {
        Info("重启：先停止再启动…");
        if (backend.Running)
        {
            await backend.StopAsync();
            Info("主服务：" + backend.Message);
        }
        await DoStartAsync();
    }

    /// <summary>编译主服务并自动重启。改完 C# 代码后在窗口里输入 build 即可，不必关窗口。</summary>
    private async Task DoBuildAsync()
    {
        var csproj = Path.Combine(projectDir, "ApiPlayground.csproj");
        if (!File.Exists(csproj)) { Warn("找不到项目文件：" + csproj); return; }

        Info("正在编译主服务（dotnet build）…");
        if (backend.Running)
        {
            Info("先停止主服务，避免 dll 被占用…");
            await backend.StopAsync();
            Info("主服务：" + backend.Message);
        }

        var (ok, exitCode, tail) = await ServiceBuild.BuildAsync(projectDir);
        foreach (var line in tail)
            Console.WriteLine("  " + line);

        if (ok)
        {
            Info("编译成功，正在重新启动主服务…");
            await DoStartAsync();
        }
        else
        {
            Warn($"编译失败（退出码 {exitCode}），主服务保持停止；修正后再次输入 build。");
        }
    }

    /// <summary>启动时检查源码是否比已编译产物新，避免误跑旧版本。</summary>
    private void WarnIfStale()
    {
        try
        {
            var dllInfo = new FileInfo(dll);
            if (!dllInfo.Exists) return;

            var sep = Path.DirectorySeparatorChar;
            var newest = Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null || newest.LastWriteTimeUtc <= dllInfo.LastWriteTimeUtc) return;
            Warn($"检测到源码比已编译版本新（{newest.Name}），当前运行的是旧版本；输入 build 可编译并重启。");
        }
        catch { /* 权限等原因忽略 */ }
    }

    private void ShowLogs(string arg)
    {
        var rawMode = arg.StartsWith("raw", StringComparison.OrdinalIgnoreCase);
        if (rawMode) arg = arg[3..].Trim();

        var n = int.TryParse(arg, out var k) && k > 0 ? Math.Min(k, 200) : 12;
        var lines = backend.RecentLogs(n);
        if (lines.Count == 0)
        {
            Info("暂无主服务输出（服务未启动或还没产生日志）");
            return;
        }

        var texts = new List<string>();
        foreach (var line in lines)
        {
            var text = rawMode ? line : Compact(line);
            if (text is not null) texts.Add(text);
        }

        if (texts.Count == 0)
        {
            Info($"{lines.Count} 行都是框架级 Info，已按精简模式隐藏；要看原文用 logs raw {n}");
            _logCursor = backend.LogSeq;
            return;
        }

        Note(rawMode
            ? $"最近 {texts.Count} 行主服务输出（原始格式）"
            : $"最近 {texts.Count} 条主服务输出（已从 {lines.Count} 行精简）");

        foreach (var text in texts)
            Console.WriteLine("  " + (text.Length > 200 ? text[..200] + " …" : text));

        _logCursor = backend.LogSeq;   // 这些行已看过，实时流水不再重复打印
    }

    /// <summary>
    /// 实时流水开关：默认开，日志太吵时可临时关掉。
    /// </summary>
    private void ToggleStream(string arg)
    {
        _streamOn = arg switch
        {
            "on" or "1" or "" => true,
            "off" or "0" => false,
            _ => !_streamOn
        };
        if (_streamOn) _logCursor = backend.LogSeq;   // 重新打开时从当前位置接上，不补历史
        Info(_streamOn ? "主服务输出实时流水：已开启（精简模式）" : "主服务输出实时流水：已暂停（logs 仍可随时回看）");
    }

    /// <summary>把上次打印之后的新日志行输出到窗口；单次最多 30 行，防止压测时刷屏。</summary>
    private void FlushLogs()
    {
        var seq = backend.LogSeq;
        if (seq == _logCursor) return;

        var start = Math.Max(_logCursor, seq - 30);
        var skipped = (int)(start - _logCursor);

        var shown = 0;
        foreach (var line in backend.LogsSince(start, 30))
        {
            var text = Compact(line);
            if (text is null) continue;
            Live(text);
            shown++;
        }
        if (shown == 0 && skipped > 0) { /* 全是框架噪音，静默略过，不打扰 */ }
        else if (skipped > 0)
            Live($"… 另有 {skipped} 行未显示（输出过快，输入 stream off 可暂停流水）");

        _logCursor = seq;
    }

    // ---------- 日志精简：框架 Info 只留有用的，其余按人话重写 ----------

    // 单行日志形如：info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://... - 200 - ... 12.3ms
    private static readonly Regex LogLine = new(@"^(info|warn|fail|crit|dbug|trce): ([^\[]*)\[\d+\]\s?(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex RequestFinished = new(
        @"^Request finished HTTP/[\d.]+ (\w+) (\S+) - (\d+) - (\S*)\s*([\d.]+)ms$", RegexOptions.Compiled);

    /// <summary>返回要打印的精简文本；null 表示这行不值得显示。</summary>
    private static string? Compact(string line)
    {
        var s = line.TrimEnd();
        if (s.Length == 0) return null;

        var m = LogLine.Match(s);
        if (!m.Success) return s;   // 主服务自己 Console 打印的内容，原样保留

        var level = m.Groups[1].Value;
        var category = m.Groups[2].Value.Trim();
        var message = m.Groups[3].Value.Trim();

        if (level is "warn" or "fail" or "crit")
            return (level == "warn" ? "⚠ " : "✖ ") + Shorten(category) + message;

        if (level != "info") return null;   // dbug / trce 不显示

        if (!category.StartsWith("Microsoft.", StringComparison.Ordinal))
            return message;                 // 业务自己的 Info 保留

        if (message.StartsWith("Request finished", StringComparison.Ordinal))
        {
            var r = RequestFinished.Match(message);
            if (!r.Success) return "→ " + message;
            var path = UrlPath(r.Groups[2].Value);
            if (path.StartsWith("/_internal/", StringComparison.Ordinal)) return null;   // 内部停机端点不显示
            var ms = double.TryParse(r.Groups[5].Value, out var v) ? v : 0;
            return $"→ {r.Groups[1].Value} {path}  {r.Groups[3].Value}  {ms:0.0}ms";
        }

        if (message.Contains("Now listening on", StringComparison.OrdinalIgnoreCase))
        {
            var idx = message.IndexOf(':');
            return "▸ 监听地址 " + (idx >= 0 ? message[(idx + 1)..].Trim() : message);
        }
        if (message.StartsWith("Application started", StringComparison.Ordinal))
            return "▸ 主服务已就绪";
        if (message.StartsWith("Application is shutting down", StringComparison.Ordinal))
            return "▸ 主服务开始停机";
        if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return "✖ 端口被占用：" + message;

        return null;   // 其余框架 Info（Request starting / Hosting environment / Content root / 序列化细节…）不显示
    }

    private static string Shorten(string category) =>
        category.StartsWith("Microsoft.", StringComparison.Ordinal) ? "" : category + " ";

    private static string UrlPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : url;

    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(controllerUrl + "/") { UseShellExecute = true });
            Info("已在默认浏览器打开 " + controllerUrl + "/");
        }
        catch (Exception ex)
        {
            Warn("无法自动打开浏览器（" + ex.Message + "），请手动访问 " + controllerUrl + "/");
        }
    }

    private async Task ExitAsync()
    {
        _closing = true;
        Info("正在退出控制器…");
        if (backend.Running)
        {
            await backend.StopAsync();
            Info("主服务：" + backend.Message);
        }
        Info("端口 5188 已释放，再见。");
        requestShutdown();
    }

    // ---------- 状态变化监视 ----------

    private async Task WatchAsync()
    {
        bool? last = null;
        var lastMessage = "";
        while (!_closing)
        {
            try { await Task.Delay(1000); } catch { return; }
            if (_closing) return;

            try { FlushLogs(); } catch { /* 输出失败不影响状态轮询 */ }

            var running = backend.Running;
            var message = backend.Message;
            if (last is null) { last = running; lastMessage = message; continue; }

            if (running != last)
            {
                if (running)
                {
                    Note($"主服务已上线（pid {backend.Pid}）");
                }
                else if (backend.Crashed)
                {
                    FlushLogs();   // 崩溃前的最后输出先摊出来
                    // 异常退出时把原因和最后几行输出直接摊在窗口里，不用再敲 logs 才知道发生了什么
                    Warn(backend.Message);
                    var tail = backend.RecentLogs(12)
                        .Select(Compact)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .TakeLast(6)
                        .ToArray();
                    if (tail.Length == 0) tail = backend.RecentLogs(6).ToArray();
                    foreach (var line in tail)
                        Console.WriteLine("    " + (line!.Length > 180 ? line[..180] + " …" : line));
                    _logCursor = backend.LogSeq;
                    Warn("输入 start 可重新拉起；若提示端口占用，先关掉占用 5180 的程序。");
                }
                else
                {
                    Note("主服务已离线");
                }
                last = running;
                lastMessage = message;
                UpdateTitle();
            }
            else if (message != lastMessage)
            {
                lastMessage = message;
                UpdateTitle();
            }
        }
    }

    // ---------- 输出辅助 ----------

    private void Banner()
    {
        const string rule = "  ------------------------------------------------------------------";
        Console.WriteLine();
        Console.WriteLine(rule);
        Console.WriteLine("   .NET 10 API Playground  ·  服务控制器");
        Console.WriteLine(rule);
        Console.WriteLine($"   页面地址   {controllerUrl}/");
        Console.WriteLine($"   主服务     {backend.BackendUrl}  （受控进程，可随时启停）");
        Console.WriteLine("   命令       start / stop / restart / build / status / logs / stream / open / help / exit");
        Console.WriteLine("   日志       主服务输出实时打在本窗口（前缀 │，只留请求结果与告警）；stream off 可暂停。");
        Console.WriteLine("   说明       此窗口不关，服务就一直在；关闭窗口或 exit 会一并停止主服务。");
        Console.WriteLine();
        WarnIfStale();
        if (!Console.IsInputRedirected) Prompt();   // 后台运行（stdin 被重定向）时不画提示符
    }

    private void Help()
    {
        Note("可用命令");
        var rows = new (string Cmd, string Desc)[]
        {
            ("status", "查看运行状态、PID、已运行时长（空行回车同效）"),
            ("start", "启动主服务（已在运行时不会重复启动）"),
            ("stop", "优雅停止主服务，5 秒未退才强制结束"),
            ("restart", "先停再启，改完代码后重启用（不编译）"),
            ("build", "编译主服务并自动重启，改完 C# 代码用这个"),
            ("logs [n]", "精简显示最近 n 行输出（默认 12 行）；logs raw 看原始日志"),
            ("stream", "开关主服务输出实时流水（stream on / off，默认开）"),
            ("open", "用默认浏览器打开调试页面"),
            ("clear", "清屏"),
            ("help", "显示本帮助"),
            ("exit", "停止主服务并退出控制器"),
        };
        foreach (var (cmd, desc) in rows)
            Console.WriteLine($"  {cmd,-11}{desc}");
        Console.WriteLine("  提示：Ctrl+C 与关闭窗口等效于 exit。");
    }

    private void Prompt()
    {
        Console.Write("  > ");
        _promptShown = true;
    }

    /// <summary>实时打印一行主服务输出；不会吞掉用户正在输入的内容，打完把提示符补回去。</summary>
    private void Live(string line)
    {
        var text = line.Length > 200 ? line[..200] + " …" : line;
        lock (_liveGate)   // Kestrel 线程（页面触发的启停）也可能进来，避免与流水输出交错
        {
            var old = Console.ForegroundColor;
            if (_promptShown) Console.WriteLine();     // 另起一行，别让日志接在 "  > " 后面
            try { Console.ForegroundColor = ConsoleColor.DarkGray; } catch { }
            Console.WriteLine("  │ " + text);
            try { Console.ForegroundColor = old; } catch { }
            if (_promptShown) Console.Write("  > ");
        }
    }

    /// <summary>给页面（/control/start|stop）用：把页面上的操作也写进窗口，日志只有一个出口。</summary>
    internal void LogExternal(string text) => Live(text);

    private void Write(string label, string value, bool ok = false)
    {
        Console.Write("  " + label);
        var old = Console.ForegroundColor;
        try { Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Gray; } catch { }
        Console.WriteLine(value);
        try { Console.ForegroundColor = old; } catch { }
    }

    private static void Note(string message) => Line(message, ConsoleColor.Cyan);
    private static void Info(string message) => Line(message, ConsoleColor.Gray);
    private static void Warn(string message) => Line(message, ConsoleColor.Yellow);

    private static void Line(string message, ConsoleColor color)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var old = Console.ForegroundColor;
        try { Console.ForegroundColor = ConsoleColor.DarkGray; } catch { }
        Console.Write($"[{stamp}] ");
        try { Console.ForegroundColor = color; } catch { }
        Console.WriteLine(message);
        try { Console.ForegroundColor = old; } catch { }
    }

    private void UpdateTitle()
    {
        try
        {
            Console.Title = "API Playground 控制器 · "
                + (backend.Running ? $"服务运行中 pid {backend.Pid}" : "服务已停止")
                + " · " + controllerUrl;
        }
        catch { /* 无控制台时忽略 */ }
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
        : t.TotalMinutes >= 1 ? $"{t.Minutes} 分 {t.Seconds} 秒"
        : $"{Math.Max(0, (int)t.TotalSeconds)} 秒";

    private static (string cmd, string arg) Split(string line)
    {
        var text = line.Trim();
        var idx = text.IndexOf(' ');
        return idx < 0
            ? (text.ToLowerInvariant(), "")
            : (text[..idx].ToLowerInvariant(), text[(idx + 1)..].Trim());
    }
}
