using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceController;

const string ControllerUrl = "http://127.0.0.1:5188";
const string BackendUrl = "http://127.0.0.1:5180";

// 界面外壳：默认「主窗口 + 托盘」（双击出正常窗口，点 × 收进托盘）；
// --console 保留原来的命令行窗口，--tray 可回退到上一版的纯托盘模式。
// 必须在设置 Console.OutputEncoding 之前挂控制台，否则编码设置打在 Stream.Null 上。
var consoleMode = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase)
                             || a.Equals("-c", StringComparison.OrdinalIgnoreCase));
var trayMode = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-t", StringComparison.OrdinalIgnoreCase));
if (consoleMode) ConsoleHost.TryAttach();

// 控制台窗口里要输出中文，统一按 UTF-8；重定向或无控制台时忽略失败
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 无控制台 */ }

// 从 exe 所在目录逐级向上找包含 ApiPlayground/ 的目录；这样发布成单文件 exe 放在任何位置都能定位项目
static string ResolveRoot()
{
    var env = Environment.GetEnvironmentVariable("API_PLAYGROUND_ROOT");
    if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ApiPlayground", "ApiPlayground.csproj")))
            return dir.FullName;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}

static void OpenBrowser(string url, Action<string> say)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        say($"[{DateTime.Now:HH:mm:ss}] 已在默认浏览器打开 {url}");
    }
    catch (Exception ex)
    {
        say($"[{DateTime.Now:HH:mm:ss}] 无法自动打开浏览器（{ex.Message}），请手动访问 {url}");
    }
}

// 端口占用探测：能自己绑上就说明空闲，绑不上说明已被别的进程占着
static bool PortBusy(int port)
{
    System.Net.Sockets.TcpListener? listener = null;
    try
    {
        listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        return false;
    }
    catch { return true; }
    finally { try { listener?.Stop(); } catch { /* 忽略 */ } }
}

// 5188 上跑的是不是本程序（而不是别的服务）：看 /control/status 是否返回控制器字段
static async Task<bool> LooksLikeOursAsync(int port)
{
    try
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var text = await http.GetStringAsync($"http://127.0.0.1:{port}/control/status");
        return text.Contains("\"controllerUrl\"", StringComparison.Ordinal);
    }
    catch { return false; }
}

// 重复双击 exe 时，通知已经在跑的那个实例把主窗口调到前台。
// 返回「是否通知成功」以及「那个实例有没有窗口」——命令行/托盘模式没有窗口，需要退化为文字提示。
static async Task<(bool Reached, bool HasWindow)> AskShowExistingAsync()
{
    try
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await http.PostAsync("http://127.0.0.1:5188/control/show", null);
        var text = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, text.Contains("\"hasWindow\":true", StringComparison.Ordinal));
    }
    catch { return (false, false); }   // 旧版本控制器没有这个端点
}

// 双击 exe 时窗口一闪而过看不到报错，给一个停顿；被重定向或无控制台时直接跳过
static void PauseIfInteractive()
{
    try
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
        Console.WriteLine();
        Console.Write("按任意键关闭窗口…");
        Console.ReadKey(true);
        Console.WriteLine();
    }
    catch { /* 无控制台 */ }
}

var root = ResolveRoot();
var projectDir = Path.Combine(root, "ApiPlayground");
var wwwroot = Path.Combine(projectDir, "wwwroot");
var dll = Path.Combine(projectDir, "bin", "Debug", "net10.0", "ApiPlayground.dll");

// 窗口模式虽然看得见日志，仍统一落文件：托盘/命令行模式与事后排查都靠它
var logFile = Path.Combine(root, "logs", $"controller-{DateTime.Now:yyyy-MM-dd}.log");

// 统一输出出口：写日志文件 + 控制台 +（窗口模式下）实时送进主窗口。
// 窗口在独立 STA 线程里稍后才创建，所以先缓冲；窗口就绪后按原顺序回放，保证早期日志一行不丢。
var startupLog = new List<string>();
var sinkGate = new object();
Action<string>? liveSink = null;

void Say(string text)
{
    TrayShell.AppendFile(logFile, text);
    lock (sinkGate)
    {
        if (liveSink is not null) liveSink(text);
        else startupLog.Add(text);
    }
    try { Console.WriteLine(text); } catch { /* 无控制台 */ }
}

// 窗口外壳在 UI 线程调用：一个锁里完成「取走早期日志 + 挂上实时出口」，严格保序、不重复。
Func<Action<string>, IReadOnlyList<string>> connectLog = sink =>
{
    lock (sinkGate)
    {
        var lines = startupLog.ToArray();
        startupLog.Clear();
        liveSink = sink;
        return lines;
    }
};

