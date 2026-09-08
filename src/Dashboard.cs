namespace G19Claude;

public sealed record ModelSlice(string Display, long Tokens, double Cost);

/// <summary>
/// Everything the three pages need, aggregated once per poll rather than per frame.
/// </summary>
public sealed record Dashboard
{
    public static readonly Dashboard Empty = new();

    public bool HasData { get; init; }

    // Current session
    public string SessionId { get; init; } = "";
    public string SessionModel { get; init; } = "";
    public int SessionMessages { get; init; }
    public long SessionInput { get; init; }
    public long SessionOutput { get; init; }
    public long SessionCacheWrite { get; init; }
    public long SessionCacheRead { get; init; }
    public double SessionCost { get; init; }
    public TimeSpan SessionLength { get; init; }
    public long SessionTokens => SessionInput + SessionOutput + SessionCacheWrite + SessionCacheRead;

    /// <summary>Throughput over the session, the closest thing to a live burn rate.</summary>
    public double TokensPerMinute => SessionLength.TotalMinutes >= 1
        ? SessionTokens / SessionLength.TotalMinutes
        : SessionTokens;

    // Current five-hour block
    public bool HasBlock { get; init; }
    public long BlockTokens { get; init; }
    public double BlockCost { get; init; }
    public int BlockMessages { get; init; }
    public TimeSpan BlockRemaining { get; init; }
    public DateTimeOffset BlockStart { get; init; }

    // Today
    public long TodayTokens { get; init; }
    public double TodayCost { get; init; }
    public int TodayMessages { get; init; }
    public IReadOnlyList<ModelSlice> TodayByModel { get; init; } = Array.Empty<ModelSlice>();

    public static Dashboard Build(IReadOnlyList<UsageEntry> entries, string activeSessionId)
    {
        if (entries.Count == 0) return Empty;

        var now = DateTimeOffset.UtcNow;
        var session = entries.Where(e => e.SessionId == activeSessionId).ToList();

        // "Today" follows the user's local calendar day, not UTC - the display sits on their desk.
        var midnight = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset).ToUniversalTime();
        var today = entries.Where(e => e.Timestamp >= midnight).ToList();

        var blocks = UsageStore.BuildBlocks(entries);
        var block = blocks.LastOrDefault(b => b.IsActive(now));

        var byModel = today
            .GroupBy(e => Pricing.Display(e.Model))
            .Select(g => new ModelSlice(g.Key, g.Sum(e => e.TotalTokens), g.Sum(Pricing.Cost)))
            .OrderByDescending(s => s.Cost)
            .Take(4)
            .ToList();

        return new Dashboard
        {
            HasData = true,

            SessionId = activeSessionId,
            SessionModel = session.Count > 0 ? Pricing.Display(session[^1].Model) : "",
            SessionMessages = session.Count,
            SessionInput = session.Sum(e => e.InputTokens),
            SessionOutput = session.Sum(e => e.OutputTokens),
            SessionCacheWrite = session.Sum(e => e.CacheWriteTokens),
            SessionCacheRead = session.Sum(e => e.CacheReadTokens),
            SessionCost = session.Sum(Pricing.Cost),
            SessionLength = session.Count > 1
                ? session[^1].Timestamp - session[0].Timestamp
                : TimeSpan.Zero,

            HasBlock = block is not null,
            BlockTokens = block?.TotalTokens ?? 0,
            BlockCost = block?.Cost ?? 0,
            BlockMessages = block?.Entries.Count ?? 0,
            BlockRemaining = block?.Remaining(now) ?? TimeSpan.Zero,
            BlockStart = block?.Start ?? default,

            TodayTokens = today.Sum(e => e.TotalTokens),
            TodayCost = today.Sum(Pricing.Cost),
            TodayMessages = today.Count,
            TodayByModel = byModel,
        };
    }
}
