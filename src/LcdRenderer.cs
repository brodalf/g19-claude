using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace G19Claude;

public enum Page { Session = 0, Block = 1, Tasks = 2, Sessions = 3, Today = 4 }

public sealed class LcdRenderer : IDisposable
{
    private const int W = LogitechLcd.ColorWidth;
    private const int H = LogitechLcd.ColorHeight;
    public const int PageCount = 5;

    // Claude's clay/terracotta accent against a warm near-black.
    private static readonly Color Background = Color.FromArgb(255, 15, 14, 13);
    private static readonly Color Accent = Color.FromArgb(217, 119, 87);
    private static readonly Color AccentDim = Color.FromArgb(140, 78, 58);
    private static readonly Color TextPrimary = Color.FromArgb(240, 238, 234);
    private static readonly Color TextSecondary = Color.FromArgb(168, 164, 158);
    private static readonly Color TextTertiary = Color.FromArgb(108, 104, 99);
    private static readonly Color Divider = Color.FromArgb(46, 43, 40);
    private static readonly Color Warn = Color.FromArgb(226, 178, 84);
    private static readonly Color Danger = Color.FromArgb(214, 92, 84);
    private static readonly Color Done = Color.FromArgb(124, 202, 128);

    private readonly Bitmap _canvas = new(W, H, PixelFormat.Format32bppArgb);
    private readonly Graphics _g;
    private readonly byte[] _buffer = new byte[LogitechLcd.ColorBufferBytes];

