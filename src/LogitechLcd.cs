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

    private static bool _resolverInstalled;

    /// <summary>
    /// Installs the native resolver. Call once before touching any other member.
    /// </summary>
    public static void Initialize()
    {
        if (_resolverInstalled) return;
        NativeLibrary.SetDllImportResolver(typeof(LogitechLcd).Assembly, Resolve);
        _resolverInstalled = true;
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(libraryName, Dll, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        throw new DllNotFoundException(
            $"{Dll} nicht gefunden. Ist die Logitech Gaming Software installiert? " +
            $"Alternativ den SDK-Ordner ueber die Umgebungsvariable G19_LCD_SDK setzen. " +
            $"Gesucht in:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", CandidatePaths()));
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var arch = Environment.Is64BitProcess ? "x64" : "x86";

        var overridePath = Environment.GetEnvironmentVariable("G19_LCD_SDK");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return Path.Combine(overridePath, Dll);
            yield return Path.Combine(overridePath, arch, Dll);
        }

        yield return Path.Combine(AppContext.BaseDirectory, Dll);

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "Logitech Gaming Software", "SDK", "LCD", arch, Dll);
        }
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
