using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiPlayground.Services;

/// <summary>
/// POST 实验类接口：请求体回显、multipart 上传、JWT 解码、正则、文本变换、逐行 diff。
/// 全部本地计算、零上游依赖；统一受 <see cref="PostBody.MaxBytes"/> 限制。
/// </summary>
public static class PostLabService
{
    private const int MaxUploadBytes = 2 * 1024 * 1024;      // 单文件 2 MB
    private const int MaxUploadTotal = 4 * 1024 * 1024;      // 整个表单 4 MB
    private const int MaxDiffLines = 1000;

    private static readonly Regex WordSplit = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex CaseBoundary = new(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);

    // ---------- 请求体回显 ----------

    /// <summary>把收到的请求原样拆开给你看：方法、Content-Type、头、字节数、body 解析结果。</summary>
    public static async Task<object?> EchoAsync(HttpRequest request)
    {
        var bytes = await PostBody.ReadRawAsync(request);
        var contentType = request.ContentType ?? "";

        object? json = null;
        object? form = null;
        string? text = null;
        string? parseError = null;
        string kind;

        if (bytes.Length == 0)
        {
            kind = "empty";
        }
        else if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                json = doc.RootElement.Clone();
                kind = "json";
            }
            catch (JsonException ex)
            {
                kind = "text";
                text = DecodeText(bytes);
                parseError = "声明为 JSON 但解析失败：" + ex.Message;
            }
        }
        else if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in (DecodeText(bytes) ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                var key = idx < 0 ? pair : pair[..idx];
                var value = idx < 0 ? "" : pair[(idx + 1)..];
                dict[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
            }
            form = dict;
            kind = "form";
        }
        else if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            kind = "multipart";
            text = "这是 multipart 请求，请用 POST /api/forms/upload 做文件上传演示";
        }
        else
        {
            text = DecodeText(bytes);
            kind = text is null ? "binary" : "text";
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (headers.Count >= 24) break;
            headers[header.Key] = header.Value.ToString();
        }

        return new
        {
            method = request.Method,
            path = request.Path.Value + request.QueryString.Value,
            contentType = contentType.Length == 0 ? "(未提供)" : contentType,
            declaredLength = request.ContentLength,
            receivedBytes = bytes.Length,
            bodyKind = kind,
            json,
            form,
            text = Truncate(text, 8000),
            binaryPreview = kind == "binary" ? Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 128))).ToLowerInvariant() : null,
            parseError,
            headers,
            note = kind switch
            {
                "empty" => "请求体为空：POST 也能不带 body，会照常回显",
                "json" => "已按 JSON 解析，json 字段就是解析后的对象；原文见 text 不重复返回",
                "form" => "按 application/x-www-form-urlencoded 解析成键值对",
                "multipart" => "multipart 请改用 /api/forms/upload",
                "binary" => "不是合法 UTF-8，给出前 128 字节的十六进制预览",
                _ => "按文本回显（最多 8000 字符，超出截断）"
            }
        };
    }

    private static string? DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max] + $"…（已截断，共 {text.Length} 字符）";

    // ---------- multipart 上传 ----------

    public static async Task<object?> UploadAsync(HttpRequest request)
    {
        if (!request.HasFormContentType)
            throw new ApiException("NOT_MULTIPART", "Content-Type 必须是 multipart/form-data（表单上传）");

        if (request.ContentLength is > MaxUploadTotal)
            throw new ApiException("BODY_TOO_LARGE", $"整个表单超过上限 {MaxUploadTotal / 1024 / 1024} MB");

        var form = await request.ReadFormAsync();
        if (form.Files.Count == 0 && form.Count == 0)
            throw new ApiException("EMPTY_FORM", "表单为空：请至少上传一个文件或填一个字段");

        var fileNames = new HashSet<string>(form.Files.Select(f => f.Name), StringComparer.Ordinal);
        var files = new List<object>();
        long total = 0;

        foreach (var file in form.Files)
        {
            if (file.Length > MaxUploadBytes)
                throw new ApiException("FILE_TOO_LARGE",
                    $"文件「{file.FileName}」{file.Length} 字节，超过单文件上限 {MaxUploadBytes / 1024 / 1024} MB");

            total += file.Length;
            if (total > MaxUploadTotal)
                throw new ApiException("BODY_TOO_LARGE", $"所有文件合计超过上限 {MaxUploadTotal / 1024 / 1024} MB");

            string sha256;
            await using (var stream = file.OpenReadStream())
                sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

            files.Add(new
            {
                field = file.Name,
                fileName = string.IsNullOrWhiteSpace(file.FileName) ? "(未命名)" : file.FileName,
                contentType = file.ContentType ?? "(未知)",
                size = file.Length,
                sizeText = SizeText(file.Length),
                sha256
            });
        }

        var fields = form
            .Where(kv => !fileNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);

        return new
        {
            fileCount = files.Count,
            totalBytes = total,
            totalText = SizeText(total),
            files,
            fields,
            limits = new { perFile = MaxUploadBytes, total = MaxUploadTotal, perFileText = SizeText(MaxUploadBytes), totalText = SizeText(MaxUploadTotal) },
            note = "文件只做内存解析与 SHA-256 校验，不落盘；每次请求结束即释放"
        };
    }

    private static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.00} MB"
    };

    // ---------- JWT 解码 ----------

    public static object Jwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ApiException("MISSING_TOKEN", "请提供 token（header.payload.signature 三段）");

        var parts = token.Trim().Split('.');
        if (parts.Length < 2)
            throw new ApiException("JWT_INVALID", "JWT 至少要有 header 与 payload 两段，用 . 分隔");

        var header = ParseSegment(parts[0], "header");
        var payload = ParseSegment(parts[1], "payload");

        DateTimeOffset? iat = null, nbf = null, exp = null;
        if (payload.TryGetProperty("iat", out var iatEl) && iatEl.TryGetInt64(out var iatSec))
            iat = DateTimeOffset.FromUnixTimeSeconds(iatSec);
        if (payload.TryGetProperty("nbf", out var nbfEl) && nbfEl.TryGetInt64(out var nbfSec))
            nbf = DateTimeOffset.FromUnixTimeSeconds(nbfSec);
        if (payload.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expSec))
            exp = DateTimeOffset.FromUnixTimeSeconds(expSec);

        var now = DateTimeOffset.UtcNow;
        return new
        {
            segments = parts.Length,
            header,
            payload,
            signature = parts.Length >= 3
                ? new { present = parts[2].Length > 0, length = parts[2].Length }
                : null,
            times = new
            {
                issuedAt = iat?.ToString("O"),
                notBefore = nbf?.ToString("O"),
                expiresAt = exp?.ToString("O"),
                expired = exp is not null && exp < now,
                expiresInSeconds = exp is null ? (long?)null : (long)(exp.Value - now).TotalSeconds,
                now = now.ToString("O")
            },
            note = "只做 Base64URL 解码，不验签：签名正确性需要服务端密钥，这里不校验"
        };
    }

    private static JsonElement ParseSegment(string segment, string name)
    {
        var text = DecodeBase64Url(segment, name);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ApiException("JWT_INVALID", $"{name} 段不是合法 JSON：{ex.Message}");
        }
    }

    private static string DecodeBase64Url(string segment, string name)
    {
        var raw = segment.Replace('-', '+').Replace('_', '/');
        switch (raw.Length % 4)
        {
            case 1: throw new ApiException("JWT_INVALID", $"{name} 段的 Base64URL 长度不合法");
            case 2: raw += "=="; break;
            case 3: raw += "="; break;
        }
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
        catch (FormatException) { throw new ApiException("JWT_INVALID", $"{name} 段不是合法 Base64URL"); }
    }

    // ---------- 正则 ----------

    public static object RegexRun(string? pattern, string? input, string? modeRaw,
        string? replacement, bool ignoreCase, bool multiline, int timeoutMs)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ApiException("MISSING_PATTERN", "请提供 pattern（正则表达式）");
        if (pattern.Length > 500)
            throw new ApiException("PATTERN_TOO_LONG", "pattern 最长 500 字符");

        input ??= "";
        if (Encoding.UTF8.GetByteCount(input) > PostBody.MaxBytes)
            throw new ApiException("INPUT_TOO_LARGE", $"输入文本超过上限 {PostBody.MaxBytes} 字节");

        var options = RegexOptions.None;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Multiline;

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 2000));
        Regex regex;
        try { regex = new Regex(pattern, options, timeout); }
        catch (ArgumentException ex) { throw new ApiException("REGEX_INVALID", "正则不合法：" + ex.Message); }

        var mode = (modeRaw ?? "matches").Trim().ToLowerInvariant();
        try
        {
            var matches = regex.Matches(input);

            if (mode == "replace")
            {
                var output = regex.Replace(input, replacement ?? "");
                return new
                {
                    mode = "replace",
                    pattern,
                    options = OptionText(ignoreCase, multiline),
                    replacements = matches.Count,
                    output,
                    inputBytes = Encoding.UTF8.GetByteCount(input),
                    outputBytes = Encoding.UTF8.GetByteCount(output)
                };
            }

            if (mode == "split")
            {
                var parts = regex.Split(input);
                return new
                {
                    mode = "split",
                    pattern,
                    options = OptionText(ignoreCase, multiline),
                    count = parts.Length,
                    items = parts.Take(200).ToArray(),
                    truncated = parts.Length > 200
                };
            }

            if (mode != "matches")
                throw new ApiException("INVALID_MODE", $"不支持的模式「{modeRaw}」，可选 matches / replace / split");

            var list = new List<object>();
            foreach (Match m in matches)
            {
                if (list.Count >= 200) break;
                list.Add(new
                {
                    index = m.Index,
                    length = m.Length,
                    value = m.Value,
                    groups = m.Groups.Cast<Group>()
                        .Select((g, i) => new
                        {
                            index = i,
                            name = regex.GroupNameFromNumber(i),
                            success = g.Success,
                            value = g.Success ? g.Value : null
                        }).ToArray()
                });
            }

            return new
            {
                mode = "matches",
                pattern,
                options = OptionText(ignoreCase, multiline),
                matchCount = matches.Count,
                returned = list.Count,
                truncated = matches.Count > list.Count,
                matches = list
            };
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ApiException("REGEX_TIMEOUT",
                $"正则执行超过 {timeout.TotalMilliseconds:0} ms，疑似灾难性回溯；请简化 pattern 或减少输入");
        }
    }

    private static string OptionText(bool ignoreCase, bool multiline)
    {
        var items = new List<string>();
        if (ignoreCase) items.Add("IgnoreCase");
        if (multiline) items.Add("Multiline");
        return items.Count == 0 ? "(默认)" : string.Join(" | ", items);
    }

    // ---------- 文本变换 ----------

    public static object Text(string? text, string? opRaw)
    {
        text ??= "";
        if (Encoding.UTF8.GetByteCount(text) > PostBody.MaxBytes)
            throw new ApiException("INPUT_TOO_LARGE", $"输入文本超过上限 {PostBody.MaxBytes} 字节");

        var op = (opRaw ?? "camel").Trim().ToLowerInvariant();
        var words = Words(text);

        string output;
        string? note = null;
        switch (op)
        {
            case "camel": output = words.Length == 0 ? "" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(Capitalize)); break;
            case "pascal": output = string.Concat(words.Select(Capitalize)); break;
            case "snake": output = string.Join('_', words.Select(w => w.ToLowerInvariant())); break;
            case "kebab": output = string.Join('-', words.Select(w => w.ToLowerInvariant())); break;
            case "constant": output = string.Join('_', words.Select(w => w.ToUpperInvariant())); break;
            case "upper": output = text.ToUpperInvariant(); break;
            case "lower": output = text.ToLowerInvariant(); break;
            case "title": output = string.Join(' ', words.Select(Capitalize)); break;
            case "trim": output = Regex.Replace(text.Trim(), @"\s+", " "); break;
            case "urlencode": output = WebUtility.UrlEncode(text); break;
            case "urldecode": output = WebUtility.UrlDecode(text); break;
            case "htmlencode": output = WebUtility.HtmlEncode(text); break;
            case "htmldecode": output = WebUtility.HtmlDecode(text); break;
            case "sort-lines": output = string.Join('\n', SplitLines(text).OrderBy(l => l, StringComparer.Ordinal)); break;
            case "dedupe-lines":
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                output = string.Join('\n', SplitLines(text).Where(seen.Add));
                break;
            }
            case "reverse-lines": output = string.Join('\n', SplitLines(text).Reverse()); break;
            case "count":
                var lines = SplitLines(text);
                return new
                {
                    op = "count",
                    chars = text.Length,
                    charsNoSpace = text.Count(c => !char.IsWhiteSpace(c)),
                    lines = lines.Length,
                    words = Regex.Split(text.Trim(), @"\s+").Count(w => w.Length > 0),
                    bytes = Encoding.UTF8.GetByteCount(text)
                };
            default:
                throw new ApiException("INVALID_OP",
                    $"不支持的变换「{opRaw}」，可选 camel / pascal / snake / kebab / constant / upper / lower / title / trim / urlencode / urldecode / htmlencode / htmldecode / sort-lines / dedupe-lines / reverse-lines / count");
        }

        if (op is "camel" or "pascal" or "snake" or "kebab" or "constant")
            note = "命名转换按非字母数字切词，并在小写→大写处拆开；中文会被当作分隔符";

        return new
        {
            op,
            output,
            changed = output != text,
            inputChars = text.Length,
            outputChars = output.Length,
            note
        };
    }

    private static string Capitalize(string word)
        => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static string[] Words(string text)
        => WordSplit.Split(CaseBoundary.Replace(text, "$1 $2"))
                    .Where(w => w.Length > 0)
                    .ToArray();

    // ---------- 逐行 diff ----------

    public static object Diff(string? left, string? right)
    {
        var a = SplitLines(left ?? "");
        var b = SplitLines(right ?? "");
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
            throw new ApiException("TOO_MANY_LINES", $"单侧最多 {MaxDiffLines} 行（当前 {a.Length} / {b.Length}）");

        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var lines = new List<object>();
        int x = 0, y = 0, added = 0, removed = 0, same = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                lines.Add(new { type = " ", text = a[x] });
                same++; x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                lines.Add(new { type = "-", text = a[x] });
                removed++; x++;
            }
            else
            {
                lines.Add(new { type = "+", text = b[y] });
                added++; y++;
            }
        }
        while (x < n) { lines.Add(new { type = "-", text = a[x++] }); removed++; }
        while (y < m) { lines.Add(new { type = "+", text = b[y++] }); added++; }

        return new
        {
            leftLines = n,
            rightLines = m,
            added,
            removed,
            same,
            identical = added == 0 && removed == 0,
            lines
        };
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