    private readonly Font _fontSmall = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _fontSmallBold = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _fontBody = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _fontBodyBold = new("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _fontBig = new("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _fontHuge = new("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly StringFormat _sf = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };

    public LcdRenderer()
    {
        _g = Graphics.FromImage(_canvas);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public void Render(Dashboard d, Page page, AppConfig config, ClaudeControl control, bool showBanner)
    {
        _g.Clear(Background);
        DrawHeader(d, control, config);

        if (showBanner && d.HasData && !d.IsWorking)
        {
            DrawDoneBanner(d);
            Push();
            return;
        }

        if (!d.HasData)
        {
            DrawIdle();
        }
        else
        {
            switch (page)
            {
                case Page.Session: DrawSession(d); break;
                case Page.Block: DrawBlock(d, config); break;
                case Page.Tasks: DrawTasks(d); break;
                case Page.Sessions: DrawSessions(d); break;
                case Page.Today: DrawToday(d); break;
            }

            DrawPageDots(page);
        }

        Push();
    }

    // -- chrome --------------------------------------------------------------

    /// <summary>
    /// The state badge is the most valuable thing in the header - it answers "can I stop
    /// watching yet" - so it gets the right-hand slot on every page.
    /// </summary>
    private void DrawHeader(Dashboard d, ClaudeControl control, AppConfig config)
    {
        using var accent = new SolidBrush(Accent);
        using var secondary = new SolidBrush(TextSecondary);
        using var divider = new Pen(Divider);

        _g.FillEllipse(accent, 16, 15, 6, 6);
        _g.DrawString("CLAUDE", _fontSmallBold, secondary, 28, 12, _sf);

        // Waiting long enough turns the badge amber - the completion banner is long gone by
        // then, and this is what catches you when you walked away.
        var waitingTooLong = !d.IsWorking &&
                             d.DoneSince >= TimeSpan.FromMinutes(Math.Max(1, config.IdleWarningMinutes));

        var (badge, colour) =
            control.IsSuspended ? ("EINGEFROREN", Danger)
            : !d.HasData ? ("--", TextTertiary)
            : d.IsWorking ? ($"ARBEITET {Clock(d.WorkingFor)}", Accent)
            : waitingTooLong ? ($"WARTET {Clock(d.DoneSince)}", Warn)
            : ($"FERTIG {Clock(d.DoneSince)}", Done);

        using var badgeBrush = new SolidBrush(colour);
        _g.DrawString(badge, _fontSmallBold, badgeBrush, W - 16 - Measure(badge, _fontSmallBold), 12, _sf);

        _g.DrawLine(divider, 16, 30, W - 16, 30);
    }

    /// <summary>
    /// Full-screen completion banner - readable from across the room, which is the whole point.
    /// </summary>
    private void DrawDoneBanner(Dashboard d)
    {
        using var done = new SolidBrush(Done);
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        using var glow = new SolidBrush(Color.FromArgb(26, Done));
        _g.FillRectangle(glow, 0, 34, W, H - 34);

        Centered("FERTIG", _fontHuge, done, 62);

        var since = d.DoneSince;
        Centered(since.TotalSeconds < 60 ? $"vor {since.Seconds}s" : $"vor {Clock(since)}",
            _fontBody, secondary, 108);

        using var divider = new Pen(Color.FromArgb(60, 90, 62));
        _g.DrawLine(divider, 40, 138, W - 40, 138);

        if (d.LastTurnDuration > TimeSpan.Zero)
        {
            Centered($"Turn dauerte {Clock(d.LastTurnDuration)}", _fontBodyBold, primary, 150);
            Centered($"{d.LastTurnRequests} Anfragen  -  {Money(d.LastTurnCost)}", _fontSmall, tertiary, 172);
        }

        Centered("Taste = ausblenden", _fontSmall, tertiary, 210);
    }

    private static string Clock(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    private void DrawIdle()
    {
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        Centered("Keine Nutzungsdaten", _fontBody, secondary, 106);
        Centered("~/.claude/projects wird beobachtet", _fontSmall, tertiary, 130);
    }

    private void DrawPageDots(Page page)
    {
        const int radius = 3, gap = 12;
        var total = PageCount * radius * 2 + (PageCount - 1) * (gap - radius * 2);
        var x = (W - total) / 2f;

        for (var i = 0; i < PageCount; i++)
        {
            using var brush = new SolidBrush(i == (int)page ? Accent : Color.FromArgb(58, 54, 50));
            _g.FillEllipse(brush, x + i * gap, 228, radius * 2, radius * 2);
        }
    }

    // -- page 1: current session --------------------------------------------

    private void DrawSession(Dashboard d)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);
        using var accent = new SolidBrush(Accent);
        using var divider = new Pen(Divider);

        _g.DrawString("SESSION", _fontSmallBold, tertiary, 16, 40, _sf);
        var model = d.SessionModel;
        _g.DrawString(model, _fontSmallBold, accent, W - 16 - Measure(model, _fontSmallBold), 40, _sf);

        _g.DrawString(Tokens(d.SessionTokens), _fontBig, primary, 16, 52, _sf);

        var cost = Money(d.SessionCost);
        _g.DrawString(cost, _fontBodyBold, accent, W - 16 - Measure(cost, _fontBodyBold), 62, _sf);

        // One line beats four rows here: what matters is that cache reads dominate and cost
        // almost nothing, not the exact split.
        var cacheShare = d.SessionTokens > 0 ? d.SessionCacheRead * 100.0 / d.SessionTokens : 0;
        _g.DrawString($"{cacheShare:0}% Cache  -  {d.TokensPerMinute:N0} Tok/min",
            _fontSmall, tertiary, 16, 88, _sf);

        _g.DrawLine(divider, 16, 106, W - 16, 106);

        // Context utilisation: the number that tells you a compaction is coming.
        _g.DrawString("KONTEXT", _fontSmallBold, tertiary, 16, 114, _sf);
        var window = $"{Tokens(d.ContextTokens)} / {Tokens(d.ContextWindow)}";
        _g.DrawString(window, _fontSmall, secondary, W - 16 - Measure(window, _fontSmall), 114, _sf);

        using var contextBrush = new SolidBrush(d.ContextFraction switch
        {
            >= 0.9 => Danger,
            >= 0.7 => Warn,
            _ => TextPrimary,
        });

        _g.DrawString($"{d.ContextFraction * 100:0}%", _fontBig, contextBrush, 16, 128, _sf);
        DrawBar(16, 164, W - 32, 6, d.ContextFraction);

        _g.DrawLine(divider, 16, 180, W - 16, 180);

        _g.DrawString($"{d.SessionMessages:N0} Anfragen", _fontSmall, secondary, 16, 188, _sf);

        var errors = d.ErrorCount == 0 ? "keine Fehler" : $"{d.ErrorCount} Fehler";
        using var errorBrush = new SolidBrush(d.ErrorCount == 0 ? TextTertiary : Warn);
        _g.DrawString(errors, _fontSmallBold, errorBrush, W - 16 - Measure(errors, _fontSmallBold), 188, _sf);

        DrawCurrentTool(d, 208);
    }

    /// <summary>
    /// What Claude is doing right now, from the last tool_use block. Only meaningful while a
    /// turn is running - after end_turn it would just be the last thing it happened to do.
    /// </summary>
    private void DrawCurrentTool(Dashboard d, int y)
    {
        if (!d.IsWorking || string.IsNullOrEmpty(d.CurrentTool)) return;

        using var accent = new SolidBrush(Accent);
        using var secondary = new SolidBrush(TextSecondary);

        _g.FillRectangle(accent, 16, y + 3, 3, 10);

        var label = d.CurrentTool;
        _g.DrawString(label, _fontSmallBold, accent, 25, y, _sf);

        var detail = d.CurrentToolDetail;
        if (string.IsNullOrEmpty(detail)) return;

        var x = 25 + (int)Measure(label, _fontSmallBold) + 8;
        Clipped(detail, _fontSmall, secondary, x, y, W - x - 16);
    }

    private void Row(int y, string label, string value, Brush labelBrush, Brush valueBrush)
    {
        _g.DrawString(label, _fontSmall, labelBrush, 16, y, _sf);
        _g.DrawString(value, _fontSmallBold, valueBrush, W - 16 - Measure(value, _fontSmallBold), y, _sf);
    }

    // -- page 2: five-hour block --------------------------------------------

    /// <summary>
    /// Two gauges: the five-hour window and the rolling seven days, each as a percentage of a
    /// reference budget. The percentages are only as meaningful as those budgets - see the
    /// README on why the real quota cannot be read locally.
    /// </summary>
    private void DrawBlock(Dashboard d, AppConfig config)
    {
        using var tertiary = new SolidBrush(TextTertiary);
        using var secondary = new SolidBrush(TextSecondary);

        var reset = d.HasBlock
            ? $"Reset {(int)d.BlockRemaining.TotalHours}:{d.BlockRemaining.Minutes:00}"
            : "kein Fenster";

        DrawGauge(
            y: 40,
            label: "5 STUNDEN",
            note: reset,
            fraction: d.BlockFraction,
            used: d.BlockTokens,
            budget: config.BlockBudgetTokens);

        using var divider = new Pen(Divider);
        _g.DrawLine(divider, 16, 122, W - 16, 122);

        DrawGauge(
            y: 130,
            label: "7 TAGE",
            note: $"{d.WeeklyMessages:N0} Anfragen",
            fraction: d.WeeklyFraction,
            used: d.WeeklyTokens,
            budget: config.WeeklyBudgetTokens);

        if (d.BlockFraction is null || d.WeeklyFraction is null)
            Centered("Budget setzen:  G19Claude.exe --calibrate", _fontSmall, tertiary, 210);
        else
            Centered($"{Money(d.BlockCost)} im Fenster  -  {Money(d.WeeklyCost)} in 7 Tagen",
                _fontSmall, secondary, 210);
    }

    private void DrawGauge(int y, string label, string note, double? fraction, long used, long budget)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        _g.DrawString(label, _fontSmallBold, tertiary, 16, y, _sf);
        _g.DrawString(note, _fontSmall, tertiary, W - 16 - Measure(note, _fontSmall), y, _sf);

        if (fraction is null)
        {
            _g.DrawString(Tokens(used), _fontBig, primary, 16, y + 14, _sf);
            _g.DrawString("kein Budget", _fontSmall, tertiary,
                W - 16 - Measure("kein Budget", _fontSmall), y + 30, _sf);
            DrawBar(16, y + 54, W - 32, 8, 0);
            return;
        }

        var value = fraction.Value;
        var percent = $"{value * 100:0}%";

        // The percentage is the headline; the raw counts stay available but secondary.
        using var percentBrush = new SolidBrush(value switch
        {
            >= 0.9 => Danger,
            >= 0.7 => Warn,
            _ => TextPrimary,
        });

        _g.DrawString(percent, _fontBig, percentBrush, 16, y + 14, _sf);

        var counts = $"{Tokens(used)} / {Tokens(budget)}";
        _g.DrawString(counts, _fontSmall, secondary, W - 16 - Measure(counts, _fontSmall), y + 32, _sf);

        // Over budget still draws a full bar - the percentage above carries the overshoot.
        DrawBar(16, y + 54, W - 32, 8, Math.Clamp(value, 0, 1));
    }

    private void DrawBar(int x, int y, int width, int height, double fraction)
    {
        using var track = new SolidBrush(Color.FromArgb(52, 48, 44));
        _g.FillRectangle(track, x, y, width, height);

        var colour = fraction switch
        {
            >= 0.9 => Danger,
            >= 0.7 => Warn,
            _ => Accent,
        };

        var fill = (float)(width * fraction);
        if (fill <= 0) return;

        using var brush = new LinearGradientBrush(
            new RectangleF(x, y, Math.Max(fill, 1), height),
            AccentDim, colour, LinearGradientMode.Horizontal);
        _g.FillRectangle(brush, x, y, fill, height);
    }

    // -- page 3: task list ---------------------------------------------------

    private const int TaskRows = 7;
    private const int TaskRowHeight = 20;

    private void DrawTasks(Dashboard d)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        _g.DrawString("TASKS", _fontSmallBold, tertiary, 16, 40, _sf);

        if (d.Tasks.Count == 0)
        {
            Centered("Keine Task-Liste", _fontBody, secondary, 106);
            Centered("in dieser Session", _fontSmall, tertiary, 128);
            return;
        }

        var done = d.Tasks.Count(t => t.State == TaskState.Completed);
        var counter = $"{done}/{d.Tasks.Count} fertig";
        _g.DrawString(counter, _fontSmall, tertiary, W - 16 - Measure(counter, _fontSmall), 40, _sf);

        DrawBar(16, 58, W - 32, 5, done / (double)d.Tasks.Count);

        // Scroll so the work in flight is always on screen: start at the first unfinished task,
        // and only fall back to the tail once everything is done.
        var first = d.Tasks.ToList().FindIndex(t => t.State != TaskState.Completed);
        if (first < 0) first = Math.Max(0, d.Tasks.Count - TaskRows);
        first = Math.Clamp(first, 0, Math.Max(0, d.Tasks.Count - TaskRows));

        var y = 74;
        foreach (var task in d.Tasks.Skip(first).Take(TaskRows))
        {
            DrawTaskRow(task, y);
            y += TaskRowHeight;
        }

        var hidden = d.Tasks.Count - first - Math.Min(TaskRows, d.Tasks.Count - first);
        if (hidden > 0)
            _g.DrawString($"+{hidden} weitere", _fontSmall, tertiary, 34, y, _sf);
    }