// 没有窗口时（早期错误、纯托盘/命令行模式）必须让用户知道的事改用系统消息框
void Alert(string title, string text)
{
    Say($"[{DateTime.Now:HH:mm:ss}] {title}：{text.Replace(Environment.NewLine, " ")}");
    if (consoleMode) return;
    // 纯 Win32 消息框：此时可能还没有 WPF Application，也不保证主线程是 STA
    try { NativeMethods.Warn(title, text); }
    catch { /* 没有桌面环境时，至少已经写进日志 */ }
}

// 启动偏好：默认不自动打开浏览器（已有主窗口，点「打开页面」即可）。
// 窗口里的勾选框写入 logs/ui-settings.json；命令行开关可临时覆盖。
var settings = UiSettings.Load(root);
var forceBrowser = args.Any(a => a.Equals("--open-browser", StringComparison.OrdinalIgnoreCase));
var noBrowser = args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase));
var autoOpenBrowser = forceBrowser || (!noBrowser && settings.AutoOpenBrowser);
var modeName = consoleMode ? "命令行" : trayMode ? "托盘" : "窗口";

// 把实际收到的参数写进日志：排查「为什么又自己开了浏览器」这类问题全靠这一行
Say($"[{DateTime.Now:HH:mm:ss}] 启动参数：{(args.Length == 0 ? "（无）" : string.Join(" ", args))}"
    + $"｜模式：{modeName}｜自动打开页面：{(autoOpenBrowser ? "是" : "否")}"
    + $"（配置值 {(settings.AutoOpenBrowser ? "是" : "否")}）");

// ---------- 重复启动保护 ----------
// 之前重复双击 exe 时，第二个实例会在 Kestrel 绑定时抛 IOException（address already in use），
// 窗口直接带着英文堆栈崩掉，看起来就像「失败了」。这里改成：复用已在运行的实例，把窗口唤到前台。
// 注意判断顺序：先发 HTTP 问一句「你是谁」，再退回端口绑定探测。
// Windows 上 SO_REUSEADDR 允许两个进程同时绑同一端口，单靠预绑定会误判成「端口空闲」，反而把已在运行的实例挤死。
using var single = new Mutex(initiallyOwned: true, @"Local\ApiPlaygroundController", out var isFirst);

if (!isFirst)
{
    // 另一个实例正持有互斥体：等它把 HTTP 端点拉起来（首次启动要编译，最多等 20 秒）
    for (var i = 0; i < 40 && !await LooksLikeOursAsync(5188); i++)
        await Task.Delay(500);
}

if (await LooksLikeOursAsync(5188))
{
    Say($"[{DateTime.Now:HH:mm:ss}] 检测到已有控制器在运行（端口 5188）。");
    var (reached, hasWindow) = await AskShowExistingAsync();
    Say(reached
        ? hasWindow ? "  已通知它把主窗口调到前台，本次不再重复启动。"
                    : "  已通知它（该实例是命令行/托盘模式，没有主窗口）。"
        : "  未能通知已有实例（可能是旧版本控制器），本次不再重复启动。");
    Say($"  页面地址：{ControllerUrl}/");
    if (autoOpenBrowser) OpenBrowser(ControllerUrl + "/", Say);
    if (!hasWindow && !autoOpenBrowser)
        Alert("控制器已在运行",
            $"已有控制器在运行（端口 5188）。\n页面地址：{ControllerUrl}/\n"
            + "它当前是命令行/托盘模式，没有主窗口；如需窗口界面，请先从托盘菜单退出该实例，再重新双击本程序。");
    if (consoleMode) PauseIfInteractive();
    return 0;
}

if (PortBusy(5188))
{
    Alert("端口被占用", $"控制台端口 5188 已被其它程序占用，无法启动。\n"
        + "请先关闭占用该端口的程序（netstat -ano | findstr 5188），再重新运行本程序。");
    if (consoleMode) PauseIfInteractive();
    return 1;
}

// 首次运行（或误删 bin）时先把主服务编译出来，否则只有一句启动失败
if (!File.Exists(dll))
{
    Say($"[{DateTime.Now:HH:mm:ss}] 未找到已编译的主服务，正在执行 dotnet build …");
    var (built, _, tail) = await ServiceBuild.BuildAsync(projectDir, 6);
    foreach (var line in tail) Say("  " + line);
    Say(built
        ? $"[{DateTime.Now:HH:mm:ss}] 编译完成"
        : $"[{DateTime.Now:HH:mm:ss}] 编译失败，启动后请在窗口里点「重新编译」");
    Say("");
}

