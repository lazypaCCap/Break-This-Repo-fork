// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Numerics · NumericKernel — 汎用カーネル / 泛型内核
//  One arithmetic kernel. Eight numeric types. No boxing, no per-type copy of the code.
//
//  It is written once because C# generics carry a *static contract*, not just a type:
//  `where T : INumber{T}` — deliberately not `where T : struct`, which would exclude BigInteger —
//  lets one body say `T.Zero`, `a + b`, `a < b` and `T.CreateChecked(n)` for int, long, Int128,
//  Half, float, double, decimal and System.Numerics.BigInteger.
//
//  The wound in Java — erasure, plus the absence of static abstract interface members:
//  · Java generics are erased, so nothing below is writable in Java at all. There is no `a + b`
//    for two `T`s, no `T.Zero`, no `T.MinValue`, no `T.CreateChecked`. Every operation becomes an
//    object method call — `a.add(b)`, `T.valueOf(0)` — with a cast at the call site and a heap
//    object per element. `List<int>` is not slow in Java, it is *illegal*: the candidates are
//    `List<Integer>` at ~16 bytes of mark word + class pointer per element (~24 with payload), or
//    a hand-written `IntList` copy-pasted again for long, double and float. Java 21 changed none of
//    this: no operator generics, no primitive specialisation, no value classes (Valhalla still has
//    not shipped). And BigDecimal — the one arbitrary-precision type on the JVM — cannot join
//    generic arithmetic at all, because its arithmetic is an instance method rather than a static
//    contract the compiler can bind.
//  · No `Span{T}` either. Java's equivalent entry point is `List<Integer>` (three indirections per
//    element), or an `int[]` with offset and count passed by hand — which is what C# itself did
//    before `ref struct` existed.
//
//  The wound in Rust — and NOT "Rust cannot do generic maths", because it can do it well:
//  · Monomorphisation. `sum::<i32>` and `sum::<f64>` are two separate compiled copies of the whole
//    method, in this crate and in every crate that instantiates it, and likewise for every method
//    that calls them. Here the body exists once in the assembly and the runtime instantiates it,
//    sharing one compiled implementation across reference-type instantiations.
//  · The orphan rule. `impl MyTrait for f64` is E0117 when both trait and type are foreign; C#'s
//    local interfaces can be closed over any type, which is what admits BigInteger — a BCL type
//    the BCL itself had to instantiate by hand because it was bound by the same rule.
//  · No decimal type in Rust's standard library at all. `f64` cannot represent 0.1 and base-10
//    arithmetic needs the rust_decimal crate; in C#, `decimal` is a keyword, a 128-bit base-10
//    type and an `INumber{decimal}`, so it drops into this file with no dependency edge.
//
//  Honesty note: this is not a claim that C# beats Rust's borrow checker or coherence checking —
//  Rust's is stricter and catches at compile time real bugs that CLR interface dispatch defers to
//  runtime. The narrow claim, which is true: for "same arithmetic, every numeric type, no boxing,
//  no per-type duplication", C# 11+ ships the answer in the box; Java and Rust's std do not.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Numerics;

namespace Elysia.Numerics;

