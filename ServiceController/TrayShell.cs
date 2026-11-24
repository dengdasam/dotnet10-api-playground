using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ServiceController;

/// <summary>
/// 托盘外壳：双击 exe 不再弹黑窗口，托盘图标常驻，右键即可启停、编译、看日志。
/// 零第三方依赖——图标、菜单、气泡都是 WinForms 框架自带（Microsoft.WindowsDesktop.App）。
///
/// 日志出口是文件：主服务 stdout/stderr 与页面触发的启停统一落到 logs/controller-yyyy-MM-dd.log，
/// 菜单「查看日志」用默认程序打开。这样即使没有任何窗口，出问题也有据可查。
/// </summary>
internal sealed class TrayShell : IControllerUi
{
    private static readonly object FileGate = new();
    private const long MaxLogBytes = 8L * 1024 * 1024;   // 单日文件上限，压测时防止日志写爆磁盘

    private readonly BackendProcess _backend;
    private readonly string _controllerUrl;
    private readonly string _projectDir;
    private readonly string _logFile;
    private readonly Action _requestShutdown;

    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.Timer _poll = new() { Interval = 700 };
    private readonly Forms.Control _marshal = new();     // 只为跨线程 BeginInvoke 而存在，从不显示
    private readonly List<IntPtr> _iconHandles = [];
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Forms.ApplicationContext _ctx = new();

    private Drawing.Icon? _icoRunning, _icoStopped, _icoError;
    private Forms.ToolStripMenuItem _miStatus = null!, _miStart = null!, _miStop = null!, _miBuild = null!;

    private long _logCursor;
    private bool _busy;               // 正在启停/编译，菜单项临时禁用，防连点
    private bool _crashedNotified;
    private bool _exiting;
    private bool? _lastRunning;       // 上次轮询到的运行状态，用于把"掉线/上线"记进日志

    public TrayShell(BackendProcess backend, string controllerUrl, string projectDir, string logFile, Action requestShutdown)
    {
        _backend = backend;
        _controllerUrl = controllerUrl;
        _projectDir = projectDir;
        _logFile = logFile;
        _requestShutdown = requestShutdown;
    }

    /// <summary>在独立的 STA 线程上执行；返回即表示托盘已完全清理。</summary>
    public void Run()
    {
        try
        {
            _marshal.CreateControl();
            _icoRunning = MakeIcon(Drawing.Color.FromArgb(46, 125, 79));    // 运行中：绿
            _icoStopped = MakeIcon(Drawing.Color.FromArgb(154, 165, 171));  // 已停止：灰
            _icoError = MakeIcon(Drawing.Color.FromArgb(192, 57, 43));      // 异常退出：红

            BuildMenu();
            _tray.Icon = _icoStopped;
            _tray.Text = "ApiPlayground · 启动中";
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += (_, _) => OpenPage();
            _tray.Visible = true;

            _poll.Tick += (_, _) => Poll();
            _poll.Start();

            Append($"[{Now}] 托盘已就绪：双击图标打开调试页，右键可启停服务、重新编译、查看日志。");
            Poll();
            Forms.Application.Run(_ctx);
        }
        catch (Exception ex)
        {
            Append($"[{Now}] ✖ 托盘启动失败：{ex.Message}");
            Append(ex.ToString());
        }
        finally
        {
            Cleanup();
            _done.TrySetResult();
        }
    }

    // ---------- 菜单 ----------

    private void BuildMenu()
    {
        _miStatus = new Forms.ToolStripMenuItem("状态：读取中…") { Enabled = false };

        var miOpen = new Forms.ToolStripMenuItem("打开调试页", null, (_, _) => OpenPage())
        {
            Font = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold)
        };
        _miStart = new Forms.ToolStripMenuItem("启动服务", null, async (_, _) => await DoStartAsync());
        _miStop = new Forms.ToolStripMenuItem("停止服务", null, async (_, _) => await DoStopAsync());
        _miBuild = new Forms.ToolStripMenuItem("重新编译并重启", null, async (_, _) => await DoBuildAsync());
        var miLog = new Forms.ToolStripMenuItem("查看日志", null, (_, _) => OpenLog());
        var miFolder = new Forms.ToolStripMenuItem("打开日志文件夹", null, (_, _) => OpenFolder());
        var miExit = new Forms.ToolStripMenuItem("退出（同时停止服务）", null, async (_, _) => await ExitAsync());

