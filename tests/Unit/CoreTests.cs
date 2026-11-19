using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class WavIOTests
{
    [Fact]
    public void Roundtrip_and_truncated_header()
    {
        var path = Path.Combine(Path.GetTempPath(), "hilde-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var samples = Enumerable.Range(0, 16000).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
            WavIO.WriteMono16k(path, samples);
            var back = WavIO.ReadMono16k(path);
            Assert.Equal(samples.Length, back.Length);
            Assert.InRange(Math.Abs(back[123] - samples[123]), 0, 0.0002);

            // Absturz simulieren: data-Größe 0 im Header
            var bytes = File.ReadAllBytes(path);
            bytes[40] = bytes[41] = bytes[42] = bytes[43] = 0;
            File.WriteAllBytes(path, bytes);
            Assert.Equal(samples.Length, WavIO.ReadMono16k(path).Length);
            Assert.Equal(1, WavIO.ParseFormatTag(bytes));
        }
        finally { File.Delete(path); }
    }
}

public class PriorityGateTests
{
    [Fact]
    public async Task High_priority_overtakes_waiting_low()
    {
        var g = new PriorityGate();
        var first = await g.EnterAsync(false, default);
        var low = g.EnterAsync(false, default);
        var high = g.EnterAsync(true, default);
        Assert.False(low.IsCompleted);
        Assert.False(high.IsCompleted);
        first.Dispose();
        var h = await high;
        Assert.False(low.IsCompleted);
        h.Dispose();
        (await low).Dispose();
    }

    [Fact]
    public async Task Cancelled_waiter_is_removed()
    {
        var g = new PriorityGate();
        var first = await g.EnterAsync(true, default);
        using var cts = new CancellationTokenSource();
        var waiting = g.EnterAsync(false, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        (await g.EnterAsync(false, default)).Dispose();
    }
}

public class ErrorCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_messages_german()
    {
        var all = HildeErrors.All;
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        Assert.All(all, e => Assert.Matches(@"^H-[A-Z]{3}-\d{3}$", e.Id));
        Assert.All(all, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        Assert.Same(HildeErrors.Unexpected, HildeErrors.Get("gibt-es-nicht"));
    }
}
