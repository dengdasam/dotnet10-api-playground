using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ServiceController;

/// <summary>
/// WPF 窗口外壳：主窗口（<see cref="MainWindow"/>，XAML）+ 系统托盘共用同一个 STA/Dispatcher 消息泵、
/// 同一个状态轮询、同一份日志出口。点 × 只隐藏窗口到托盘（服务继续跑），托盘「退出」才真正停止服务。
///
/// 可见界面 100% WPF；仅托盘图标复用 WinForms 的 NotifyIcon 类型（无窗口、无控件、不可见），
/// 因为 WPF 没有原生托盘控件，这样能保持零 NuGet 依赖。
/// 日志仍统一落到 logs/controller-yyyy-MM-dd.log，窗口只是同一个日志源的实时视图。
/// </summary>
internal sealed class WindowShell : IControllerUi
{
    // A 冷调文档感：#F7F9FA 底 / 白卡片 / 深靛 #3B5A6A / 细边框 #E2E7EB；无渐变、无阴影、无彩色分级色块
    private static readonly Drawing.Color PrimaryColor = Drawing.Color.FromArgb(59, 90, 106);
    private static readonly Drawing.Color GreenColor = Drawing.Color.FromArgb(46, 125, 79);
    private static readonly Drawing.Color GrayColor = Drawing.Color.FromArgb(154, 165, 171);
    private static readonly Drawing.Color RedColor = Drawing.Color.FromArgb(192, 57, 43);

    private readonly BackendProcess _backend;
    private readonly string _controllerUrl;
    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _logFile;
    private readonly UiSettings _settings;
    private readonly Func<Action<string>, IReadOnlyList<string>> _connect;
    private readonly Action _requestShutdown;

    private readonly Forms.ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IntPtr> _iconHandles = [];
    private readonly List<string> _pending = [];
    private readonly object _pendingGate = new();

    private System.Windows.Application _app = null!;
    private MainWindow _window = null!;
    private Forms.NotifyIcon _tray = null!;
    private DispatcherTimer _poll = null!;
    private Forms.ToolStripMenuItem _miStatus = null!, _miStart = null!, _miStop = null!, _miBuild = null!;

    private Drawing.Icon? _appIcon, _icoRunning, _icoStopped, _icoError;
    private long _logCursor;
    private bool _busy, _exiting, _crashedNotified;
    private bool? _lastRunning;

    public WindowShell(
        BackendProcess backend,
        string controllerUrl,
        string root,
        string projectDir,
        string logFile,
        UiSettings settings,
        Func<Action<string>, IReadOnlyList<string>> connect,
        Action requestShutdown)
    {
        _backend = backend;
        _controllerUrl = controllerUrl;
        _root = root;
        _projectDir = projectDir;
        _logFile = logFile;
        _settings = settings;
        _connect = connect;
        _requestShutdown = requestShutdown;
    }

    public bool HasWindow => true;

    /// <summary>在独立的 STA 线程上执行；返回即表示 WPF 窗口与托盘已完全清理。</summary>
    public void Run()
    {
        try
        {
            _appIcon = MakeIcon(PrimaryColor);
            _icoRunning = MakeIcon(GreenColor);
            _icoStopped = MakeIcon(GrayColor);
            _icoError = MakeIcon(RedColor);

            _app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            _window = new MainWindow(
                _controllerUrl + "/",
                _backend.BackendUrl + "/",
                _logFile,
                _settings.AutoOpenBrowser,
                () => _ = DoStartAsync(),
                () => _ = DoStopAsync(),
                () => _ = DoBuildAsync(),
                () => OpenPage(),
                () => OpenUrl(_backend.BackendUrl + "/"),
                () => OpenLog(),
                () => OpenFolder(),
                () => ClearLog(),
                () => CopyLog(),
                value =>
                {
                    _settings.AutoOpenBrowser = value;
                    _settings.Save(_root);
                    AppendAll($"[{Now}] 启动偏好已保存：启动时自动打开页面 = {(value ? "是" : "否")}（下次启动生效）");
                });
            _window.Icon = IconToSource(_appIcon.Handle);
            _window.Closing += OnClosing;
            _app.MainWindow = _window;

            BuildTray();
            _tray.Visible = true;

            _poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _poll.Tick += (_, _) => Poll();
            _poll.Start();

            // 把窗口起来之前产生的日志（启动参数、首次编译等）严格按时间顺序回放；
            // connect 在同一个锁里取快照并挂上实时出口，中间不会丢行也不会重复。
            var startup = _connect(PushToWindow);
            string[] early;
            lock (_pendingGate) { early = _pending.ToArray(); _pending.Clear(); }
            foreach (var line in startup) _window.AppendLog(line);
            foreach (var line in early) _window.AppendLog(line);

            AppendAll($"[{Now}] 主窗口已就绪（WPF）：点右上角 × 会最小化到托盘，主服务继续运行。");
            AppendAll($"[{Now}] 启动偏好：启动时自动打开页面 = {(_settings.AutoOpenBrowser ? "是" : "否")}"
                + $"（可在下方勾选框修改，配置文件 {Path.GetFileName(UiSettings.FilePathOf(_root))}）");
            Poll();

            _app.Run(_window);      // 阻塞到 Application.Shutdown()
        }
        catch (Exception ex)
        {
            TrayShell.AppendFile(_logFile, $"[{Now}] ✖ WPF 窗口线程异常：{ex}");
        }
        finally
        {
            Cleanup();
            _done.TrySetResult();
        }
    }

