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

        if (args.Contains("--dump"))
        {
            DumpOnce(store);
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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var poller = Task.Run(() => PollLoop(store, cts.Token), cts.Token);

        Console.WriteLine("Laeuft. Tasten am Display: links/rechts = Seite, OK = Pause.");
        Console.WriteLine("Seiten: 1 Session, 2 5-Stunden-Fenster, 3 Heute.");
        Console.WriteLine("Beenden mit Strg+C.");
        Console.WriteLine();

        try
        {
            RenderLoop(config, control, cts.Token);
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

    private static void PollLoop(UsageStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                store.Refresh();
                _dashboard = Dashboard.Build(store.Snapshot(), store.ActiveSessionId);
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

    private static void RenderLoop(AppConfig config, ClaudeControl control, CancellationToken ct)
    {
        using var renderer = new LcdRenderer();

        var frameTime = TimeSpan.FromSeconds(1.0 / TargetFps);
        var buttons = new ButtonReader();
        var page = Page.Session;
        var warnedDisconnected = false;
        var lastAction = "";

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

            if (buttons.WasPressed(LogitechLcd.ButtonRight)) page = Next(page, 1);
            if (buttons.WasPressed(LogitechLcd.ButtonLeft)) page = Next(page, -1);

            if (buttons.WasPressed(LogitechLcd.ButtonOk))
            {
                control.Toggle();
                if (control.LastAction != lastAction)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {control.LastAction}");
                    lastAction = control.LastAction;
                }
            }

            renderer.Render(_dashboard, page, config, control);

            var remaining = frameTime - (DateTime.UtcNow - frameStart);
            if (remaining > TimeSpan.Zero) Sleep(remaining, ct);
        }
    }

    /// <summary>Prints the aggregate to the console once - useful without a G19 attached.</summary>
    private static void DumpOnce(UsageStore store)
    {
        store.Refresh();
        var d = Dashboard.Build(store.Snapshot(), store.ActiveSessionId);

        if (!d.HasData)
        {
            Console.WriteLine("Keine Nutzungsdaten gefunden.");
            return;
        }

        Console.WriteLine($"Aktive Session : {d.SessionId}");
        Console.WriteLine($"  Modell       : {d.SessionModel}");
        Console.WriteLine($"  Anfragen     : {d.SessionMessages:N0}");
        Console.WriteLine($"  Eingabe      : {d.SessionInput:N0}");
        Console.WriteLine($"  Ausgabe      : {d.SessionOutput:N0}");
        Console.WriteLine($"  Cache Write  : {d.SessionCacheWrite:N0}");
        Console.WriteLine($"  Cache Read   : {d.SessionCacheRead:N0}");
        Console.WriteLine($"  Gesamt       : {d.SessionTokens:N0}");
        Console.WriteLine($"  API-Aequiv.  : ${d.SessionCost:0.00}");
        Console.WriteLine();
        Console.WriteLine($"5-Stunden-Fenster: {(d.HasBlock ? "aktiv" : "keines")}");
        if (d.HasBlock)
        {
            Console.WriteLine($"  Start        : {d.BlockStart.ToLocalTime():HH:mm}");
            Console.WriteLine($"  Reset in     : {(int)d.BlockRemaining.TotalHours}:{d.BlockRemaining.Minutes:00}");
            Console.WriteLine($"  Tokens       : {d.BlockTokens:N0}");
            Console.WriteLine($"  API-Aequiv.  : ${d.BlockCost:0.00}");
        }
        Console.WriteLine();
        Console.WriteLine($"Heute: {d.TodayTokens:N0} Tokens, {d.TodayMessages:N0} Anfragen, ${d.TodayCost:0.00}");
        foreach (var slice in d.TodayByModel)
            Console.WriteLine($"  {slice.Display,-12} {slice.Tokens,14:N0}  ${slice.Cost:0.00}");
    }

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

              G19Claude.exe [--dump]

              --dump    Werte einmal auf der Konsole ausgeben und beenden
                        (funktioniert ohne angeschlossenen G19)
              --help    Diese Hilfe

            Seiten (Tasten links/rechts unter dem Display):
              1  Session    Tokens der laufenden Session, aufgeschluesselt
              2  Fenster    Verbrauch im laufenden 5-Stunden-Fenster
              3  Heute      Tagessumme, aufgeschluesselt nach Modell

            OK-Taste: je nach pauseMode in config.json
              interrupt  Escape an das Claude-Fenster - beendet den laufenden Turn (Standard)
              suspend    friert die Claude-Prozesse ein, erneut druecken setzt fort
              off        Anzeige only

            Datenquelle sind die lokalen Transkripte unter ~/.claude/projects.
            Nichts davon verlaesst den Rechner.
            """);
    }
}
