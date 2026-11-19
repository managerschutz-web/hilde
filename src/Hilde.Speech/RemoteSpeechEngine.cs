using Hilde.Core;
using Hilde.Core.Engines;

namespace Hilde.Speech;

/// <summary>ISpeechRecognitionEngine auf HILDE-Seite – spricht mit dem isolierten Whisper-Prozess.</summary>
public sealed class RemoteSpeechEngine(EngineProcessClient client) : ISpeechRecognitionEngine
{
    public EngineProcessClient Client { get; } = client;

    public async Task<SpeechResult> TranscribeAsync(SpeechRequest request, CancellationToken ct)
    {
        // Diktate haben Vorrang vor Hintergrundaufträgen (Phase 3).
        using var _ = await Client.Gate.EnterAsync(highPriority: true, ct).ConfigureAwait(false);
        // Timeout wird vom Aufrufer über ct gesteuert; hier großzügige Obergrenze.
        return await Client.CallAsync<SpeechResult>(EngineProtocol.Methods.Transcribe,
            new TranscribeParams(request.WavPath, request.Language, request.InitialPrompt),
            TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
    }

    public Task<EngineHealth> HealthAsync(CancellationToken ct) => Client.HealthAsync(ct);

    public Task UnloadAsync() => Client.StopAsync();
}
