namespace Hilde.Core;

/// <summary>Alle lokalen Ablageorte unter %LOCALAPPDATA%\HILDE\ (Pflichtenheft Kap. 6).</summary>
public sealed class HildePaths
{
    public string Root { get; }
    public string Config => Path.Combine(Root, "Config");
    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string ModelsStateFile => Path.Combine(Config, "models.json");
    public string Database => Path.Combine(Root, "Database");
    public string DatabaseFile => Path.Combine(Database, "hilde.db");
    public string Recordings => Path.Combine(Root, "Recordings");
    public string Transcripts => Path.Combine(Root, "Transcripts");
    public string Models => Path.Combine(Root, "Models");
    public string WhisperModels => Path.Combine(Models, "whisper");
    public string LlmModels => Path.Combine(Models, "llm");
    public string DiarizationModels => Path.Combine(Models, "diarization");
    public string VadModels => Path.Combine(Models, "vad");
    public string Temp => Path.Combine(Root, "Temp");
    public string Logs => Path.Combine(Root, "Logs");
    public string WebViewData => Path.Combine(Root, "WebView2");

    public HildePaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HILDE");
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Config, Database, Recordings, Transcripts, Models,
                     WhisperModels, LlmModels, DiarizationModels, VadModels, Temp, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string RecordingDir(string recordingId) => Path.Combine(Recordings, recordingId);

    public string ModelDir(ModelKind kind) => kind switch
    {
        ModelKind.Whisper => WhisperModels,
        ModelKind.Llm => LlmModels,
        ModelKind.Diarization => DiarizationModels,
        ModelKind.Vad => VadModels,
        _ => Models
    };

    /// <summary>Löscht verwaiste Temp-Dateien, die älter als <paramref name="maxAge"/> sind.</summary>
    public void CleanTemp(TimeSpan maxAge)
    {
        if (!Directory.Exists(Temp)) return;
        foreach (var file in Directory.EnumerateFiles(Temp, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > maxAge) File.Delete(file);
            }
            catch (IOException) { /* in Benutzung – beim nächsten Start erneut */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
