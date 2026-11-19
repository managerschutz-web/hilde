# Kerninterfaces (Hilde.Core)

| Interface | Implementierung | Aufgabe |
|---|---|---|
| `IAudioCaptureService` | `WasapiCaptureService` | Aufnahme, Pegel, Gerätewechsel, Normalisierung 16 kHz |
| `IVoiceActivityDetector` | `SileroVoiceActivityDetector` | Sprache ja/nein, Sprachanteil |
| `ISpeechRecognitionEngine` | `RemoteSpeechEngine` → `WhisperEngineHandler` | Transkription im isolierten Prozess |
| `ITextProcessingEngine` | `TextProcessingPipeline` → `LlamaEngineHandler` | Original/Bereinigt/Professionell + Prüfung |
| `IDiarizationEngine` | Phase 3 (sherpa-onnx) | Sprechersegmente |
| `ITextInsertionService` | `Win32TextInsertionService` | Ziel merken, einfügen, Fallback |
| `IModelManager` | `ModelManager` | Manifest, Download, SHA-256, Offline-Import |
| `ITranscriptRepository` | `SqliteTranscriptRepository` | Archiv, Wörterbuch, Begriffe |
| `IDictionaryService` | `DictionaryService` | Ersetzungen, geschützte Begriffe, Whisper-Prompt |
| `ISettingsStore` | `JsonSettingsStore` | settings.json (atomar) |

## Engine-Protokoll v1

Anfrage `{"id":1,"method":"transcribe","params":{...}}` → Antwort `{"id":1,"ok":true,"result":{...}}`
bzw. `{"id":1,"ok":false,"errorId":"H-…","error":"…"}`.

| Methode | Parameter | Ergebnis |
|---|---|---|
| `hello` | – | `{protocolVersion, kind, engineVersion, processId}` |
| `health` | – | `EngineHealth` |
| `load` | – | `EngineHealth` (Modell vorab laden) |
| `transcribe` | `{wavPath, language, prompt}` | `SpeechResult {text, detectedLanguage, segments[]}` |
| `generate` | `{prompt, maxTokens, stop[], temperature}` | `{text, tokens, seconds}` |
| `shutdown` | – | – (Prozess endet) |

## Oberfläche ⇄ .NET (UiBridge)

Methoden u. a.: `app.info`, `settings.get/save`, `hotkey.capture`, `devices.list`, `mic.monitorStart/Stop`,
`transcripts.list/get/update/delete/reprocess/copy/export`, `dictionary.get/saveRule/deleteRule/preview/saveTerm/deleteTerm`,
`setup.system/complete`, `models.status/download/cancel/import`, `engines.health`, `test.start/stop/cancel`.
Ereignisse: `status`, `level`, `transcript`, `settings`, `modelProgress`, `modelsDone`, `navigate`.
