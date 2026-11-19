namespace Hilde.Core;

// Kerninterfaces laut Pflichtenheft 5.2.

public interface IAudioCaptureService : IDisposable
{
    /// <summary>Pegel 0..1, ca. 20× pro Sekunde.</summary>
    event EventHandler<float>? LevelChanged;
    /// <summary>Gerät während der Aufnahme verloren (abgezogen, gesperrt …).</summary>
    event EventHandler<string>? CaptureFailed;
    bool IsRecording { get; }
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
    /// <summary>Startet die Aufnahme. Rohdaten werden laufend in <paramref name="targetWavPath"/> gesichert.</summary>
    void Start(string? deviceId, string targetWavPath);
    /// <summary>Beendet die Aufnahme und liefert 16-kHz-Mono-PCM.</summary>
    Task<CapturedAudio> StopAsync(CancellationToken ct = default);
    /// <summary>Verwirft die laufende Aufnahme vollständig.</summary>
    void Cancel();
    /// <summary>Nur Pegelmessung (Mikrofontest), ohne Datei.</summary>
    void StartMonitor(string? deviceId);
    void StopMonitor();
    /// <summary>Wandelt eine Rohaufnahme (beliebiges Format) in 16-kHz-Mono-PCM um (auch nach Absturz).</summary>
    CapturedAudio NormalizeFile(string rawWavPath, string outWavPath);
}

public interface IVoiceActivityDetector
{
    /// <summary>Liefert Anteil Sprache und die Grenzen (Start/Ende) der Sprachaktivität.</summary>
    VadResult Analyze(ReadOnlySpan<float> samples16k);
}

public sealed record VadResult(bool HasSpeech, TimeSpan SpeechStart, TimeSpan SpeechEnd, double SpeechRatio);

public interface ISpeechRecognitionEngine
{
    Task<SpeechResult> TranscribeAsync(SpeechRequest request, CancellationToken ct);
    Task<EngineHealth> HealthAsync(CancellationToken ct);
    Task UnloadAsync();
}

public interface ITextProcessingEngine
{
    Task<TextResult> ProcessAsync(TextRequest request, CancellationToken ct);
    Task<EngineHealth> HealthAsync(CancellationToken ct);
    Task UnloadAsync();
    bool IsAvailable { get; }
}

public interface IDiarizationEngine
{
    // Phase 3: Sprechertrennung (sherpa-onnx).
    Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(string wavPath, CancellationToken ct);
}

public sealed record SpeakerSegment(TimeSpan Start, TimeSpan End, int Speaker, bool Uncertain);

public interface ITextInsertionService
{
    /// <summary>Muss beim Hotkey-Druck aufgerufen werden, bevor irgendein HILDE-Fenster erscheint.</summary>
    InsertionTarget CaptureTarget();
    Task<InsertionOutcome> InsertAsync(InsertionTarget target, string text, CancellationToken ct);
}

public interface IModelManager
{
    event EventHandler<ModelProgress>? Progress;
    IReadOnlyList<ModelStatus> GetStatus(PerformanceTier tier);
    string? GetInstalledPath(ModelKind kind, PerformanceTier tier);
    Task DownloadAsync(PerformanceTier tier, bool includeLlm, CancellationToken ct);
    Task<int> ImportOfflinePackageAsync(string folderOrZip, CancellationToken ct);
    Task<bool> VerifyAsync(string modelId, CancellationToken ct);
}

public sealed record ModelStatus(string Id, ModelKind Kind, string DisplayName, long? SizeBytes,
    bool Installed, bool Verified, string License, bool Required);

public sealed record ModelProgress(string ModelId, string DisplayName, long BytesDone, long? BytesTotal,
    string Phase, string? ErrorId = null, string? Message = null);

public interface ITranscriptRepository
{
    Task InitializeAsync();
    Task SaveAsync(TranscriptRecord record);
    Task<TranscriptRecord?> GetAsync(string id);
    Task<IReadOnlyList<TranscriptRecord>> ListAsync(TranscriptQuery query);
    Task DeleteAsync(string id);
    Task<IReadOnlyList<TranscriptRecord>> FindUnfinishedAsync();
    Task<IReadOnlyList<ReplacementRule>> GetReplacementsAsync();
    Task<ReplacementRule> SaveReplacementAsync(ReplacementRule rule);
    Task DeleteReplacementAsync(long id);
    Task<IReadOnlyList<VocabularyTerm>> GetTermsAsync();
    Task<VocabularyTerm> SaveTermAsync(VocabularyTerm term);
    Task DeleteTermAsync(long id);
}

public interface ISettingsStore
{
    HildeSettings Current { get; }
    event EventHandler<HildeSettings>? Changed;
    void Save(HildeSettings settings);
}
