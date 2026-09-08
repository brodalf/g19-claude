using System.Text;
using System.Text.Json;

namespace G19Claude;

/// <summary>
/// Reads token usage out of the Claude Code transcripts under ~/.claude/projects.
///
/// The transcripts are append-only JSONL and the newest one is being written while we read it,
/// so each file is tracked by byte offset and only the newly appended bytes are parsed. A full
/// re-read of a multi-megabyte transcript every second would be wasteful and would fight the
/// writer for the file.
///
/// Nothing here leaves the machine, and no message content is retained - only timestamps,
/// model names and token counts.
/// </summary>
public sealed class UsageStore
{
    private readonly string _root;
    private readonly int _historyDays;

    private readonly Dictionary<string, FileCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Request ids already counted, with when they were seen. A set alone would grow without
    /// bound in a process meant to run for weeks, so these are pruned with the entries.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _seenRequests = new(StringComparer.Ordinal);
    private readonly List<UsageEntry> _entries = new();
    private readonly List<TurnMarker> _markers = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public UsageStore(int historyDays)
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        _historyDays = historyDays;
    }

    public bool RootExists => Directory.Exists(_root);
    public string Root => _root;

    /// <summary>Session id of the most recently written transcript, or empty if there is none.</summary>
    public string ActiveSessionId { get; private set; } = "";

    public DateTimeOffset LastActivity { get; private set; }

    public IReadOnlyList<UsageEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public IReadOnlyList<TurnMarker> MarkerSnapshot()
    {
        lock (_gate) return _markers.ToArray();
    }

    public IReadOnlyList<SessionInfo> SessionSnapshot()
    {
        lock (_gate) return _sessions.Values.ToArray();
    }

    public SessionInfo? Session(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public void Refresh()
    {
        if (!RootExists) return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_historyDays);

        FileInfo? newest = null;
        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;

            if (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = info;

            try { ReadNew(info); }
            catch (IOException) { /* the writer holds it for a moment; next tick will catch up */ }
            catch (UnauthorizedAccessException) { }
        }

        if (newest is not null)
        {
            ActiveSessionId = Path.GetFileNameWithoutExtension(newest.Name);
            LastActivity = new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
        }

        Prune(cutoff);
    }

    /// <summary>Read at most this much per pass, so a first read of a large transcript does not
    /// allocate the whole file at once.</summary>
    private const int MaxChunkBytes = 1 << 20;

    private void ReadNew(FileInfo info)
    {
        if (!_cursors.TryGetValue(info.FullName, out var cursor))
        {
            cursor = new FileCursor();
            _cursors[info.FullName] = cursor;
        }

        cursor.LastSeenUtc = DateTime.UtcNow;

        // A shorter file than last time means it was rotated or rewritten - start over.
        if (info.Length < cursor.Offset) cursor.Reset();

        if (info.Length <= cursor.Offset) return;

        using var stream = new FileStream(
            info.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        stream.Seek(cursor.Offset, SeekOrigin.Begin);

        var project = Path.GetFileName(Path.GetDirectoryName(info.FullName)) ?? "";
        var remaining = info.Length - cursor.Offset;

        var buffer = new byte[(int)Math.Min(remaining, MaxChunkBytes)];

        // A UTF-8 character can span the chunk boundary, and the decoder may hold up to three
        // bytes of it back; the small slack covers the character it completes on the next pass.
        var chars = new char[buffer.Length + 4];

        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read <= 0) break;

            cursor.Offset += read;
            remaining -= read;

            // The decoder carries incomplete multi-byte sequences across calls. Decoding each
            // chunk independently would corrupt every character split by a boundary - which in
            // these transcripts means umlauts and emoji, and the JSON line then fails to parse
            // and is dropped without trace.
            var count = cursor.Decoder.GetChars(buffer, 0, read, chars, 0);
            if (count > 0) ScanLines(cursor, chars.AsSpan(0, count), project);
        }
    }

    private void ScanLines(FileCursor cursor, ReadOnlySpan<char> text, string project)
    {
        var start = 0;

        while (true)
        {
            var newline = text[start..].IndexOf('\n');
            if (newline < 0)
            {
                // Trailing characters without a newline are an incomplete line still being written.
                cursor.Partial.Append(text[start..]);
                return;
            }

            var end = start + newline;
            var line = text[start..end].TrimEnd('\r');
            start = end + 1;

            if (cursor.Partial.Length > 0)
            {
                cursor.Partial.Append(line);
                var joined = cursor.Partial.ToString();
                cursor.Partial.Clear();
                if (joined.Length > 2) TryAdd(joined, project);
            }
            else if (line.Length > 2)
            {
                TryAdd(line.ToString(), project);
            }
        }
    }

    private void TryAdd(string line, string project)
    {
        UsageEntry entry;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("timestamp", out var ts) ||
                !DateTimeOffset.TryParse(ts.GetString(), out var timestamp)) return;

            timestamp = timestamp.ToUniversalTime();

            var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "";
            var kind = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            if (!root.TryGetProperty("message", out var message)) return;

            // Turn state comes from stop_reason: "end_turn" means Claude handed control back,
            // anything else (typically "tool_use") means it is still working. User entries -
            // which include tool results - also mean work is in flight. Every other line type
            // (attachment, system, queue-operation, ...) is bookkeeping and must not move the
            // state, or the display would flicker between working and done.
            if (kind is "assistant" or "user")
            {
                var stop = message.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                var isEndTurn = kind == "assistant" && stop == "end_turn";

                lock (_gate) _markers.Add(new TurnMarker(timestamp, sessionId, isEndTurn));

                TrackSession(root, message, kind, sessionId, project, timestamp);
            }

            if (!message.TryGetProperty("usage", out var usage)) return;

            // Every retry of a request repeats its usage block; the request id keeps the totals
            // from double-counting.
            var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            if (requestId is not null)
            {
                lock (_gate)
                {
                    if (!_seenRequests.TryAdd(requestId, timestamp)) return;
                }
            }

            entry = new UsageEntry(
                Timestamp: timestamp.ToUniversalTime(),
                SessionId: root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "",
                Project: project,
                Model: message.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                InputTokens: Long(usage, "input_tokens"),
                OutputTokens: Long(usage, "output_tokens"),
                CacheWriteTokens: Long(usage, "cache_creation_input_tokens"),
                CacheReadTokens: Long(usage, "cache_read_input_tokens"));
        }
        catch (JsonException)
        {
            return;
        }

        lock (_gate) _entries.Add(entry);
    }

    /// <summary>
    /// Accumulates the per-session live state: how full the context is, which tool Claude last
    /// reached for, and how many tool results came back as errors.
    ///
    /// Each transcript line is parsed exactly once - files are read incrementally by offset -
    /// so the error tally can simply be incremented here without needing its own de-duplication.
    /// </summary>
    private void TrackSession(
        JsonElement root, JsonElement message, string kind,
        string sessionId, string project, DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        lock (_gate)
        {
            var info = _sessions.GetValueOrDefault(sessionId)
                       ?? new SessionInfo { SessionId = sessionId };

            var cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() : null;

            info = info with
            {
                LastActivity = timestamp,
                Project = string.IsNullOrWhiteSpace(cwd)
                    ? (string.IsNullOrEmpty(info.Project) ? project : info.Project)
                    : Path.GetFileName(cwd.TrimEnd('\\', '/')),
            };

            if (kind == "assistant")
            {
                if (message.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } model)
                    info = info with { LastModel = model };

                if (message.TryGetProperty("usage", out var usage))
                {
                    info = info with
                    {
                        ContextTokens = Long(usage, "input_tokens")
                                        + Long(usage, "cache_read_input_tokens")
                                        + Long(usage, "cache_creation_input_tokens"),
                    };
                }

                if (FindToolUse(message) is { } tool)
                    info = info with { LastTool = tool.Name, LastToolDetail = tool.Detail };
            }
            else if (kind == "user")
            {
                info = info with { ErrorCount = info.ErrorCount + CountErrors(message) };
            }

            _sessions[sessionId] = info;
        }
    }

    private static (string Name, string Detail)? FindToolUse(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        // The last tool_use block in the message is the most recent thing Claude asked for.
        (string, string)? found = null;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "tool_use") continue;

            var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var detail = "";

            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
                input.TryGetProperty("description", out var d))
            {
                detail = d.GetString() ?? "";
            }

            found = (name, detail);
        }

        return found;
    }

    private static int CountErrors(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return 0;

        var errors = 0;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "tool_result") continue;
            if (block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True) errors++;
        }

        return errors;
    }

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private void Prune(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Timestamp < cutoff);
            _markers.RemoveAll(m => m.Timestamp < cutoff);

            foreach (var stale in _seenRequests.Where(p => p.Value < cutoff).Select(p => p.Key).ToList())
                _seenRequests.Remove(stale);

            foreach (var stale in _sessions.Where(p => p.Value.LastActivity < cutoff).Select(p => p.Key).ToList())
                _sessions.Remove(stale);
        }

        // Cursors for transcripts that have aged out of the window are dead weight; the file is
        // no longer enumerated, so the cursor would never be touched again.
        var unseen = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var stale in _cursors.Where(p => p.Value.LastSeenUtc < unseen).Select(p => p.Key).ToList())
            _cursors.Remove(stale);
    }

    /// <summary>
    /// Groups entries into five-hour blocks. A block opens at the first entry (floored to the
    /// hour) and closes five hours later, or as soon as five idle hours pass.
    /// </summary>
    public static List<UsageBlock> BuildBlocks(IReadOnlyList<UsageEntry> entries)
    {
        var blocks = new List<UsageBlock>();
        if (entries.Count == 0) return blocks;

        var window = TimeSpan.FromHours(5);
        var ordered = entries.OrderBy(e => e.Timestamp).ToList();

        var start = FloorToHour(ordered[0].Timestamp);
        var current = new List<UsageEntry>();
        var previous = ordered[0].Timestamp;

        foreach (var entry in ordered)
        {
            var overran = entry.Timestamp - start >= window;
            var wentIdle = entry.Timestamp - previous >= window;

            if (current.Count > 0 && (overran || wentIdle))
            {
                blocks.Add(new UsageBlock(start, start + window, current));
                start = FloorToHour(entry.Timestamp);
                current = new List<UsageEntry>();
            }

            current.Add(entry);
            previous = entry.Timestamp;
        }

        if (current.Count > 0) blocks.Add(new UsageBlock(start, start + window, current));
        return blocks;
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);

    private sealed class FileCursor
    {
        public long Offset;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public readonly StringBuilder Partial = new();
        public readonly Decoder Decoder = Encoding.UTF8.GetDecoder();

        public void Reset()
        {
            Offset = 0;
            Partial.Clear();
            Decoder.Reset();
        }
    }
}
