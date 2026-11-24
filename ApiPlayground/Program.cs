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
builder.Services.AddSingleton<CatService>();
builder.Services.AddSingleton<DogService>();

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

// 请求日志：只记 /api 下的调用，一行摘要进环形缓冲（/api/logs 可查）
app.UseRequestLog();

var endpoints = new EndpointInfo[]
{
    new("GET",      "/api",                  "概览", "接口清单（服务自描述）"),
    new("GET",      "/api/health",           "概览", "服务健康与运行时信息"),
    new("GET",      "/api/weather",          "天气", "按中文城市名查实时天气（离线自动兜底）"),
    new("GET",      "/api/weather/cities",   "天气", "内置中文城市表（经纬度）"),
    new("GET·POST", "/api/calculator",       "计算", "数学表达式求值（GET 查询串或 POST JSON 请求体）"),
    new("POST",     "/api/calculator",       "计算", "同上，按 POST JSON 计一条，目录与实际路由数一致"),
    new("GET",      "/api/time/now",         "时间", "指定时区的当前时间"),
    new("GET",      "/api/time/zones",       "时间", "常用时区表"),
    new("GET",      "/api/time/convert",     "时间", "跨时区时间换算"),
    new("GET",      "/api/time/diff",        "时间", "两个时间之间的差值"),
    new("GET",      "/api/units/categories", "换算", "单位类别与单位表（含 14 种货币）"),
    new("GET",      "/api/units/convert",    "换算", "长度/重量/温度/数据/速度/面积/体积/时间/汇率换算"),
    new("GET",      "/api/cats/random",      "猫咪", "随机猫图（可按品种/数量/尺寸筛选）"),
    new("GET",      "/api/cats/breeds",      "猫咪", "TheCatAPI 品种列表（关键字搜索，缓存 30 分钟）"),
    new("GET",      "/api/cats/breeds/{id}", "猫咪", "按 4 字母 ID 查单个品种详情"),
    new("GET",      "/api/cats/images/{id}", "猫咪", "按图片 ID 查图（命中本地缓存则不请求上游）"),
    new("GET",      "/api/dogs/breeds",      "狗狗", "犬种列表（283 个，支持关键字/分组/低敏筛选）"),
    new("GET",      "/api/dogs/breeds/{id}", "狗狗", "犬种详情（UUID / 英文名 / 中文名均可）"),
    new("GET",      "/api/dogs/random",      "狗狗", "随机犬图（附品种信息与图片署名）"),
    new("GET",      "/api/dogs/image",       "狗狗", "解析上游 302 图片地址（可指定犬种/尺寸）"),
    new("GET",      "/api/dogs/facts",       "狗狗", "随机狗狗趣闻（上游 no-store，每次不同）"),
    new("GET",      "/api/dogs/groups",      "狗狗", "犬种分组统计（AKC 9 组）"),

    // ---- 开发者工具箱：纯本地计算，零上游依赖 ----
    new("GET",      "/api/tools/uuid",       "工具", "生成 v4 UUID（1~50 个，可选大写/去连字符）"),
    new("GET",      "/api/tools/hash",       "工具", "文本哈希（md5/sha1/sha256/sha512，十六进制与 Base64）"),
    new("GET·POST", "/api/tools/base64",     "工具", "Base64 编解码（支持 URL 安全变体）"),
    new("POST",     "/api/tools/base64",     "工具", "同上，按 POST JSON 计一条，目录与实际路由数一致"),
    new("GET·POST", "/api/tools/json",       "工具", "JSON 格式化 / 压缩 / 校验（含嵌套深度与字节统计）"),
    new("POST",     "/api/tools/json",       "工具", "同上，按 POST JSON 计一条，目录与实际路由数一致"),

    // ---- 限流与流式 ----
    new("GET",      "/api/ratelimit",        "限流", "令牌桶限流演示（按 IP 计数，返回 X-RateLimit-* 头）"),
    new("GET",      "/api/stream/ticks",     "流式", "SSE 服务端推送（text/event-stream，逐条推送后关闭）"),

    // ---- 运维与契约 ----
    new("GET",      "/api/upstreams",        "运维", "四个上游的连通性、缓存新鲜度与冷却状态"),
    new("GET",      "/api/logs",             "运维", "最近请求日志（环形缓冲，含状态码与耗时）"),
    new("GET",      "/api/config",           "运维", "当前生效的运行参数与环境变量名"),
    new("GET",      "/openapi.json",         "契约", "OpenAPI 3.0 文档（可直接导入 Postman / Insomnia）"),
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
    endpointCount = endpoints.Length,   // 直接跟目录长度走，避免手写数字和实际注册数悄悄对不上
    weatherProvider = "Open-Meteo（不可用时自动降级为本地模拟）",
    catApi = CatService.ConfigStatus(),
    dogApi = DogService.ConfigStatus(),
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

// ---------- 猫咪图片（TheCatAPI） ----------
// 路由顺序：静态路径必须先于 /{id} 参数路由注册，否则 "breeds" 会被当成 ID 吃掉。
app.MapGet("/api/cats/breeds", async (string? q, string? limit, CatService cats) =>
    await ApiWrap.RunAsync(async () => await cats.BreedsAsync(q, limit)));

app.MapGet("/api/cats/random", async (string? limit, string? breed, string? hasBreeds, string? size, CatService cats) =>
    await ApiWrap.RunAsync(async () => await cats.RandomAsync(limit, breed, hasBreeds, size)));

app.MapGet("/api/cats/breeds/{id}", async (string id, CatService cats) =>
    await ApiWrap.RunAsync(async () => await cats.BreedAsync(id)));

app.MapGet("/api/cats/images/{id}", async (string id, CatService cats) =>
    await ApiWrap.RunAsync(async () => await cats.ImageAsync(id)));

// ---------- 狗狗（dogapi.dog，免密钥） ----------
// 静态路径先注册，避免 "breeds" 被 /{id} 参数路由吃掉。
app.MapGet("/api/dogs/breeds", async (string? q, string? group, string? limit, string? hypo, DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.BreedsAsync(q, group, limit, hypo)));

app.MapGet("/api/dogs/groups", async (DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.GroupsAsync()));

app.MapGet("/api/dogs/random", async (string? breed, string? size, DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.RandomAsync(breed, size)));

app.MapGet("/api/dogs/image", async (string? breed, string? size, DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.ImageAsync(breed, size)));

app.MapGet("/api/dogs/facts", async (string? limit, DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.FactsAsync(limit)));

app.MapGet("/api/dogs/breeds/{id}", async (string id, DogService dogs) =>
    await ApiWrap.RunAsync(async () => await dogs.BreedAsync(id)));

// ---------- 开发者工具箱（纯本地计算，零上游依赖） ----------
// 取值顺序：query → body 里的同名字段 → body 原文。
// 最后一个兜底是为了"直接把一段 JSON 贴进来格式化"这种用法（body 里没有 text 字段时整段就是输入）。
static async Task<(string? Text, string? Mode, string? UrlSafe)> ReadToolInputAsync(HttpRequest request)
{
    var text = request.Query["text"].ToString();
    var mode = request.Query["mode"].ToString();
    var urlSafe = request.Query["urlSafe"].ToString();

    if (!request.HasJsonContentType() || request.ContentLength is null or 0)
        return (Blank(text) ? null : text, Blank(mode) ? null : mode, Blank(urlSafe) ? null : urlSafe);

    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    if (Blank(raw))
        return (Blank(text) ? null : text, Blank(mode) ? null : mode, Blank(urlSafe) ? null : urlSafe);

    string? bodyText = null;
    var hasTextField = false;
    try
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
                if (prop.Name.Equals("text", StringComparison.OrdinalIgnoreCase)) { bodyText = v; hasTextField = true; }
                else if (prop.Name.Equals("mode", StringComparison.OrdinalIgnoreCase) && Blank(mode)) mode = v;
                else if (prop.Name.Equals("urlSafe", StringComparison.OrdinalIgnoreCase) && Blank(urlSafe)) urlSafe = v;
            }
            // 对象里没有 text 字段：整个 body 原文就是输入
            if (!hasTextField) bodyText = raw;
        }
        else
        {
            bodyText = raw;
        }
    }
    catch (JsonException)
    {
        throw new ApiException("INVALID_JSON", "请求体不是合法 JSON");
    }

    return (Blank(text) ? bodyText : text,
            Blank(mode) ? null : mode,
            Blank(urlSafe) ? null : urlSafe);
}

