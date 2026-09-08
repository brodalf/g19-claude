using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace G19Claude;

public enum Page { Session = 0, Block = 1, Today = 2 }

public sealed class LcdRenderer : IDisposable
{
    private const int W = LogitechLcd.ColorWidth;
    private const int H = LogitechLcd.ColorHeight;
    public const int PageCount = 3;

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
        DrawHeader(d, control);

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
    private void DrawHeader(Dashboard d, ClaudeControl control)
    {
        using var accent = new SolidBrush(Accent);
        using var secondary = new SolidBrush(TextSecondary);
        using var divider = new Pen(Divider);

        _g.FillEllipse(accent, 16, 15, 6, 6);
        _g.DrawString("CLAUDE", _fontSmallBold, secondary, 28, 12, _sf);

        var (badge, colour) =
            control.IsSuspended ? ("EINGEFROREN", Danger)
            : !d.HasData ? ("--", TextTertiary)
            : d.IsWorking ? ($"ARBEITET {Clock(d.WorkingFor)}", Accent)
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

        _g.DrawString("SESSION", _fontSmallBold, tertiary, 16, 40, _sf);
        var model = d.SessionModel;
        _g.DrawString(model, _fontSmallBold, accent, W - 16 - Measure(model, _fontSmallBold), 40, _sf);

        Centered(Tokens(d.SessionTokens), _fontHuge, primary, 58);
        Centered("Tokens gesamt", _fontSmall, tertiary, 100);

        using var divider = new Pen(Divider);
        _g.DrawLine(divider, 16, 122, W - 16, 122);

        // Cache reads dwarf everything else in a long session, so the split is worth showing.
        Row(132, "Eingabe", Tokens(d.SessionInput), secondary, primary);
        Row(150, "Ausgabe", Tokens(d.SessionOutput), secondary, primary);
        Row(168, "Cache geschrieben", Tokens(d.SessionCacheWrite), secondary, primary);
        Row(186, "Cache gelesen", Tokens(d.SessionCacheRead), secondary, primary);

        _g.DrawLine(divider, 16, 204, W - 16, 204);

        var rate = $"{d.TokensPerMinute:N0} Tok/min";
        _g.DrawString(rate, _fontSmall, tertiary, 16, 210, _sf);

        var cost = Money(d.SessionCost);
        _g.DrawString(cost, _fontSmallBold, accent, W - 16 - Measure(cost, _fontSmallBold), 210, _sf);
    }

    private void Row(int y, string label, string value, Brush labelBrush, Brush valueBrush)
    {
        _g.DrawString(label, _fontSmall, labelBrush, 16, y, _sf);
        _g.DrawString(value, _fontSmallBold, valueBrush, W - 16 - Measure(value, _fontSmallBold), y, _sf);
    }

    // -- page 2: five-hour block --------------------------------------------

    private void DrawBlock(Dashboard d, AppConfig config)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);
        using var accent = new SolidBrush(Accent);

        _g.DrawString("5-STUNDEN-FENSTER", _fontSmallBold, tertiary, 16, 40, _sf);

        if (!d.HasBlock)
        {
            Centered("Kein aktives Fenster", _fontBody, secondary, 110);
            return;
        }

        Centered(Tokens(d.BlockTokens), _fontHuge, primary, 56);

        var budget = config.BlockBudgetTokens;
        if (budget > 0)
        {
            var fraction = Math.Clamp(d.BlockTokens / (double)budget, 0, 1);
            Centered($"{fraction * 100:0}% von {Tokens(budget)}", _fontSmall, tertiary, 100);
            DrawBar(16, 122, W - 32, 8, fraction);
        }
        else
        {
            Centered("kein Budget gesetzt (config.json)", _fontSmall, tertiary, 100);
        }

        using var divider = new Pen(Divider);
        _g.DrawLine(divider, 16, 146, W - 16, 146);

        Row(156, "Anfragen", d.BlockMessages.ToString("N0"), secondary, primary);
        Row(176, "Fenster startete", d.BlockStart.ToLocalTime().ToString("HH:mm"), secondary, primary);

        var remaining = d.BlockRemaining;
        Row(196, "Reset in", $"{(int)remaining.TotalHours}:{remaining.Minutes:00}", secondary, primary);

        var cost = Money(d.BlockCost);
        _g.DrawString(cost, _fontSmallBold, accent, W - 16 - Measure(cost, _fontSmallBold), 212, _sf);
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

    // -- page 3: today -------------------------------------------------------

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
        _fontHuge.Dispose();
        _g.Dispose();
        _canvas.Dispose();
    }
}
