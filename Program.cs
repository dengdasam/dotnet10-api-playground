using System.Text.Json;
using ApiPlayground;
using ApiPlayground.Services;

var builder = WebApplication.CreateBuilder(args);

// 固定监听地址，方便页面与文档给出稳定链接；命令行 --urls 或环境变量 API_URL 可覆盖
var listenUrl = Environment.GetEnvironmentVariable("API_URL") ?? "http://127.0.0.1:5188";
if (!args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
    builder.WebHost.UseUrls(listenUrl);

builder.Services.AddSingleton<WeatherService>();

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

app.Run();
