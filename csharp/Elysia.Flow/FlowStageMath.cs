// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Flow · FlowStageMath — 夢の算術 — the deterministic arithmetic the showcase rests on.
//
//  Every transformation here is a pure function of (value, index). That is a self-imposed
//  constraint with a purpose: the showcase must be *verifiable*. With random inputs, "18 values
//  in, checksum 4851" would prove nothing; because the payload is pure, FlowShowcase can
//  recompute the expected result independently and compare against it.
//
//  Wound, Java: none. Java does pure functions perfectly well.
//  Wound, Rust: none. A pure `fn(i32) -> i32` is where Rust is happiest.
//  Claim, C#: also none — and saying so is the point. Nothing in this project wins because of
//  arithmetic. It wins because of `await`, `yield return`, `Channel<T>` and `ValueTask`, and
//  those claims are only credible when the payload is boring enough to check by hand.
// ═══════════════════════════════════════════════════════════════════════════════════════════

namespace Elysia.Flow;

/// <summary>Pure, allocation-free integer arithmetic used by every stage of the pipeline.</summary>
/// <remarks>Deterministic, culture-free, no <c>Random</c>, no <c>DateTime.Now</c>, no ambient state:
/// two runs produce byte-identical counts and checksums. <c>internal</c> so the assembly's only
/// public surface is the frozen API in <see cref="DreamPipeline"/> and <see cref="FlowShowcase"/>.</remarks>
internal static class FlowStageMath
{
    /// <summary>Applies one weave stage: called <c>stages</c> times per seed value.</summary>
    /// <param name="value">The value entering this stage.</param>
    /// <param name="stage">Zero-based stage index — a number, never a clock reading.</param>
    /// <returns>The value leaving this stage.</returns>
    internal static int WeaveStep(int value, int stage) => unchecked((value * 3) + stage + 1);

    /// <summary>Body of the synchronous fast path of <see cref="DreamPipeline.FastPathAsync"/>.
    /// Deliberately tiny, so "no Task was allocated" is a claim about the design and not about the JIT.</summary>
    /// <param name="value">A value inside the fast-path range.</param>
    /// <returns>The transformed value.</returns>
    internal static int FastTransform(int value) => unchecked((value * 7) + 1);

    /// <summary>Body of the asynchronous slow path of <see cref="DreamPipeline.FastPathAsync"/>.
    /// Arithmetic identical to <see cref="FastTransform"/>: the only difference between the two
    /// paths is the shape of the return value, which is the entire point of <c>ValueTask</c>.</summary>
    /// <param name="value">A value outside the fast-path range.</param>
    /// <returns>The transformed value.</returns>
    internal static int SlowTransform(int value) => unchecked((value * 7) + 1);

    /// <summary>Per-item transformation for the <c>Parallel.ForEachAsync</c> fan-out. Order-sensitive,
    /// so a fan-out that dropped or reordered an item would change the merged checksum.</summary>
    /// <param name="value">The value assigned to one parallel body.</param>
    /// <returns>The transformed value.</returns>
    internal static int FanTransform(int value) => unchecked((value * 5) + 2);
}
