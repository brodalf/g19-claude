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
    /// <summary>Token budget for one five-hour window. Zero hides the budget bar.</summary>
    public long BlockBudgetTokens { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PauseMode PauseMode { get; set; } = PauseMode.Interrupt;

    /// <summary>Process name to act on, without the .exe suffix.</summary>
    public string ProcessName { get; set; } = "claude";

    /// <summary>How far back to read transcripts. Two days is plenty for today plus the block.</summary>
    public int HistoryDays { get; set; } = 2;

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
