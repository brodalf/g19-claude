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
|  * CLAUDE                    ARBEITET 1:23 |
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

**2 — Limits** — zwei Anzeigen in Prozent, das 5-Stunden-Fenster mit Reset-Countdown
und die rollierenden 7 Tage:

```
+------------------------------------------+
|  * CLAUDE                    ARBEITET 0:12 |
|  ---------------------------------------- |
|  5 STUNDEN                     Reset 3:19 |
|  80%                       28.1M / 35.1M  |
|  ========================------------      |
|  ---------------------------------------- |
|  7 TAGE                      353 Anfragen |
|  80%                       94.0M / 117.5M |
|  ========================------------      |
|                                           |
|   $18.75 im Fenster  -  $87.48 in 7 Tagen |
+------------------------------------------+
```

Die Prozentzahl wechselt ab 70 % auf Gelb und ab 90 % auf Rot.

### Worauf sich die Prozente beziehen

**Nicht auf Anthropics Kontingent** — das liegt nirgends lokal vor (siehe unten).
Bezugsgröße ist ein Budget in der `config.json`, und damit es von Anfang an sinnvoll
ist, leitet `--calibrate` es aus deinem eigenen Verlauf ab:

```powershell
G19Claude.exe --calibrate
```

Gesetzt werden dein **größtes bisheriges 5-Stunden-Fenster plus 25 %** und dein
**7-Tage-Verbrauch plus 25 %**. Die Reserve ist wichtig: ohne sie stünde die Anzeige
im Moment der Kalibrierung zwangsläufig auf 100 % und wäre wertlos. Mit ihr liest sich
eine intensive Phase als etwa 80 %, und Gelb bzw. Rot erscheinen erst jenseits deines
eigenen Rekords.

Liegen weniger als sieben Tage Verlauf vor, sagt `--calibrate` das ausdrücklich — der
Wochenwert ist dann noch keine volle Woche. Nach ein paar Tagen einfach erneut laufen
lassen. Beide Werte lassen sich jederzeit von Hand in der `config.json` überschreiben.

**3 — Tasks** — Claude Codes Todo-Liste der laufenden Session: erledigt, in Arbeit,
offen und blockiert, mit Fortschrittsbalken. Die Liste scrollt automatisch zur ersten
unerledigten Aufgabe, damit das gerade Laufende immer sichtbar ist. Sessions ohne
Todo-Liste zeigen das ausdrücklich an.

**4 — Heute** — Tagessumme und Aufschlüsselung nach Modell.

## Fertig-Meldung

Oben rechts steht auf jeder Seite, ob Claude gerade arbeitet — `ARBEITET 1:23` in
Terrakotta, `FERTIG 0:42` in Grün. Sobald ein Turn abgeschlossen ist, übernimmt für
25 Sekunden ein Vollbild-Banner:

```
+------------------------------------------+
|  * CLAUDE                     FERTIG 0:07 |
|  ---------------------------------------- |
|                                           |
|                FERTIG                     |
|                 vor 7s                    |
|        --------------------               |
|          Turn dauerte 5:13                |
|        54 Anfragen  -  $9.42              |
|                                           |
|            Taste = ausblenden             |
+------------------------------------------+
```

Gleichzeitig **blinkt die Tastaturbeleuchtung grün** (`LogitechLed.dll`, vier
Sekunden). Das ist der Teil, den du bemerkst, ohne aufs Display zu schauen. Danach
wird die vorherige Beleuchtung wiederhergestellt.

Während das Banner steht, blendet **jede** Taste es nur aus und tut sonst nichts —
so kann das Wegdrücken nicht versehentlich Claude unterbrechen. Nimmt Claude die
Arbeit wieder auf, verschwindet das Banner sofort.

### Woher der Zustand kommt

Aus dem `stop_reason` der Assistant-Zeilen im Transkript: `end_turn` heißt, Claude hat
zurückgegeben und wartet; alles andere — typischerweise `tool_use`, oder eine
`user`-Zeile, unter der auch Tool-Ergebnisse laufen — heißt, es läuft noch. Alle
übrigen Zeilentypen (`attachment`, `system`, `queue-operation`, …) sind Buchhaltung
und dürfen den Zustand nicht bewegen, sonst flackert die Anzeige.

Eine Eigenheit war dabei entscheidend: **Claude Code schreibt pro Turn zwei
`end_turn`-Zeilen** mit verschiedenen uuids und praktisch gleichem Zeitstempel. Ohne
sie zusammenzufassen ist der „vorherige end_turn" immer der Zwilling des aktuellen,
und jede gemessene Turn-Dauer kommt als null heraus.

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
  "historyDays": 2,
  "notifyOnDone": true,
  "doneBannerSeconds": 25,
  "notifyWithLed": true,
  "ledRed": 0,
  "ledGreen": 100,
  "ledBlue": 25,
  "ledFlashSeconds": 4,
  "ledFlashIntervalMs": 400
}
```

| Feld | Bedeutung |
|---|---|
| `blockBudgetTokens` | Token-Budget für ein 5-Stunden-Fenster. `0` blendet den Balken aus. |
| `pauseMode` | Verhalten der OK-Taste, siehe unten |
| `processName` | Prozessname ohne `.exe`, auf den die Pause wirkt |
| `historyDays` | Wie weit zurück Transkripte gelesen werden |
| `notifyOnDone` | Vollbild-Banner beim Fertigwerden |
| `doneBannerSeconds` | Wie lange das Banner steht |
| `notifyWithLed` | Tastaturbeleuchtung blinken lassen |
| `ledRed` / `ledGreen` / `ledBlue` | Blinkfarbe in **Prozent** (0–100), nicht 0–255 |
| `ledFlashSeconds` / `ledFlashIntervalMs` | Dauer und Taktung des Blinkens |

Das LED-Signal schlägt weich fehl: fehlt `LogitechLed.dll` oder verweigert das SDK die
Initialisierung, läuft alles Übrige weiter und nur das Blinken entfällt. Der Status
steht beim Start auf der Konsole.

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
| `src/Dashboard.cs` | Aggregation für die vier Seiten, Turn-Zustand |
| `src/TaskStore.cs` | Liest die Todo-Liste aus `~/.claude/tasks/<session>/` |
| `src/ClaudeControl.cs` | Interrupt bzw. Suspend/Resume |
| `src/LedNotifier.cs` | Blinken der Tastaturbeleuchtung, scheitert weich |
| `src/NativeSdk.cs` | Gemeinsamer Resolver für LCD- und LED-DLL |
| `src/AppConfig.cs` | `config.json` |
| `src/LcdRenderer.cs` | Zeichnet die Seiten und das Fertig-Banner auf 320×240 |
| `src/LogitechLcd.cs` | P/Invoke auf `LogitechLcd.dll` |
| `src/Program.cs` | Poll- und Render-Schleife, Zustandsübergang |

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

### Ein Resolver für beide SDKs

`NativeLibrary.SetDllImportResolver` lässt sich **pro Assembly nur einmal** aufrufen.
Ein zweiter Aufruf wirft „A resolver is already set for the assembly" — genau der
Fall, wenn neben dem LCD- auch das LED-SDK dazukommt. Beide Bibliotheken werden
deshalb über einen gemeinsamen Resolver in `src/NativeSdk.cs` aufgelöst.
