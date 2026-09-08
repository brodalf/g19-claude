namespace G19Claude;

internal static class Program
{
    private const int TargetFps = 5;

    private static volatile Dashboard _dashboard = Dashboard.Empty;

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var config = AppConfig.Load();

        Console.WriteLine("G19Claude - Claude-Nutzung auf dem Logitech G19 LCD");
        Console.WriteLine($"Konfiguration: {AppConfig.Path}");
        Console.WriteLine($"Pause-Modus:   {config.PauseMode}");
        Console.WriteLine($"Block-Budget:  {(config.BlockBudgetTokens > 0 ? $"{config.BlockBudgetTokens:N0} Tokens" : "nicht gesetzt")}");
        Console.WriteLine();

        var store = new UsageStore(config.HistoryDays);
        if (!store.RootExists)
        {
            Console.Error.WriteLine($"Nicht gefunden: {store.Root}");
            Console.Error.WriteLine("Ohne Claude-Code-Transkripte gibt es nichts anzuzeigen.");
            return 4;
        }

        if (args.Contains("--calibrate"))
        {
            Calibrate(store, config);
            return 0;
        }

        if (args.Contains("--dump"))
        {
            DumpOnce(store, config, ArgValue(args, "--session"));
            return 0;
        }

        try
        {
            LogitechLcd.Initialize();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!LogitechLcd.LogiLcdInit("Claude", LogitechLcd.TypeColor))
        {
            Console.Error.WriteLine("LogiLcdInit fehlgeschlagen. Laeuft die Logitech Gaming Software (LCore.exe)?");
            return 3;
        }

        var control = new ClaudeControl(config);

        using var led = new LedNotifier();
        if (config.NotifyWithLed)
        {
            led.TryInitialize();
            Console.WriteLine($"LED-Signal:    {led.Status}");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var poller = Task.Run(() => PollLoop(store, new TaskStore(), config, cts.Token), cts.Token);

        Console.WriteLine("Laeuft. Tasten am Display: links/rechts = Seite, OK = Pause.");
        Console.WriteLine("Seiten: 1 Session, 2 Limits, 3 Tasks, 4 Sessions, 5 Heute.");
        Console.WriteLine("Beenden mit Strg+C.");
        Console.WriteLine();

        try
        {
            RenderLoop(config, control, led, cts.Token);
        }
        finally
        {
            cts.Cancel();
            try { poller.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down anyway */ }

            // Leaving Claude frozen after the applet exits would be a nasty surprise.
            control.ResumeOnShutdown();

            LogitechLcd.LogiLcdShutdown();
            Console.WriteLine();
            Console.WriteLine("Beendet.");
        }

        return 0;
    }

    private static void PollLoop(UsageStore store, TaskStore tasks, AppConfig config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                store.Refresh();
                tasks.Refresh(store.ActiveSessionId);
                _dashboard = Dashboard.Build(
                    store.Snapshot(), store.MarkerSnapshot(), store.ActiveSessionId, config, tasks.Tasks, store.SessionSnapshot());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Lesefehler: {ex.Message}");
            }

