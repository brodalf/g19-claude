using System.Reflection;
using System.Runtime.InteropServices;

namespace G19Claude;

/// <summary>
/// Resolves the Logitech SDK DLLs, which live inside the LGS install directory and are not on
/// PATH.
///
/// This is deliberately one resolver for both SDKs: .NET allows only a single
/// <see cref="NativeLibrary.SetDllImportResolver"/> call per assembly, and a second one throws
/// "A resolver is already set for the assembly". Adding the LED SDK alongside the LCD SDK is
/// exactly the case that trips over it.
/// </summary>
internal static class NativeSdk
{
    private const string SdkRoot = "Logitech Gaming Software";

    /// <summary>Library file name to its subdirectory under SDK\, plus the env var that overrides it.</summary>
    private static readonly Dictionary<string, (string Folder, string EnvVar)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogitechLcd.dll"] = ("LCD", "G19_LCD_SDK"),
        ["LogitechLed.dll"] = ("LED", "G19_LED_SDK"),
    };

    private static bool _installed;
    private static readonly object Gate = new();

    public static void EnsureResolver()
    {
        lock (Gate)
        {
            if (_installed) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeSdk).Assembly, Resolve);
            _installed = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!Known.ContainsKey(libraryName)) return IntPtr.Zero;

        foreach (var candidate in CandidatePaths(libraryName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Returning zero lets the runtime raise DllNotFoundException at the call site, which
        // each caller handles in the way that suits it - fatal for the LCD, ignorable for LEDs.
        return IntPtr.Zero;
    }

    public static IEnumerable<string> CandidatePaths(string libraryName)
    {
        if (!Known.TryGetValue(libraryName, out var known)) yield break;

        var arch = Environment.Is64BitProcess ? "x64" : "x86";

        var custom = Environment.GetEnvironmentVariable(known.EnvVar);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            yield return Path.Combine(custom, libraryName);
            yield return Path.Combine(custom, arch, libraryName);
        }

        yield return Path.Combine(AppContext.BaseDirectory, libraryName);

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, SdkRoot, "SDK", known.Folder, arch, libraryName);
        }
    }
}
