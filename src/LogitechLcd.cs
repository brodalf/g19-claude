using System.Runtime.InteropServices;

namespace G19Claude;

/// <summary>
/// P/Invoke surface for LogitechLcd.dll from the Logitech Gaming Software LCD SDK.
///
/// Two details matter here and are easy to get wrong:
///   * The DLL ships inside the LGS install directory and is not on PATH, so it is
///     resolved explicitly via <see cref="NativeLibrary.SetDllImportResolver"/>.
///   * The SDK returns a C++ <c>bool</c> (one byte). The default .NET marshalling for
///     <c>bool</c> is the four-byte Win32 BOOL, which reads garbage, so every returning
///     entry point is annotated with <c>UnmanagedType.I1</c>.
/// </summary>
internal static class LogitechLcd
{
    public const int TypeMono = 0x00000001;
    public const int TypeColor = 0x00000002;

    public const int ColorWidth = 320;
    public const int ColorHeight = 240;
    public const int ColorBufferBytes = ColorWidth * ColorHeight * 4;

    public const int ButtonLeft = 0x00000100;
    public const int ButtonRight = 0x00000200;
    public const int ButtonOk = 0x00000400;
    public const int ButtonCancel = 0x00000800;
    public const int ButtonUp = 0x00001000;
    public const int ButtonDown = 0x00002000;
    public const int ButtonMenu = 0x00004000;

    private const string Dll = "LogitechLcd.dll";

    /// <summary>
    /// Waits until LGS has been up long enough to take LCD clients, then registers, retrying
    /// if LogiLcdInit refuses.
    ///
    /// Launched from Autostart, the applets come up a few seconds after LCore.exe. After a
    /// reboot on 2026-09-22 all three started 6 s after LCore, kept running, and never
    /// appeared on the display; restarting them minutes later worked immediately. Waiting for
    /// LGS to settle removes that race.
    /// </summary>
    public static bool Connect(string name)
    {
        WaitForLgs(settle: TimeSpan.FromSeconds(20), maxWait: TimeSpan.FromMinutes(3));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (LogiLcdInit(name, TypeColor)) return true;
            Thread.Sleep(3000);
        }

        return false;
    }

    /// <summary>Drops the registration and registers again - for when LGS lost us.</summary>
    public static bool Reconnect(string name)
    {
        try { LogiLcdShutdown(); } catch { /* registration was already gone */ }
        return LogiLcdInit(name, TypeColor);
    }

    private static void WaitForLgs(TimeSpan settle, TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;

        while (DateTime.UtcNow < deadline)
        {
            var lcore = System.Diagnostics.Process.GetProcessesByName("LCore");
            try
            {
                if (lcore.Length > 0)
                {
                    var uptime = DateTime.Now - lcore[0].StartTime;
                    if (uptime < settle) Thread.Sleep(settle - uptime);
                    return;
                }
            }
            catch
            {
                // StartTime unreadable - LGS is there, which is what matters.
                return;
            }
            finally
            {
                foreach (var p in lcore) p.Dispose();
            }

            Thread.Sleep(2000);
        }
    }

    /// <summary>
    /// Installs the shared native resolver and verifies the DLL is actually reachable, so a
    /// missing LGS install fails here with a useful message rather than at the first P/Invoke.
    /// </summary>
    public static void Initialize()
    {
        NativeSdk.EnsureResolver();

        var candidates = NativeSdk.CandidatePaths(Dll).ToList();
        if (candidates.Any(File.Exists)) return;

        throw new DllNotFoundException(
            $"{Dll} nicht gefunden. Ist die Logitech Gaming Software installiert? " +
            $"Alternativ den SDK-Ordner ueber die Umgebungsvariable G19_LCD_SDK setzen. " +
            $"Gesucht in:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", candidates));
    }

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdInit(string friendlyName, int lcdType);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdIsConnected(int lcdType);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdIsButtonPressed(int button);

    [DllImport(Dll)]
    public static extern void LogiLcdUpdate();

    [DllImport(Dll)]
    public static extern void LogiLcdShutdown();

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdColorSetBackground(byte[] colorBitmap);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdColorSetTitle(string text, int red, int green, int blue);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLcdColorSetText(int lineNumber, string text, int red, int green, int blue);
}
