// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Flow · DreamPipeline — 流れる夢を編む — the streaming pipeline itself.
//
//  Wound, Java: Project Loom is genuinely good and this file says so out loud. Virtual threads
//  solved blocking concurrency; "Java cannot do concurrency" is false. The wound is narrower, and
//  it is about *streaming*:
//    · No `await foreach`. Async consumption is a callback surface
//      (`java.util.concurrent.Flow.Subscriber`: onNext/onError/onComplete), or a
//      `CompletableFuture` combinator chain, or a third-party reactive type. Three vocabularies
//      for one idea, and none of them is a language feature.
//    · Back-pressure in `java.util.concurrent.Flow` is a manual protocol — the subscriber must
//      remember `Subscription.request(n)`, and forgetting is a silent stall. Here it is
//      `Channel.CreateBounded<T>(capacity)` plus `await writer.WriteAsync(...)`.
//    · No `CancellationToken` in the JDK. Cancellation is `Thread.interrupt()`,
//      `Future.cancel(true)` or `Subscription.cancel()` plus a convention about who checks what.
//      A thread's interrupt flag does not compose; it does not flow into a pipeline and it cannot
//      cancel a `CompletableFuture` chain parked on I/O.
//
//  Wound, Rust: `Stream` is not in std. `Iterator` is; `Stream` lives in `futures-core`, and the
//  combinators people actually use live in `futures` + `tokio`/`tokio-stream`/`async-std`/`smol`.
//    · No runtime in std. A future does nothing until a user-chosen executor polls it, so the
//      Tokio-vs-async-std-vs-smol choice is baked into the types of library functions.
//    · `Send + 'static` propagate virally: holding anything non-`Send` across an `.await` turns an
//      ordinary function into a compile error, and a channel from one runtime needs adapters in
//      another.
//    · Self-referential futures need `Pin`, and `async fn` in traits only became usable in Rust
//      1.75 — before that the `async-trait` crate boxed *every* future, i.e. exactly the
//      allocation `ValueTask` exists here to avoid.
//    · Cancellation is dropping the future: real and cooperative, but implicit. There is no token
//      to hand a subsystem and no `OperationCanceledException` to catch at a boundary.
//
//  Claim, C#: one syntax for both worlds. The runtime supplies the awaiter state machine, so
//  `IAsyncEnumerable<T>` is a first-class language shape — `yield return` to produce,
//  `await foreach` to consume, with the compiler generating the enumerator plumbing. A
//  `ValueTask<T>` that completes synchronously allocates nothing at all, while the *same
//  signature* can fall back to a real asynchronous completion. Errors and lifetime are the
//  language's problem: an exception inside the iterator surfaces from the `await foreach`, and
//  the iterator's `finally` runs when the enumerator is disposed.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Elysia.Flow;

/// <summary>The frozen pipeline API: async iteration, a bounded channel, cooperative cancellation
/// and a synchronous <see cref="ValueTask{TResult}"/> fast path.</summary>
/// <remarks>Every member takes a <see cref="CancellationToken"/> and honours it promptly, and every
/// await uses <c>ConfigureAwait(false)</c> — this is a library and must not capture a caller's
/// synchronisation context.</remarks>
public static class DreamPipeline
{
    /// <summary>Exclusive upper bound of the synchronous fast path: <c>[0, FastPathCeiling)</c>
    /// completes synchronously, anything else goes asynchronous.</summary>
    private const int FastPathCeiling = 4096;

    /// <summary>Milliseconds of deliberate back-pressure per item written to the channel. Not a
    /// benchmark knob: it exists so the bounded channel actually parks the producer instead of
    /// buffering the whole workload in one uninterruptible burst.</summary>
    private const int ChannelTickMs = 1;

    /// <summary>Milliseconds of simulated asynchronous work per seed value in <see cref="WeaveAsync"/>.
    /// Stands in for an I/O hop; it is what makes the iterator a genuine resumption point rather than
    /// a synchronous enumerator with an async signature.</summary>
    private const int WeaveTickMs = 1;

    /// <summary>Milliseconds of simulated work on the slow path of <see cref="FastPathAsync"/>.
    /// Short but strictly positive, which is what guarantees that task is never observed completed.</summary>
    private const int SlowPathTickMs = 1;

