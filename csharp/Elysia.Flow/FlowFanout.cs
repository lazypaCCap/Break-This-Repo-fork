// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Flow · FlowFanout — 夢の分岐と合流 — one workload, many awaiters, one merged answer.
//
//  Wound, Java: fan-out is mature — parallel streams, ExecutorService.invokeAll,
//  StructuredTaskScope on modern JDKs. The honest differences are ergonomic and diagnostic:
//  merging means a Callable/Future per unit, a checked InterruptedException at every call site,
//  and a mutable accumulator with explicit synchronisation. The failure mode is quiet — an
//  ExecutionException unwraps into somebody else's exception, and one cancelled sub-task can
//  silence the rest.
//
//  Wound, Rust: FuturesUnordered or tokio's JoinSet gets the same shape and both are excellent,
//  but both are dependencies and neither is in std. JoinSet ties you to Tokio's runtime; spawning
//  requires `Send + 'static` on the future, so every captured reference must be owned or
//  Arc-wrapped and a single `&mut` in the closure ends the conversation with a trait-bound error.
//  Cancellation is again Drop: dropping the set drops the tasks, with no token a subsystem deep
//  inside a task can observe on its way out.
//
//  Claim, C#: `Parallel.ForEachAsync` is in the base class library. It takes the caller's
//  CancellationToken and hands each body a token already linked to it, so a body can forward the
//  same signal into its own I/O. Results merge into the slot the item owns — no collector to
//  learn, no lock on the hot path — and one `await` reports the first failure while the rest of
//  the in-flight work unwinds.
//
//  The peak-concurrency counter is not decoration: it is how the showcase proves the bodies really
//  overlapped, without ever asserting on wall-clock time (which would be flaky under load).
// ═══════════════════════════════════════════════════════════════════════════════════════════

namespace Elysia.Flow;

/// <summary>A deterministic fan-out / fan-in over a small in-memory workload.</summary>
/// <remarks>Results merge by index, so output does not depend on completion order and the checksum is
/// stable. Concurrency is observed by counting bodies simultaneously in flight, never by timing.</remarks>
internal static class FlowFanout
{
    /// <summary>Per-body delay in milliseconds: long enough for overlap to be observable, short enough
    /// to keep the whole fan-out in the low tens of milliseconds.</summary>
    private const int FanOutTickMs = 2;

    /// <summary>Runs <paramref name="items"/> through <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// with bounded parallelism, merges the per-item results and reports the observed peak overlap.</summary>
    /// <param name="items">The workload. Read concurrently, never mutated.</param>
    /// <param name="degreeOfParallelism">Maximum bodies in flight at once; must be positive.</param>
    /// <param name="cancellationToken">Assigned to <see cref="ParallelOptions.CancellationToken"/>;
    /// each body receives a token already linked to it, so it can forward the same signal into its
    /// own I/O and unwind immediately.</param>
    /// <returns>The number of merged results, their sum in index order, and the greatest number of
    /// bodies that were simultaneously in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism"/> is less than one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    internal static async Task<(int Count, long Checksum, int PeakConcurrency)> FanOutAsync(
        IReadOnlyList<int> items,
        int degreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (degreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degreeOfParallelism),
                degreeOfParallelism,
                "Fanning out needs at least one worker.");
        }

        // Each body writes exactly one slot, so the merge is lock-free and cannot depend on which
        // body finished first.
        var merged = new int[items.Count];

        var inFlight = 0;
        var peak = 0;
        var peakGate = new object();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = degreeOfParallelism,
            },
            async (index, bodyToken) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (peakGate)
                {
                    if (now > peak)
                    {
                        peak = now;
                    }
                }

                try
                {
                    await Task.Delay(FanOutTickMs, bodyToken).ConfigureAwait(false);
                    merged[index] = FlowStageMath.FanTransform(items[index]);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            }).ConfigureAwait(false);

        long checksum = 0;
        for (var i = 0; i < merged.Length; i++)
        {
            checksum += merged[i];
        }

        return (merged.Length, checksum, peak);
    }
}
