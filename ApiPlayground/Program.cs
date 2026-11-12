using System.Text;
using System.Text.Json;
using ApiPlayground;
using ApiPlayground.Services;

// 控制台里输出中文，统一按 UTF-8；被重定向或无控制台时忽略失败
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 无控制台 */ }

var builder = WebApplication.CreateBuilder(args);

// 固定监听地址，方便页面与文档给出稳定链接；命令行 --urls 或环境变量 API_URL 可覆盖
// 主服务只监听内部端口；对外统一由 ServiceController（5188）反向代理
var listenUrl = Environment.GetEnvironmentVariable("API_URL") ?? "http://127.0.0.1:5180";
if (!args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
    builder.WebHost.UseUrls(listenUrl);

// 少写一个 Server 响应头：省一次 header 序列化，也顺手不暴露版本号
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// 单实例保护：Windows 的 SO_REUSEADDR 有时允许两个进程绑同一地址，
// 结果就是两个主服务并存、请求被分走，看起来像"接口时好时坏"。按端口加互斥体，重复启动直接退出。
static string MutexKey(string url) => new(url.Where(char.IsLetterOrDigit).ToArray());

var backendMutexName = @"Local\ApiPlaygroundBackend_" + MutexKey(listenUrl);
using var backendSingle = new Mutex(true, backendMutexName, out var isFirstBackend);
if (!isFirstBackend)
{
    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] 监听地址 {listenUrl} 上已有主服务在运行，本次不重复启动。");
    Console.Error.WriteLine("  如需重启，请先关闭已有进程（或直接使用控制台窗口的 restart 命令）。");
    Environment.ExitCode = 0;
    return;
}

builder.Services.AddSingleton<WeatherService>();

// 日志级别：直接 dotnet run 时默认 Warning（压测里每个请求 4 行 Info 的同步 I/O 比业务本身还贵）；
// 从控制台窗口拉起时控制器会注入 API_LOG_LEVEL=Information，窗口里就能看到请求流水。
var logLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("API_LOG_LEVEL"), ignoreCase: true, out var parsed)
    ? parsed
    : LogLevel.Warning;

// 输出格式改成单行：默认的多行结构（类别一行、内容缩进一行）在窗口里刷屏且没法被上层逐行过滤。
// 单行后每行形如 `info: Microsoft.Hosting.Lifetime[14] Now listening on: http://...`，控制器可据此精简。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
    o.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
});
builder.Logging.SetMinimumLevel(logLevel);

// Info 级下把两类纯噪音（结果序列化、端点进入/退出）压掉：每个请求只留「开始 / 完成（含耗时）」两行
// 想看全量用 API_LOG_LEVEL=Debug
if (logLevel == LogLevel.Information)
{
    builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var endpoints = new[]
{
    new { method = "GET",      path = "/api",                  section = "概览",   description = "接口清单（服务自描述）" },
    new { method = "GET",      path = "/api/health",           section = "概览",   description = "服务健康与运行时信息" },
    new { method = "GET",      path = "/api/weather",          section = "天气",   description = "按中文城市名查实时天气（离线自动兜底）" },
    new { method = "GET",      path = "/api/weather/cities",   section = "天气",   description = "内置中文城市表（经纬度）" },
    new { method = "GET·POST", path = "/api/calculator",       section = "计算",   description = "数学表达式求值" },
    new { method = "GET",      path = "/api/time/now",         section = "时间",   description = "指定时区的当前时间" },
    new { method = "GET",      path = "/api/time/zones",       section = "时间",   description = "常用时区表" },
    new { method = "GET",      path = "/api/time/convert",     section = "时间",   description = "跨时区时间换算" },
    new { method = "GET",      path = "/api/time/diff",        section = "时间",   description = "两个时间之间的差值" },
    new { method = "GET",      path = "/api/units/categories", section = "换算",   description = "单位类别与单位表（含 14 种货币）" },
    new { method = "GET",      path = "/api/units/convert",    section = "换算",   description = "长度/重量/温度/数据/速度/面积/体积/时间/汇率换算" },
};

// ---------- 概览 ----------
app.MapGet("/api", () => ApiWrap.Run(() => endpoints));

app.MapGet("/api/health", () => ApiWrap.Run(() => new
{
    status = "healthy",
    framework = "net10.0",
    runtime = Environment.Version.ToString(),
    listenUrl,
    publicUrl = "http://127.0.0.1:5188",
    serverTime = DateTimeOffset.Now.ToString("O"),
    endpointCount = 12,
    weatherProvider = "Open-Meteo（不可用时自动降级为本地模拟）",
    dependencies = "仅 Microsoft.AspNetCore.App 框架引用，零 NuGet 包"
}));

// ---------- 天气 ----------
app.MapGet("/api/weather/cities", () => ApiWrap.Run(() => WeatherService.ListCities()));

app.MapGet("/api/weather", async (string? city, WeatherService weather) =>
    await ApiWrap.RunAsync(async () => await weather.GetAsync(city)));

// ---------- 计算器 ----------
app.MapGet("/api/calculator", (string? expression) =>
    ApiWrap.Run(() => ExpressionEvaluator.Evaluate(expression)));

app.MapPost("/api/calculator", async (HttpRequest request) =>
    await ApiWrap.RunAsync(async () =>
    {
        string? expression = request.Query["expression"];
        if (request.HasJsonContentType() && request.ContentLength > 0)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("expression", out var node))
                    expression = node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
            }
            catch (JsonException)
            {
                throw new ApiException("INVALID_JSON", "请求体不是合法 JSON 对象");
            }
        }
        return (object?)ExpressionEvaluator.Evaluate(expression);
    }));

