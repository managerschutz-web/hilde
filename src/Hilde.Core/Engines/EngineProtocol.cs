using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hilde.Core.Engines;

/// <summary>
/// Versioniertes Protokoll zwischen HILDE und den isolierten Engine-Prozessen
/// (eine JSON-Nachricht pro Zeile über eine nur für den aktuellen Benutzer zugängliche Named Pipe).
/// </summary>
public static class EngineProtocol
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static class Methods
    {
        public const string Hello = "hello";
        public const string Health = "health";
        public const string Load = "load";
        public const string Transcribe = "transcribe";
        public const string Generate = "generate";
        public const string Shutdown = "shutdown";
    }
}

public sealed record EngineRequest(long Id, string Method, JsonElement? Params);

public sealed record EngineResponse(long Id, bool Ok, JsonElement? Result, string? ErrorId, string? Error);

public sealed record HelloResult(int ProtocolVersion, string Kind, string EngineVersion, int ProcessId);

public sealed record TranscribeParams(string WavPath, string Language, string? Prompt, bool Fast = false);

public sealed record GenerateParams(string Prompt, int MaxTokens, IReadOnlyList<string> Stop, float Temperature);

public sealed record GenerateResult(string Text, int Tokens, double Seconds);

public sealed record EngineLaunchOptions(string ModelPath, bool UseGpu, int Threads);

public enum EngineKind { Speech, Language }
