namespace Hilde.Core;

public enum HildeState { Ready, Recording, Paused, Processing, Error, Done }

public enum RecordingMode { PushToTalk, HandsFree }

public enum TextMode { Original, Clean, Professional }

public enum ModelKind { Whisper, Llm, Vad, Diarization, Tts }

public enum PerformanceTier { Light, Standard }

public enum TranscriptKind { Dictation, Meeting, File }

public static class TranscriptStatus
{
    public const string Recording = "recording";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Recovered = "recovered";
    public const string NoSpeech = "nospeech";
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Ergebnis einer Aufnahme: 16-kHz-Mono-WAV auf Platte, plus Kennzahlen.</summary>
public sealed record CapturedAudio(string WavPath, TimeSpan Duration, float PeakLevel);

public sealed record TimedSegment(TimeSpan Start, TimeSpan End, string Text);

/// <param name="Fast">Kurze Aufnahmen mit verkleinertem Audio-Kontext erkennen (deutlich schneller).</param>
/// <param name="Background">Vorschau/Hintergrund – Diktat-Endfassungen haben Vorrang.</param>
public sealed record SpeechRequest(string WavPath, string Language, string? InitialPrompt, bool Fast = false, bool Background = false);

public sealed record SpeechResult(string Text, string DetectedLanguage, IReadOnlyList<TimedSegment> Segments);

public sealed record TextRequest(string Text, TextMode Mode, string Language, IReadOnlyList<string> ProtectedTerms);

public sealed record TextResult(string Text, bool UsedModel, string? FallbackReason);

public sealed record EngineHealth(bool Running, bool ModelLoaded, string? Backend, string? Detail);

public enum InsertionMethod { Auto, Clipboard, Typing }

public sealed record InsertionOutcome(bool Success, string Method, string? ErrorId, string? Detail)
{
    public static InsertionOutcome Ok(string method) => new(true, method, null, null);
    public static InsertionOutcome Fail(string errorId, string? detail = null) => new(false, "none", errorId, detail);
}

/// <summary>Merkt sich das beim Aufnahmestart aktive Eingabeziel.</summary>
public abstract class InsertionTarget
{
    public abstract string Description { get; }
    public abstract string? ProcessName { get; }
    public abstract bool IsRemoteSession { get; }
}

public sealed class TranscriptRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TranscriptKind Kind { get; set; } = TranscriptKind.Dictation;
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "Mikrofon";
    public string Language { get; set; } = "de";
    public string Status { get; set; } = TranscriptStatus.Recording;
    public TextMode Mode { get; set; } = TextMode.Clean;
    /// <summary>Unverändertes Whisper-Ergebnis (Pflicht: getrennt aufbewahren).</summary>
    public string RawText { get; set; } = "";
    /// <summary>Nach Wörterbuch und KI-/Regelbereinigung.</summary>
    public string CleanText { get; set; } = "";
    /// <summary>Vom Nutzer ggf. nachbearbeitet; initial = eingefügter Text.</summary>
    public string FinalText { get; set; } = "";
    public long DurationMs { get; set; }
    public string? AudioPath { get; set; }
    public string? TargetApp { get; set; }
    public string? InsertionMethod { get; set; }
    public string? ErrorId { get; set; }
    public string? FallbackReason { get; set; }
    public string? SpeakersJson { get; set; }
    /// <summary>Erkannte Abschnitte mit Zeitmarken (für Audio-Wiedergabe im Archiv).</summary>
    public string? SegmentsJson { get; set; }
}

public sealed record TranscriptQuery(string? Search, TranscriptKind? Kind, int Limit = 200, int Offset = 0);

public sealed class ReplacementRule
{
    public long Id { get; set; }
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool WholeWord { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class VocabularyTerm
{
    public long Id { get; set; }
    public string Term { get; set; } = "";
}