        _menu.Items.AddRange([
            _miStatus,
            new Forms.ToolStripSeparator(),
            miOpen,
            new Forms.ToolStripSeparator(),
            _miStart, _miStop, _miBuild,
            new Forms.ToolStripSeparator(),
            miLog, miFolder,
            new Forms.ToolStripSeparator(),
            miExit,
        ]);
        _menu.ShowImageMargin = false;
    }

    /// <summary>700ms 一次：把新日志落到文件、刷新图标颜色与菜单可用性、异常退出弹一次气泡。</summary>
    private void Poll()
    {
        if (_exiting) return;

        FlushBackendLog();

        var running = _backend.Running;
        var crashed = _backend.Crashed;
        var message = _backend.Message;

        // 托盘没有窗口，状态变化必须落一行日志——否则服务半夜掉线，事后完全查不到时点与原因
        if (_lastRunning is { } previous && previous != running)
            Append($"[{Now}] 状态变化：{(running ? "主服务已上线" : "主服务已离线")}"
                + (running ? "" : $"（{message}）"));
        _lastRunning = running;

        try
        {
            _tray.Icon = crashed ? _icoError : running ? _icoRunning : _icoStopped;

            var state = crashed ? "异常退出" : running ? "运行中" : "已停止";
            var pid = running && _backend.Pid is { } p ? $" · pid {p}" : "";
            var tip = $"ApiPlayground · {state}{pid}";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;   // NotifyIcon.Text 有长度上限

            _miStatus.Text = running && _backend.StartedAt is { } t
                ? $"服务运行中 · 已运行 {Duration(DateTimeOffset.Now - t)}"
                : crashed ? "服务异常退出 · " + Shorten(message, 36)
                : "服务已停止";

            _miStart.Enabled = !running && !_busy;
            _miStop.Enabled = running && !_busy;
            _miBuild.Enabled = !_busy;
        }
        catch { /* 菜单刚被重建或正在销毁时忽略 */ }

        if (crashed && !_crashedNotified)
        {
            _crashedNotified = true;
            Balloon("主服务异常退出", message, Forms.ToolTipIcon.Error);
        }
        else if (!crashed)
        {
            _crashedNotified = false;
        }
    }

    /// <summary>把主服务新产生的输出追加到日志文件；落后太多（压测刷屏）就只补最后 200 行。</summary>
    private void FlushBackendLog()
    {
        var seq = _backend.LogSeq;
        if (seq == _logCursor) return;

        var start = Math.Max(_logCursor, seq - 200);
        foreach (var line in _backend.LogsSince(start, 200))
            Append("  " + line);

        _logCursor = seq;
    }

    // ---------- 动作 ----------

    private async Task DoStartAsync()
    {
        if (_busy) return;
        _busy = true;
        Poll();
        try
        {
            Append($"[{Now}] 正在启动主服务…");
            _logCursor = _backend.LogSeq;   // 只显示本次启动产生的新输出
            var sw = Stopwatch.StartNew();
            await _backend.StartAsync();
            sw.Stop();
            FlushBackendLog();

            if (_backend.Running)
            {
                Append($"[{Now}] 启动完成，耗时 {sw.ElapsedMilliseconds} ms（pid {_backend.Pid}）");
            }
            else
            {
                Append($"[{Now}] ✖ 启动未成功：{_backend.Message}");
                Balloon("启动未成功", _backend.Message, Forms.ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Append($"[{Now}] ✖ 启动出错：{ex.Message}");
        }
        finally { _busy = false; Poll(); }
    }

    private async Task DoStopAsync()
    {
        if (_busy) return;
        _busy = true;
        Poll();
        try
        {
            Append($"[{Now}] 正在优雅停止主服务…");
            await _backend.StopAsync();
            FlushBackendLog();
            Append($"[{Now}] {_backend.Message}");
        }
        catch (Exception ex)
        {
            Append($"[{Now}] ✖ 停止出错：{ex.Message}");
        }
        finally { _busy = false; Poll(); }
    }

    /// <summary>改完 C# 代码后点这里：先停服务（否则 dll 被占用），编译，再自动拉起。</summary>
    private async Task DoBuildAsync()
    {
        if (_busy) return;
        _busy = true;
        Poll();
        try
        {
            Append($"[{Now}] 正在编译主服务（dotnet build）…");
            if (_backend.Running)
            {
                Append($"[{Now}] 先停止主服务，避免 dll 被占用…");
                await _backend.StopAsync();
            }

            var (ok, code, tail) = await ServiceBuild.BuildAsync(_projectDir);
            foreach (var line in tail) Append("  " + line);

            if (ok)
            {
                Append($"[{Now}] 编译成功，正在重新启动主服务…");
                _busy = false;                 // DoStartAsync 会自己置位
                await DoStartAsync();
                return;
            }

            Append($"[{Now}] ✖ 编译失败（退出码 {code}），主服务保持停止；修正后再次点击「重新编译并重启」。");
            Balloon("编译失败", $"退出码 {code}，详情见日志文件。", Forms.ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            Append($"[{Now}] ✖ 编译出错：{ex.Message}");
        }
        finally { _busy = false; Poll(); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Append($"[{Now}] 正在退出控制器…");

        try
        {
            if (_backend.Running)
            {
                await _backend.StopAsync();
                Append($"[{Now}] 主服务：{_backend.Message}");
            }
        }
        catch { /* 退出路径不抛错 */ }

        Append($"[{Now}] 端口 5188 已释放，再见。");
        try { _poll.Stop(); } catch { }
        _requestShutdown();                                    // 让 Kestrel 停下，主线程从 RunAsync 返回
        InvokeUi(() => { try { _tray.Visible = false; _ctx.ExitThread(); } catch { } });
    }

    // ---------- 外部入口 ----------

    public void LogExternal(string text) => Append($"[{Now}] {text}");

    public async Task ShutdownAsync()
    {
        _exiting = true;
        InvokeUi(() => { try { _poll.Stop(); _tray.Visible = false; _ctx.ExitThread(); } catch { } });
        await Task.WhenAny(_done.Task, Task.Delay(3000));       // 等托盘线程清理完，避免图标残影
    }

    // ---------- 打开类 ----------

    private void OpenPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_controllerUrl + "/") { UseShellExecute = true });
            Append($"[{Now}] 已在默认浏览器打开 {_controllerUrl}/");
        }
        catch (Exception ex)
        {
            Balloon("无法打开浏览器", ex.Message + "\n请手动访问 " + _controllerUrl + "/", Forms.ToolTipIcon.Warning);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_logFile))
            {
                Balloon("暂无日志", "还没有产生日志文件。\n" + _logFile, Forms.ToolTipIcon.Info);
                return;
            }
            Process.Start(new ProcessStartInfo(_logFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Balloon("无法打开日志", ex.Message + "\n路径：" + _logFile, Forms.ToolTipIcon.Warning);
        }
    }

    private void OpenFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFile)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* 资源管理器打不开不影响主流程 */ }
    }

    // ---------- 图标与提示 ----------

    /// <summary>自绘一个圆点图标：绿=运行、灰=停止、红=异常。不能用资源文件，单文件发布更省事。</summary>
    private Drawing.Icon MakeIcon(Drawing.Color color)
    {
        const int size = 32;
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var fill = new Drawing.SolidBrush(color);
            g.FillEllipse(fill, 3, 3, size - 6, size - 6);

            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(70, 0, 0, 0), 2f);
            g.DrawEllipse(ring, 3, 3, size - 6, size - 6);

            // 中心一个白点：小尺寸下也能看出是个「状态灯」而不是纯色块
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, 255, 255, 255));
            g.FillEllipse(dot, size / 2 - 3, size / 2 - 3, 6, 6);
        }

        var handle = bmp.GetHicon();
        _iconHandles.Add(handle);          // 句柄由本类负责销毁，Icon.FromHandle 不接管
        return Drawing.Icon.FromHandle(handle);
    }

    private void Balloon(string title, string text, Forms.ToolTipIcon icon)
    {
        InvokeUi(() =>
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text.Length > 250 ? text[..250] + "…" : text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            }
            catch { /* 气泡失败不影响功能 */ }
        });
    }

    private void InvokeUi(Action action)
    {
        try
        {
            if (_marshal.IsHandleCreated && _marshal.InvokeRequired) _marshal.BeginInvoke(action);
            else action();
        }
        catch { /* 句柄已销毁（正在退出）时直接放弃更新 */ }
    }

    private void Cleanup()
    {
        try { _poll.Stop(); _poll.Dispose(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
        try { _marshal.Dispose(); } catch { }

        foreach (var handle in _iconHandles)
        {
            try { DestroyIcon(handle); } catch { }
        }
        _iconHandles.Clear();
    }

    // ---------- 日志文件 ----------

    private void Append(string text) => AppendFile(_logFile, text);

    /// <summary>早期日志（编译检查等）还没有托盘实例，也用这个方法落盘。</summary>
    internal static void AppendFile(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            lock (FileGate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes) return;   // 超限后静默停止写入
                File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { /* 磁盘只读 / 权限不足时不影响服务本身 */ }
    }

    // ---------- 小工具 ----------

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
        : $"{(int)span.TotalSeconds} 秒";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
