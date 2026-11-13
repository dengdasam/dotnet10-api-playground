using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ApiPlayground.Services;

/// <summary>
/// TheCatAPI 调试服务：随机猫图、按图片 ID 查图、品种列表与品种详情。
///
/// 密钥来源（优先级从高到低）：
///   1. 环境变量 THECATAPI_KEY
///   2. 仓库根目录的 .env.local（已被 .gitignore 排除，绝不入库）
/// 两者都没有时仍可走官方公开匿名额度，响应里的 authenticated=false 会明确标注。
///
/// 性能与健壮性：品种列表缓存 30 分钟（单航班），图片搜索按查询 URL 共享单次上游往返（单航班），
/// 图片结果保留最近 20 张；任一端点上游失败（含 429 限流）后进入 30 秒负缓存：有旧数据返回
/// 旧结果并标记 stale，没有旧数据则快速失败——绝不逐条干等、也不反复打上游。
/// 连接超时 2 秒、总超时 6 秒。
/// </summary>
public sealed class CatService
{
    private const string BaseUrl = "https://api.thecatapi.com/v1";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly TimeSpan BreedsTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim BreedsGate = new(1, 1);
    private static readonly Lazy<KeyInfo> Key = new(ResolveKey);

    private static volatile BreedEntry[]? _breeds;
    private static DateTimeOffset _breedsAt = DateTimeOffset.MinValue;
    private static volatile CatImage[] _recent = [];

    // ---------- 上游故障负缓存（所有猫咪端点共享） ----------
    // 上游一旦失败：30 秒内所有猫端点直接复用失败结论，不再逐条重试打上游。
    private static DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private static string _failureNote = "连接失败";
    private static bool InFailureCooldown => DateTimeOffset.UtcNow - _lastFailure < FailureCooldown;
    private static void NoteFailure(string note)
    {
        _failureNote = note;
        _lastFailure = DateTimeOffset.UtcNow;
    }
    private static ApiException FastFail() => new("CAT_UPSTREAM_UNAVAILABLE",
        $"TheCatAPI 近期不可用（{_failureNote}），{FailureCooldown.TotalSeconds:0} 秒内跳过重试直接返回本错误；请稍后再试");

    // 图片搜索的单航班：相同查询的并发请求共享同一次上游往返，失败也只探测一次
    private static readonly ConcurrentDictionary<string, Lazy<Task<CatImage[]>>> SearchFlights = new();

    private sealed record KeyInfo(string? Value, string Source);
    private sealed record BreedEntry(
        string Id, string Name, string NameZh, string Origin, string Temperament,
        string LifeSpan, string WeightKg, string Description, string WikipediaUrl);
    private sealed record CatImage(
        string Id, string Url, int? Width, int? Height,
        string[] BreedNames, string[] BreedNamesZh);

