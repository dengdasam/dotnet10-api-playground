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

// 控制台窗口里要输出中文，统一按 UTF-8；重定向或无控制台时忽略失败
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 无控制台 */ }

const string ControllerUrl = "http://127.0.0.1:5188";
const string BackendUrl = "http://127.0.0.1:5180";

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

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 已在默认浏览器打开 {url}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 无法自动打开浏览器（{ex.Message}），请手动访问 {url}");
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

// 命令行开关：--no-browser 不自动打开页面
var autoOpenBrowser = !args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase));

// ---------- 重复启动保护 ----------
// 之前重复双击 exe 时，第二个实例会在 Kestrel 绑定时抛 IOException（address already in use），
// 窗口直接带着英文堆栈崩掉，看起来就像"失败了"。这里改成：复用已在运行的实例，只把页面打开。
// 注意判断顺序：先发 HTTP 问一句「你是谁」，再退回端口绑定探测。
// Windows 上 SO_REUSEADDR 允许两个进程同时绑同一端口，单靠预绑定会误判成"端口空闲"，反而把已在运行的实例挤死。
using var single = new Mutex(initiallyOwned: true, @"Local\ApiPlaygroundController", out var isFirst);

if (!isFirst)
{
    // 另一个实例正持有互斥体：等它把 HTTP 端点拉起来（首次启动要编译，最多等 20 秒）
    for (var i = 0; i < 40 && !await LooksLikeOursAsync(5188); i++)
        await Task.Delay(500);
}

if (await LooksLikeOursAsync(5188))
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 检测到已有控制器在运行（端口 5188）。");
    Console.WriteLine(autoOpenBrowser
        ? "  本次不再重复启动，已为你打开页面；本窗口可以关闭。"
        : "  本次不再重复启动；本窗口可以关闭。");
    Console.WriteLine($"  页面地址：{ControllerUrl}/");
    Console.WriteLine("  提示：启动/停止与请求日志都打在**那个**已在运行的窗口里，本窗口只负责把页面打开。");
    if (autoOpenBrowser) OpenBrowser(ControllerUrl + "/");
    PauseIfInteractive();
    return 0;
}

if (PortBusy(5188))
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 控制台端口 5188 已被其它程序占用。");
    Console.WriteLine("  占用者不是本程序，无法启动控制台。");
    Console.WriteLine("  请先关闭占用 5188 端口的程序（netstat -ano | findstr 5188），再重新双击本 exe。");
    PauseIfInteractive();
    return 1;
}

// 首次运行（或误删 bin）时先把主服务编译出来，否则窗口里只会看到启动失败
if (!File.Exists(dll))
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未找到已编译的主服务，正在执行 dotnet build …");
    var (built, _, tail) = await ServiceBuild.BuildAsync(projectDir, 6);
    foreach (var line in tail) Console.WriteLine("  " + line);
    Console.WriteLine(built
        ? $"[{DateTime.Now:HH:mm:ss}] 编译完成"
        : $"[{DateTime.Now:HH:mm:ss}] 编译失败，启动后请在窗口里修正并输入 build 重试");
    Console.WriteLine();
}

var bootClock = Stopwatch.StartNew();
var backend = new BackendProcess(dll, projectDir, BackendUrl, ControllerUrl);
ConsoleShell? shell = null;      // 页面触发的启停也要写进窗口日志，端点里用这个引用

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
    shell?.LogExternal("页面点了「启动服务」，正在拉起主服务…");
    var st = await backend.StartAsync();
    shell?.LogExternal(backend.Running
        ? $"页面启动完成：主服务运行中（pid {backend.Pid}）"
        : "页面启动未成功：" + backend.Message);
    return Results.Json(st);
});
app.MapPost("/control/stop", async () =>
{
    shell?.LogExternal("页面点了「停止服务」，正在优雅停止…");
    var st = await backend.StopAsync();
    shell?.LogExternal("页面停止完成：" + backend.Message);
    return Results.Json(st);
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

app.Map("/api/{**rest}", async (HttpContext ctx) =>
{
    // 进程已停止时直接回信封，不必等连接失败（否则会有约 2 秒空等）
    if (!backend.IsRunning)
    {
        await writeUnavailable(ctx);
        return;
    }

    var target = BackendUrl + ctx.Request.Path + ctx.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

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
        await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch
    {
        // 进程标记仍在但端口不通时，同样回统一信封
        await writeUnavailable(ctx);
    }
});

// 控制器退出时一并结束主服务，避免留下孤儿进程
app.Lifetime.ApplicationStopping.Register(backend.KillChild);

// 窗口里给一行就绪时间，方便判断"页面什么时候能点"
app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 控制器已就绪（{bootClock.ElapsedMilliseconds} ms），页面 {ControllerUrl}/"));

// 关闭控制台窗口（CTRL_CLOSE_EVENT）或被强制结束时不一定会走 ApplicationStopping，
// 这里再兜一层：进程退出前尽力杀掉子进程
AppDomain.CurrentDomain.ProcessExit += (_, _) => backend.KillChild();

// 控制器启动后自动拉起主服务，页面开箱可用；可在页面上停止后再启动
_ = Task.Run(async () =>
{
    try
    {
        await backend.StartAsync();

        // 主服务就绪后自动打开页面（窗口里输入 --no-browser 启动可关闭）
        if (autoOpenBrowser && backend.Running)
        {
            await Task.Delay(400);
            OpenBrowser(ControllerUrl + "/");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("自动启动主服务失败：" + ex.Message); }
});

// 交互式控制台：窗口里直接用命令启停；exit / Ctrl+C / 关窗口都会一并停掉主服务
shell = new ConsoleShell(backend, ControllerUrl, () => app.Lifetime.StopApplication(), projectDir, dll);
_ = Task.Run(shell.RunAsync);

// Ctrl+C 先优雅停主服务再退出控制器，避免直接强杀子进程
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

try
{
    await app.RunAsync();
    return 0;
}
catch (IOException ex)
{
    // 端口在预检之后又被抢占（或权限问题）：给中文提示，不要把英文堆栈甩给用户
    Console.WriteLine();
    Console.WriteLine("[启动失败] " + ex.Message);
    Console.WriteLine($"  控制台端口 {ControllerUrl} 无法监听，可能刚刚被其它程序占用。");
    Console.WriteLine("  请关闭占用 5188 的程序后重试（netstat -ano | findstr 5188）。");
    PauseIfInteractive();
    return 1;
}