// ---------- 时间与时区 ----------
app.MapGet("/api/time/now", (string? tz) =>
    ApiWrap.Run(() => TimeService.Now(tz)));

app.MapGet("/api/time/zones", () =>
    ApiWrap.Run(() => TimeService.ListZones()));

app.MapGet("/api/time/convert", (string? datetime, string? @from, string? to) =>
    ApiWrap.Run(() => TimeService.Convert(datetime, @from, to)));

app.MapGet("/api/time/diff", (string? start, string? end, string? tz) =>
    ApiWrap.Run(() => TimeService.Diff(start, end, tz)));

// ---------- 单位换算 ----------
app.MapGet("/api/units/categories", () =>
    ApiWrap.Run(() => UnitConverter.List()));

app.MapGet("/api/units/convert", async (string? category, string? @from, string? to, string? value) =>
    await ApiWrap.RunAsync(async () => await UnitConverter.ConvertAsync(category, @from, to, value)));

// 内部端点：仅供 ServiceController 调用（控制器只转发 /api/*，此路径不对外暴露）
// 延迟一小段时间再停机，确保本次响应能完整写回，Kestrel 会等在处理完在途请求后退出
app.MapPost("/_internal/shutdown", () =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        app.Lifetime.StopApplication();
    });
    return Results.Ok(new { stopping = true });
});

// 由控制器托管时监视父进程：控制器窗口一关（哪怕被任务管理器强杀），主服务随即自行退出，
// 绝不留在后台变成常驻进程。单独 dotnet run 启动时没有该环境变量，不启用此逻辑。
if (int.TryParse(Environment.GetEnvironmentVariable("API_PARENT_PID"), out var parentPid) && parentPid > 0)
{
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                if (parent.HasExited) break;
            }
            catch (ArgumentException) { break; }   // 父进程已不存在
            catch { /* 权限等原因读不到，下次再试 */ }
        }
        app.Lifetime.StopApplication();
    });
}

// 预热：服务一就绪就先把汇率和默认城市（宁波，页面默认值）的天气拉一遍。
// 上游往返约 0.2~1.3 秒，挪到启动后后台做，用户第一次点接口不必等。
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    await ExchangeRateService.WarmupAsync();
    try { await app.Services.GetRequiredService<WeatherService>().GetAsync("宁波"); } catch { /* 预热失败不影响服务 */ }
}));

try
{
    app.Run();
}
catch (IOException ex)
{
    // 端口被占用时不要甩英文堆栈：给出中文说明与可执行的下一步
    Console.Error.WriteLine();
    Console.Error.WriteLine("[启动失败] " + ex.Message);
    Console.Error.WriteLine($"  监听地址 {listenUrl} 无法绑定，通常是已有一个主服务（或别的程序）占着这个端口。");
    Console.Error.WriteLine("  处理办法：关闭占用端口的进程，或用 API_URL=http://127.0.0.1:5199 换个端口启动。");
    Environment.ExitCode = 1;
}
