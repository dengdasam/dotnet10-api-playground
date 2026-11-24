using System.Text.RegularExpressions;

namespace ApiPlayground;

/// <summary>接口目录里的一条记录：/api 的自描述与页面表格都由它渲染。</summary>
public sealed record EndpointInfo(string Method, string Path, string Section, string Description);

/// <summary>
/// 手写 OpenAPI 3.0 文档：不引 NuGet 包，直接按接口目录 + 一张参数表生成。
/// 生成的是"够用"级别——路径、方法、分组、参数名、POST 请求体示例齐全，
/// Postman / Insomnia / Apifox 都能直接导入后跑通。
/// </summary>
public static partial class OpenApi
{
    // 每个路径支持的查询参数（按需手写，比反射推断准确，也不会把框架内部参数暴露出去）
    private static readonly Dictionary<string, (string Name, string Type, string Note)[]> Queries = new()
    {
        ["/api/weather"]        = [("city", "string", "中文城市名或任意城市名，默认 宁波")],
        ["/api/time/now"]       = [("tz", "string", "IANA 时区，默认 Asia/Shanghai")],
        ["/api/time/convert"]   = [("datetime", "string", "2026-11-12 13:30 或 ISO 8601"), ("from", "string", "源时区"), ("to", "string", "目标时区")],
        ["/api/time/diff"]      = [("start", "string", "开始时间"), ("end", "string", "结束时间"), ("tz", "string", "解释时区")],
        ["/api/units/convert"]  = [("category", "string", "length/weight/temperature/data/speed/area/volume/time/currency"), ("from", "string", "源单位"), ("to", "string", "目标单位"), ("value", "number", "数值，默认 1")],
        ["/api/cats/random"]    = [("limit", "integer", "1~10"), ("breed", "string", "4 字母品种 ID"), ("hasBreeds", "integer", "1 = 只返回带品种信息的图"), ("size", "string", "full/med/small/thumb")],
        ["/api/cats/breeds"]    = [("q", "string", "名称/中文名/ID/原产地关键字"), ("limit", "integer", "0 = 全部")],
        ["/api/dogs/breeds"]    = [("q", "string", "名称/中文名/别名/原产国关键字"), ("group", "string", "AKC 分组"), ("limit", "integer", "0 = 全部 283"), ("hypo", "integer", "1 = 只看低敏")],
        ["/api/dogs/random"]    = [("breed", "string", "品种英文名，留空全库随机"), ("size", "string", "thumb/medium/large/full")],
        ["/api/dogs/image"]     = [("breed", "string", "品种英文名"), ("size", "string", "thumb/medium/large/full")],
        ["/api/dogs/facts"]     = [("limit", "integer", "1~5")],
        ["/api/tools/uuid"]     = [("count", "integer", "1~50，默认 1"), ("upper", "integer", "1 = 大写"), ("hyphen", "integer", "0 = 去掉连字符")],
        ["/api/tools/hash"]     = [("text", "string", "要计算的内容"), ("algo", "string", "md5/sha1/sha256/sha512")],
        ["/api/tools/base64"]   = [("text", "string", "原文或 Base64 文本"), ("mode", "string", "encode/decode"), ("urlSafe", "integer", "1 = URL 安全变体")],
        ["/api/tools/json"]     = [("text", "string", "JSON 文本"), ("mode", "string", "format/minify/validate")],
        ["/api/stream/ticks"]   = [("count", "integer", "推送事件数，默认 6"), ("intervalMs", "integer", "事件间隔毫秒，默认 400")],
        ["/api/logs"]           = [("limit", "integer", "返回条数，默认 30")],
        ["/api/calculator"]     = [("expression", "string", "数学表达式（GET 方式）")],
    };

    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["/api/calculator"] = "{\"expression\":\"1+2*(3-4)/5\"}",
        ["/api/tools/base64"] = "{\"text\":\"你好，API\",\"mode\":\"encode\"}",
        ["/api/tools/json"] = "{\"text\":\"{\\\"a\\\":1,\\\"b\\\":[1,2]}\",\"mode\":\"format\"}",
    };

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PathParam();

    private static IEnumerable<string> Verbs(string method)
        => method.Split('·', StringSplitOptions.RemoveEmptyEntries)
                 .Select(m => m.Trim().ToLowerInvariant())
                 .Where(m => m.Length > 0);

    public static object Build(IReadOnlyList<EndpointInfo> endpoints, string serverUrl)
    {
        var sections = endpoints.Select(e => e.Section).Distinct().ToArray();
        var paths = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (var group in endpoints.GroupBy(e => e.Path))
        {
            var operations = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var ep in group)
            {
                foreach (var verb in Verbs(ep.Method))
                    operations[verb] = Operation(ep);
            }
            paths[group.Key] = operations;
        }

        return new
        {
            openapi = "3.0.3",
            info = new
            {
                title = ".NET 10 API 调试台",
                version = "1.0.0",
                description = "本地演示用 Minimal API：统一信封、零 NuGet 依赖。业务错误也返回 HTTP 200，"
                            + "靠 success / error.code 区分；只有 /api/ratelimit 被限流时返回 429。"
            },
            servers = new[] { new { url = serverUrl, description = "经控制器统一入口（推荐）" } },
            tags = sections.Select(s => new { name = s, description = s + "相关接口" }).ToArray(),
            paths,
        };
    }

    private static object Operation(EndpointInfo ep)
    {
        var parameters = new List<object>();

        foreach (Match m in PathParam().Matches(ep.Path))
        {
            parameters.Add(new
            {
                name = m.Groups[1].Value,
                @in = "path",
                required = true,
                schema = new { type = "string" },
                description = "路径参数"
            });
        }

        if (Queries.TryGetValue(ep.Path, out var qs))
        {
            foreach (var (name, type, note) in qs)
            {
                parameters.Add(new
                {
                    name,
                    @in = "query",
                    required = false,
                    schema = new { type },
                    description = note
                });
            }
        }

        var responses = new Dictionary<string, object>
        {
            ["200"] = new { description = "统一信封 { success, data, error, elapsedMs, timestamp }" }
        };
        if (ep.Path == "/api/ratelimit")
            responses["429"] = new { description = "超出额度（带 X-RateLimit-* 与 Retry-After 头）" };

        var op = new Dictionary<string, object>
        {
            ["tags"] = new[] { ep.Section },
            ["summary"] = ep.Description,
            ["operationId"] = ep.Method.Replace("·", "_") + ep.Path.Replace("/", "_").Replace("{", "").Replace("}", ""),
            ["parameters"] = parameters,
            ["responses"] = responses,
        };

        if (Bodies.TryGetValue(ep.Path, out var example) && ep.Method.Contains("POST", StringComparison.Ordinal))
        {
            op["requestBody"] = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = new { type = "object" },
                        example = System.Text.Json.JsonDocument.Parse(example).RootElement.Clone()
                    }
                }
            };
        }

        // SSE 端点如实标出返回类型，Postman 里能看出这不是普通 JSON
        if (ep.Path.StartsWith("/api/stream", StringComparison.Ordinal))
            op["responses"] = new Dictionary<string, object>
            {
                ["200"] = new { description = "text/event-stream，逐条推送 data: {...}" }
            };

        return op;
    }
}