/// <summary>A single generic arithmetic kernel over <see cref="INumber{T}"/>.</summary>
/// <remarks>
/// <para>
/// Compiled once, instantiated per numeric type by the runtime. The constraint is
/// <see cref="INumber{T}"/> only, so the admissible set includes <see cref="System.Numerics.BigInteger"/>,
/// a reference type on the managed heap; a `where T : struct` shortcut would silently exclude it,
/// and `where T : IComparable&lt;T&gt;` (the Java-shaped constraint) would make the arithmetic
/// below impossible to write. There is no `dynamic`, no `object`, no cast and no boxing in this
/// type: the compiler emits constrained `callvirt` instructions into each instantiation's own
/// static operators, which for value types the JIT devirtualises to the same machine code a
/// hand-written `Add` loop would produce.
/// </para>
/// <para>
/// Empty input is defined, not undefined: <see cref="Sum{T}"/>, <see cref="Mean{T}"/> and
/// <see cref="Range{T}"/> return `T.Zero`-shaped answers for an empty span rather than throwing,
/// because a kernel called with a slice of unknown length should not make its caller guess.
/// </para>
/// </remarks>
public static class NumericKernel
{
    /// <summary>Adds every value in <paramref name="values"/>.</summary>
    /// <typeparam name="T">A numeric type implementing <see cref="INumber{T}"/>.</typeparam>
    /// <param name="values">The values to add; empty yields <c>T.Zero</c>.</param>
    /// <returns>The sum, accumulated in <typeparamref name="T"/> — for integers with that type's own
    /// overflow behaviour, for <see cref="System.Numerics.BigInteger"/> unbounded.</returns>
    public static T Sum<T>(ReadOnlySpan<T> values) where T : INumber<T>
    {
        // `T.Zero` is a *static abstract* member: bound to int.Zero for this instantiation over int,
        // to BigInteger.Zero over BigInteger, and folded to a constant for the primitives. Java has
        // no spelling for this line.
        T sum = T.Zero;

        for (int i = 0; i < values.Length; i++)
        {
            // A single bare `add` for the primitives. For BigInteger it is a BCL call that allocates
            // a new BigInteger — inherent to arbitrary precision, not boxing, and not a cost the
            // kernel adds. Overflow semantics are never chosen here; they belong to T.
            sum += values[i];
        }

        return sum;
    }

    /// <summary>Computes the arithmetic mean of <paramref name="values"/>.</summary>
    /// <typeparam name="T">A numeric type implementing <see cref="INumber{T}"/>.</typeparam>
    /// <param name="values">The values to average; empty yields <c>T.Zero</c>.</param>
    /// <returns>
    /// The sum divided by the count, the division performed in <typeparamref name="T"/>. So
    /// <c>Mean&lt;int&gt;([1,2,3,4,5])</c> is exactly 3 while the same abstract input through
    /// <see cref="double"/> can carry binary rounding: the kernel is generic, the arithmetic is the
    /// type's own. That divergence is the point, not a defect.
    /// </returns>
    public static T Mean<T>(ReadOnlySpan<T> values) where T : INumber<T>
    {
        if (values.Length == 0)
        {
            return T.Zero;
        }

        // T.CreateChecked is the static factory every INumberBase{T} must provide — checked, so a
        // count that does not fit throws rather than silently wrapping. No cast, no `(T)(object)`.
        T count = T.CreateChecked(values.Length);
        return Sum(values) / count;
    }

    /// <summary>Returns the smallest and largest value in <paramref name="values"/> in one pass.</summary>
    /// <typeparam name="T">A numeric type implementing <see cref="INumber{T}"/>.</typeparam>
    /// <param name="values">The values to scan; empty yields <c>(T.Zero, T.Zero)</c>.</param>
    /// <returns>A tuple of the minimum and maximum. The tuple is a value type, so returning two
    /// values from one pass allocates nothing even when <typeparamref name="T"/> lives on the heap.</returns>
    /// <remarks>
    /// Seeding with <c>values[0]</c> rather than `T.MaxValue`/`T.MinValue` is deliberate:
    /// <see cref="INumber{T}"/> exposes neither, because "the largest value of T" is not a property
    /// every numeric type has — <see cref="System.Numerics.BigInteger"/> has no maximum. The
    /// operators come from <see cref="IComparisonOperators{TSelf, TOther, TResult}"/>, which
    /// <see cref="INumber{T}"/> inherits: operator syntax over a generic type, unspellable in Java.
    /// </remarks>
    public static (T Min, T Max) Range<T>(ReadOnlySpan<T> values) where T : INumber<T>
    {
        if (values.Length == 0)
        {
            return (T.Zero, T.Zero);
        }

        T min = values[0];
        T max = values[0];

        for (int i = 1; i < values.Length; i++)
        {
            T value = values[i];

            if (value < min)
            {
                min = value;
            }
            else if (value > max)
            {
                max = value;
            }
        }

        return (min, max);
    }

