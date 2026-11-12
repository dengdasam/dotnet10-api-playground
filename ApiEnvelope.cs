using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ApiPlayground;

public sealed record ApiError(string Code, string Message);

/// <summary>所有接口统一返回的信封结构。</summary>
public sealed record ApiEnvelope(
    bool Success,
    object? Data,
    ApiError? Error,
    double ElapsedMs,
    string Timestamp);

/// <summary>业务可预期错误：仍以 HTTP 200 返回，错误信息写进 error 字段。</summary>
public sealed class ApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ApiWrap
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 保留中文原文，不转成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static IResult Make(bool ok, object? data, ApiError? error, Stopwatch sw)
        => Results.Json(
            new ApiEnvelope(
                ok,
                data,
                error,
                Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                DateTimeOffset.Now.ToString("O")),
            JsonOptions);

    public static IResult Run(Func<object?> body)
    {
        var sw = Stopwatch.StartNew();
        try { return Make(true, body(), null, sw); }
        catch (ApiException ex) { return Make(false, null, new ApiError(ex.Code, ex.Message), sw); }
        catch (Exception ex) { return Make(false, null, new ApiError("INTERNAL_ERROR", ex.Message), sw); }
    }

    public static async Task<IResult> RunAsync(Func<Task<object?>> body)
    {
        var sw = Stopwatch.StartNew();
        try { return Make(true, await body(), null, sw); }
        catch (ApiException ex) { return Make(false, null, new ApiError(ex.Code, ex.Message), sw); }
        catch (Exception ex) { return Make(false, null, new ApiError("INTERNAL_ERROR", ex.Message), sw); }
    }
}
