using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hilde.Core;
using Hilde.Core.Engines;
using NAudio.Wave;
using Serilog;

namespace Hilde.Desktop;

/// <summary>
/// Vorlesen mit lokaler Piper-Stimme. Der Text wird in Abschnitte geteilt: Der erste ist sofort hörbar,
/// die nächsten werden berechnet, während der vorige läuft.
/// </summary>
public sealed partial class ReadAloudService : IAsyncDisposable
{
    private readonly EngineProcessClient _client;
    private readonly HildePaths _paths;
    private readonly ISettingsStore _settings;
    private readonly ILogger _log = Log.ForContext<ReadAloudService>();
    private CancellationTokenSource? _cts;
    private int _session;

    public bool IsReading { get; private set; }
    public event Action<bool>? ReadingChanged;
    public EngineProcessClient Client => _client;

    public ReadAloudService(EngineProcessClient client, HildePaths paths, ISettingsStore settings)
    {
        _client = client;
        _paths = paths;
        _settings = settings;
    }

    public void Stop() => _cts?.Cancel();

    public async Task SpeakAsync(string text)
    {
        Stop();
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        var cts = _cts = new CancellationTokenSource();
        int session = Interlocked.Increment(ref _session);
        SetReading(true);
        var dir = Path.Combine(_paths.Temp, "tts");
        Directory.CreateDirectory(dir);
        var queue = Channel.CreateBounded<string>(2);
        float speed = (float)Math.Clamp(_settings.Current.ReadAloud.Speed, 0.5, 2.0);
        try
        {
            var producer = Task.Run(async () =>
            {
                try
                {
                    int i = 0;
                    foreach (var chunk in SplitForSpeech(text, 280))
                    {
                        if (cts.IsCancellationRequested) break;
                        var path = Path.Combine(dir, $"tts-{session}-{i++}.wav");
                        // Kein Abbruch-Token an die Engine: Abbruch würde den Prozess beenden (Neustart kostet Zeit).
                        await _client.CallAsync<SpeakResult>(EngineProtocol.Methods.Speak, new SpeakParams(chunk, speed, path),
                            TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
                        if (cts.IsCancellationRequested) { TryDelete(path); break; }
                        await queue.Writer.WriteAsync(path, cts.Token).ConfigureAwait(false);
                    }
                    queue.Writer.TryComplete();
                }
                catch (Exception ex) { queue.Writer.TryComplete(ex); }
            });
            await foreach (var path in queue.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                try { await PlayAsync(path, cts.Token).ConfigureAwait(false); }
                finally { TryDelete(path); }
            }
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (session == Volatile.Read(ref _session)) SetReading(false);
        }
    }

    private static async Task PlayAsync(string path, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        using var output = new WaveOutEvent { DesiredLatency = 120 };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(reader);
        output.Play();
        using (ct.Register(() => output.Stop()))
            await done.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private void SetReading(bool reading)
    {
        IsReading = reading;
        ReadingChanged?.Invoke(reading);
    }

    [GeneratedRegex(@"(?<=[.!?…:;])\s+|\n+")] private static partial Regex Boundary();

    /// <summary>Sätze zu Abschnitten bis ca. <paramref name="max"/> Zeichen bündeln (kurzer erster Abschnitt = schneller Start).</summary>
    public static List<string> SplitForSpeech(string text, int max)
    {
        var parts = Boundary().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            int limit = result.Count == 0 ? Math.Min(120, max) : max;
            if (sb.Length > 0 && sb.Length + part.Length + 1 > limit) { result.Add(sb.ToString()); sb.Clear(); }
            if (part.Length > max)
            {
                // sehr lange Sätze an Kommas teilen
                foreach (var piece in part.Split(", "))
                {
                    if (sb.Length > 0 && sb.Length + piece.Length + 2 > max) { result.Add(sb.ToString()); sb.Clear(); }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(piece);
                }
                continue;
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
