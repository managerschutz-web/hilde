using Hilde.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace Hilde.Audio;

/// <summary>
/// Mikrofonaufnahme über WASAPI (Shared Mode). Rohdaten werden laufend auf die Platte geschrieben,
/// damit bei Absturz oder Gerätewechsel nichts verloren geht. Pegel ~20×/s.
/// </summary>
public sealed class WasapiCaptureService : IAudioCaptureService
{
    private readonly ILogger _log;
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _rawPath;
    private string? _outPath;
    private TaskCompletionSource? _stopped;
    private bool _cancelled;
    private float _peak;
    private float _windowPeak;
    private DateTime _lastLevel = DateTime.MinValue;
    private long _flushCounter;
    private WasapiCapture? _monitor;
    // Live-Puffer (16 kHz Mono) für die Vorschau
    private readonly List<float> _live16k = new();
    private readonly object _liveLock = new();
    private BufferedWaveProvider? _liveIn;
    private ISampleProvider? _liveOut;
    private float[] _liveTmp = new float[8192];

    public event EventHandler<float>? LevelChanged;
    public event EventHandler<string>? CaptureFailed;

    public bool IsRecording { get { lock (_lock) return _capture != null; } }

    private volatile bool _paused;
    public bool Paused { get => _paused; set => _paused = value; }

