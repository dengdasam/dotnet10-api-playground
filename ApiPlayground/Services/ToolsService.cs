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

    public static object Uuid(string? countRaw, string? upperRaw, string? hyphenRaw)
    {
        var count = Int(countRaw, 1, 1, 50);
        var upper = Flag(upperRaw);
        var hyphen = hyphenRaw is null || Flag(hyphenRaw);   // 默认带连字符

        var items = new string[count];
        for (var i = 0; i < count; i++)
        {
            var text = hyphen ? Guid.NewGuid().ToString("D") : Guid.NewGuid().ToString("N");
            items[i] = upper ? text.ToUpperInvariant() : text;
        }

        return new
        {
            count,
            format = hyphen ? "8-4-4-4-12（带连字符）" : "32 位无连字符",
            upperCase = upper,
            items,
            version = "UUID v4（随机，来自 Guid.NewGuid）"
        };
    }

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
