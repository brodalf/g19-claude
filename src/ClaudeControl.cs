using System.Diagnostics;
using System.Runtime.InteropServices;

namespace G19Claude;

/// <summary>
/// The pause button. Two mechanisms, chosen by configuration, because "pause Claude" can
/// reasonably mean either of two very different things.
/// </summary>
public sealed class ClaudeControl
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;
    private const int ProcessSuspendResume = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private readonly AppConfig _config;
    private readonly List<int> _suspended = new();

    public ClaudeControl(AppConfig config)
    {
        _config = config;

        // Suspending Claude and then dying would leave it frozen with no obvious cause and no
        // way to undo it short of Task Manager. These cover the paths a normal shutdown misses;
        // a hard kill of this process still cannot be caught, which is one more reason Suspend
        // is opt-in.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ResumeOnShutdown();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ResumeOnShutdown();
    }

    public bool IsSuspended => _suspended.Count > 0;

    public string LastAction { get; private set; } = "";

    public int ProcessCount
    {
        get
        {
            try { return Process.GetProcessesByName(_config.ProcessName).Length; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Acts on the button press. In Suspend mode this toggles; in Interrupt mode every press
    /// sends one interrupt, since there is nothing to resume.
    /// </summary>
    public void Toggle()
    {
        switch (_config.PauseMode)
        {
            case PauseMode.Off:
                LastAction = "Pause deaktiviert";
                break;

            case PauseMode.Interrupt:
                LastAction = SendInterrupt() ? "Interrupt gesendet" : "Kein Fenster gefunden";
                break;

            case PauseMode.Suspend:
                if (IsSuspended) { Resume(); LastAction = "Fortgesetzt"; }
                else { LastAction = Suspend() ? "Eingefroren" : "Kein Prozess gefunden"; }
                break;
        }
    }

    /// <summary>
    /// Posts Escape to every Claude window. PostMessage rather than SendInput so the applet
    /// never steals focus from whatever the user is actually doing.
    /// </summary>
    private bool SendInterrupt()
    {
        var delivered = false;

        foreach (var process in SafeProcesses())
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) continue;

                PostMessage(window, WmKeyDown, VkEscape, IntPtr.Zero);
                PostMessage(window, WmKeyUp, VkEscape, IntPtr.Zero);
                delivered = true;
            }
        }

        return delivered;
    }

    private bool Suspend()
    {
        _suspended.Clear();

        foreach (var process in SafeProcesses())
        {
            using (process)
            {
                if (Act(process.Id, NtSuspendProcess)) _suspended.Add(process.Id);
            }
        }

        return _suspended.Count > 0;
    }

    private void Resume()
    {
        foreach (var id in _suspended) Act(id, NtResumeProcess);
        _suspended.Clear();
    }

    /// <summary>Leaving a process suspended after the applet exits would be a trap.</summary>
    public void ResumeOnShutdown()
    {
        if (IsSuspended) Resume();
    }

    private static bool Act(int processId, Func<IntPtr, int> action)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, processId);
        if (handle == IntPtr.Zero) return false;

        try { return action(handle) == 0; }
        finally { CloseHandle(handle); }
    }

    private IEnumerable<Process> SafeProcesses()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(_config.ProcessName); }
        catch { yield break; }

        foreach (var process in processes)
        {
            // Never target ourselves, whatever the configured name happens to match.
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            yield return process;
        }
    }
}
