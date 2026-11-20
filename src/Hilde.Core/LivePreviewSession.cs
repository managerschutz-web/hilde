using System.Text;
using Serilog;

namespace Hilde.Core;

/// <summary>
/// Live-Vorschau während der Aufnahme. Schneidet an natürlichen Sprechpausen, erkennt fertige Stücke schon
/// während gesprochen wird („Vorarbeit“) und zeigt den noch offenen Rest vorläufig an.
/// Beim Loslassen muss dann nur noch der letzte Abschnitt erkannt werden.
/// </summary>
public sealed class LivePreviewSession
{
    private const int Rate = WavIO.SampleRate;
    private readonly IAudioCaptureService _audio;
    private readonly ISpeechRecognitionEngine _speech;
    private readonly string _language;
    private readonly string? _basePrompt;
    private readonly string _dir;
    private readonly ILogger _log;
    private readonly Action<string, string> _onPreview;
    private readonly StringBuilder _committed = new();
    private volatile bool _stop;
    private volatile bool _abandoned;
    private Task? _loop;
    private int _counter;

    public int CommittedSamples { get; private set; }
    public string CommittedText => _committed.ToString().Trim();
    public bool Failed { get; private set; }

    public LivePreviewSession(IAudioCaptureService audio, ISpeechRecognitionEngine speech, string language,
        string? basePrompt, string dir, ILogger log, Action<string, string> onPreview)
    {
        _audio = audio; _speech = speech; _language = language; _basePrompt = basePrompt;
        _dir = dir; _log = log; _onPreview = onPreview;
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Aufnahme verworfen: nichts mehr anzeigen, Ergebnis wird nicht gebraucht.</summary>
    public void Abandon() { _abandoned = true; _stop = true; }

    /// <summary>Aufnahme beendet: laufendes Teilstück noch abschließen lassen (spart Zeit), dann stoppen.</summary>
    public async Task StopAsync()
    {
        _stop = true;
        if (_loop == null) return;
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) { _log.Debug(ex, "Vorschau beenden"); Failed = true; }
    }

    private async Task LoopAsync()
    {
        int lastProvisionalEnd = 0;
        string provisional = "";
        try
        {
            while (!_stop)
            {
                await Task.Delay(600).ConfigureAwait(false);
                if (_stop) break;
                int total = _audio.LiveSampleCount;
                if (total < 2 * Rate) continue;

                int? cut = FindCut(total);
                if (cut is int c && c > CommittedSamples)
                {
                    var text = await TranscribeAsync(CommittedSamples, c).ConfigureAwait(false);
                    if (_abandoned) return;
                    if (text.Length > 0) _committed.Append(' ').Append(text);
                    CommittedSamples = c;
                    provisional = "";
                    _onPreview(CommittedText, provisional);
                    continue;
                }

                int open = total - CommittedSamples;
                if (open > Rate && total - lastProvisionalEnd > Rate * 8 / 10)
                {
                    provisional = await TranscribeAsync(CommittedSamples, total).ConfigureAwait(false);
                    lastProvisionalEnd = total;
                    if (_abandoned) return;
                    _onPreview(CommittedText, provisional);
                }
            }
        }
        catch (Exception ex)
        {
            Failed = true;
            _log.Warning(ex, "Live-Vorschau abgebrochen – Endfassung wird vollständig erkannt");
        }
    }

    private async Task<string> TranscribeAsync(int from, int to)
    {
        var samples = _audio.CopyLiveSamples(from, to - from);
        if (!HasEnergy(samples)) return "";
        var path = Path.Combine(_dir, $"live-{Interlocked.Increment(ref _counter)}.wav");
        WavIO.WriteMono16k(path, samples);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await _speech.TranscribeAsync(
                new SpeechRequest(path, _language, BuildPrompt(_basePrompt, CommittedText), Fast: true, Background: true), cts.Token)
                .ConfigureAwait(false);
            return HallucinationFilter.Clean(r.Text);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Kontext für Whisper: Fachbegriffe + Ende des bisherigen Texts (bessere Übergänge).</summary>
    public static string? BuildPrompt(string? basePrompt, string committed)
    {
        var tail = committed.Length > 200 ? committed[^200..] : committed;
        var p = ((basePrompt ?? "") + " " + tail).Trim();
        return p.Length == 0 ? null : p;
    }

    /// <summary>Schnittpunkt an einer Sprechpause (≥ 360 ms leise) mindestens 3 s nach dem letzten Schnitt.</summary>
    private int? FindCut(int total)
    {
        int end = total - Rate * 6 / 10;          // die letzten 0,6 s nie schneiden (Wort könnte weitergehen)
        int start = CommittedSamples;
        if (end - start < 3 * Rate) return null;
        var samples = _audio.CopyLiveSamples(start, end - start);
        return FindPause(samples, minOffset: 3 * Rate, forceAfter: 22 * Rate) is int rel ? start + rel : null;
    }

    public static int? FindPause(float[] samples, int minOffset, int forceAfter)
    {
        const int frame = Rate / 50; // 20 ms
        int frames = samples.Length / frame;
        if (frames < 10) return null;
        var rms = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < frame; i++) { var v = samples[f * frame + i]; sum += v * v; }
            rms[f] = Math.Sqrt(sum / frame);
        }
        var sorted = rms.OrderBy(x => x).ToArray();
        double threshold = Math.Max(0.006, sorted[frames / 5] * 2.5);

        int runStart = -1, bestMid = -1;
        for (int f = 0; f <= frames; f++)
        {
            bool quiet = f < frames && rms[f] < threshold;
            if (quiet) { if (runStart < 0) runStart = f; continue; }
            if (runStart >= 0)
            {
                int len = f - runStart;
                int mid = (runStart + f) / 2;
                if (len >= 18 && mid * frame >= minOffset) bestMid = mid; // letzte passende Pause
                runStart = -1;
            }
        }
        if (bestMid >= 0) return bestMid * frame;

        // Keine Pause, aber sehr lang am Stück gesprochen: an der leisesten Stelle der letzten 8 s schneiden.
        if (samples.Length >= forceAfter)
        {
            int from = Math.Max(minOffset / frame, frames - 8 * 50);
            int best = from; double bestVal = double.MaxValue;
            for (int f = from; f + 15 <= frames; f++)
            {
                double s = 0;
                for (int k = 0; k < 15; k++) s += rms[f + k];
                if (s < bestVal) { bestVal = s; best = f + 7; }
            }
            return best * frame;
        }
        return null;
    }

    public static bool HasEnergy(float[] samples)
    {
        if (samples.Length < Rate / 4) return false;
        double sum = 0;
        int loud = 0;
        const int frame = Rate / 50;
        for (int f = 0; f + frame <= samples.Length; f += frame)
        {
            sum = 0;
            for (int i = 0; i < frame; i++) sum += samples[f + i] * samples[f + i];
            if (Math.Sqrt(sum / frame) > 0.015) loud++;
        }
        return loud >= 8; // ≥ 160 ms deutlich hörbar
    }
}
