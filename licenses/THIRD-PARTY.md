# HILDE – Drittkomponenten und Lizenzen

Stand: 19.11.2026 · Status je Eintrag: **zu prüfen** (vor Freigabe juristisch/organisatorisch bestätigen
und hier auf „geprüft“ setzen). Koe dient ausschließlich als funktionale Referenz – es wurde **kein
Koe-Quellcode** übernommen.

## Laufzeit-Bibliotheken (im Installer enthalten)

| Komponente | Zweck | Lizenz | Status |
|---|---|---|---|
| .NET 10 Runtime / WPF / WinForms | Laufzeit, Oberfläche | MIT | zu prüfen |
| Microsoft.Web.WebView2 (SDK) | HTML-Oberfläche | Microsoft-Softwarelizenz (WebView2 SDK) | zu prüfen |
| Microsoft Edge WebView2 Runtime | Laufzeit (Evergreen) | Microsoft-Softwarelizenz | zu prüfen |
| NAudio | WASAPI-Aufnahme, Resampling | MIT | zu prüfen |
| Whisper.net (+ Runtime, Runtime.Vulkan) | Bindung an whisper.cpp | MIT | zu prüfen |
| whisper.cpp / ggml | Spracherkennung | MIT | zu prüfen |
| LLamaSharp (+ Backend.Cpu, Backend.Vulkan) | Bindung an llama.cpp | MIT | zu prüfen |
| llama.cpp | lokale Textbereinigung | MIT | zu prüfen |
| Microsoft.ML.OnnxRuntime | Silero VAD | MIT | zu prüfen |
| Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite | Archiv | MIT / Apache-2.0 / Public Domain | zu prüfen |
| Serilog, Serilog.Sinks.File | Protokolle | Apache-2.0 | zu prüfen |
| Vulkan-Loader (über Runtime-Pakete) | GPU-Beschleunigung | Apache-2.0 | zu prüfen |

## Modelle (separat geladen, siehe `models/manifest.json`)

| Modell | Lizenz | Hinweis |
|---|---|---|
| Silero VAD v5 | MIT | |
| Whisper small / large-v3-turbo (GGML) | MIT | OpenAI-Gewichte, konvertiert von whisper.cpp |
| Qwen2.5 1.5B Instruct (GGUF) | Apache-2.0 | Stufe „Leicht“ |
| Gemma 3 4B Instruct (GGUF) | Gemma Terms of Use | gewerbliche Nutzung erlaubt, Prohibited Use Policy beachten – **vor Freigabe bewerten** |
| Gemma 4 (Koe-Referenz) | Gemma Terms of Use | nur nach Lizenz-, Format- und Windows-Kompatibilitätsprüfung |

## Build-Werkzeuge (nicht ausgeliefert)

WiX Toolset 5 (MS-RL), TypeScript (Apache-2.0), xUnit (Apache-2.0).

## Phase 3 (noch nicht enthalten)

sherpa-onnx (Apache-2.0) für Sprechertrennung, FFmpeg (LGPL-Build, dynamisch, Quellangebot beachten).