    private void DrawTaskRow(TaskItem task, int y)
    {
        var colour = task.State switch
        {
            TaskState.Completed => Done,
            TaskState.InProgress => Accent,
            TaskState.Blocked => Warn,
            _ => TextTertiary,
        };

        using var marker = new SolidBrush(colour);
        using var ring = new Pen(colour);

        switch (task.State)
        {
            case TaskState.Completed:
                _g.FillEllipse(marker, 16, y + 3, 8, 8);
                break;

            case TaskState.InProgress:
                // Filled dot inside a ring - the active row should read differently at a glance.
                _g.DrawEllipse(ring, 14, y + 1, 12, 12);
                _g.FillEllipse(marker, 17, y + 4, 6, 6);
                break;

            default:
                _g.DrawEllipse(ring, 16, y + 3, 8, 8);
                break;
        }

        using var text = new SolidBrush(task.State switch
        {
            TaskState.Completed => TextTertiary,
            TaskState.InProgress => TextPrimary,
            _ => TextSecondary,
        });

        var font = task.State == TaskState.InProgress ? _fontSmallBold : _fontSmall;
        Clipped(task.Display, font, text, 34, y, W - 34 - 16);
    }

    private void Clipped(string text, Font font, Brush brush, int x, int y, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return;

        _g.SetClip(new Rectangle(x, y - 2, maxWidth, (int)font.Size + 8));
        _g.DrawString(text, font, brush, x, y, _sf);
        _g.ResetClip();
    }

