namespace G19Claude;

/// <summary>
/// A point in the transcript that moves the turn state.
///
/// <paramref name="IsEndTurn"/> marks an assistant message whose stop_reason was "end_turn" -
/// Claude handed control back and is waiting. Everything else (a tool_use stop, a user entry,
/// a tool result) means work is still in flight.
/// </summary>
public sealed record TurnMarker(DateTimeOffset Timestamp, string SessionId, bool IsEndTurn);

/// <summary>
/// Live state of one Claude Code session, accumulated as its transcript is read.
/// </summary>
public sealed record SessionInfo
{
    public required string SessionId { get; init; }
    public string Project { get; init; } = "";
    public DateTimeOffset LastActivity { get; init; }
    public string LastModel { get; init; } = "";

    /// <summary>
    /// Size of the last request's prompt: fresh input plus everything read from or written to
    /// cache. That sum is what actually occupies the context window.
    /// </summary>
    public long ContextTokens { get; init; }

    /// <summary>Most recent tool Claude invoked, with the description it gave for the call.</summary>
    public string LastTool { get; init; } = "";
    public string LastToolDetail { get; init; } = "";

    /// <summary>Tool results that came back flagged as errors - a session grinding is visible here.</summary>
    public int ErrorCount { get; init; }

    public long ContextWindow => Pricing.For(LastModel).ContextWindow;

    public double ContextFraction => ContextWindow > 0
        ? Math.Clamp(ContextTokens / (double)ContextWindow, 0, 1)
        : 0;
}

/// <summary>One assistant response, with the token counts Claude Code recorded for it.</summary>
public sealed record UsageEntry(
    DateTimeOffset Timestamp,
    string SessionId,
    string Project,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens)
{
    /// <summary>
    /// Everything that entered or left the model. Cache reads dominate this number in a long
    /// session and are almost free, so it is a poor cost proxy - use <see cref="Pricing"/> for
    /// that - but it is the right number for "how much did this conversation move".
    /// </summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;
}

/// <summary>
/// API-equivalent pricing in USD per million tokens.
///
/// Base input/output rates are the published Claude API rates. Cache rates follow the standard
/// multipliers: a 5-minute cache write costs 1.25x input, a 1-hour write 2x, and a cache read
/// 0.1x. Claude Code transcripts do not distinguish the two write TTLs at the top level, so the
/// 1-hour rate is used - Claude Code requests a 1-hour TTL.
///
/// On a Claude subscription nothing here is billed. These figures answer "what would this have
/// cost on the API", which is the only comparable unit available locally.
/// </summary>
public sealed record ModelRates(
    string Match, string Display,
    double Input, double Output, double CacheWrite, double CacheRead,
    long ContextWindow)
{
    public double Cost(UsageEntry e) =>
        (e.InputTokens * Input
         + e.OutputTokens * Output
         + e.CacheWriteTokens * CacheWrite
         + e.CacheReadTokens * CacheRead) / 1_000_000.0;
}

public static class Pricing
{
    private const long Context1M = 1_000_000;
    private const long Context200k = 200_000;

    private static readonly ModelRates[] Table =
    {
        new("claude-opus-5",   "Opus 5",     5.00, 25.00, 10.00, 0.50, Context1M),
        new("claude-opus-4-8", "Opus 4.8",   5.00, 25.00, 10.00, 0.50, Context1M),
        new("claude-opus-4-7", "Opus 4.7",   5.00, 25.00, 10.00, 0.50, Context1M),
        new("claude-opus",     "Opus",       5.00, 25.00, 10.00, 0.50, Context1M),
        new("claude-sonnet-5", "Sonnet 5",   2.00, 10.00,  4.00, 0.20, Context1M),
        new("claude-sonnet",   "Sonnet",     3.00, 15.00,  6.00, 0.30, Context1M),
        new("claude-haiku",    "Haiku 4.5",  1.00,  5.00,  2.00, 0.10, Context200k),
        new("claude-fable",    "Fable 5.1", 10.00, 50.00, 20.00, 0.25, Context1M),
    };

    private static readonly ModelRates Fallback = new("", "?", 5.00, 25.00, 10.00, 0.50, Context1M);

    public static ModelRates For(string model)
    {
        foreach (var rates in Table)
        {
            if (model.Contains(rates.Match, StringComparison.OrdinalIgnoreCase))
                return rates;
        }

        return Fallback;
    }

    public static string Display(string model) => For(model).Display;

    public static double Cost(UsageEntry entry) => For(entry.Model).Cost(entry);
}

/// <summary>
/// A rolling five-hour usage window, reconstructed from message timestamps.
///
/// Claude's usage limits reset on five-hour windows that begin with the first message after a
/// reset. Nothing local records where those boundaries actually fell, so they are rebuilt the
/// same way the community usage tools do: a block opens at the first message (floored to the
/// hour) and closes five hours later, or earlier if five idle hours pass first.
///
/// This is a reconstruction, not the server's own accounting.
/// </summary>
public sealed record UsageBlock(DateTimeOffset Start, DateTimeOffset End, List<UsageEntry> Entries)
{
    public long TotalTokens => Entries.Sum(e => e.TotalTokens);
    public long BillableTokens => Entries.Sum(e => e.InputTokens + e.OutputTokens + e.CacheWriteTokens);
    public double Cost => Entries.Sum(Pricing.Cost);
    public bool IsActive(DateTimeOffset now) => now >= Start && now < End;
    public TimeSpan Remaining(DateTimeOffset now) => End > now ? End - now : TimeSpan.Zero;
}
