// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Flow · FlowShowcase — 夢の記録 — the report, and the proof that the report is true.
//
//  Wound, Java: `CompletableFuture` chains read well right up to the moment they do not.
//  thenCompose/thenCombine/handle/exceptionally compose into a graph the type system cannot check
//  and the debugger cannot step through, and a cancellation inside that graph is indistinguishable
//  from a failure. `SelfCheckAsync` is the alternative shape: async work written top to bottom,
//  assertions as ordinary statements, a cancellation caught as OperationCanceledException at a line
//  you can point at. Threads and `CompletableFuture` are both genuinely fine here — what is not fine
//  is that the two cannot be expressed in one syntax.
//
//  Wound, Rust: verifying an allocation claim means `#[bench]`, `criterion` (a dev-dependency) or a
//  custom global allocator; verifying runtime behaviour means adding a runtime crate or turning the
//  future inside out by hand. Here the two claims that matter — "the fast path allocated zero bytes"
//  and "the fan-out really overlapped" — are made with `GC.GetAllocatedBytesForCurrentThread()` and
//  an `Interlocked` counter, both in the base class library, in a method returning a tuple.
//
//  Claim, C#: a showcase that checks itself. One syntax runs the demo and verifies the demo — there
//  is no separate "async world" API surface to learn and no combinator soup to translate a result
//  through, which is exactly why the assertions below can be written as plain `if`-shaped steps.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Globalization;

namespace Elysia.Flow;

/// <summary>The runnable showcase: a human-readable report and a self-audit of the same machinery.</summary>
/// <remarks>Neither method writes to the console — both return data and let the caller decide, which is
/// what makes them usable from a test harness or a host process. The project has no <c>Console</c> reference.</remarks>
public static class FlowShowcase
{
    /// <summary>Number of seed values in <see cref="Seeds"/>.</summary>
    private const int SeedCount = 6;

    /// <summary>Seed values for the whole showcase (six of them).</summary>
    private static readonly int[] Seeds = { 1, 2, 3, 4, 5, 6 };

    /// <summary>Weave stages applied to every seed value.</summary>
    private const int Stages = 3;

    /// <summary>Bound of the channel in the middle of the pipeline.</summary>
    private const int ChannelCapacity = 4;

    /// <summary>Maximum number of fan-out bodies in flight at once.</summary>
    private const int FanOutDegree = 4;

    /// <summary>Value routed through the synchronous fast path, and the value routed through the
    /// asynchronous slow path on the same signature.</summary>
    private const int FastPathValue = 17;

    /// <summary>See <see cref="FastPathValue"/>: any value outside the fast-path range takes the slow path.</summary>
    private const int SlowPathValue = -5;

    /// <summary>One-based index at which the cancellation demo pulls the token, counted in values
    /// observed rather than in elapsed time so the run stays deterministic.</summary>
    private const int CancelAfterObserved = 3;

    /// <summary>Total values the weave must emit: every seed value at every stage.</summary>
    private const int ExpectedValues = SeedCount * Stages;

    /// <summary>Runs the whole pipeline once and returns a report, one human-readable line per stage.</summary>
    /// <returns>A read-only list of report lines; the last carries the real elapsed wall-clock time
    /// measured with <see cref="Stopwatch"/>.</returns>
    /// <remarks>Stages are reported in flow order: weave (<see cref="DreamPipeline.WeaveAsync"/>),
    /// channel (<see cref="DreamPipeline.ThroughChannelAsync"/>), fan-out
    /// (<see cref="FlowFanout.FanOutAsync"/>), <see cref="ValueTask{TResult}"/> dispatch, then a
    /// mid-flight cancellation.</remarks>
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var lines = new List<string>();
        var total = Stopwatch.StartNew();