            try { Task.Delay(1000, ct).Wait(ct); }
            catch (OperationCanceledException) { return; }
            catch (AggregateException) { return; }
        }
    }

    private static void RenderLoop(AppConfig config, ClaudeControl control, LedNotifier led, CancellationToken ct)
    {
        using var renderer = new LcdRenderer();

        var frameTime = TimeSpan.FromSeconds(1.0 / TargetFps);
        var buttons = new ButtonReader();
        var page = Page.Session;
        var warnedDisconnected = false;
        var lastAction = "";

        // The first observed state must not count as a transition, or starting the applet
        // while Claude happens to be idle would fire a completion banner for nothing.
        var primed = false;
        var wasWorking = true;
        var bannerUntil = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;

            if (!LogitechLcd.LogiLcdIsConnected(LogitechLcd.TypeColor))
            {
                if (!warnedDisconnected)
                {
                    Console.WriteLine("Warte auf das G19-Display ...");
                    warnedDisconnected = true;
                }

                Sleep(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (warnedDisconnected)
            {
                Console.WriteLine("Display verbunden.");
                warnedDisconnected = false;
            }

            var d = _dashboard;

            if (d.HasData)
            {
                if (primed && wasWorking && !d.IsWorking)
                {
                    bannerUntil = DateTime.UtcNow.AddSeconds(config.DoneBannerSeconds);

                    if (config.NotifyWithLed)
                        led.Flash(config.LedRed, config.LedGreen, config.LedBlue,
                            config.LedFlashSeconds * 1000, config.LedFlashIntervalMs);

                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] Fertig - Turn dauerte " +
                        $"{d.LastTurnDuration:mm\\:ss}, {d.LastTurnRequests} Anfragen, ${d.LastTurnCost:0.00}");
                }

                // Work resuming clears the banner immediately - a stale "done" is worse than none.
                if (d.IsWorking && bannerUntil > DateTime.MinValue)
                {
                    bannerUntil = DateTime.MinValue;
                    led.StopEffects();
                }

                wasWorking = d.IsWorking;
                primed = true;
            }

            var showBanner = config.NotifyOnDone && DateTime.UtcNow < bannerUntil;

            var left = buttons.WasPressed(LogitechLcd.ButtonLeft);
            var right = buttons.WasPressed(LogitechLcd.ButtonRight);
            var ok = buttons.WasPressed(LogitechLcd.ButtonOk);

            if (showBanner && (left || right || ok))
            {
                // While the banner is up any button dismisses it and does nothing else, so
                // reaching for it cannot accidentally interrupt Claude.
                bannerUntil = DateTime.MinValue;
                led.StopEffects();
                showBanner = false;
            }
            else
            {
                if (right) page = Next(page, 1);
                if (left) page = Next(page, -1);

                if (ok)
                {
                    control.Toggle();
                    if (control.LastAction != lastAction)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {control.LastAction}");
                        lastAction = control.LastAction;
                    }
                }
            }

            renderer.Render(d, page, config, control, showBanner);

            var remaining = frameTime - (DateTime.UtcNow - frameStart);
            if (remaining > TimeSpan.Zero) Sleep(remaining, ct);
        }
    }

    /// <summary>
    /// Derives reference budgets from what this machine has actually consumed.
    ///
    /// Anthropic's real quota is not readable locally, so a percentage needs some denominator.
    /// The busiest five-hour window on record is the most defensible one available: at 100%
    /// you are back at your own high-water mark. It is a personal yardstick, not the account
    /// limit, and the README says so plainly.
    /// </summary>
    private static void Calibrate(UsageStore store, AppConfig config)
    {
        // A config written before the weekly gauge existed can still say 2 days, which would
        // silently truncate the seven-day window. Calibration is the right moment to fix it.
        if (config.HistoryDays < 8)
        {
            Console.WriteLine($"historyDays {config.HistoryDays} -> 8 (fuer das 7-Tage-Fenster noetig)");
            config.HistoryDays = 8;
            config.Save();
            store = new UsageStore(config.HistoryDays);
        }

        Console.WriteLine("Lese Verlauf ...");
        store.Refresh();

        var entries = store.Snapshot();
        if (entries.Count == 0)
        {
            Console.WriteLine("Keine Nutzungsdaten gefunden - nichts zu kalibrieren.");
            return;
        }

        // Headroom matters. Setting the budget to the observed peak puts you at 100% the moment
        // you calibrate, which tells you nothing. A quarter above the peak means a heavy stretch
        // reads around 80% and the warning colours only appear past your own record.
        const double headroom = 1.25;

        var blocks = UsageStore.BuildBlocks(entries);
        var busiest = blocks.Max(b => b.TotalTokens);
        var first = entries.Min(e => e.Timestamp);
        var span = DateTimeOffset.UtcNow - first;

        var weekStart = DateTimeOffset.UtcNow.AddDays(-7);
        var weekly = entries.Where(e => e.Timestamp >= weekStart).Sum(e => e.TotalTokens);

        config.BlockBudgetTokens = RoundUp((long)(busiest * headroom));
        config.WeeklyBudgetTokens = RoundUp((long)(weekly * headroom));
        config.Save();

        Console.WriteLine($"Verlauf ab       : {first.ToLocalTime():dd.MM. HH:mm}  ({entries.Count:N0} Anfragen, {blocks.Count} Fenster)");
        Console.WriteLine($"Groesstes Fenster: {busiest:N0} Tokens");
        Console.WriteLine($"Letzte 7 Tage    : {weekly:N0} Tokens");
        Console.WriteLine();
        Console.WriteLine($"Gesetzt: blockBudgetTokens  = {config.BlockBudgetTokens:N0}  (Spitze + 25 %)");
        Console.WriteLine($"Gesetzt: weeklyBudgetTokens = {config.WeeklyBudgetTokens:N0}  (7 Tage + 25 %)");
        Console.WriteLine($"in {AppConfig.Path}");
        Console.WriteLine();

        if (span < TimeSpan.FromDays(7))
        {
            Console.WriteLine($"ACHTUNG: Es liegen erst {span.TotalDays:0.0} Tage Verlauf vor. Der Wochenwert ist");
            Console.WriteLine("noch keine volle Woche - nach ein paar Tagen erneut kalibrieren.");
            Console.WriteLine();
        }

        Console.WriteLine("Das sind deine eigenen Verbrauchswerte, nicht Anthropics Limit.");
        Console.WriteLine("Werte in der config.json jederzeit von Hand anpassbar.");
    }

    /// <summary>Rounds up to a readable step so the budget does not look spuriously precise.</summary>
    private static long RoundUp(long value) => value switch
    {
        >= 1_000_000 => (long)Math.Ceiling(value / 100_000.0) * 100_000,
        >= 100_000 => (long)Math.Ceiling(value / 10_000.0) * 10_000,
        _ => (long)Math.Ceiling(value / 1_000.0) * 1_000,
    };

    /// <summary>Prints the aggregate to the console once - useful without a G19 attached.</summary>
    private static void DumpOnce(UsageStore store, AppConfig config, string? sessionOverride)
    {
        store.Refresh();

        var sessionId = string.IsNullOrWhiteSpace(sessionOverride) ? store.ActiveSessionId : sessionOverride;

        var tasks = new TaskStore();
        tasks.Refresh(sessionId);

        var d = Dashboard.Build(
            store.Snapshot(), store.MarkerSnapshot(), sessionId, config, tasks.Tasks, store.SessionSnapshot());

        if (!d.HasData)
        {
            Console.WriteLine("Keine Nutzungsdaten gefunden.");
            return;
        }

        Console.WriteLine($"Zustand        : {(d.IsWorking ? $"ARBEITET seit {d.WorkingFor:mm\\:ss}" : $"FERTIG seit {d.DoneSince:mm\\:ss}")}");
        Console.WriteLine($"Letzter Turn   : {d.LastTurnDuration:mm\\:ss}, {d.LastTurnRequests} Anfragen, ${d.LastTurnCost:0.00}");
        Console.WriteLine();
        Console.WriteLine($"Aktive Session : {d.SessionId}");
        Console.WriteLine($"  Modell       : {d.SessionModel}");
        Console.WriteLine($"  Anfragen     : {d.SessionMessages:N0}");
        Console.WriteLine($"  Eingabe      : {d.SessionInput:N0}");
        Console.WriteLine($"  Ausgabe      : {d.SessionOutput:N0}");
        Console.WriteLine($"  Cache Write  : {d.SessionCacheWrite:N0}");
        Console.WriteLine($"  Cache Read   : {d.SessionCacheRead:N0}");
        Console.WriteLine($"  Gesamt       : {d.SessionTokens:N0}");
        Console.WriteLine($"  API-Aequiv.  : ${d.SessionCost:0.00}");
        Console.WriteLine($"  Kontext      : {d.ContextTokens:N0} / {d.ContextWindow:N0}  = {d.ContextFraction * 100:0}%");
        Console.WriteLine($"  Fehler       : {d.ErrorCount}");
        if (!string.IsNullOrEmpty(d.CurrentTool))
            Console.WriteLine($"  Werkzeug     : {d.CurrentTool} - {d.CurrentToolDetail}");
        Console.WriteLine();
        Console.WriteLine($"5-Stunden-Fenster: {(d.HasBlock ? "aktiv" : "keines")}");
        if (d.HasBlock)
        {
            Console.WriteLine($"  Start        : {d.BlockStart.ToLocalTime():HH:mm}");
            Console.WriteLine($"  Reset in     : {(int)d.BlockRemaining.TotalHours}:{d.BlockRemaining.Minutes:00}");
            Console.WriteLine($"  Tokens       : {d.BlockTokens:N0}{Percent(d.BlockFraction, config.BlockBudgetTokens)}");
            Console.WriteLine($"  API-Aequiv.  : ${d.BlockCost:0.00}");
        }
        Console.WriteLine();
        Console.WriteLine($"7 Tage rollierend: {d.WeeklyTokens:N0} Tokens{Percent(d.WeeklyFraction, config.WeeklyBudgetTokens)}");
        Console.WriteLine($"  Anfragen     : {d.WeeklyMessages:N0}");
        Console.WriteLine($"  API-Aequiv.  : ${d.WeeklyCost:0.00}");
        Console.WriteLine();
        Console.WriteLine($"Heute: {d.TodayTokens:N0} Tokens, {d.TodayMessages:N0} Anfragen, ${d.TodayCost:0.00}");
        foreach (var slice in d.TodayByModel)
            Console.WriteLine($"  {slice.Display,-12} {slice.Tokens,14:N0}  ${slice.Cost:0.00}");

        Console.WriteLine();
        Console.WriteLine($"Sessions der letzten 12 h: {d.Sessions.Count}");
        foreach (var s in d.Sessions)
        {
            var state = s.IsWorking ? "arbeitet" : $"wartet {s.Idle:hh\\:mm\\:ss}";
            var marker = s.IsActive ? "*" : " ";
            Console.WriteLine($"  {marker} {s.Project,-22} {state,-18} ctx {s.ContextFraction * 100,3:0}%  {s.ErrorCount} Fehler");
        }

        Console.WriteLine();
        if (d.Tasks.Count == 0)
        {
            Console.WriteLine("Tasks: keine Task-Liste in dieser Session");
        }
        else
        {
            var done = d.Tasks.Count(t => t.State == TaskState.Completed);
            Console.WriteLine($"Tasks: {done}/{d.Tasks.Count} fertig");
            foreach (var task in d.Tasks)
            {
                var glyph = task.State switch
                {
                    TaskState.Completed => "[x]",
                    TaskState.InProgress => "[>]",
                    TaskState.Blocked => "[!]",
                    _ => "[ ]",
                };
                Console.WriteLine($"  {glyph} {task.Display}");
            }
        }
    }

    /// <summary>Reads "--flag value" or "--flag=value" out of the argument list.</summary>
    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal)) return args[i][(flag.Length + 1)..];
        }

        return null;
    }

    private static string Percent(double? fraction, long budget) =>
        fraction is null ? "  (kein Budget gesetzt)" : $"  = {fraction.Value * 100:0}% von {budget:N0}";

    private static Page Next(Page page, int delta) =>
        (Page)(((int)page + delta + LcdRenderer.PageCount) % LcdRenderer.PageCount);

    private static void Sleep(TimeSpan duration, CancellationToken ct)
    {
        try { ct.WaitHandle.WaitOne(duration); }
        catch (ObjectDisposedException) { /* cancelled during shutdown */ }
    }

    /// <summary>Level-triggered button state to edge-triggered events.</summary>
    private sealed class ButtonReader
    {
        private readonly HashSet<int> _down = new();

        public bool WasPressed(int button)
        {
            if (LogitechLcd.LogiLcdIsButtonPressed(button)) return _down.Add(button);
            _down.Remove(button);
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            G19Claude - zeigt den Claude-Token-Verbrauch auf dem Logitech G19 LCD.

              G19Claude.exe [--dump] [--calibrate]

              --dump      Werte einmal auf der Konsole ausgeben und beenden
                          (funktioniert ohne angeschlossenen G19)
              --calibrate Referenzbudgets aus dem eigenen Verlauf setzen und beenden
              --help      Diese Hilfe

            Seiten (Tasten links/rechts unter dem Display):
              1  Session    Tokens der laufenden Session, aufgeschluesselt
              2  Limits     5-Stunden-Fenster und 7 Tage, jeweils in Prozent
              3  Heute      Tagessumme, aufgeschluesselt nach Modell

            Die Prozentwerte beziehen sich auf Budgets in der config.json, nicht auf
            Anthropics Kontingent - das liegt nirgends lokal vor. --calibrate setzt sie
            auf die eigenen Hoechstwerte.

            OK-Taste: je nach pauseMode in config.json
              interrupt  Escape an das Claude-Fenster - beendet den laufenden Turn (Standard)
              suspend    friert die Claude-Prozesse ein, erneut druecken setzt fort
              off        Anzeige only

            Datenquelle sind die lokalen Transkripte unter ~/.claude/projects.
            Nichts davon verlaesst den Rechner.
            """);
    }
}
