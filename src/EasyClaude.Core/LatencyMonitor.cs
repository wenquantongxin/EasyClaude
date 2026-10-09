namespace EasyClaude.Core;

public sealed record LatencySample(string? Node, int? DelayMs, int ConsecutiveFailures, DateTimeOffset MeasuredAt);

public static class LatencyMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static async Task RunAsync(Func<CancellationToken, Task<(string? Node, int? Delay)>> probe,
        Action<LatencySample> publish, TimeProvider time, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, time);
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                (string? node, int? delay) = (null, null);
                try { (node, delay) = await probe(ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* A failed sample must not end periodic monitoring. */ }
                failures = delay is null ? failures + 1 : 0;
                ct.ThrowIfCancellationRequested();
                publish(new(node, delay, failures, time.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