    /// <summary>常见品种的中文名；未收录时原样保留英文名，不做机翻。</summary>
    private static readonly Dictionary<string, string> BreedZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abys"] = "阿比西尼亚猫", ["aege"] = "爱琴猫", ["abob"] = "美国短尾猫",
        ["acur"] = "美国卷耳猫", ["asho"] = "美国短毛猫", ["awir"] = "美国硬毛猫",
        ["beng"] = "孟加拉猫", ["birm"] = "伯曼猫", ["bomb"] = "孟买猫",
        ["bslo"] = "英国长毛猫", ["bsho"] = "英国短毛猫", ["bure"] = "缅甸猫",
        ["cspa"] = "加州闪亮猫", ["ctif"] = "蒂凡尼猫", ["cycl"] = "万象猫",
        ["emau"] = "埃及猫", ["ebur"] = "欧洲缅甸猫", ["esho"] = "异国短毛猫",
        ["hbro"] = "哈瓦那棕猫", ["hima"] = "喜马拉雅猫", ["jbob"] = "日本短尾猫",
        ["khao"] = "柯曼尼猫", ["kori"] = "科尼斯雷克斯猫", ["lihu"] = "狮身猫",
        ["mcoo"] = "缅因猫", ["mala"] = "马来猫", ["manx"] = "马恩岛猫",
        ["munc"] = "曼基康猫", ["nobe"] = "挪威森林猫", ["ocic"] = "奥西猫",
        ["pers"] = "波斯猫", ["pixi"] = "皮克斯猫", ["ragd"] = "布偶猫",
        ["rblu"] = "俄罗斯蓝猫", ["sava"] = "萨凡纳猫", ["sfol"] = "苏格兰折耳猫",
        ["siam"] = "暹罗猫", ["sibe"] = "西伯利亚猫", ["sing"] = "新加坡猫",
        ["snow"] = "雪鞋猫", ["soma"] = "索马里猫", ["sphy"] = "斯芬克斯猫",
        ["tonk"] = "东奇尼猫", ["tvan"] = "土耳其安哥拉猫", ["tuvg"] = "土耳其梵猫",
        ["ycho"] = "约克巧克力猫",
    };

    // ---------- 对外方法 ----------

    /// <summary>服务状态：给 /api/health 用，只暴露是否配置，不暴露密钥内容。</summary>
    public static object ConfigStatus() => new
    {
        keyConfigured = Key.Value.Value is not null,
        keySource = Key.Value.Source,
        upstream = "TheCatAPI /v1",
        note = Key.Value.Value is null
            ? "未配置密钥，当前使用公开匿名额度；设置 THECATAPI_KEY 或 .env.local 可提升额度"
            : "密钥已就绪，请求自动附加 x-api-key 头"
    };

    /// <summary>随机猫图；可按品种 ID、是否带品种信息、图片尺寸、数量过滤。</summary>
    public async Task<object> RandomAsync(string? limitRaw, string? breed, string? hasBreedsRaw, string? size)
    {
        var limit = ParseLimit(limitRaw, 1, 10);
        var breedId = (breed ?? "").Trim();
        if (breedId.Length > 40)
            throw new ApiException("INVALID_BREED", "品种 ID 过长（上限 40 字符）");
        var withBreeds = ParseBool(hasBreedsRaw);
        var sizeValue = ParseSize(size);

        var parts = new List<string> { "limit=" + limit.ToString(CultureInfo.InvariantCulture) };
        if (withBreeds) parts.Add("has_breeds=1");
        if (breedId.Length > 0) parts.Add("breed_ids=" + Uri.EscapeDataString(breedId));
        if (sizeValue is not null) parts.Add("size=" + sizeValue);
        var url = "/images/search?" + string.Join("&", parts);

        // 负缓存命中：近期上游失败时直接回旧图或快速失败，不再逐条干等
        if (InFailureCooldown)
        {
            if (_recent.Length > 0)
                return ShapeImages(TakeRecent(limit), "cache", stale: true, note: "上游近期不可用，返回最近一次成功获取的猫图");
            throw FastFail();
        }

        try
        {
            var images = await SearchOnceAsync(url);
            return ShapeImages(images, "live", stale: false, note: "来自 TheCatAPI 实时接口");
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            NoteFailure(ex.Message);
            if (_recent.Length > 0)
                return ShapeImages(TakeRecent(limit), "cache", stale: true, note: "上游不可用，已返回最近一次成功获取的猫图");
            throw FastFail();
        }
    }

    /// <summary>按图片 ID 查图。</summary>
    public async Task<object> ImageAsync(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0)
            throw new ApiException("MISSING_IMAGE_ID", "请提供图片 ID，例如 /api/cats/images/MTY3ODIyMQ");
        if (key.Length > 40 || !key.All(char.IsLetterOrDigit))
            throw new ApiException("INVALID_IMAGE_ID", "图片 ID 格式不正确（只允许字母与数字，上限 40 字符）");

        // 先看本地最近结果，命中就不必打扰上游
        var hit = _recent.FirstOrDefault(i => i.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
            return ShapeImage(hit, "cache", note: "命中本地最近结果缓存，未请求上游");

        // 负缓存命中：上游近期失败，快速失败不干等
        if (InFailureCooldown)
            throw FastFail();

        try
        {
            using var doc = await GetJsonAsync("/images/" + Uri.EscapeDataString(key));
            var images = ParseImages(doc.RootElement);
            if (images.Length == 0)
                throw new ApiException("CAT_IMAGE_NOT_FOUND", $"未找到图片「{key}」，请确认 ID 是否正确");
            _recent = images.Concat(_recent).GroupBy(i => i.Id).Select(g => g.First()).Take(20).ToArray();
            return ShapeImage(images[0], "live", note: "来自 TheCatAPI /v1/images/{id}");
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            NoteFailure(ex.Message);
            throw new ApiException("CAT_UPSTREAM_UNAVAILABLE",
                "无法连接 TheCatAPI（超时或响应异常），请检查网络后重试");
        }
    }

    /// <summary>品种列表：支持名称/原产地关键字过滤与数量上限，结果缓存 30 分钟。</summary>
    public async Task<object> BreedsAsync(string? q, string? limitRaw)
    {
        var limit = ParseLimit(limitRaw, 0, 200, allowZero: true);
        var items = await EnsureBreedsAsync();
        var age = DateTimeOffset.UtcNow - _breedsAt;
        var cached = age > TimeSpan.FromMilliseconds(50);
        return ShapeBreeds(FilterBreeds(items, q, limit), items.Length, cached, age);
    }

    /// <summary>单品种详情，复用品种缓存。</summary>
    public async Task<object> BreedAsync(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0)
            throw new ApiException("MISSING_BREED", "请提供 4 字母品种 ID，例如 pers（波斯猫）");
        if (key.Length > 40)
            throw new ApiException("INVALID_BREED", "品种 ID 过长（上限 40 字符）");

        var items = await EnsureBreedsAsync();
        var hit = items.FirstOrDefault(b => b.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
                  ?? items.FirstOrDefault(b => b.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new ApiException("CAT_BREED_NOT_FOUND",
                $"未找到品种「{key}」，请用 /api/cats/breeds 查看可用 ID");

        return new
        {
            id = hit.Id,
            name = hit.Name,
            nameZh = hit.NameZh,
            origin = hit.Origin,
            temperament = hit.Temperament,
            lifeSpan = hit.LifeSpan,
            weightKg = hit.WeightKg,
            description = hit.Description,
            wikipediaUrl = hit.WikipediaUrl,
            sampleUrl = "https://api.thecatapi.com/v1/images/search?breed_ids=" + hit.Id,
            cached = DateTimeOffset.UtcNow - _breedsAt > TimeSpan.FromMilliseconds(50),
            authenticated = Key.Value.Value is not null
        };
    }

    /// <summary>启动时预热品种列表，把首次上游往返挪出用户请求。</summary>
    public static async Task WarmupAsync()
    {
        try { await EnsureBreedsAsync(); } catch { /* 预热失败不影响服务启动 */ }
    }

    // ---------- 内部实现 ----------

    private static async Task<BreedEntry[]> EnsureBreedsAsync()
    {
        var hot = _breeds;
        if (hot is not null && DateTimeOffset.UtcNow - _breedsAt < BreedsTtl) return hot;

        // 上游近期失败且没有缓存：快速失败，不重复探测
        if (hot is null && InFailureCooldown) throw FastFail();

        await BreedsGate.WaitAsync();
        try
        {
            hot = _breeds;
            if (hot is not null && DateTimeOffset.UtcNow - _breedsAt < BreedsTtl) return hot;
            // 拿到门后再查一次：排队期间前面的请求可能已经失败了
            if (InFailureCooldown)
            {
                if (hot is not null) return hot;
                throw FastFail();
            }

            try
            {
                using var doc = await GetJsonAsync("/breeds");
                var items = ParseBreeds(doc.RootElement);
                if (items.Length == 0) throw new InvalidOperationException("上游品种列表为空");
                _breeds = items;
                _breedsAt = DateTimeOffset.UtcNow;
                return items;
            }
            catch (ApiException) { throw; }
            catch (Exception ex)
            {
                NoteFailure(ex.Message);
                // 上游挂了但缓存还在：返回旧数据（过 TTL 也算），由 ShapeBreeds 标注 cached
                if (hot is not null) return hot;
                throw new ApiException("CAT_UPSTREAM_UNAVAILABLE",
                    "无法获取 TheCatAPI 品种列表（超时或响应异常），请检查网络后重试");
            }
        }
        finally { BreedsGate.Release(); }
    }

    /// <summary>单航班：相同查询 URL 的并发请求共享同一次上游往返，失败也只探测一次。</summary>
    private static async Task<CatImage[]> SearchOnceAsync(string url)
    {
        Lazy<Task<CatImage[]>>? flight = null;
        try
        {
            flight = SearchFlights.GetOrAdd(url, _ => new Lazy<Task<CatImage[]>>(() => FetchImagesAsync(url)));
            return await flight.Value.ConfigureAwait(false);
        }
        finally
        {
            // 完成后移除：下一个请求开始新一轮探测；已在共享同任务的并发方不受影响
            if (flight is not null)
                SearchFlights.TryRemove(new KeyValuePair<string, Lazy<Task<CatImage[]>>>(url, flight));
        }
    }

    private static async Task<CatImage[]> FetchImagesAsync(string url)
    {
        using var doc = await GetJsonAsync(url);
        var images = ParseImages(doc.RootElement);
        if (images.Length == 0)
            throw new ApiException("CAT_NO_IMAGE", "上游未返回图片，可尝试去掉品种筛选或放宽条件");
        // 只保留最近 20 张，作为上游故障时的兜底
        _recent = images.Concat(_recent).GroupBy(i => i.Id).Select(g => g.First()).Take(20).ToArray();
        return images;
    }

    private static async Task<JsonDocument> GetJsonAsync(string relativeUrl)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + relativeUrl);
        if (Key.Value.Value is { } key) req.Headers.TryAddWithoutValidation("x-api-key", key);
        using var resp = await Http.SendAsync(req);

        if ((int)resp.StatusCode == 401)
            throw new ApiException("CAT_API_KEY_INVALID", "TheCatAPI 拒绝了密钥（401），请检查 THECATAPI_KEY 或 .env.local");
        if ((int)resp.StatusCode == 429)
        {
            // 限流时继续打上游只会更糟：进入 30 秒负缓存
            NoteFailure("上游触发限流 429");
            throw new ApiException("CAT_API_RATE_LIMIT", "TheCatAPI 触发限流（429），请稍后再试");
        }

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static BreedEntry[] ParseBreeds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        return root.EnumerateArray().Select(e =>
        {
            var id = Text(e, "id") ?? "";
            var name = Text(e, "name") ?? "";
            return new BreedEntry(
                id,
                name,
                BreedZh.TryGetValue(id, out var zh) ? zh : name,
                Text(e, "origin") ?? "",
                Text(e, "temperament") ?? "",
                Text(e, "life_span") ?? "",
                Text(e.GetOptional("weight"), "metric") ?? "",
                Text(e, "description") ?? "",
                Text(e, "wikipedia_url") ?? "");
        }).Where(b => b.Id.Length > 0).ToArray();
    }

    private static CatImage[] ParseImages(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(ParseImage).Where(i => i.Url.Length > 0).ToArray();
        if (root.ValueKind == JsonValueKind.Object)
        {
            var one = ParseImage(root);
            return one.Url.Length > 0 ? [one] : [];
        }
        return [];
    }

    private static CatImage ParseImage(JsonElement e)
    {
        var names = new List<string>();
        var namesZh = new List<string>();
        if (e.TryGetProperty("breeds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in arr.EnumerateArray())
            {
                var name = Text(b, "name");
                if (name is null) continue;
                names.Add(name);
                var id = Text(b, "id") ?? "";
                namesZh.Add(BreedZh.TryGetValue(id, out var zh) ? zh : name);
            }
        }
        return new CatImage(
            Text(e, "id") ?? "",
            Text(e, "url") ?? "",
            Int(e, "width"), Int(e, "height"),
            names.ToArray(), namesZh.ToArray());
    }

    private static BreedEntry[] FilterBreeds(BreedEntry[] items, string? q, int limit)
    {
        var needle = (q ?? "").Trim();
        IEnumerable<BreedEntry> seq = items;
        if (needle.Length > 0)
            seq = seq.Where(b =>
                b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || b.NameZh.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || b.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || b.Origin.Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (limit > 0) seq = seq.Take(limit);
        return seq.ToArray();
    }

    private static CatImage[] TakeRecent(int limit) => _recent.Take(Math.Min(limit, _recent.Length)).ToArray();

    private static object ShapeImages(CatImage[] images, string source, bool stale, string note) => new
    {
        count = images.Length,
        images = images.Select(ShapeImageCore).ToArray(),
        source,
        cached = source == "cache",
        stale,
        authenticated = Key.Value.Value is not null,
        provider = "TheCatAPI /v1/images/search",
        note
    };

    private static object ShapeImage(CatImage image, string source, string note) => new
    {
        image = ShapeImageCore(image),
        source,
        cached = source == "cache",
        authenticated = Key.Value.Value is not null,
        provider = "TheCatAPI /v1/images/{id}",
        note
    };

    private static object ShapeImageCore(CatImage i)
    {
        double? ratio = i.Width is > 0 && i.Height is > 0
            ? Math.Round((double)i.Width.GetValueOrDefault() / i.Height.GetValueOrDefault(), 2)
            : null;
        return new
        {
            id = i.Id,
            url = i.Url,
            width = i.Width,
            height = i.Height,
            aspectRatio = ratio,
        breedNames = i.BreedNames,
        breedNamesZh = i.BreedNamesZh,
        breedLabel = i.BreedNamesZh.Length > 0
            ? string.Join(" / ", i.BreedNamesZh)
            : (i.BreedNames.Length > 0 ? string.Join(" / ", i.BreedNames) : "未标注品种")
        };
    }

    private static object ShapeBreeds(BreedEntry[] items, int total, bool cached, TimeSpan age) => new
    {
        total,
        returned = items.Length,
        items = items.Select(b => new
        {
            id = b.Id,
            name = b.Name,
            nameZh = b.NameZh,
            origin = b.Origin,
            temperament = b.Temperament,
            lifeSpan = b.LifeSpan,
            weightKg = b.WeightKg
        }).ToArray(),
        cached,
        cacheAgeSeconds = cached ? Math.Round(age.TotalSeconds, 1) : (double?)null,
        authenticated = Key.Value.Value is not null,
        provider = "TheCatAPI /v1/breeds",
        note = cached ? "命中本地 30 分钟缓存" : "来自 TheCatAPI 实时接口"
    };

    // ---------- 参数解析 ----------

    private static int ParseLimit(string? raw, int min, int max, bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return min == 0 && allowZero ? 0 : 1;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ApiException("INVALID_LIMIT", $"limit 必须是整数（{min}~{max}）");
        if (n < min || n > max)
            throw new ApiException("INVALID_LIMIT", $"limit 超出范围（{min}~{max}）");
        return n;
    }

    private static bool ParseBool(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s is "1" or "true" or "yes" or "on";
    }

    private static string? ParseSize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s == "full") return null;
        return s switch
        {
            "small" or "med" or "thumb" => s,
            _ => throw new ApiException("INVALID_SIZE", "size 只能是 full / small / med / thumb")
        };
    }

    private static string? Text(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? Int(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;

    /// <summary>从环境变量或 .env.local 读取密钥；返回的来源只用于展示，不含密钥本身。</summary>
    private static KeyInfo ResolveKey()
    {
        var env = Environment.GetEnvironmentVariable("THECATAPI_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return new KeyInfo(env.Trim(), "环境变量 THECATAPI_KEY");

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (var level = 0; level < 6 && dir is not null; level++, dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, ".env.local");
                if (!File.Exists(file)) continue;
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 1 || !line[..eq].Trim().Equals("THECATAPI_KEY", StringComparison.Ordinal)) continue;
                    var value = line[(eq + 1)..].Trim().Trim('"', '\'');
                    return value.Length > 0 ? new KeyInfo(value, ".env.local（本地文件）") : new KeyInfo(null, "未配置");
                }
            }
        }
        return new KeyInfo(null, "未配置");
    }
}

/// <summary>JsonElement 的小扩展：取嵌套对象，取不到时返回 undefined 而不是抛异常。</summary>
internal static class JsonElementExtensions
{
    public static JsonElement GetOptional(this JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
}
