# HILDE – Architektur

## Grundsatz

Koe definiert *was* HILDE kann, Windows definiert *wie*. Alles läuft lokal; nach der Modell-Einrichtung
gibt es keine Netzwerkverbindungen (Ausnahme: ausdrücklich gestarteter Modell-Download).

## Prozesse

```
HILDE.exe  (WPF + WebView2, .NET 10)
 ├─ Pill (WPF, WS_EX_NOACTIVATE – stiehlt nie den Fokus)
 ├─ Tray (NotifyIcon)
 ├─ Hauptfenster (WebView2 → ui/ unter https://app.hilde.example, nur lokal gemappt)
 ├─ Tastatur-Hook (eigener Thread, WH_KEYBOARD_LL)
 ├─ DictationController (Zustandsmaschine + Pipeline, Hilde.Core)
 │
 ├─▶ Hilde.Engine.exe --kind speech    (whisper.cpp, eigener Prozess, Named Pipe)
 └─▶ Hilde.Engine.exe --kind language  (llama.cpp,   eigener Prozess, Named Pipe)
```

Engine-Prozesse: Start bei Bedarf (Vorwärmen, sobald eine Aufnahme > 400 ms läuft), Healthcheck,
Timeout, Abbruch = Prozess beenden, Neustart beim nächsten Aufruf, Entladen nach Inaktivität (Standard
5 min), CPU-Fallback nach GPU-Absturz, Beenden wenn HILDE endet. Protokoll: eine JSON-Zeile je Nachricht,
Versionsprüfung beim `hello`. Pipes sind `CurrentUserOnly`.

## Diktierpipeline (Pflichtenheft 5.3)

1. Hotkey ↓ → Zielfenster/Fokus merken (`CaptureTarget`), Aufnahme sofort starten (kein abgeschnittener Wortanfang).
2. WASAPI-Rohdaten laufend nach `Recordings/{id}/capture.wav` (≈ 1 s Flush → Absturzschutz).
3. Hotkey ↑ → Normalisieren auf 16 kHz Mono PCM (`mic16k.wav`), Silero VAD (Fallback Energie).
4. Whisper → Rohtranskript, Halluzinationsfilter (Untertitel-Floskeln). **Rohtext wird sofort gespeichert.**
5. Wörterbuch (exakte Regeln) → KI-Bereinigung (optional) → Sicherheitsprüfung → Wörterbuch erneut.
6. Einfügen: UIA-Prüfung (Passwort/schreibgeschützt) → Unicode-Zwischenablage + Strg+V (Konsole: Umschalt+Einfg)
   bzw. Unicode-Tippen → sonst Kopierfenster.
7. Archiv nach Einstellung; Audio wird nach Erfolg gelöscht, außer „Audio behalten“.

Fehler in Schritt 5 verlieren nie das Diktat: Rückfall auf regelbasierte Bereinigung.
Fehler in Schritt 4: Aufnahme bleibt gesichert, Archiv-Status „Fehlgeschlagen“, „Erneut verarbeiten“.
Absturz: Beim Start findet `RecoverAsync` unfertige Aufnahmen und verarbeitet sie (ohne Einfügen).

## Hotkey-Gesten (`GestureRecognizer`, testbar ohne Windows)

| Geste | Wirkung |
|---|---|
| halten ≥ 250 ms, loslassen | Push-to-Talk: verarbeiten und einfügen |
| kurzer Tipp | nichts (vorläufige Aufnahme wird verworfen) |
| Doppeltipp (≤ 350 ms) | Freihand-Aufnahme; erneuter Druck beendet |
| Strg rechts + andere Taste | Tastenkürzel → Aufnahme verworfen |
| Esc während Aufnahme | verwerfen (Esc wird dann nicht weitergereicht) |

Der Hook wertet ausschließlich Auslösetaste und Esc aus; andere Tasten nur als „irgendeine Taste“.
Keine Taste wird gespeichert oder protokolliert.

## KI-Sicherheitsregeln (`OutputValidator`)

Verworfen wird eine KI-Ausgabe, wenn sie: neue Zahlen enthält; Zahlen ohne erkennbare Selbstkorrektur
entfernt; geschützte Begriffe, Links/E-Mails oder Kürzel verändert; zu lang/zu kurz ist; zu viele neue Wörter
oder neue Eigennamen enthält; nach Meta-Antwort aussieht. Das Diktat wird im Prompt als Inhalt markiert,
nie als Anweisung; Temperatur 0,1, fester Seed.

## Projekte

| Projekt | Inhalt |
|---|---|
| Hilde.Core | Interfaces, Zustandsmaschine, Pipeline, Fehlerkatalog, Engine-Client, WAV |
| Hilde.Audio | WASAPI-Aufnahme, Pegel, Normalisierung, Silero VAD |
| Hilde.Speech | Whisper-Adapter (Client + Engine-Handler) |
| Hilde.Language | Wörterbuch, Regelbereinigung, Prompts, Validator, llama-Handler |
| Hilde.Insertion | Win32/UIA-Einfügen, Zwischenablage, Tastatur-Hook |
| Hilde.Storage | SQLite-Archiv, settings.json |
| Hilde.Setup | Modellmanifest, Download/Resume/SHA-256, Offline-Import, Systemprüfung |
| Hilde.EngineHost | Hilde.Engine.exe (isolierter Engine-Prozess) |
| Hilde.Desktop | HILDE.exe: Pill, Tray, WebView2, Brücke, Composition Root |
| Hilde.Diarization, Hilde.Media | Phase 3 (angelegt) |

## Entwicklungsstand (intern)

Phase 1 (Fundament) und Phase 2 (Diktat) sind umgesetzt. Phase 3 (Meetings, Systemaudio, Dateien,
Sprechertrennung) folgt; die Datenbank enthält bereits die Tabelle `jobs` für die Warteschlange.
