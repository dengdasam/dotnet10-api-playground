using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ApiPlayground.Services;

public sealed record Note(
    string Id, string Title, string Body, string[] Tags, bool Pinned,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WebhookEvent(
    string Id, DateTimeOffset ReceivedAt, string EventName, string ContentType,
    string SourceIp, int Bytes, object? Json, string? Raw, string? ParseError, string[] HeaderNames);

/// <summary>
/// 有状态的 POST 演示：便签 CRUD（含 Idempotency-Key）与 Webhook 接收。
/// 全部存在内存里，进程重启即清空——这是演示接口，不做持久化。
/// </summary>
public sealed class PostStore
{
    private const int MaxNotes = 200;
    private const int MaxTagsPerNote = 8;
    private const int MaxTagLength = 24;
    private const int MaxTitleLength = 120;
    private const int MaxBodyLength = 10_000;
    private const int MaxHooks = 50;
    private const int MaxIdempotencyEntries = 500;
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Note> _notes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string NoteId, DateTimeOffset At)> _idempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<WebhookEvent> _hooks = new();
    private long _noteSeq;
    private long _hookSeq;

    // ---------- 便签 ----------

    public (Note Note, bool Replay) CreateNote(string? title, string? body, string[] tags,
        bool pinned, string? idempotencyKey)
    {
        var cleanTitle = (title ?? "").Trim();
        if (cleanTitle.Length == 0)
            throw new ApiException("MISSING_TITLE", "title 必填，1~120 个字符");
        if (cleanTitle.Length > MaxTitleLength)
            throw new ApiException("TITLE_TOO_LONG", $"title 最长 {MaxTitleLength} 个字符");

        var cleanBody = body ?? "";
        if (cleanBody.Length > MaxBodyLength)
            throw new ApiException("BODY_TOO_LONG", $"body 最长 {MaxBodyLength} 个字符");

        var key = (idempotencyKey ?? "").Trim();
        if (key.Length > 100)
            throw new ApiException("IDEMPOTENCY_KEY_TOO_LONG", "Idempotency-Key 最长 100 个字符");

        if (key.Length > 0)
        {
            PurgeIdempotency();
            if (_idempotency.TryGetValue(key, out var hit) && _notes.TryGetValue(hit.NoteId, out var existing))
                return (existing, true);
        }

        if (_notes.Count >= MaxNotes)
            TrimNotes();

        var now = DateTimeOffset.Now;
        var id = NewNoteId();
        var note = new Note(id, cleanTitle, cleanBody, NormalizeTags(tags), pinned, now, now);
        _notes[id] = note;

        if (key.Length > 0)
            _idempotency[key] = (id, now);

        return (note, false);
    }

    public object ListNotes(string? q, string? tag, int limit)
    {
        var keyword = (q ?? "").Trim();
        var tagFilter = (tag ?? "").Trim();

        IEnumerable<Note> rows = _notes.Values;
        if (keyword.Length > 0)
            rows = rows.Where(n => n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                || n.Body.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (tagFilter.Length > 0)
            rows = rows.Where(n => n.Tags.Contains(tagFilter, StringComparer.OrdinalIgnoreCase));

        var ordered = rows
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToArray();

        var take = Math.Clamp(limit, 1, 100);
        return new
        {
            total = ordered.Length,
            returned = Math.Min(take, ordered.Length),
            limit = take,
            items = ordered.Take(take).ToArray(),
            note = "pinned 优先，其余按创建时间倒序；内存存储，重启清空"
        };
    }

    public Note GetNote(string id)
        => _notes.TryGetValue(id, out var note)
            ? note
            : throw new ApiException("NOTE_NOT_FOUND", $"没有 id 为「{id}」的便签");

    public object DeleteNote(string id)
    {
        if (!_notes.TryRemove(id, out _))
            throw new ApiException("NOTE_NOT_FOUND", $"没有 id 为「{id}」的便签，无需删除");
        return new { id, deleted = true, remaining = _notes.Count };
    }

    private static string[] NormalizeTags(string[]? tags)
    {
        if (tags is null || tags.Length == 0) return [];
        var clean = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (clean.Length > MaxTagsPerNote)
            throw new ApiException("TOO_MANY_TAGS", $"最多 {MaxTagsPerNote} 个 tag");
        if (clean.Any(t => t.Length > MaxTagLength))
            throw new ApiException("TAG_TOO_LONG", $"单个 tag 最长 {MaxTagLength} 个字符");
        return clean;
    }

    private void TrimNotes()
    {
        // 超容量时先淘汰最旧的非置顶便签；全置顶就淘汰最旧的
        var victim = _notes.Values
            .OrderBy(n => n.Pinned)
            .ThenBy(n => n.CreatedAt)
            .FirstOrDefault();
        if (victim is not null) _notes.TryRemove(victim.Id, out _);
    }

    private void PurgeIdempotency()
    {
        if (_idempotency.Count < MaxIdempotencyEntries) return;
        var cutoff = DateTimeOffset.Now - IdempotencyWindow;
        foreach (var pair in _idempotency)
        {
            if (pair.Value.At < cutoff) _idempotency.TryRemove(pair.Key, out _);
        }
        // 仍然过多就按时间淘汰一半
        if (_idempotency.Count >= MaxIdempotencyEntries)
        {
            foreach (var pair in _idempotency.OrderBy(p => p.Value.At).Take(MaxIdempotencyEntries / 2))
                _idempotency.TryRemove(pair.Key, out _);
        }
    }

    // ---------- Webhook ----------

    public async Task<object?> AddHookAsync(HttpRequest request)
    {
        var bytes = await PostBody.ReadRawAsync(request, 32 * 1024);

        object? json = null;
        string? raw = null;
        string? parseError = null;
        if (bytes.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                json = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                raw = Truncate(Encoding.UTF8.GetString(bytes), 2000);
                parseError = "不是合法 JSON，已按原文保留：" + ex.Message;
            }
        }

        var headerEvent = request.Headers["X-Event"].ToString();
        var bodyEvent = json is JsonElement el && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String
            ? ev.GetString()
            : null;

        var headerNames = request.Headers.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        var now = DateTimeOffset.Now;
        var item = new WebhookEvent(
            NewHookId(),
            now,
            !string.IsNullOrWhiteSpace(headerEvent) ? headerEvent
                : !string.IsNullOrWhiteSpace(bodyEvent) ? bodyEvent! : "(未命名事件)",
            request.ContentType ?? "(未提供)",
            request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            bytes.Length,
            json,
            raw,
            parseError,
            headerNames);

        _hooks.Enqueue(item);
        while (_hooks.Count > MaxHooks) _hooks.TryDequeue(out _);

        return new
        {
            received = true,
            id = item.Id,
            receivedAt = item.ReceivedAt.ToString("O"),
            eventName = item.EventName,
            bytes = item.Bytes,
            json = item.Json,
            raw = item.Raw,
            parseError = item.ParseError,
            stored = _hooks.Count,
            note = "只记录不处理；用 GET /api/webhook/recent 查看最近事件，最多保留 " + MaxHooks + " 条"
        };
    }

    public object RecentHooks(int limit)
    {
        var take = Math.Clamp(limit, 1, MaxHooks);
        var all = _hooks.ToArray();
        var slice = all.Reverse().Take(take).ToArray();
        return new
        {
            stored = all.Length,
            returned = slice.Length,
            items = slice,
            note = "按接收时间倒序；内存环形缓冲，最多保留 " + MaxHooks + " 条"
        };
    }

    // ---------- 小工具 ----------

    private string NewNoteId()
        => "n" + Interlocked.Increment(ref _noteSeq).ToString("x4") + "-" + Guid.NewGuid().ToString("N")[..6];

    private string NewHookId()
        => "w" + Interlocked.Increment(ref _hookSeq).ToString("x4") + "-" + Guid.NewGuid().ToString("N")[..6];

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