static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

app.MapGet("/api/tools/uuid", (string? count, string? upper, string? hyphen) =>
    ApiWrap.Run(() => ToolsService.Uuid(count, upper, hyphen)));

app.MapGet("/api/tools/hash", (string? text, string? algo) =>
    ApiWrap.Run(() => ToolsService.Hash(text, algo)));

app.MapGet("/api/tools/base64", (string? text, string? mode, string? urlSafe) =>
    ApiWrap.Run(() => ToolsService.Base64(text, mode, urlSafe)));

app.MapPost("/api/tools/base64", async (HttpRequest request) =>
    await ApiWrap.RunAsync(async () =>
    {
        var input = await ReadToolInputAsync(request);
        return ToolsService.Base64(input.Text, input.Mode, input.UrlSafe);
    }));

app.MapGet("/api/tools/json", (string? text, string? mode) =>
    ApiWrap.Run(() => ToolsService.Json(text, mode)));

app.MapPost("/api/tools/json", async (HttpRequest request) =>
    await ApiWrap.RunAsync(async () =>
    {
        var input = await ReadToolInputAsync(request);
        return ToolsService.Json(input.Text, input.Mode);
    }));

// ---------- 限流演示：令牌桶，按客户端 IP 独立计数 ----------
// 只作用于本端点：全站限流会误伤页面初始化那批并行请求，也会让压测数字失去意义
app.MapGet("/api/ratelimit", (HttpContext ctx) =>
{
    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    var clientKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
    var decision = RateLimiter.Take(clientKey, AppConfig.RateLimitPerMinute);

    ctx.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
    ctx.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
    if (!decision.Allowed)
        ctx.Response.Headers["Retry-After"] = Math.Ceiling(decision.RetryAfterSeconds).ToString();

    var data = new
    {
        allowed = decision.Allowed,
        limit = decision.Limit,
        remaining = decision.Remaining,
        windowSeconds = 60,
        tokensLeft = decision.Tokens,
        retryAfterSeconds = decision.RetryAfterSeconds,
        clientKey,
        activeClients = RateLimiter.ActiveClients,
        note = "令牌桶：容量 = 每分钟额度，按时间线性补充；只作用于本端点，额度可用 API_RATE_LIMIT 调整"
    };

    return decision.Allowed
        ? ApiWrap.Raw(true, data, null, startedAt, StatusCodes.Status200OK)
        : ApiWrap.Raw(false, data,
            new ApiError("RATE_LIMITED", $"超出额度（{decision.Limit} 次/分钟），请约 {decision.RetryAfterSeconds:0.0} 秒后再试"),
            startedAt, StatusCodes.Status429TooManyRequests);
});

