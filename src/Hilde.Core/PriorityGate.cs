namespace Hilde.Core;

/// <summary>
/// Exklusiver Zugriff auf eine Engine mit Vorrang für Diktate vor Hintergrundaufträgen.
/// </summary>
public sealed class PriorityGate
{
    private readonly object _lock = new();
    private bool _busy;
    private readonly LinkedList<(bool High, TaskCompletionSource<IDisposable> Tcs)> _waiting = new();

    public Task<IDisposable> EnterAsync(bool highPriority, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_busy)
            {
                _busy = true;
                return Task.FromResult<IDisposable>(new Releaser(this));
            }
            var tcs = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = (highPriority, tcs);
            LinkedListNode<(bool, TaskCompletionSource<IDisposable>)> node;
            if (highPriority)
            {
                // hinter alle wartenden Hochprioritären, vor alle Niedrigen
                var n = _waiting.First;
                while (n != null && n.Value.High) n = n.Next;
                node = n == null ? _waiting.AddLast(entry) : _waiting.AddBefore(n, entry);
            }
            else node = _waiting.AddLast(entry);

            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    lock (_lock)
                    {
                        if (node.List != null) { _waiting.Remove(node); tcs.TrySetCanceled(ct); }
                    }
                });
            }
            return tcs.Task;
        }
    }

    private void Release()
    {
        TaskCompletionSource<IDisposable>? next = null;
        lock (_lock)
        {
            if (_waiting.First is { } first)
            {
                _waiting.RemoveFirst();
                next = first.Value.Tcs;
            }
            else _busy = false;
        }
        next?.TrySetResult(new Releaser(this));
    }

    private sealed class Releaser(PriorityGate gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}