    /// <summary>Computes the dot product of two equally long spans.</summary>
    /// <typeparam name="T">A numeric type implementing <see cref="INumber{T}"/>.</typeparam>
    /// <param name="left">The left vector.</param>
    /// <param name="right">The right vector; must match <paramref name="left"/> in length.</param>
    /// <returns>The sum of the element-wise products, accumulated in <typeparamref name="T"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the spans differ in length. The hot path
    /// allocates nothing, including when the exception is never taken.</exception>
    public static T Dot<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right) where T : INumber<T>
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException(
                "Dot requires two vectors of equal length; a mismatched dot product is a caller bug, not a value to guess at.",
                nameof(right));
        }

        T accumulator = T.Zero;

        for (int i = 0; i < left.Length; i++)
        {
            // `*` and `+=` on a T the compiler knows only as INumber{T}: two constrained callvirts
            // into T's own static operators. This one expression is what Java's erased generics
            // cannot compile, and the reason IntVector, LongVector and DoubleVector exist in Java
            // codebases as near-identical copies of each other.
            accumulator += left[i] * right[i];
        }

        return accumulator;
    }

    /// <summary>
    /// Reports what the kernel can discover about <typeparamref name="T"/> at run time, purely from
    /// static abstract members — no reflection, no type switch, no registration table.
    /// </summary>
    /// <typeparam name="T">A numeric type implementing <see cref="INumber{T}"/>.</typeparam>
    /// <returns>A one-line capability report for <typeparamref name="T"/>.</returns>
    /// <remarks>
    /// <para>
    /// Every fact after the type name is read through the type parameter: <see cref="INumberBase{T}.Radix"/>
    /// (a static abstract property) and <see cref="INumberBase{T}.IsInteger"/> /
    /// <see cref="INumberBase{T}.IsRealNumber"/> (static abstract methods classifying a value the
    /// caller passes in). Java has no equivalent construct — its interfaces cannot carry static
    /// abstracts, so "ask the type what it is" means reflection on a `Class&lt;?&gt;` or a registry
    /// every new numeric type must remember to join. Rust does have associated functions and
    /// constants and would answer the same way; what differs is that Rust's answer is a
    /// monomorphised copy of this method per instantiation, and that a Rust trait here could not be
    /// implemented for a foreign type by a foreign crate.
    /// </para>
    /// <para>
    /// The probes are deliberately the same expression for all eight types — `1+1` and `1/2` as
    /// arithmetic, then `IsInteger(1/2)` as a classification of the *result*. `1/2` is 0.5 for
    /// <see cref="double"/> and 0 for <see cref="int"/> from one line of source, so `IsInteger(1/2)`
    /// is <see langword="false"/> for the former and <see langword="true"/> for the latter. Nothing
    /// in this method chose those answers; the arithmetic did.
    /// </para>
    /// </remarks>
    public static string Describe<T>() where T : INumber<T>
    {
        T one = T.One;
        T two = one + one;
        T half = one / two;

        // These two are static abstract *methods*, not properties: they classify a value the caller
        // hands them, so they cannot be read as `T.IsInteger` — they must be called, and the call is
        // bound to T's own implementation at instantiation.
        bool halfIsWhole = T.IsInteger(half);
        bool halfIsReal = T.IsRealNumber(half);

        // Interpolation here is a formatting concern, not an arithmetic one, and is deliberately kept
        // out of the kernel's hot paths: AppendFormatted over a generic T is a call through
        // object.ToString(), which for a type that failed to override ToString would box. Nothing in
        // the four arithmetic methods above formats anything.
        return $"{typeof(T).Name} radix={T.Radix} 1+1={two} 1/2={half} "
             + $"IsInteger(1/2)={halfIsWhole} IsRealNumber(1/2)={halfIsReal}";
    }
}
