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
    private readonly HashSet<string> _seenRequests = new(StringComparer.Ordinal);
    private readonly List<UsageEntry> _entries = new();
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

    private void ReadNew(FileInfo info)
    {
        if (!_cursors.TryGetValue(info.FullName, out var cursor))
        {
            cursor = new FileCursor();
            _cursors[info.FullName] = cursor;
        }

        // A shorter file than last time means it was rotated or rewritten - start over.
        if (info.Length < cursor.Offset)
        {
            cursor.Offset = 0;
            cursor.Partial.Clear();
        }

        if (info.Length == cursor.Offset) return;

        using var stream = new FileStream(
            info.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        stream.Seek(cursor.Offset, SeekOrigin.Begin);

        var buffer = new byte[info.Length - cursor.Offset];
        var read = stream.Read(buffer, 0, buffer.Length);
        cursor.Offset += read;

        var text = cursor.Partial + Encoding.UTF8.GetString(buffer, 0, read);
        cursor.Partial.Clear();

        var project = Path.GetFileName(Path.GetDirectoryName(info.FullName)) ?? "";
        var start = 0;

        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                // Trailing bytes without a newline are an incomplete line still being written.
                cursor.Partial.Append(text, start, text.Length - start);
                break;
            }

            var line = text.AsSpan(start, newline - start).TrimEnd('\r');
            start = newline + 1;

            if (line.Length > 2) TryAdd(line.ToString(), project);
        }
    }

    private void TryAdd(string line, string project)
    {
        UsageEntry entry;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("usage", out var usage)) return;

            // Every retry of a request repeats its usage block; the request id keeps the totals
            // from double-counting.
            var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            if (requestId is not null && !_seenRequests.Add(requestId)) return;

            if (!root.TryGetProperty("timestamp", out var ts) ||
                !DateTimeOffset.TryParse(ts.GetString(), out var timestamp)) return;

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

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private void Prune(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.RemoveAll(e => e.Timestamp < cutoff);
        }
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
        public readonly StringBuilder Partial = new();
    }
}
