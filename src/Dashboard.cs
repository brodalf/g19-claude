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

    // Turn state - the "is Claude done yet" signal
    public bool IsWorking { get; init; } = true;
    public DateTimeOffset DoneAt { get; init; }
    public DateTimeOffset WorkingSince { get; init; }
    public TimeSpan LastTurnDuration { get; init; }
    public double LastTurnCost { get; init; }
    public int LastTurnRequests { get; init; }

    public TimeSpan DoneSince => IsWorking || DoneAt == default
        ? TimeSpan.Zero
        : Max(DateTimeOffset.UtcNow - DoneAt, TimeSpan.Zero);

    public TimeSpan WorkingFor => !IsWorking || WorkingSince == default
        ? TimeSpan.Zero
        : Max(DateTimeOffset.UtcNow - WorkingSince, TimeSpan.Zero);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // Rolling seven-day window. Claude's weekly limit resets on a fixed schedule that is not
    // knowable locally, so this is a rolling sum rather than a calendar week.
    public long WeeklyTokens { get; init; }
    public double WeeklyCost { get; init; }
    public int WeeklyMessages { get; init; }

    /// <summary>Fraction of the reference budget, or null when no budget is configured.</summary>
    public double? BlockFraction { get; init; }
    public double? WeeklyFraction { get; init; }

    /// <summary>Claude Code's todo list for the active session, empty when it never used one.</summary>
    public IReadOnlyList<TaskItem> Tasks { get; init; } = Array.Empty<TaskItem>();

    // Today
    public long TodayTokens { get; init; }
    public double TodayCost { get; init; }
    public int TodayMessages { get; init; }
    public IReadOnlyList<ModelSlice> TodayByModel { get; init; } = Array.Empty<ModelSlice>();

    public static Dashboard Build(
        IReadOnlyList<UsageEntry> entries,
        IReadOnlyList<TurnMarker> markers,
        string activeSessionId,
        AppConfig config,
        IReadOnlyList<TaskItem> tasks)
    {
        if (entries.Count == 0) return Empty;

        var now = DateTimeOffset.UtcNow;
        var session = entries.Where(e => e.SessionId == activeSessionId).ToList();
        var turn = ResolveTurn(session, markers, activeSessionId);

        // "Today" follows the user's local calendar day, not UTC - the display sits on their desk.
        var midnight = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset).ToUniversalTime();
        var today = entries.Where(e => e.Timestamp >= midnight).ToList();

        var blocks = UsageStore.BuildBlocks(entries);
        var block = blocks.LastOrDefault(b => b.IsActive(now));

        var weekStart = now.AddDays(-7);
        var week = entries.Where(e => e.Timestamp >= weekStart).ToList();
        var weekTokens = week.Sum(e => e.TotalTokens);
        var blockTokens = block?.TotalTokens ?? 0;

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

            Tasks = tasks,

            WeeklyTokens = weekTokens,
            WeeklyCost = week.Sum(Pricing.Cost),
            WeeklyMessages = week.Count,

            BlockFraction = config.BlockBudgetTokens > 0
                ? blockTokens / (double)config.BlockBudgetTokens
                : null,
            WeeklyFraction = config.WeeklyBudgetTokens > 0
                ? weekTokens / (double)config.WeeklyBudgetTokens
                : null,

            IsWorking = turn.IsWorking,
            DoneAt = turn.DoneAt,
            WorkingSince = turn.WorkingSince,
            LastTurnDuration = turn.Duration,
            LastTurnCost = turn.Cost,
            LastTurnRequests = turn.Requests,

            TodayTokens = today.Sum(e => e.TotalTokens),
            TodayCost = today.Sum(Pricing.Cost),
            TodayMessages = today.Count,
            TodayByModel = byModel,
        };
    }

    private sealed record TurnState(
        bool IsWorking, DateTimeOffset DoneAt, DateTimeOffset WorkingSince,
        TimeSpan Duration, double Cost, int Requests);

    /// <summary>
    /// Collapses each run of consecutive end_turn markers into its last entry.
    ///
    /// Claude Code writes two end_turn lines per completed turn - distinct uuids, effectively
    /// the same timestamp. Without collapsing them, the "previous end_turn" is always the twin
    /// of the current one and every measured turn comes out as zero length. Observed as
    /// end_turn indices 8,9 / 23,24 / 30,31 / 219,220 in a real transcript.
    /// </summary>
    private static List<TurnMarker> Collapse(IEnumerable<TurnMarker> ordered)
    {
        var result = new List<TurnMarker>();

        foreach (var marker in ordered)
        {
            if (marker.IsEndTurn && result.Count > 0 && result[^1].IsEndTurn)
                result[^1] = marker;
            else
                result.Add(marker);
        }

        return result;
    }

    /// <summary>
    /// Derives whether Claude is mid-turn, and what the last completed turn cost.
    ///
    /// A turn runs from just after the previous "end_turn" to the next one, so its duration is
    /// the whole cycle the user waited through - prompt, tool calls and all - not just the
    /// final message.
    /// </summary>
    private static TurnState ResolveTurn(
        List<UsageEntry> session, IReadOnlyList<TurnMarker> markers, string activeSessionId)
    {
        var own = Collapse(markers
            .Where(m => m.SessionId == activeSessionId)
            .OrderBy(m => m.Timestamp));

        if (own.Count == 0)
            return new TurnState(true, default, default, TimeSpan.Zero, 0, 0);

        var lastEnd = own.FindLastIndex(m => m.IsEndTurn);
        var isWorking = !own[^1].IsEndTurn;

        // Work started with the first marker after the previous completed turn.
        var workingSince = isWorking
            ? own[Math.Clamp(lastEnd + 1, 0, own.Count - 1)].Timestamp
            : default;

        var doneAt = isWorking ? default : own[^1].Timestamp;

        if (lastEnd < 0)
            return new TurnState(isWorking, doneAt, workingSince, TimeSpan.Zero, 0, 0);

        var previousEnd = lastEnd > 0 ? own.FindLastIndex(lastEnd - 1, m => m.IsEndTurn) : -1;
        var turnStart = own[Math.Clamp(previousEnd + 1, 0, lastEnd)].Timestamp;
        var turnEnd = own[lastEnd].Timestamp;

        var inTurn = session.Where(e => e.Timestamp > turnStart && e.Timestamp <= turnEnd).ToList();

        return new TurnState(
            isWorking,
            doneAt,
            workingSince,
            turnEnd - turnStart,
            inTurn.Sum(Pricing.Cost),
            inTurn.Count);
    }
}