    // ---------- 窗口事件 ----------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;              // 关窗口 ≠ 退出：只藏到托盘
        _window.Hide();
        AppendAll($"[{Now}] 主窗口已最小化到托盘，主服务继续运行；双击托盘图标可重新打开。");
        Balloon("已最小化到托盘", "主服务继续运行；双击托盘图标可重新打开主窗口。", Forms.ToolTipIcon.Info);
    }

    /// <summary>重复双击 exe 时由 /control/show 调用：把已有实例的窗口调到前台。</summary>
    public void ShowWindow() => InvokeUi(() =>
    {
        try
        {
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;      // 前台锁定时的常用技巧：闪一下置顶再撤掉
            _window.Topmost = false;
            AppendAll($"[{Now}] 已把主窗口调到前台（检测到重复启动）。");
        }
        catch { /* 窗体正在销毁时忽略 */ }
    });

    // ---------- 托盘 ----------

    private void BuildTray()
    {
        _miStatus = new Forms.ToolStripMenuItem("状态：读取中…") { Enabled = false };
        var miShow = new Forms.ToolStripMenuItem("显示主窗口", null, (_, _) => ShowWindow())
        {
            Font = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold),
        };
        _miStart = new Forms.ToolStripMenuItem("启动服务", null, async (_, _) => await DoStartAsync());
        _miStop = new Forms.ToolStripMenuItem("停止服务", null, async (_, _) => await DoStopAsync());
        _miBuild = new Forms.ToolStripMenuItem("重新编译并重启", null, async (_, _) => await DoBuildAsync());
        var miExit = new Forms.ToolStripMenuItem("退出（同时停止服务）", null, async (_, _) => await ExitAsync());

        _menu.Items.AddRange([
            _miStatus,
            new Forms.ToolStripSeparator(),
            miShow,
            new Forms.ToolStripMenuItem("打开调试页", null, (_, _) => OpenPage()),
            new Forms.ToolStripSeparator(),
            _miStart, _miStop, _miBuild,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("查看日志", null, (_, _) => OpenLog()),
            new Forms.ToolStripMenuItem("打开日志文件夹", null, (_, _) => OpenFolder()),
            new Forms.ToolStripSeparator(),
            miExit,
        ]);

        _tray = new Forms.NotifyIcon
        {
            Icon = _icoStopped,
            Text = "ApiPlayground · 启动中",
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    // ---------- 状态轮询 ----------

    private void Poll()
    {
        if (_exiting) return;

        FlushBackendLog();

        var running = _backend.Running;
        var crashed = _backend.Crashed;
        var message = _backend.Message;

        if (_lastRunning is { } previous && previous != running)
            AppendAll($"[{Now}] 状态变化：{(running ? "主服务已上线" : "主服务已离线")}"
                + (running ? "" : $"（{message}）"));
        _lastRunning = running;

        try
        {
            var state = crashed ? "异常退出" : running ? "运行中" : "已停止";
            var hex = crashed ? "#C0392B" : running ? "#2E7D4F" : "#9AA5AB";
            var meta = running
                ? $"pid {_backend.Pid} · 已运行 {Duration(DateTimeOffset.Now - (_backend.StartedAt ?? DateTimeOffset.Now))}"
                  + $" · {Shorten(message, 34)}"
                : crashed ? "异常退出 · " + Shorten(message, 52)
                : "未运行 · 点「启动服务」拉起主服务";

            _window.SetStatus(state, meta, hex);
            _window.SetBusy(_busy, running);

            var tip = $"ApiPlayground · {state}{(running ? $" · pid {_backend.Pid}" : "")}";
            _tray.Icon = crashed ? _icoError : running ? _icoRunning : _icoStopped;
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
            _miStatus.Text = running && _backend.StartedAt is { } t
                ? $"服务运行中 · 已运行 {Duration(DateTimeOffset.Now - t)}"
                : crashed ? "服务异常退出 · " + Shorten(message, 36)
                : "服务已停止";

            _miStart.Enabled = !running && !_busy;
            _miStop.Enabled = running && !_busy;
            _miBuild.Enabled = !_busy;
        }
        catch { /* 控件正在销毁时忽略 */ }

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

    /// <summary>把主服务新产生的输出精简后写进窗口与日志文件；输出过快时只补最后若干行。</summary>
    private void FlushBackendLog()
    {
        var seq = _backend.LogSeq;
        if (seq == _logCursor) return;

        var start = Math.Max(_logCursor, seq - 40);
        var skipped = (int)(start - _logCursor);
        var shown = 0;
        foreach (var line in _backend.LogsSince(start, 40))
        {
            var text = ConsoleShell.Compact(line);
            if (text is null) continue;
            AppendAll("  │ " + text);
            shown++;
        }
        if (skipped > 0 && shown > 0)
            AppendAll($"  │ … 另有 {skipped} 行未显示（输出过快，已自动跳过）");

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
            AppendAll($"[{Now}] 正在启动主服务…");
            _logCursor = _backend.LogSeq;
            var sw = Stopwatch.StartNew();
            await _backend.StartAsync();
            sw.Stop();
            FlushBackendLog();
            if (_backend.Running)
                AppendAll($"[{Now}] 启动完成，耗时 {sw.ElapsedMilliseconds} ms（pid {_backend.Pid}）");
            else
            {
                AppendAll($"[{Now}] ✖ 启动未成功：{_backend.Message}");
                Balloon("启动未成功", _backend.Message, Forms.ToolTipIcon.Error);
            }
        }
        catch (Exception ex) { AppendAll($"[{Now}] ✖ 启动出错：{ex.Message}"); }
        finally { _busy = false; Poll(); }
    }

    private async Task DoStopAsync()
    {
        if (_busy) return;
        _busy = true;
        Poll();
        try
        {
            AppendAll($"[{Now}] 正在优雅停止主服务…");
            await _backend.StopAsync();
            FlushBackendLog();
            AppendAll($"[{Now}] {_backend.Message}");
        }
        catch (Exception ex) { AppendAll($"[{Now}] ✖ 停止出错：{ex.Message}"); }
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
            AppendAll($"[{Now}] 正在编译主服务（dotnet build）…");
            if (_backend.Running)
            {
                AppendAll($"[{Now}] 先停止主服务，避免 dll 被占用…");
                await _backend.StopAsync();
            }

            var (ok, code, tail) = await ServiceBuild.BuildAsync(_projectDir);
            foreach (var line in tail) AppendAll("  " + line);

            if (ok)
            {
                AppendAll($"[{Now}] 编译成功，正在重新启动主服务…");
                _busy = false;                 // DoStartAsync 会自己置位
                await DoStartAsync();
                return;
            }

            AppendAll($"[{Now}] ✖ 编译失败（退出码 {code}），主服务保持停止；修正后再次点击「重新编译」。");
            Balloon("编译失败", $"退出码 {code}，详情见日志文件。", Forms.ToolTipIcon.Error);
        }
        catch (Exception ex) { AppendAll($"[{Now}] ✖ 编译出错：{ex.Message}"); }
        finally { _busy = false; Poll(); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        AppendAll($"[{Now}] 正在退出控制器…");
        try
        {
            if (_backend.Running)
            {
                await _backend.StopAsync();
                AppendAll($"[{Now}] 主服务：{_backend.Message}");
            }
        }
        catch { /* 退出路径不抛错 */ }

        AppendAll($"[{Now}] 端口 5188 已释放，再见。");
        StopUi();
        _requestShutdown();
    }

    // ---------- 日志出口 ----------

    public void LogExternal(string text) => AppendAll($"[{Now}] {text}");

    /// <summary>写日志文件 + 追加到窗口；程序内所有动作与页面触发的启停都走这里，日志只有这一个来源。</summary>
    private void AppendAll(string text)
    {
        TrayShell.AppendFile(_logFile, text);
        PushToWindow(text);
    }

    /// <summary>Program.Say 的实时出口：只往窗口送，文件已由 Say 写过，避免重复落盘。</summary>
    private void PushToWindow(string text)
    {
        var window = _window;
        if (window is not null)
        {
            var dispatcher = window.Dispatcher;
            try
            {
                if (dispatcher.CheckAccess()) window.AppendLog(text);
                else dispatcher.BeginInvoke(() => window.AppendLog(text));
                return;
            }
            catch { /* Dispatcher 正在关闭，落进 pending */ }
        }
        lock (_pendingGate)
        {
            if (_pending.Count < 500) _pending.Add(text);
        }
    }

    private void ClearLog()
    {
        _window.ClearLog();
        AppendAll($"[{Now}] 已清空窗口日志（日志文件内容不变）");
    }

    private void CopyLog()
    {
        try
        {
            var text = _window.LogText;
            if (text.Length == 0) { AppendAll($"[{Now}] 窗口暂无日志可复制"); return; }

            // 剪贴板常被其它进程短暂占用，重试几次；仍失败就给一句手动复制的提示
            Exception? last = null;
            for (var i = 0; i < 4; i++)
            {
                try { System.Windows.Clipboard.SetText(text); last = null; break; }
                catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(80); }
            }
            if (last is not null) throw last;
            AppendAll($"[{Now}] 已复制窗口日志到剪贴板（{text.Length} 字符）");
        }
        catch (Exception ex)
        {
            AppendAll($"[{Now}] ✖ 复制失败（{ex.Message}）；可点一下日志框，按 Ctrl+A 后 Ctrl+C 手动复制");
        }
    }

    // ---------- 打开类 ----------

    private void OpenPage() => OpenUrl(_controllerUrl + "/");

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppendAll($"[{Now}] 已在默认浏览器打开 {url}");
        }
        catch (Exception ex) { AppendAll($"[{Now}] ✖ 无法打开浏览器（{ex.Message}），请手动访问 {url}"); }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_logFile)) { AppendAll($"[{Now}] 暂无日志文件：{_logFile}"); return; }
            Process.Start(new ProcessStartInfo(_logFile) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendAll($"[{Now}] ✖ 无法打开日志：{ex.Message}"); }
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

    // ---------- 图标、编组、收尾 ----------

    /// <summary>自绘圆点图标：主色=应用图标，绿=运行、灰=停止、红=异常。不用资源文件，单文件发布更省事。</summary>
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
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, 255, 255, 255));
            g.FillEllipse(dot, size / 2 - 3, size / 2 - 3, 6, 6);
        }
        var handle = bmp.GetHicon();
        _iconHandles.Add(handle);
        return Drawing.Icon.FromHandle(handle);
    }

    private static ImageSource IconToSource(IntPtr hicon) =>
        System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            hicon, System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

    private void Balloon(string title, string text, Forms.ToolTipIcon icon) => InvokeUi(() =>
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

    private void InvokeUi(Action action)
    {
        try
        {
            var dispatcher = _window?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        catch { /* Dispatcher 已关闭时直接放弃更新 */ }
    }

    private void StopUi() => InvokeUi(() =>
    {
        try { _poll?.Stop(); } catch { }
        try { if (_tray is not null) _tray.Visible = false; } catch { }
        try { _app?.Shutdown(); } catch { }
    });

    private void Cleanup()
    {
        try { _poll?.Stop(); } catch { }
        try { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        try { _menu.Dispose(); } catch { }
        foreach (var handle in _iconHandles)
        {
            try { DestroyIcon(handle); } catch { }
        }
        _iconHandles.Clear();
    }

    public async Task ShutdownAsync()
    {
        _exiting = true;
        StopUi();
        await Task.WhenAny(_done.Task, Task.Delay(3000));
    }

    // ---------- 小工具 ----------

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
        : $"{Math.Max(0, (int)span.TotalSeconds)} 秒";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
