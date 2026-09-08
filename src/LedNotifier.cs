using System.Runtime.InteropServices;

namespace G19Claude;

/// <summary>
/// Flashes the keyboard backlight when Claude finishes a turn.
///
/// The point of this is glanceability: the LCD is small and easy to miss, but the whole
/// keyboard changing colour is visible from across the room without looking at anything.
///
/// Everything here fails soft. If the LED SDK is missing or refuses to initialise, the
/// applet keeps working and only the on-screen notification remains - a backlight effect
/// is never worth taking the applet down for.
/// </summary>
public sealed class LedNotifier : IDisposable
{
    private const string Dll = "LogitechLed.dll";

    private const int DeviceRgb = 0x02;
    private const int DevicePerKeyRgb = 0x04;

    private bool _ready;

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedInitWithName(string name);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedSetTargetDevice(int targetDevice);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedSaveCurrentLighting();

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedRestoreLighting();

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedFlashLighting(
        int redPercentage, int greenPercentage, int bluePercentage,
        int milliSecondsDuration, int milliSecondsInterval);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedStopEffects();

    [DllImport(Dll)]
    private static extern void LogiLedShutdown();

    public string Status { get; private set; } = "nicht initialisiert";

    public bool TryInitialize()
    {
        try
        {
            NativeSdk.EnsureResolver();

            if (!LogiLedInitWithName("G19Claude"))
            {
                Status = "LogiLedInit abgelehnt (laeuft LGS?)";
                return false;
            }

            // The G19 has one backlight zone; asking for per-key as well is harmless and
            // makes the same build useful on a per-key board.
            LogiLedSetTargetDevice(DeviceRgb | DevicePerKeyRgb);

            // Remember whatever profile LGS had set, so the keyboard goes back to normal.
            LogiLedSaveCurrentLighting();

            _ready = true;
            Status = "bereit";
            return true;
        }
        catch (DllNotFoundException)
        {
            Status = "LogitechLed.dll nicht gefunden";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Fehler: {ex.Message}";
            return false;
        }
    }

    /// <summary>Percentages, 0-100, as the SDK expects - not 0-255.</summary>
    public void Flash(int red, int green, int blue, int durationMs, int intervalMs)
    {
        if (!_ready) return;

        try
        {
            LogiLedFlashLighting(
                Math.Clamp(red, 0, 100), Math.Clamp(green, 0, 100), Math.Clamp(blue, 0, 100),
                durationMs, intervalMs);
        }
        catch
        {
            _ready = false;
            Status = "Effekt fehlgeschlagen - LED deaktiviert";
        }
    }

    public void StopEffects()
    {
        if (!_ready) return;
        try { LogiLedStopEffects(); } catch { /* going away anyway */ }
    }

    public void Dispose()
    {
        if (!_ready) return;

        try
        {
            LogiLedStopEffects();
            LogiLedRestoreLighting();
            LogiLedShutdown();
        }
        catch
        {
            // Nothing useful to do while shutting down.
        }

        _ready = false;
    }
}
