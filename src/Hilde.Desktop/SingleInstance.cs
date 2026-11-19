namespace Hilde.Desktop;

/// <summary>Nur eine HILDE pro Benutzer; ein zweiter Start holt das Hauptfenster nach vorn.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool IsFirst { get; }
    public event Action? ActivationRequested;

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\HILDE-SingleInstance", out bool created);
        IsFirst = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HILDE-Activate");
    }

    public void SignalFirst() => _activate.Set();

    public void Listen()
    {
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                if (_activate.WaitOne(500) && !_disposed) ActivationRequested?.Invoke();
            }
        }) { IsBackground = true, Name = "HILDE-SingleInstance" };
        _listener.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _listener?.Join(1000);
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