    // -- page 4: all sessions ------------------------------------------------

    private void DrawSessions(Dashboard d)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        _g.DrawString("SESSIONS", _fontSmallBold, tertiary, 16, 40, _sf);

        if (d.Sessions.Count == 0)
        {
            Centered("Keine Sessions der letzten 12 h", _fontBody, secondary, 108);
            return;
        }

        var working = d.Sessions.Count(s => s.IsWorking);
        var summary = working > 0 ? $"{working} arbeiten" : "alle warten";
        _g.DrawString(summary, _fontSmall, tertiary, W - 16 - Measure(summary, _fontSmall), 40, _sf);

        var y = 60;
        foreach (var row in d.Sessions.Take(7))
        {
            DrawSessionRow(row, y);
            y += 23;
        }

        var hidden = d.Sessions.Count - Math.Min(7, d.Sessions.Count);
        if (hidden > 0)
            _g.DrawString($"+{hidden} weitere", _fontSmall, tertiary, 32, y, _sf);
    }

    private void DrawSessionRow(SessionRow row, int y)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        var state = row.IsWorking ? Accent : row.Idle >= TimeSpan.FromMinutes(30) ? TextTertiary : Done;

        using var dot = new SolidBrush(state);
        _g.FillEllipse(dot, 16, y + 3, 8, 8);

        // The session this applet is tracking gets a subtle backing so it is findable.
        if (row.IsActive)
        {
            using var highlight = new SolidBrush(Color.FromArgb(28, Accent));
            _g.FillRectangle(highlight, 12, y - 2, W - 24, 19);
        }

        Clipped(row.Project, row.IsActive ? _fontSmallBold : _fontSmall, primary, 32, y, 104);

        var status = row.IsWorking ? "arbeitet" : $"wartet {Compact(row.Idle)}";
        using var statusBrush = new SolidBrush(row.IsWorking ? Accent : TextSecondary);
        _g.DrawString(status, _fontSmall, statusBrush, 144, y, _sf);

        if (row.ErrorCount > 0)
        {
            var errors = $"{row.ErrorCount}!";
            using var warn = new SolidBrush(Warn);
            _g.DrawString(errors, _fontSmallBold, warn, 224, y, _sf);
        }

        var context = $"{row.ContextFraction * 100:0}%";
        using var contextBrush = new SolidBrush(row.ContextFraction >= 0.8 ? Warn : TextTertiary);
        _g.DrawString(context, _fontSmall, contextBrush, W - 16 - Measure(context, _fontSmall), y, _sf);
    }

    /// <summary>Short duration for tight rows: 45s, 12m, 3h.</summary>
    private static string Compact(TimeSpan value) => value.TotalMinutes switch
    {
        < 1 => $"{value.Seconds}s",
        < 60 => $"{(int)value.TotalMinutes}m",
        _ => $"{(int)value.TotalHours}h",
    };

    // -- page 5: today -------------------------------------------------------

    private void DrawToday(Dashboard d)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);
        using var accent = new SolidBrush(Accent);

        _g.DrawString("HEUTE", _fontSmallBold, tertiary, 16, 40, _sf);
        var messages = $"{d.TodayMessages:N0} Anfragen";
        _g.DrawString(messages, _fontSmall, tertiary, W - 16 - Measure(messages, _fontSmall), 40, _sf);

        Centered(Tokens(d.TodayTokens), _fontHuge, primary, 56);
        Centered(Money(d.TodayCost) + " API-Aequivalent", _fontSmall, tertiary, 100);

        using var divider = new Pen(Divider);
        _g.DrawLine(divider, 16, 122, W - 16, 122);

        _g.DrawString("NACH MODELL", _fontSmallBold, tertiary, 16, 130, _sf);

        var max = d.TodayByModel.Count > 0 ? d.TodayByModel.Max(m => m.Tokens) : 1;
        var y = 150;

        foreach (var slice in d.TodayByModel)
        {
            _g.DrawString(slice.Display, _fontSmall, primary, 16, y, _sf);

            var value = Tokens(slice.Tokens);
            _g.DrawString(value, _fontSmall, secondary, W - 16 - Measure(value, _fontSmall), y, _sf);

            DrawBar(16, y + 14, W - 32, 4, max > 0 ? slice.Tokens / (double)max : 0);
            y += 24;
        }
    }

    // -- helpers -------------------------------------------------------------

    /// <summary>Token counts run to millions; four significant characters is all the LCD needs.</summary>
    private static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000.0:0.00}B",
        >= 1_000_000 => $"{value / 1_000_000.0:0.00}M",
        >= 1_000 => $"{value / 1_000.0:0.0}k",
        _ => value.ToString("N0"),
    };

    private static string Money(double usd) => usd >= 100 ? $"${usd:0}" : $"${usd:0.00}";

    private float Measure(string text, Font font) => _g.MeasureString(text, font, int.MaxValue, _sf).Width;

    private void Centered(string text, Font font, Brush brush, float y) =>
        _g.DrawString(text, font, brush, (W - Measure(text, font)) / 2f, y, _sf);

    /// <summary>
    /// Format32bppArgb is B,G,R,A in memory on little-endian, matching the BGRA layout
    /// LogiLcdColorSetBackground expects, so the locked bits copy straight across.
    /// </summary>
    private void Push()
    {
        var data = _canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = W * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
            }
            else
            {
                for (var y = 0; y < H; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _buffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _canvas.UnlockBits(data);
        }

        LogitechLcd.LogiLcdColorSetBackground(_buffer);
        LogitechLcd.LogiLcdUpdate();
    }

    public void Dispose()
    {
        _sf.Dispose();
        _fontSmall.Dispose();
        _fontSmallBold.Dispose();
        _fontBody.Dispose();
        _fontBodyBold.Dispose();
        _fontBig.Dispose();
        _fontHuge.Dispose();
        _g.Dispose();
        _canvas.Dispose();
    }
}