var bootClock = Stopwatch.StartNew();
var backend = new BackendProcess(dll, projectDir, BackendUrl, ControllerUrl);
IControllerUi? ui = null;        // 页面触发的启停也要写进日志出口，端点里用这个引用

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(ControllerUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

if (!Directory.Exists(wwwroot))
    throw new DirectoryNotFoundException($"找不到静态页面目录：{wwwroot}");

// 托管与主服务同一份 index.html；主服务停止后页面依然可访问
var files = new PhysicalFileProvider(wwwroot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

// ---------- 控制接口（控制器自身提供，不转发给主服务） ----------
app.MapGet("/control/status", () => Results.Json(backend.Status()));
app.MapPost("/control/start", async () =>
{
    ui?.LogExternal("页面点了「启动服务」，正在拉起主服务…");
    var st = await backend.StartAsync();
    ui?.LogExternal(backend.Running
        ? $"页面启动完成：主服务运行中（pid {backend.Pid}）"
        : "页面启动未成功：" + backend.Message);
    return Results.Json(st);
});
app.MapPost("/control/stop", async () =>
{
    ui?.LogExternal("页面点了「停止服务」，正在优雅停止…");
    var st = await backend.StopAsync();
    ui?.LogExternal("页面停止完成：" + backend.Message);
    return Results.Json(st);
});
// 重复双击 exe 时，第二个实例用这个端点让已有实例把主窗口调到前台
app.MapPost("/control/show", () =>
{
    ui?.ShowWindow();
    return Results.Json(new { ok = ui is not null, hasWindow = ui?.HasWindow ?? false });
});

// ---------- 把 /api/* 反向代理到主服务 ----------
// 必须绕过系统代理：本机设置了 HTTP_PROXY 且无 NO_PROXY，否则 127.0.0.1 请求会被代理拦成 502
var proxyClient = new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,                              // 本机回环，绕开系统代理避免 502
    ConnectTimeout = TimeSpan.FromSeconds(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 256
})
{
    Timeout = TimeSpan.FromSeconds(30)
};

Func<HttpContext, Task> writeUnavailable = async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsJsonAsync(new
    {
        success = false,
        data = (object?)null,
        error = new
        {
            code = "SERVICE_UNAVAILABLE",
            message = backend.Crashed
                ? backend.Message.TrimEnd('。', '，') + "；请点击顶部「启动服务」重新拉起"
                : "主服务未启动，请先点击页面顶部「启动服务」"
        },
        elapsedMs = 0.0,
        timestamp = DateTimeOffset.Now.ToString("O")
    });
};

// 业务接口与 OpenAPI 文档共用同一段流式转发逻辑
RequestDelegate proxyToBackend = async (HttpContext ctx) =>
{
    // 进程已停止时直接回信封，不必等连接失败（否则会有约 2 秒空等）
    if (!backend.IsRunning)
    {
        await writeUnavailable(ctx);
        return;
    }

    var target = BackendUrl + ctx.Request.Path + ctx.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

    // 告诉后端"用户看到的是哪个地址"：/openapi.json 用它生成 servers，直连时后端自己也能拼出来
    request.Headers.TryAddWithoutValidation("X-Forwarded-Host", ctx.Request.Host.Value);
    request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", ctx.Request.Scheme);

    // 先完整读成 byte[] 再转发：直接把 ctx.Request.Body 交给 StreamContent 会丢 POST 请求体
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        if (buffer.Length > 0)
        {
            request.Content = new ByteArrayContent(buffer.ToArray());
            if (ctx.Request.ContentType is { } contentType)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
    }

    try
    {
        using var response = await proxyClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } responseType)
            ctx.Response.ContentType = responseType.ToString();

        // 逐块转发并 flush：/api/stream/ticks 这类 SSE 必须让每个事件立刻到浏览器，
        // 用 CopyToAsync 会等内部缓冲凑满，页面上表现为"卡住不动然后一次性刷出来"。
        // 8KB 缓冲对普通小响应也只是一次读写 + 一次 flush，实测开销在噪声内。
        await using var upstream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch
    {
        // 进程标记仍在但端口不通时，同样回统一信封
        await writeUnavailable(ctx);
    }
};

app.Map("/api/{**rest}", proxyToBackend);
app.Map("/openapi.json", proxyToBackend);   // 文档不在 /api 前缀下，单独挂一条

// 控制器退出时一并结束主服务，避免留下孤儿进程
app.Lifetime.ApplicationStopping.Register(backend.KillChild);