        // ── Stage A: IAsyncEnumerable produced with `yield return`, consumed with `await foreach`.
        var stageA = Stopwatch.StartNew();
        var woven = new List<int>(ExpectedValues);
        var streamed = await DreamPipeline
            .ConsumeAsync(DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None), woven.Add, CancellationToken.None)
            .ConfigureAwait(false);
        stageA.Stop();
        lines.Add($"weave    : {Seeds.Length} seed value(s) x {Stages} stage(s) -> {streamed} value(s) streamed "
                  + $"through IAsyncEnumerable<int> and consumed with await foreach in {Ms(stageA)} ms");

        // ── Stage B: the same values through a bounded Channel<int>.
        var stageB = Stopwatch.StartNew();
        var channeled = new List<int>(ExpectedValues);
        await foreach (var item in DreamPipeline
                           .ThroughChannelAsync(woven, ChannelCapacity, CancellationToken.None)
                           .ConfigureAwait(false))
        {
            channeled.Add(item);
        }

        stageB.Stop();
        lines.Add($"channel  : {woven.Count} value(s) in -> {channeled.Count} value(s) out through a bounded "
                  + $"Channel<int> (capacity {ChannelCapacity}, FullMode.Wait back-pressure) in {Ms(stageB)} ms");

        // ── Stage C: fan out with Parallel.ForEachAsync, merge by index.
        var stageC = Stopwatch.StartNew();
        var fan = await FlowFanout.FanOutAsync(channeled, FanOutDegree, CancellationToken.None).ConfigureAwait(false);
        stageC.Stop();
        lines.Add($"fanout   : {channeled.Count} value(s) fanned out over Parallel.ForEachAsync "
                  + $"(MaxDegreeOfParallelism {FanOutDegree}, observed peak concurrency {fan.PeakConcurrency}) -> "
                  + $"{fan.Count} merged result(s), checksum {fan.Checksum} in {Ms(stageC)} ms");

        // ── Stage D: one signature, two completion shapes.
        var stageD = Stopwatch.StartNew();
        var fast = DreamPipeline.FastPathAsync(FastPathValue);
        var fastSync = fast.IsCompletedSuccessfully;
        var fastValue = fastSync ? fast.Result : await fast.ConfigureAwait(false);
        var slow = DreamPipeline.FastPathAsync(SlowPathValue);
        var slowSync = slow.IsCompletedSuccessfully;
        var slowValue = await slow.ConfigureAwait(false);
        stageD.Stop();
        lines.Add($"valuetask: FastPathAsync({FastPathValue}) -> {fastValue} completed synchronously={fastSync} "
                  + $"(no Task allocated); FastPathAsync({SlowPathValue}) -> {slowValue} completed "
                  + $"synchronously={slowSync} on the same signature in {Ms(stageD)} ms");

        // ── Stage E: cancellation, caught as OperationCanceledException.
        var stageE = Stopwatch.StartNew();
        using var pipelineToken = new CancellationTokenSource();
        var observed = 0;
        var cancelled = false;
        try
        {
            await DreamPipeline
                .ConsumeAsync(
                    DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None),
                    _ => { if (++observed == CancelAfterObserved) { pipelineToken.Cancel(); } },
                    pipelineToken.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { cancelled = true; }

        stageE.Stop();
        lines.Add($"cancel   : {observed} of {ExpectedValues} value(s) observed before a mid-flight "
                  + $"CancellationToken cancelled the pipeline; OperationCanceledException caught={cancelled} "
                  + $"in {Ms(stageE)} ms");

        total.Stop();
        lines.Add($"elapsed  : {Ms(total)} ms wall clock for the whole {lines.Count}-stage run, measured with "
                  + $"System.Diagnostics.Stopwatch across {streamed} streamed value(s) and {fan.Count} merged result(s)");

        return lines;
    }

    /// <summary>Audits every claim the other files make, with real assertions over real results.</summary>
    /// <returns><c>(true, summary)</c> when every assertion holds, otherwise <c>(false, reason)</c>
    /// naming the first assertion that failed.</returns>
    /// <remarks>The assertions are deliberately strong: item counts, order preservation, determinism,
    /// that cancellation of a live pipeline really throws <see cref="OperationCanceledException"/>
    /// rather than merely stopping, that the fast <see cref="ValueTask{TResult}"/> completed
    /// synchronously with zero bytes allocated on this thread, and that the fan-out genuinely
    /// overlapped. Each one is a plain statement — there is no assertion framework in this project.</remarks>
    public static async Task<(bool Ok, string Report)> SelfCheckAsync()
    {
        var assertions = 0;

        // Warm the fast path so the allocation measurement is not the JIT's first impression of it.
        for (var warmup = 0; warmup < 8; warmup++)
        {
            _ = DreamPipeline.FastPathAsync(FastPathValue).Result;
        }

        try
        {
            // ── 1. Item counts out of the async iterator, and the values the arithmetic predicts.
            var woven = new List<int>(ExpectedValues);
            var consumed = await DreamPipeline
                .ConsumeAsync(DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None), woven.Add, CancellationToken.None)
                .ConfigureAwait(false);
            Require(consumed == ExpectedValues, $"weave produced {consumed} value(s), expected {ExpectedValues}.");
            Require(woven.Count == ExpectedValues, $"observer saw {woven.Count} value(s), expected {ExpectedValues}.");
            assertions += 2;

            var expected = new List<int>(ExpectedValues);
            foreach (var seed in Seeds)
            {
                var value = seed;
                for (var stage = 0; stage < Stages; stage++)
                {
                    value = FlowStageMath.WeaveStep(value, stage);
                    expected.Add(value);
                }
            }

            Require(woven.SequenceEqual(expected), "weave values do not match the arithmetic the pipeline documents.");
            assertions++;

            // ── 2. Two runs agree: the showcase is deterministic.
            var second = new List<int>(ExpectedValues);
            _ = await DreamPipeline
                .ConsumeAsync(DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None), second.Add, CancellationToken.None)
                .ConfigureAwait(false);
            Require(woven.SequenceEqual(second), "two identical weave runs disagreed; the pipeline is not deterministic.");
            assertions++;

            // ── 3. A bounded channel preserves count and order, and terminates.
            var channeled = new List<int>(ExpectedValues);
            await foreach (var item in DreamPipeline
                               .ThroughChannelAsync(woven, ChannelCapacity, CancellationToken.None)
                               .ConfigureAwait(false))
            {
                channeled.Add(item);
            }

            Require(channeled.Count == ExpectedValues, $"channel emitted {channeled.Count} value(s), expected {ExpectedValues}.");
            Require(channeled.SequenceEqual(woven), "the bounded channel reordered the stream.");
            assertions += 2;

            // ── 4. A token cancelled before enumeration throws, on both iterator implementations.
            using var preCancelled = new CancellationTokenSource();
            preCancelled.Cancel();

            var weaveThrew = false;
            try
            {
                _ = await DreamPipeline
                    .ConsumeAsync(DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None), _ => { }, preCancelled.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { weaveThrew = true; }

            Require(weaveThrew, "a pre-cancelled token did not cancel the IAsyncEnumerable pipeline.");
            assertions++;

            var channelThrew = false;
            try
            {
                await foreach (var item in DreamPipeline
                                   .ThroughChannelAsync(woven, ChannelCapacity, preCancelled.Token)
                                   .ConfigureAwait(false))
                {
                    _ = item;
                }
            }
            catch (OperationCanceledException) { channelThrew = true; }

            Require(channelThrew, "a pre-cancelled token did not cancel the bounded channel pipeline.");
            assertions++;

            // ── 5. A token cancelled *during* the run stops the pipeline and throws.
            using var midFlight = new CancellationTokenSource();
            var observed = 0;
            var midFlightThrew = false;
            try
            {
                _ = await DreamPipeline
                    .ConsumeAsync(
                        DreamPipeline.WeaveAsync(Seeds, Stages, CancellationToken.None),
                        _ => { if (++observed == CancelAfterObserved) { midFlight.Cancel(); } },
                        midFlight.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { midFlightThrew = true; }

            Require(midFlightThrew, "a mid-flight cancellation did not interrupt the pipeline.");
            Require(
                observed >= CancelAfterObserved && observed < ExpectedValues,
                $"mid-flight cancellation stopped at {observed} value(s), which is not a partial run.");
            assertions += 2;

            // ── 6. The ValueTask fast path is synchronous and allocates nothing on this thread.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var fast = DreamPipeline.FastPathAsync(FastPathValue);
            var fastCompleted = fast.IsCompletedSuccessfully;
            var fastValue = fastCompleted ? fast.Result : -1;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Require(fastCompleted, "FastPathAsync did not complete synchronously.");
            Require(fastValue == FlowStageMath.FastTransform(FastPathValue), $"the fast path returned {fastValue}, not the documented result.");
            Require(allocated == 0, $"the synchronous fast path allocated {allocated} byte(s); a zero-allocation claim needs zero.");
            assertions += 3;

            // ── 7. The same signature does have an asynchronous mode.
            var slow = DreamPipeline.FastPathAsync(SlowPathValue);
            Require(!slow.IsCompletedSuccessfully, "the asynchronous ValueTask path completed synchronously, so it proves nothing.");
            var slowValue = await slow.ConfigureAwait(false);
            Require(slowValue == FlowStageMath.SlowTransform(SlowPathValue), $"the asynchronous path returned {slowValue}, not the documented result.");
            assertions += 2;

            // ── 8. The fan-out merges every item and genuinely overlaps.
            var fan = await FlowFanout.FanOutAsync(channeled, FanOutDegree, CancellationToken.None).ConfigureAwait(false);

            long expectedChecksum = 0;
            foreach (var value in channeled)
            {
                expectedChecksum += FlowStageMath.FanTransform(value);
            }

            Require(fan.Count == ExpectedValues, $"fan-out merged {fan.Count} result(s), expected {ExpectedValues}.");
            Require(fan.Checksum == expectedChecksum, $"fan-out checksum {fan.Checksum} does not match the sequential checksum {expectedChecksum}.");
            Require(fan.PeakConcurrency >= 2, $"fan-out never ran more than {fan.PeakConcurrency} body/bodies concurrently.");
            assertions += 3;

            return (true,
                $"{assertions} assertions passed: weave {ExpectedValues}/{ExpectedValues} values "
                + $"(deterministic, matches documented arithmetic), channel {channeled.Count}/{ExpectedValues} "
                + $"values in order through capacity {ChannelCapacity}, cancellation threw "
                + $"OperationCanceledException both pre-emptively and mid-flight after {observed} value(s), "
                + $"ValueTask fast path synchronous with {allocated} byte(s) allocated while the same "
                + $"signature's slow path stayed asynchronous, fan-out merged {fan.Count}/{ExpectedValues} "
                + $"results with peak concurrency {fan.PeakConcurrency}.");
        }
        catch (AssertionFailure failure)
        {
            return (false, failure.Message);
        }
        catch (Exception ex)
        {
            return (false, $"self-check threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Throws <see cref="AssertionFailure"/> unless <paramref name="condition"/> holds.</summary>
    /// <param name="condition">The assertion.</param>
    /// <param name="reason">The failure text, returned verbatim from <see cref="SelfCheckAsync"/>.</param>
    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new AssertionFailure(reason);
        }
    }

    /// <summary>Formats a stopped <see cref="Stopwatch"/> as milliseconds with three decimals, using the
    /// invariant culture so the report is identical on every machine.</summary>
    /// <param name="stopwatch">A stopwatch whose elapsed time should be reported.</param>
    /// <returns>The elapsed milliseconds as a culture-invariant string.</returns>
    private static string Ms(Stopwatch stopwatch) =>
        stopwatch.Elapsed.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Signals that a <see cref="SelfCheckAsync"/> assertion failed. Never escapes this class.</summary>
    private sealed class AssertionFailure : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="AssertionFailure"/> class.</summary>
        /// <param name="reason">The human-readable failure reason.</param>
        internal AssertionFailure(string reason)
            : base(reason)
        {
        }
    }
}
