namespace Hilde.Insertion;

/// <summary>Führt Zwischenablage-Operationen auf einem eigenen STA-Thread aus (OLE-Pflicht).</summary>
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "HILDE-Clipboard" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
