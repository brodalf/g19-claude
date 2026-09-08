# G19Claude

Zeigt den Claude-Token-Verbrauch auf dem Farbdisplay einer **Logitech G19 / G19s** —
laufende Session, das aktuelle 5-Stunden-Fenster und die Tagessumme. Die OK-Taste
unter dem Display unterbricht Claude.

Die Daten kommen ausschließlich aus den lokalen Claude-Code-Transkripten unter
`~/.claude/projects`. **Nichts verlässt den Rechner**, es werden keine Anmeldedaten
benutzt und keine Nachrichteninhalte gespeichert — nur Zeitstempel, Modellnamen und
Token-Zahlen.

## Seiten

**1 — Session**

```
+------------------------------------------+
|  * CLAUDE                       OK-STOP  |
|  ---------------------------------------- |
|  SESSION                          Opus 5 |
|                 8.21M                     |
|              Tokens gesamt                |
|  ---------------------------------------- |
|  Eingabe                             120 |
|  Ausgabe                          113.2k |
|  Cache geschrieben                299.0k |
|  Cache gelesen                      7.80M |
|  ---------------------------------------- |
|  136.819 Tok/min                    $9.72 |
|                  o . .                    |
+------------------------------------------+
```

Die Aufschlüsselung ist der Punkt: Cache-Lesevorgänge machen in einer langen Session
über 90 % der Tokens aus und kosten fast nichts. Eine reine Gesamtzahl würde das
verschleiern.

**2 — 5-Stunden-Fenster** — Verbrauch im laufenden Fenster, optional gegen ein selbst
gesetztes Budget als Balken, dazu Startzeit und Countdown bis zum Reset.

**3 — Heute** — Tagessumme und Aufschlüsselung nach Modell.

## Was das Applet *nicht* kann

**Es liest nicht dein echtes Kontingent aus.** Der offizielle Limit-Stand, den
`/usage` anzeigt, wird live über die authentifizierte Verbindung geholt und liegt
nirgends lokal zwischengespeichert — durchsucht wurden `~/.claude`, `~/.claude.json`,
`sessions/` und die Backups. Ihn auszulesen würde bedeuten, die gespeicherten
OAuth-Credentials zu benutzen und einen undokumentierten Endpunkt aufzurufen. Das tut
dieses Applet bewusst nicht.

Stattdessen rekonstruiert Seite 2 das 5-Stunden-Fenster aus den Zeitstempeln und
stellt den Verbrauch gegen ein Budget, das du selbst festlegst. Das ist eine
Näherung, kein offizieller Prozentwert.

Ebenso ist die Kostenanzeige ein **API-Äquivalent**: was der Verbrauch über die
Claude API gekostet hätte. Auf einem Abo wird davon nichts abgerechnet — die Zahl
dient als Vergleichsmaßstab zwischen Sessions.

## Konfiguration

`config.json` wird beim ersten Start neben der `.exe` angelegt:

```json
{
  "blockBudgetTokens": 0,
  "pauseMode": "Interrupt",
  "processName": "claude",
  "historyDays": 2
}
```

| Feld | Bedeutung |
|---|---|
| `blockBudgetTokens` | Token-Budget für ein 5-Stunden-Fenster. `0` blendet den Balken aus. |
| `pauseMode` | Verhalten der OK-Taste, siehe unten |
| `processName` | Prozessname ohne `.exe`, auf den die Pause wirkt |
| `historyDays` | Wie weit zurück Transkripte gelesen werden |

### Die drei Pause-Modi

| Modus | Wirkung | Risiko |
|---|---|---|
| `Interrupt` *(Standard)* | Schickt **Escape** an das Claude-Fenster — derselbe Abbruch, den du selbst mit Escape auslöst. Der laufende Turn endet sauber. | Keins. Nutzt `PostMessage`, stiehlt also nicht den Fokus. |
| `Suspend` | Friert die Claude-Prozesse mit `NtSuspendProcess` ein. Erneutes Drücken setzt fort. | **Echt vorhanden.** Der Prozess wird mitten im Syscall angehalten: eine laufende Anfrage hält ihren Socket offen und kann in ein Timeout laufen, halb geschriebene Dateien bleiben halb geschrieben. |
| `Off` | Reine Anzeige. | Keins. |

`Interrupt` ist der Standard, weil er das tut, was man meistens meint, ohne etwas
kaputtmachen zu können. `Suspend` ist ein echtes Einfrieren und deshalb eine bewusste
Entscheidung. Beendet sich das Applet, während Prozesse eingefroren sind, werden sie
vorher wieder freigegeben.

## Voraussetzungen

| | |
|---|---|
| Betriebssystem | Windows 10 oder neuer |
| Hardware | Logitech G19 oder G19s, **mit angeschlossenem Netzteil** |
| Software | Logitech Gaming Software (LGS) — nicht G HUB, das unterstützt den G19 nicht |
| Build | .NET 8 SDK |

## Bauen und starten

```powershell
dotnet build -c Release
dotnet run -c Release
```

Ohne angeschlossenen G19 lassen sich die Werte auch auf der Konsole prüfen:

```powershell
dotnet run -c Release -- --dump
```

## Aufbau

| Datei | Zweck |
|---|---|
| `src/UsageStore.cs` | Inkrementelles Lesen der JSONL-Transkripte, Dedup, 5-Stunden-Blöcke |
| `src/Usage.cs` | Datensatz je Anfrage, Preistabelle, Blockmodell |
| `src/Dashboard.cs` | Aggregation für die drei Seiten |
| `src/ClaudeControl.cs` | Interrupt bzw. Suspend/Resume |
| `src/AppConfig.cs` | `config.json` |
| `src/LcdRenderer.cs` | Zeichnet die Seiten auf 320×240 |
| `src/LogitechLcd.cs` | P/Invoke auf `LogitechLcd.dll` |
| `src/Program.cs` | Poll- und Render-Schleife |

### Zwei Details, die Arbeit gemacht haben

**Inkrementelles Lesen.** Das aktive Transkript ist mehrere Megabyte groß und wird
währenddessen weitergeschrieben. Jede Datei wird deshalb per Byte-Offset verfolgt und
nur der neu angehängte Teil geparst, geöffnet mit
`FileShare.ReadWrite | FileShare.Delete`, damit der Schreiber nicht blockiert wird.
Eine unvollständige letzte Zeile wird zwischengespeichert und beim nächsten Durchlauf
vervollständigt.

**Doppelte Zählung.** Wiederholte Anfragen tauchen mit demselben `requestId` erneut im
Transkript auf. Ohne Dedup über diese ID zählt der Verbrauch zu hoch.

## Lizenz

MIT — siehe [LICENSE](LICENSE).

Nicht mit Logitech oder Anthropic verbunden.
