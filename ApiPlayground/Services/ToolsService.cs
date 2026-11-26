using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ApiPlayground.Services;

/// <summary>
/// 开发者工具箱：UUID / 哈希 / Base64 / JSON 格式化。
/// 全部本地计算、零上游依赖，所以是这套服务里响应最快的一组（亚毫秒）。
/// </summary>
public static class ToolsService
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int Int(string? raw, int fallback, int min, int max)
        => int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static bool Flag(string? raw)
        => raw is "1" or "true" or "yes" or "on";

    // ---------- UUID ----------

    private static readonly DateTimeOffset GregorianEpoch = new(1582, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public static object Uuid(string? countRaw, string? upperRaw, string? hyphenRaw,
        string? versionRaw, string? namespaceRaw, string? nameRaw)
    {
        var count = Int(countRaw, 1, 1, 50);
        var upper = Flag(upperRaw);
        var hyphen = hyphenRaw is null || Flag(hyphenRaw);   // 默认带连字符
        var version = (versionRaw ?? "4").Trim().ToLowerInvariant().TrimStart('v');

        if (version is not ("1" or "3" or "4" or "5" or "7"))
            throw new ApiException("INVALID_VERSION",
                $"不支持的 UUID 版本「{versionRaw}」，可选 1 / 3 / 4 / 5 / 7（也可写 v4）");

        var name = nameRaw ?? "";
        var ns = Guid.Empty;
        if (version is "3" or "5")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ApiException("MISSING_NAME",
                    $"v{version} 是「命名 UUID」，需要 namespace + name；请在 name 里给出名称");
            ns = ParseNamespace(namespaceRaw);
        }

        var items = new string[count];
        for (var i = 0; i < count; i++)
        {
            var g = version switch
            {
                "1" => NewV1(),
                "3" => NamedUuid(ns, name, 3),
                "4" => Guid.NewGuid(),
                "5" => NamedUuid(ns, name, 5),
                _ => NewV7()
            };
            var text = hyphen ? g.ToString("D") : g.ToString("N");
            items[i] = upper ? text.ToUpperInvariant() : text;
        }

        return new
        {
            version = "v" + version,
            count,
            format = hyphen ? "8-4-4-4-12（带连字符）" : "32 位无连字符",
            upperCase = upper,
            namespaceId = version is "3" or "5" ? $"{NamespaceLabel(namespaceRaw)} / {ns:D}" : null,
            name = version is "3" or "5" ? name : null,
            items,
            spec = VersionSpec(version),
            note = VersionNote(version)
        };
    }

    /// <summary>RFC 4122 v1：60 位 100ns 时间戳 + 14 位时钟序列 + 48 位节点（随机，隐私友好）。</summary>
    private static Guid NewV1()
    {
        var ticks = (DateTimeOffset.UtcNow - GregorianEpoch).Ticks;
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);

        b[0] = (byte)(ticks >> 24);
        b[1] = (byte)(ticks >> 16);
        b[2] = (byte)(ticks >> 8);
        b[3] = (byte)ticks;
        b[4] = (byte)(ticks >> 40);
        b[5] = (byte)(ticks >> 32);
        b[6] = (byte)(((ticks >> 56) & 0x0F) | 0x10);
        b[7] = (byte)(ticks >> 48);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);   // variant 10
        b[10] |= 0x01;                          // 随机节点：多播位置 1，表示不是真实 MAC

        return new Guid(b, bigEndian: true);
    }

    /// <summary>
    /// RFC 9562 v7：48 位毫秒时间戳 + 12 位 rand_a（同毫秒内递增，保证单调）+ 62 位随机。
    /// .NET 内置的 CreateVersion7 在同一毫秒内用随机 rand_a，连续生成不保证有序，所以自己实现。
    /// </summary>
    private static readonly object V7Gate = new();
    private static long _v7LastMs;
    private static int _v7Seq;

    private static Guid NewV7()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int seq;
        lock (V7Gate)
        {
            if (ms == _v7LastMs)
            {
                _v7Seq = (_v7Seq + 1) & 0x0FFF;        // 同毫秒内 +1
            }
            else
            {
                _v7LastMs = ms;
                _v7Seq = RandomNumberGenerator.GetInt32(0, 0x1000);   // 新毫秒随机起点
            }
            seq = _v7Seq;
        }

        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        b[0] = (byte)(ms >> 40);
        b[1] = (byte)(ms >> 32);
        b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16);
        b[4] = (byte)(ms >> 8);
        b[5] = (byte)ms;
        b[6] = (byte)(0x70 | ((seq >> 8) & 0x0F));    // version 7 + rand_a 高 4 位
        b[7] = (byte)(seq & 0xFF);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);          // variant 10
        return new Guid(b, bigEndian: true);
    }

    /// <summary>RFC 4122 v3/v5：hash(namespace + name)，v3 用 MD5、v5 用 SHA-1。</summary>
    private static Guid NamedUuid(Guid ns, string name, int version)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[version == 3 ? 16 : 20];
        if (version == 3) MD5.HashData(input, hash);
        else SHA1.HashData(input, hash);

        Span<byte> uuid = stackalloc byte[16];
        hash[..16].CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | (version << 4));
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }

    private static Guid ParseNamespace(string? raw)
    {
        var key = (raw ?? "dns").Trim().ToLowerInvariant();
        return key switch
        {
            "" or "dns" => new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"),
            "url" => new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8"),
            "oid" => new Guid("6ba7b812-9dad-11d1-80b4-00c04fd430c8"),
            "x500" or "x.500" => new Guid("6ba7b814-9dad-11d1-80b4-00c04fd430c8"),
            _ => Guid.TryParse(raw, out var custom)
                ? custom
                : throw new ApiException("INVALID_NAMESPACE",
                    $"命名空间「{raw}」不合法，可选 dns / url / oid / x500，或直接给一个 UUID")
        };
    }

    private static string NamespaceLabel(string? raw) => (raw ?? "dns").Trim().ToLowerInvariant() switch
    {
        "" or "dns" => "DNS",
        "url" => "URL",
        "oid" => "OID",
        "x500" or "x.500" => "X500",
        _ => "自定义命名空间"
    };

    private static string VersionSpec(string version) => version switch
    {
        "1" => "RFC 4122 v1：60 位时间戳 + 时钟序列 + 48 位节点（节点随机化，不暴露真实 MAC）",
        "3" => "RFC 4122 v3：MD5(命名空间 + 名称)，确定性输出",
        "4" => "RFC 4122 v4：122 位随机数，最常用但完全无序",
        "5" => "RFC 4122 v5：SHA-1(命名空间 + 名称)，确定性输出，比 v3 更推荐",
        _ => "RFC 9562 v7：48 位毫秒时间戳 + 随机位，按时间单调递增"
    };

    private static string VersionNote(string version) => version switch
    {
        "1" => "适合需要「生成时间 + 节点」信息的场景；时间可被反解，不适合暴露给用户",
        "3" => "同一个 namespace + name 永远得到同一个 UUID（适合从业务键派生 ID）；MD5 已弱，新项目优先 v5",
        "4" => "适合不需要排序的标识（会话、Trace ID）；做主键时随机写入会打散 B+ 树索引",
        "5" => "同一个 namespace + name 永远得到同一个 UUID（适合从业务键派生 ID），SHA-1 比 v3 更稳",
        _ => "适合数据库主键：按生成时间有序，写入集中在新页，索引更友好；同毫秒内仍保持单调"
    };

    // ---------- 哈希 ----------

    public static object Hash(string? text, string? algoRaw)
    {
        if (string.IsNullOrEmpty(text))
            throw new ApiException("MISSING_TEXT", "请通过 text 参数（或 POST body 的 text 字段）提供要计算的内容");

        var algo = (algoRaw ?? "sha256").Trim().ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(text);

        byte[] digest;
        switch (algo)
        {
            case "md5": digest = MD5.HashData(bytes); break;
            case "sha1": digest = SHA1.HashData(bytes); break;
            case "sha256": digest = SHA256.HashData(bytes); break;
            case "sha512": digest = SHA512.HashData(bytes); break;
            default:
                throw new ApiException("INVALID_ALGO", $"不支持的算法「{algoRaw}」，可选 md5 / sha1 / sha256 / sha512");
        }

        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return new
        {
            algo,
            inputBytes = bytes.Length,
            digestBytes = digest.Length,
            hex,
            base64 = Convert.ToBase64String(digest),
            bits = digest.Length * 8
        };
    }

    // ---------- Base64 ----------

    public static object Base64(string? text, string? modeRaw, string? urlSafeRaw)
    {
        if (string.IsNullOrEmpty(text))
            throw new ApiException("MISSING_TEXT", "请通过 text 参数（或 POST body 的 text 字段）提供内容");

        var mode = (modeRaw ?? "encode").Trim().ToLowerInvariant();
        var urlSafe = Flag(urlSafeRaw);

        if (mode == "encode")
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var encoded = Convert.ToBase64String(bytes);
            if (urlSafe) encoded = encoded.Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return new
            {
                mode = "encode",
                urlSafe,
                input = text,
                inputBytes = bytes.Length,
                output = encoded,
                note = urlSafe ? "URL 安全变体：+ → -、/ → _，去掉尾部 =" : "标准 Base64（RFC 4648）"
            };
        }

        if (mode == "decode")
        {
            var raw = text.Trim();
            if (urlSafe) raw = raw.Replace('-', '+').Replace('_', '/');
            // 补齐 Base64 要求的 4 的倍数长度
            var pad = raw.Length % 4;
            if (pad == 2) raw += "==";
            else if (pad == 3) raw += "=";
            else if (pad == 1)
                throw new ApiException("BASE64_INVALID", "不是合法的 Base64 文本（长度模 4 余 1）");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(raw); }
            catch (FormatException)
            {
                throw new ApiException("BASE64_INVALID", "不是合法的 Base64 文本，请检查是否含非法字符");
            }

            string decoded;
            try { decoded = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                return new
                {
                    mode = "decode",
                    urlSafe,
                    input = text,
                    decodedBytes = bytes.Length,
                    output = Convert.ToHexString(bytes).ToLowerInvariant(),
                    isText = false,
                    note = "解码结果不是合法 UTF-8，已改用十六进制展示"
                };
            }

            return new
            {
                mode = "decode",
                urlSafe,
                input = text,
                decodedBytes = bytes.Length,
                output = decoded,
                isText = true,
                note = "解码结果按 UTF-8 解释为文本"
            };
        }

        throw new ApiException("INVALID_MODE", $"不支持的模式「{modeRaw}」，可选 encode / decode");
    }

    // ---------- JSON ----------

    public static object Json(string? text, string? modeRaw)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ApiException("MISSING_TEXT", "请通过 text 参数（或 POST body 的 text 字段）提供 JSON 文本");

        var mode = (modeRaw ?? "format").Trim().ToLowerInvariant();
        if (mode is not ("format" or "minify" or "validate"))
            throw new ApiException("INVALID_MODE", $"不支持的模式「{modeRaw}」，可选 format / minify / validate");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            throw new ApiException("JSON_INVALID", $"不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var inputBytes = Encoding.UTF8.GetByteCount(text);

            if (mode == "validate")
            {
                return new
                {
                    mode,
                    valid = true,
                    inputBytes,
                    rootKind = root.ValueKind.ToString().ToLowerInvariant(),
                    topLevelCount = root.ValueKind switch
                    {
                        JsonValueKind.Object => root.EnumerateObject().Count(),
                        JsonValueKind.Array => root.GetArrayLength(),
                        _ => 0
                    },
                    depth = Depth(root),
                    note = "只校验不重写，原文保持不变"
                };
            }

            var output = mode == "format"
                ? JsonSerializer.Serialize(root, Pretty)
                : JsonSerializer.Serialize(root, Compact);
            var outputBytes = Encoding.UTF8.GetByteCount(output);

            return new
            {
                mode,
                valid = true,
                inputBytes,
                outputBytes,
                savedBytes = inputBytes - outputBytes,
                rootKind = root.ValueKind.ToString().ToLowerInvariant(),
                depth = Depth(root),
                output
            };
        }
    }

    /// <summary>JSON 最大嵌套深度，用来判断一段文本结构有多深（不含标量自身）。</summary>
    private static int Depth(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var max = 0;
                foreach (var p in el.EnumerateObject())
                    max = Math.Max(max, Depth(p.Value));
                return max + 1;
            }
            case JsonValueKind.Array:
            {
                var max = 0;
                for (var i = 0; i < el.GetArrayLength(); i++)
                    max = Math.Max(max, Depth(el[i]));
                return max + 1;
            }
            default:
                return 0;
        }
    }
}