    public WasapiCaptureService(ILogger log) => _log = log.ForContext<WasapiCaptureService>();

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        using var en = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }
        try { defaultId ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; } catch { }
        var list = new List<AudioDeviceInfo>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (d) list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        return list;
    }

    private static MMDevice ResolveDevice(string? deviceId)
    {
        var en = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    var d = en.GetDevice(deviceId);
                    if (d.State == DeviceState.Active) return d;
                }
                catch { /* Gerät nicht mehr da → Standard */ }
            }
            try { return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
            catch { return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        }
        catch (Exception ex)
        {
            throw new HildeException(HildeErrors.NoMicrophone, ex);
        }
    }

    public void Start(string? deviceId, string targetWavPath)
    {
        lock (_lock)
        {
            if (_capture != null) throw new InvalidOperationException("Aufnahme läuft bereits.");
            StopMonitorLocked();
            var device = ResolveDevice(deviceId);
            WasapiCapture capture;
            try
            {
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
            }
            catch (Exception ex)
            {
                throw new HildeException(HildeErrors.MicrophoneBlocked, ex);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetWavPath)!);
            _rawPath = targetWavPath;
            _outPath = Path.Combine(Path.GetDirectoryName(targetWavPath)!, "mic16k.wav");
            _writer = new WaveFileWriter(targetWavPath, capture.WaveFormat);
            lock (_liveLock)
            {
                _live16k.Clear();
                _liveIn = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate, 1))
                {
                    ReadFully = false, BufferDuration = TimeSpan.FromSeconds(20), DiscardOnBufferOverflow = true
                };
                ISampleProvider src = _liveIn.ToSampleProvider();
                _liveOut = capture.WaveFormat.SampleRate == WavIO.SampleRate ? src : new WdlResamplingSampleProvider(src, WavIO.SampleRate);
            }
            _cancelled = false;
            _paused = false;
            _peak = 0;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                CleanupLocked();
                // Häufigste Ursache: Datenschutzeinstellung „Mikrofonzugriff“ aus.
                throw new HildeException(HildeErrors.MicrophoneBlocked, ex);
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_paused) return; // pausiert: nichts speichern, Pegel bleibt bei 0
        WaveFileWriter? w;
        WaveFormat fmt;
        lock (_lock)
        {
            w = _writer;
            if (w == null || _capture == null) return;
            fmt = _capture.WaveFormat;
            w.Write(e.Buffer, 0, e.BytesRecorded);
            // Etwa jede Sekunde auf die Platte bringen (Absturzschutz).
            if (++_flushCounter % 20 == 0) w.Flush();
        }
        ReportLevel(e.Buffer, e.BytesRecorded, fmt);
        AppendLive(e.Buffer, e.BytesRecorded, fmt);
    }

    private void AppendLive(byte[] buffer, int bytes, WaveFormat fmt)
    {
        try
        {
            lock (_liveLock)
            {
                if (_liveIn == null || _liveOut == null) return;
                int ch = Math.Max(1, fmt.Channels);
                bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible && fmt.BitsPerSample == 32);
                int bps = fmt.BitsPerSample / 8;
                int frames = bytes / (bps * ch);
                var mono = new byte[frames * 4];
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = (f * ch + c) * bps;
                        sum += isFloat ? BitConverter.ToSingle(buffer, o) : BitConverter.ToInt16(buffer, o) / 32768f;
                    }
                    BitConverter.TryWriteBytes(mono.AsSpan(f * 4), sum / ch);
                }
                _liveIn.AddSamples(mono, 0, mono.Length);
                int read;
                while ((read = _liveOut.Read(_liveTmp, 0, _liveTmp.Length)) > 0)
                    for (int i = 0; i < read; i++) _live16k.Add(_liveTmp[i]);
            }
        }
        catch (Exception ex) { _log.Debug(ex, "Live-Puffer"); }
    }

    public int LiveSampleCount { get { lock (_liveLock) return _live16k.Count; } }

    public float[] CopyLiveSamples(int start, int count)
    {
        lock (_liveLock)
        {
            start = Math.Clamp(start, 0, _live16k.Count);
            count = Math.Clamp(count, 0, _live16k.Count - start);
            var result = new float[count];
            _live16k.CopyTo(start, result, 0, count);
            return result;
        }
    }

    private void ReportLevel(byte[] buffer, int bytes, WaveFormat fmt)
    {
        float peak = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible && fmt.BitsPerSample == 32))
        {
            for (int i = 0; i + 4 <= bytes; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytes; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
        }
        _windowPeak = Math.Max(_windowPeak, peak);
        _peak = Math.Max(_peak, peak);
        var now = DateTime.UtcNow;
        if ((now - _lastLevel).TotalMilliseconds >= 50)
        {
            _lastLevel = now;
            // leicht logarithmisch für eine natürlichere Anzeige
            var level = (float)Math.Clamp(Math.Sqrt(_windowPeak), 0, 1);
            _windowPeak = 0;
            LevelChanged?.Invoke(this, level);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        bool failed = e.Exception != null;
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _stopped?.TrySetResult();
        }
        if (failed && !_cancelled)
        {
            _log.Warning(e.Exception, "{ErrorId} Aufnahmegerät weggefallen", HildeErrors.MicrophoneLost.Id);
            CaptureFailed?.Invoke(this, HildeErrors.MicrophoneLost.Id);
        }
    }

    public async Task<CapturedAudio> StopAsync(CancellationToken ct = default)
    {
        WasapiCapture? cap;
        Task stopped;
        string raw, outp;
        lock (_lock)
        {
            cap = _capture ?? throw new InvalidOperationException("Keine Aufnahme aktiv.");
            stopped = _stopped!.Task;
            raw = _rawPath!;
            outp = _outPath!;
        }
        try { cap.StopRecording(); } catch (Exception ex) { _log.Debug(ex, "StopRecording"); }
        await Task.WhenAny(stopped, Task.Delay(2000, ct)).ConfigureAwait(false);
        float peak;
        lock (_lock)
        {
            peak = _peak;
            CleanupLocked();
        }
        LevelChanged?.Invoke(this, 0);
        var result = await Task.Run(() => NormalizeFile(raw, outp), ct).ConfigureAwait(false);
        return result with { PeakLevel = peak };
    }

    public void Cancel()
    {
        WasapiCapture? cap;
        lock (_lock)
        {
            cap = _capture;
            _cancelled = true;
        }
        if (cap == null) return;
        try { cap.StopRecording(); } catch { }
        lock (_lock) CleanupLocked();
        LevelChanged?.Invoke(this, 0);
    }

    private void CleanupLocked()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            try { _capture.Dispose(); } catch { }
        }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _capture = null;
    }

    /// <summary>Beliebige WAV (auch unvollständig nach Absturz) → 16 kHz Mono 16 Bit.</summary>
    public CapturedAudio NormalizeFile(string rawWavPath, string outWavPath)
    {
        var bytes = File.ReadAllBytes(rawWavPath);
        // Header selbst auswerten: funktioniert auch bei nicht finalisierten Dateien nach Absturz.
        var (offset, length, channels, rate, bits) = WavIO.ParseHeader(bytes);
        var tag = WavIO.ParseFormatTag(bytes);
        WaveFormat format = tag == 3
            ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)
            : new WaveFormat(rate, bits, channels);
        using var data = new MemoryStream(bytes, offset, length, writable: false);
        using var rawStream = new RawSourceWaveStream(data, format);
        ISampleProvider provider = rawStream.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1) provider = new DownmixToMonoProvider(provider);
        if (provider.WaveFormat.SampleRate != WavIO.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, WavIO.SampleRate);

        var samples = new List<float>(capacity: (int)Math.Min(int.MaxValue / 2, length / Math.Max(1, format.BlockAlign) / 3 + 1024));
        var buf = new float[16000];
        int read;
        while ((read = provider.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) samples.Add(buf[i]);
        var arr = samples.ToArray();
        WavIO.WriteMono16k(outWavPath, arr);
        float peak = 0;
        foreach (var s in arr) peak = Math.Max(peak, Math.Abs(s));
        return new CapturedAudio(outWavPath, TimeSpan.FromSeconds(arr.Length / (double)WavIO.SampleRate), peak);
    }

    public void StartMonitor(string? deviceId)
    {
        lock (_lock)
        {
            if (_capture != null) return;
            StopMonitorLocked();
            var device = ResolveDevice(deviceId);
            var m = new WasapiCapture(device, true, 50);
            m.DataAvailable += (_, e) => ReportLevel(e.Buffer, e.BytesRecorded, m.WaveFormat);
            m.StartRecording();
            _monitor = m;
        }
    }

    public void StopMonitor()
    {
        lock (_lock) StopMonitorLocked();
        LevelChanged?.Invoke(this, 0);
    }

    private void StopMonitorLocked()
    {
        if (_monitor == null) return;
        try { _monitor.StopRecording(); _monitor.Dispose(); } catch { }
        _monitor = null;
    }

    public void Dispose()
    {
        Cancel();
        StopMonitor();
    }
}

/// <summary>Mittelt beliebig viele Kanäle zu Mono.</summary>
internal sealed class DownmixToMonoProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = [];

    public DownmixToMonoProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_buffer.Length < needed) _buffer = new float[needed];
        int read = _source.Read(_buffer, 0, needed);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
