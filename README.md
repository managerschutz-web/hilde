# HILDE

**Taste drücken. Sprechen. Loslassen.** Lokale KI-Diktier- und Transkriptionssoftware für Windows 11 –
ohne Cloud, ohne Konto, ohne Telemetrie. Funktionale Referenz: Koe (kein Code übernommen).

> Interner Entwicklungsstand **0.2 – Phase 1 + 2** (Fundament + Diktat). Meetings, Systemaudio,
> Datei-Transkription und Sprechertrennung folgen in Phase 3. Keine öffentliche Freigabe vor Gesamtabnahme.

## Was schon geht

- Globale Diktiertaste (Standard **Strg rechts**): halten = Aufnahme, loslassen = einfügen; Doppeltipp = freihändig; Esc = verwerfen
- whisper.cpp (Vulkan/CPU) im isolierten Prozess, Silero VAD, Halluzinationsfilter
- Drei Textmodi (Original · Bereinigt · Professionell) mit lokalem LLM (llama.cpp) und Sicherheitsprüfung,
  regelbasierter Rückfall – das Diktat geht nie verloren
- Einfügen ins ursprünglich aktive Feld (auch RDP/RemoteApp mit Zwischenablage-Umleitung), sonst Kopierfenster
- Wörterbuch (exakte Ersetzungen mit Vorschau, Fachbegriffe als Erkennungshilfe), durchsuchbares Archiv mit Export
- Glass-Pill ohne Fokusdiebstahl, Tray, Hauptfenster (WebView2, hell/dunkel), Einrichtungsassistent mit Testdiktat
- Modellverwaltung: fortsetzbarer Download, SHA-256, Offline-Paket, Leistungsstufen Leicht/Standard
- Absturzschutz: laufend gesicherte Aufnahmen, Wiederherstellung beim Start, Engine-Neustart, GPU→CPU-Fallback

## HILDE-Setup.exe bauen – ohne eigenen Windows-Rechner

1. Diesen Ordner als (privates) GitHub-Repository hochladen.
2. GitHub baut bei jedem Push automatisch (`.github/workflows/build.yml`, Windows-Runner).
3. Unter **Actions → HILDE Build → letzter Lauf → Artifacts** liegt `HILDE-Setup-<Version>` mit
   `HILDE-Setup-<Version>.exe` und `SHA256SUMS.txt`. Geht auch vom Handy aus.
4. Ein Tag `v1.0.0` erzeugt einen Release-Entwurf (dann müssen alle Modell-Prüfsummen gesetzt sein).

## Lokal bauen (Windows)

Voraussetzungen: .NET 10 SDK, Node 22. Dann `./scripts/build.ps1` – Ergebnis unter `installer/Bundle/bin/`.

## Struktur

```
src/        Hilde.Core · Audio · Speech · Language · Insertion · Storage · Setup · EngineHost · Desktop
            (Diarization, Media: Phase 3)
ui/         Oberfläche (TypeScript → ui/dist, im Repo bereits kompiliert)
tests/      Unit (xUnit), WindowsE2E (Einfügen in Notepad), Integration, AudioFixtures
models/     manifest.json – Quellen, Größen, SHA-256, Lizenzen
installer/  WiX 5: Package (MSI, pro Benutzer) + Bundle (HILDE-Setup.exe inkl. WebView2)
scripts/    build, update-manifest (Prüfsummen + Offline-Paket), check-manifest, sign
docs/       architecture, interfaces, privacy, release-checklist
licenses/   Drittkomponenten
```

## Daten

`%LOCALAPPDATA%\HILDE\` – `Config\settings.json`, `Config\models.json`, `Database\hilde.db`, `Recordings\`,
`Models\{whisper,llm,vad}\`, `Logs\` (ohne Inhalte), `Temp\`.

---
Made with ♥ in Ummaschumm · wetzel.digital
