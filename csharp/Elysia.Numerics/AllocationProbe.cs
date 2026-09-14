// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Numerics · AllocationProbe — 割り当ての証拠 / 分配证据
//  The measurement that turns "generic code boxes" from an objection into a number.
//
//  "Generics box" is the most common objection to this design, and it is a correct objection to
//  *Java's* design: in Java, `long total = 0; for (int x : xs) total += x;` over `List<Integer>`
//  allocates nothing inside the loop only because the allocations already happened — one ~16-byte
//  object header plus 4-byte payload per element, at insert time, plus the backing Object[] of
//  pointers. No Java program performs generic arithmetic over 100,000 values with a 0-byte delta,
//  because in Java there is no generic arithmetic over values, only arithmetic over objects holding
//  values.
//
//  In Rust the delta below is 0 too, and this file is not a claim against Rust: `slice.iter().sum()`
//  over `i32` is a zero-allocation, zero-boxing, monomorphised loop, the same shape as the one
//  measured here. The probe is in the project because the evidence is cheap and portable, and
//  because a 0 here also witnesses that the kernel is not secretly `object`-based underneath. What
//  Rust pays to reach the same 0 is a compiled copy of the loop per instantiation — code size and
//  build time — not allocation while running.
//
//  The technique is deliberately primitive: read a counter, run the loop, read the counter again.
//  Nothing is sampled, averaged or inferred, and the number is reported verbatim by
//  NumericShowcase.Run() and asserted by NumericShowcase.SelfCheck.
// ═══════════════════════════════════════════════════════════════════════════════════════════

namespace Elysia.Numerics;

/// <summary>Measures managed allocations across a fixed number of generic kernel invocations.</summary>
/// <remarks>
/// <para>
/// <see cref="GenericOps"/> counts <em>kernel invocations</em>, not loop iterations: the measured
/// loop runs <c>GenericOps / 2</c> iterations, each performing exactly one
/// <see cref="NumericKernel.Sum{T}"/> call and one <see cref="NumericKernel.Range{T}"/> call whose
/// <c>Max</c> member is consumed. 100,000 invocations, 50,000 iterations, one number.
/// </para>
/// <para>
/// The warm-up loop exists so the measured window contains no first-call JIT work. It is not a
/// retry and no data is discarded: one uninterrupted measurement is reported, and if it is ever
/// non-zero the honest response is to read the loop rather than the counter.
/// </para>
/// </remarks>
internal static class AllocationProbe
{
    /// <summary>The exact number of generic kernel invocations inside the measured window: 100,000.</summary>
    internal const int GenericOps = 100_000;

    /// <summary>The running total of everything the measurement loops computed.</summary>
    /// <remarks>
    /// A dead local accumulation is fair game for an optimiser; a store to a static property that
    /// another method can read is not. Writing it costs one field store *after* the counter has
    /// already been read, so it cannot perturb the delta it protects.
    /// </remarks>
    internal static long ObservedTotal { get; private set; }

    /// <summary>Runs the measurement and returns the managed bytes this thread allocated while it ran.</summary>
    /// <returns>The allocation delta in bytes. Expected and asserted to be exactly <c>0</c>.</returns>
    internal static long Measure()
    {
        // A five-element int span: one cache line, and the shape the kernel is written for — a slice
        // its caller already had.
        int[] data = [1, 2, 3, 4, 5];
        ReadOnlySpan<int> span = data;

        // Warm-up: instantiate and JIT NumericKernel<int>.Sum and .Range before the counter is read,
        // and leave the tiered JIT nothing to promote mid-measurement.
        long warmup = 0;
        for (int i = 0; i < 1_000; i++)
        {
            warmup += NumericKernel.Sum(span);
            warmup += NumericKernel.Range(span).Max;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        long sink = 0;
        for (int i = 0; i < GenericOps / 2; i++)
        {
            // Both calls return T by value and neither creates a heap object for int: the span is a
            // by-ref struct, the accumulator is a register, the (int Min, int Max) tuple returns in
            // registers. String formatting — the one operation in this module that can genuinely
            // allocate — is kept strictly outside this window.
            sink += NumericKernel.Sum(span);
            sink += NumericKernel.Range(span).Max;
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        // Hand the accumulated total to a field the optimiser cannot see through, now that the
        // measurement is complete.
        ObservedTotal = warmup + sink;

        return after - before;
    }
}
