using System.Text.Json;

namespace ApiPlayground.Services;

/// <summary>
/// POST 请求体读取：统一大小上限与错误码，避免每个接口各写一遍。
/// 除 /api/echo、/api/webhook 需要原样回显外，其余接口都要求 JSON 对象。
/// </summary>
public static class PostBody
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>读原始字节；超过上限直接抛业务错误，不把大 body 读进内存。</summary>
    public static async Task<byte[]> ReadRawAsync(HttpRequest request, int maxBytes = MaxBytes)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
            throw new ApiException("BODY_TOO_LARGE",
                $"请求体超过上限 {maxBytes} 字节（当前 {request.ContentLength} 字节）");

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ApiException("BODY_TOO_LARGE", $"请求体超过上限 {maxBytes} 字节");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>读 JSON 对象；空 body 返回空字典，非对象/非法 JSON 抛对应错误码。</summary>
    public static async Task<Dictionary<string, JsonElement>> ReadObjectAsync(HttpRequest request, int maxBytes = MaxBytes)
    {
        var raw = await ReadRawAsync(request, maxBytes);
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (raw.Length == 0) return map;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            throw new ApiException("INVALID_JSON", "请求体不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ApiException("INVALID_BODY", "请求体必须是 JSON 对象，例如 { \"text\": \"...\" }");
            foreach (var prop in doc.RootElement.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }

    public static string? Str(this Dictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText()
        };
    }

    public static int Int(this Dictionary<string, JsonElement> map, string key, int fallback, int min, int max)
    {
        if (!map.TryGetValue(key, out var el)) return fallback;
        int value;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (!el.TryGetInt32(out value)) return fallback;
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            if (!int.TryParse(el.GetString(), out value)) return fallback;
        }
        else return fallback;
        return Math.Clamp(value, min, max);
    }

    public static bool Bool(this Dictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => el.GetString() is "1" or "true" or "yes" or "on",
            _ => false
        };
    }
}