// ---------- SSE 流式：逐条推送后主动关闭 ----------
// 注意：这里不走 ApiWrap，SSE 的载荷是 data: {...} 行而不是信封
app.MapGet("/api/stream/ticks", async (HttpContext ctx, string? count, string? intervalMs) =>
{
    var total = Math.Clamp(int.TryParse(count, out var c) ? c : 6, 1, AppConfig.SseMaxEvents);
    var gap = Math.Clamp(int.TryParse(intervalMs, out var i) ? i : 400, 50, 2000);

    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";     // 有反向代理时明确要求不缓冲
    await ctx.Response.Body.FlushAsync();

    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    for (var k = 1; k <= total; k++)
    {
        if (ctx.RequestAborted.IsCancellationRequested) return;

        var payload = JsonSerializer.Serialize(new
        {
            seq = k,
            total,
            value = Math.Round(Random.Shared.NextDouble() * 100, 2),
            elapsedMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1),
            at = DateTimeOffset.Now.ToString("HH:mm:ss.fff")
        });

        await ctx.Response.WriteAsync($"id: {k}\nevent: tick\ndata: {payload}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        if (k < total) await Task.Delay(gap, ctx.RequestAborted);
    }

    await ctx.Response.WriteAsync($"event: done\ndata: {{\"total\":{total}}}\n\n", ctx.RequestAborted);
    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
});

// ---------- 运维：上游状态 / 请求日志 / 生效配置 ----------
app.MapGet("/api/upstreams", () => ApiWrap.Run(() =>
{
    var items = new object[]
    {
        WeatherService.UpstreamStatus(),
        ExchangeRateService.UpstreamStatus(),
        CatService.UpstreamStatus(),
        DogService.UpstreamStatus()
    };
    return new
    {
        checkedAt = DateTimeOffset.Now.ToString("O"),
        count = items.Length,
        items,
        note = "coolingDown=true 表示上游刚失败过、正在退避；此时请求走缓存或快速失败"
    };
}));

app.MapGet("/api/logs", (string? limit) =>
    ApiWrap.Run(() => RequestLog.Snapshot(int.TryParse(limit, out var n) ? n : 30)));

app.MapGet("/api/config", () => ApiWrap.Run(() => AppConfig.Snapshot(listenUrl)));

// ---------- 契约 ----------
app.MapGet("/openapi.json", (HttpContext ctx) =>
{
    // 经控制器访问时用 X-Forwarded-Host，直连时用请求自己的 Host
    var forwardedHost = ctx.Request.Headers["X-Forwarded-Host"].ToString();
    var forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
    var serverUrl = string.IsNullOrWhiteSpace(forwardedHost)
        ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
        : $"{(string.IsNullOrWhiteSpace(forwardedProto) ? "http" : forwardedProto)}://{forwardedHost}";
    return ApiWrap.Document(OpenApi.Build(endpoints, serverUrl));
});

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
// 四个上游互不依赖，并行预热：总耗时取最慢的那个（汇率约 0.2~1.3 秒），而不是四者相加。
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    static async Task Quiet(Func<Task> job)
    {
        try { await job(); } catch { /* 预热失败不影响服务，请求时会自行重试 */ }
    }

    var weather = app.Services.GetRequiredService<WeatherService>();
    await Task.WhenAll(
        Quiet(ExchangeRateService.WarmupAsync),
        Quiet(CatService.WarmupAsync),      // 品种列表约 60+ 条，提前拉一次，页面下拉不必等
        Quiet(DogService.WarmupAsync),      // 先读内置种子（毫秒级），1.8MB 索引在后台自行刷新
        Quiet(() => weather.GetAsync("宁波")));
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
