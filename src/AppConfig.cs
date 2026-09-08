using System.Text.Json;
using System.Text.Json.Serialization;

namespace G19Claude;

public enum PauseMode
{
    /// <summary>The button does nothing. Display only.</summary>
    Off,

    /// <summary>
    /// Post an Escape keypress to the Claude window - the same interrupt as pressing Escape
    /// yourself. Safe: it ends the current turn cleanly and the session stays healthy.
    /// </summary>
    Interrupt,

    /// <summary>
    /// Freeze the Claude processes outright with NtSuspendProcess. This is a real pause, but
    /// it stops the process mid-syscall: an in-flight request keeps its socket open and can
    /// time out, and anything the process was writing stays half-written until it resumes.
    /// Opt in deliberately.
    /// </summary>
    Suspend,
}

public sealed class AppConfig
{
    /// <summary>
    /// Reference token budget for one five-hour window. Zero means no percentage can be shown.
    /// This is your own number - Anthropic's actual quota is not readable locally - and
    /// --calibrate fills it from your observed high-water mark.
    /// </summary>
    public long BlockBudgetTokens { get; set; }

    /// <summary>Reference budget for the rolling seven-day window.</summary>
    public long WeeklyBudgetTokens { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PauseMode PauseMode { get; set; } = PauseMode.Interrupt;

    /// <summary>Process name to act on, without the .exe suffix.</summary>
    public string ProcessName { get; set; } = "claude";

    /// <summary>
    /// How far back to read transcripts. Eight days so the rolling seven-day window is
    /// complete, with a day of slack.
    /// </summary>
    public int HistoryDays { get; set; } = 8;

    /// <summary>Show the full-screen banner when Claude finishes a turn.</summary>
    public bool NotifyOnDone { get; set; } = true;

    /// <summary>How long the banner stays up before collapsing to the header badge.</summary>
    public int DoneBannerSeconds { get; set; } = 25;

    /// <summary>
    /// Also flash the keyboard backlight. This is the part you notice without looking at the
    /// display - the whole keyboard changes colour.
    /// </summary>
    public bool NotifyWithLed { get; set; } = true;

    /// <summary>Flash colour as SDK percentages, 0-100. Default is a clear green.</summary>
    public int LedRed { get; set; }
    public int LedGreen { get; set; } = 100;
    public int LedBlue { get; set; } = 25;

    public int LedFlashSeconds { get; set; } = 4;
    public int LedFlashIntervalMs { get; set; } = 400;

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"config.json unlesbar ({ex.Message}) - Standardwerte werden benutzt.");
        }

        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Options)); }
        catch (IOException) { /* read-only install directory - run without persisting */ }
    }
}