// ---------- 界面外壳 ----------
// 先建外壳再拉起主服务：这样自动启动那一步产生的日志也有明确出口（窗口/托盘写文件 / 控制台写窗口）
if (consoleMode)
{
    var shell = new ConsoleShell(backend, ControllerUrl, () => app.Lifetime.StopApplication(), projectDir, dll);
    ui = shell;
    _ = Task.Run(shell.RunAsync);
}
else if (trayMode)
{
    // 纯托盘回退模式：整个 TrayShell（含 NotifyIcon / 菜单 / 定时器）必须在同一个 STA 线程上创建和使用
    TrayShell? tray = null;
    var ready = new ManualResetEventSlim(false);
    var thread = new Thread(() =>
    {
        try
        {
            tray = new TrayShell(backend, ControllerUrl, projectDir, logFile, () => app.Lifetime.StopApplication());
            ready.Set();
            tray.Run();
        }
        catch (Exception ex)
        {
            ready.Set();
            Say($"[{DateTime.Now:HH:mm:ss}] ✖ 托盘线程异常：{ex.Message}");
        }
    })
    { IsBackground = false, Name = "tray-ui" };
    thread.SetApartmentState(ApartmentState.STA);   // WinForms 要求 STA
    thread.Start();
    ready.Wait(5000);
    ui = tray;
}
else
{
    // 默认：带主窗口的窗体外壳。窗口 + 托盘共用一个 STA 消息泵、一个状态轮询、一份日志出口。
    WindowShell? window = null;
    var ready = new ManualResetEventSlim(false);
    var thread = new Thread(() =>
    {
        try
        {
            window = new WindowShell(backend, ControllerUrl, root, projectDir, logFile, settings,
                                     connectLog, () => app.Lifetime.StopApplication());
            ready.Set();
            window.Run();
        }
        catch (Exception ex)
        {
            ready.Set();
            Say($"[{DateTime.Now:HH:mm:ss}] ✖ 窗口线程异常：{ex.Message}");
        }
    })
    { IsBackground = false, Name = "window-ui" };
    thread.SetApartmentState(ApartmentState.STA);   // WinForms 要求 STA
    thread.Start();
    ready.Wait(5000);
    ui = window;
}

// 给一行就绪时间，方便判断「页面什么时候能点」
app.Lifetime.ApplicationStarted.Register(() =>
    Say($"[{DateTime.Now:HH:mm:ss}] 控制器已就绪（{bootClock.ElapsedMilliseconds} ms），页面 {ControllerUrl}/"));

// 关闭窗口（CTRL_CLOSE_EVENT）或被强制结束时不一定会走 ApplicationStopping，
// 这里再兜一层：进程退出前尽力杀掉子进程
AppDomain.CurrentDomain.ProcessExit += (_, _) => backend.KillChild();

// 控制器启动后自动拉起主服务，页面开箱可用；可在窗口/页面上或托盘菜单里停止后再启动
_ = Task.Run(async () =>
{
    try
    {
        await backend.StartAsync();
        ui?.LogExternal(backend.Running
            ? $"主服务已上线（pid {backend.Pid}）"
            : "主服务启动未成功：" + backend.Message);

        // 默认不自动开浏览器（已有主窗口）；勾选框或 --open-browser 打开时才开
        if (autoOpenBrowser && backend.Running)
        {
            await Task.Delay(400);
            OpenBrowser(ControllerUrl + "/", Say);
        }
    }
    catch (Exception ex) { Say($"[{DateTime.Now:HH:mm:ss}] 自动启动主服务失败：{ex.Message}"); }
});

// Ctrl+C 先优雅停主服务再退出控制器，避免直接强杀子进程（窗口/托盘模式没有控制台，跳过）
if (consoleMode)
{
    try
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 拦下默认的立即终止
            _ = Task.Run(async () =>
            {
                try { await backend.StopAsync(); } catch { /* 忽略 */ }
                app.Lifetime.StopApplication();
            });
        };
    }
    catch { /* 无控制台环境忽略 */ }
}

try
{
    await app.RunAsync();
    if (ui is not null) await ui.ShutdownAsync();   // 让窗口/托盘图标消失 / 停掉状态轮询
    return 0;
}
catch (IOException ex)
{
    // 端口在预检之后又被抢占（或权限问题）：给中文提示，不要把英文堆栈甩给用户
    Alert("启动失败", $"控制台端口 {ControllerUrl} 无法监听，可能刚刚被其它程序占用。\n"
        + $"请关闭占用 5188 的程序后重试（netstat -ano | findstr 5188）。\n\n{ex.Message}");
    if (consoleMode) PauseIfInteractive();
    return 1;
}