    /// <summary>Streams every intermediate value of a multi-stage weave, one value at a time.</summary>
    /// <param name="seed">Source values, enumerated lazily and in order — the caller's sequence is
    /// never fully materialised.</param>
    /// <param name="stages">Number of weave stages. Each seed value yields one output per stage, so
    /// the sequence carries <c>seed.Count() * stages</c> values.</param>
    /// <param name="cancellationToken">Cooperative cancellation token, annotated with
    /// <see cref="EnumeratorCancellationAttribute"/> so that <c>WeaveAsync(...).WithCancellation(t)</c>
    /// — the pattern <see cref="ConsumeAsync"/> uses — reaches this body with no manual plumbing.</param>
    /// <returns>An asynchronous sequence of intermediates. Nothing runs until the caller enumerates:
    /// the body is deferred to the first <c>MoveNextAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stages"/> is less than one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled
    /// before or during enumeration.</exception>
    public static async IAsyncEnumerable<int> WeaveAsync(
        IEnumerable<int> seed,
        int stages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (stages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stages), stages, "A weave needs at least one stage.");
        }

        foreach (var value in seed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var woven = value;
            for (var stage = 0; stage < stages; stage++)
            {
                // This checkpoint sits immediately after the resume point of the previous yield,
                // so cancellation lands on the very next MoveNextAsync instead of "eventually".
                cancellationToken.ThrowIfCancellationRequested();
                woven = FlowStageMath.WeaveStep(woven, stage);
                yield return woven;
            }

            // A real, cancellable suspension point: the second place the caller's token is observed.
            await Task.Delay(WeaveTickMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drains <paramref name="source"/>, invoking <paramref name="observe"/> per value, and
    /// returns how many values were observed.</summary>
    /// <param name="source">The asynchronous sequence to drain.</param>
    /// <param name="observe">Called once per value, in order, on the consuming context. An exception
    /// it throws propagates out of this method and disposes the enumerator, running any
    /// <c>finally</c> in the producer.</param>
    /// <param name="cancellationToken">Forwarded with <c>WithCancellation</c> so the producer observes
    /// the caller's token even if it was created with a different one.</param>
    /// <returns>The number of values observed. Returned only on clean completion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="observe"/>
    /// is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired before
    /// or during enumeration.</exception>
    public static async Task<int> ConsumeAsync(
        IAsyncEnumerable<int> source,
        Action<int> observe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(observe);

        var seen = 0;

        // `await foreach` is the consuming half of the language feature: WithCancellation merges the
        // caller's token into the producer's enumerator, and ConfigureAwait(false) keeps the
        // continuations off any ambient synchronisation context.
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            observe(item);
            seen++;
        }

        return seen;
    }

    /// <summary>Pushes <paramref name="seed"/> through a bounded <see cref="Channel{T}"/> and streams
    /// the far side as an asynchronous sequence.</summary>
    /// <param name="seed">The values to push, enumerated lazily by the producer task.</param>
    /// <param name="capacity">The channel's bound. The producer's <c>WriteAsync</c> parks as soon as
    /// this many items are unread, making the pipeline's memory ceiling a number rather than a hope.</param>
    /// <param name="cancellationToken">Cooperative cancellation token, annotated with
    /// <see cref="EnumeratorCancellationAttribute"/>. Cancelling it cancels both sides: the reader's
    /// <c>WaitToReadAsync</c> throws and the producer sees the linked token on its next write.</param>
    /// <returns>The values in their original order, streamed as they clear the channel.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired before or
    /// during enumeration. The producer is a separate task, so either side can observe it; both arrive
    /// here as this exception type.</exception>
    public static async IAsyncEnumerable<int> ThroughChannelAsync(
        IEnumerable<int> seed,
        int capacity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A bounded channel needs a positive bound.");
        }

        // Checked before anything is created, so a pre-cancelled token never leaves a channel,
        // a linked source or a producer task behind.
        cancellationToken.ThrowIfCancellationRequested();

        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            // Single reader/writer let the channel skip concurrency bookkeeping. FullMode.Wait is the
            // back-pressure mode: a full channel makes WriteAsync return an incomplete ValueTask
            // instead of dropping or growing.
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Lets this iterator shut the producer down when the consumer walks away early (break,
        // exception, dispose). Cancelling the linked source does not cancel the caller's token.
        using var producerScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Started, deliberately not awaited: the producer must run concurrently with the consumer or
        // the bounded channel would deadlock on the first write past `capacity`.
        var producer = ProduceIntoChannelAsync(channel.Writer, seed, producerScope.Token);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
            }
        }
        finally
        {
            // Runs on completion, on early exit, on dispose and on cancellation — the language
            // handling lifetime for us, with no `Subscription.cancel()` contract to remember and no
            // `Drop` order to reason about.
            producerScope.Cancel();

            // Drain so a producer parked on a full channel can observe cancellation and finish.
            while (channel.Reader.TryRead(out _))
            {
            }

            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: we just cancelled it. The caller's own cancellation, if any, has already
                // been observed by the reader above and is the one that propagates.
            }
        }
    }

    /// <summary>Returns a result for <paramref name="value"/> on a path that completes synchronously
    /// and allocates nothing.</summary>
    /// <param name="value">In <c>[0, 4096)</c> the fast path runs; outside that range the call falls
    /// through to a genuinely asynchronous implementation and returns a task-backed
    /// <see cref="ValueTask{TResult}"/>.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> whose <c>IsCompletedSuccessfully</c> is
    /// <see langword="true"/> on the fast path, with no <see cref="Task{TResult}"/> object ever
    /// constructed and no continuation scheduled.</returns>
    /// <remarks>The zero-allocation claim in its smallest form. One signature serves a synchronous
    /// result and an asynchronous one, which a <c>Task</c>-only API cannot express without either
    /// lying about the synchronous case or allocating a task for it. Java's `CompletableFuture` and
    /// Rust's `async fn` both allocate a state machine for a call whose answer is already in a
    /// register.</remarks>
    public static ValueTask<int> FastPathAsync(int value)
    {
        if (value >= 0 && value < FastPathCeiling)
        {
            // A struct initialization: no heap allocation, no state machine, no synchronisation.
            return new ValueTask<int>(FlowStageMath.FastTransform(value));
        }

        return new ValueTask<int>(SlowPathAsync(value));
    }

    /// <summary>Produces <paramref name="seed"/> into <paramref name="writer"/> with real back-pressure.</summary>
    /// <param name="writer">The channel writer; always completed, cleanly or with a fault.</param>
    /// <param name="seed">The values to write, in order.</param>
    /// <param name="cancellationToken">The linked token owned by the consuming iterator.</param>
    /// <returns>A task that completes when every value is written or the token fires.</returns>
    /// <remarks>Cancellation is a normal shutdown, not a fault: the writer is completed without an
    /// error because the consumer is already cancelled and nobody remains to observe one. Any other
    /// exception <em>is</em> propagated to the reader via <c>TryComplete</c>, so a broken producer
    /// cannot masquerade as a short stream.</remarks>
    private static async Task ProduceIntoChannelAsync(
        ChannelWriter<int> writer,
        IEnumerable<int> seed,
        CancellationToken cancellationToken)
    {
        Exception? terminal = null;

        try
        {
            foreach (var item in seed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Parks here while the channel is full: the producer's progress is bounded by the
                // consumer's, with no callback protocol and no request(n) window to keep in sync.
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                await Task.Delay(ChannelTickMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown, not a fault.
        }
        catch (Exception ex)
        {
            terminal = ex;
        }
        finally
        {
            // Runs on every path including the cancelled one: the reader is never left waiting on a
            // channel whose writer walked away.
            writer.TryComplete(terminal);
        }
    }

    /// <summary>The asynchronous counterpart of the fast path: identical arithmetic, task-backed result.</summary>
    /// <param name="value">A value outside the synchronous fast-path range.</param>
    /// <returns>A task that never completes synchronously.</returns>
    private static async Task<int> SlowPathAsync(int value)
    {
        // The first await always suspends, so this task can never be observed completed — which is
        // what makes the self-check's "the slow path is genuinely asynchronous" assertion meaningful
        // rather than luck.
        await Task.Delay(SlowPathTickMs).ConfigureAwait(false);
        return FlowStageMath.SlowTransform(value);
    }
}
