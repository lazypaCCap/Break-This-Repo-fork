// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Numerics · NumericShowcase — 実演と自己検証 / 演示与自检
//  The same generic code, run eight times, and then checked. No arithmetic lives in this file:
//  it calls NumericKernel, and it calls it through generic helpers of its own, so the
//  demonstration is generic too rather than a switch over type names. If a per-type branch ever
//  crept in, the run would stop proving anything — so there is none.
//
//  The wound it pokes in Java: this program cannot be written in this shape. "Run one routine for
//  int, long, double, float, BigDecimal and BigInteger and print a line for each" is, in Java,
//  either eight copy-pasted methods, or reflection through `Class<?>` and `Method.invoke` with
//  boxed arguments and a checked exception on the signature, or a `Stream` pipeline that boxes
//  every intermediate. Java's own generics stop at `Number`, which offers no arithmetic and no
//  static members — so the Sum/Mean/Min/Max line below has no Java spelling that is at once
//  generic and allocation-free.
//
//  The wound it pokes in Rust: Rust can run one routine over all of these too, via traits, with two
//  costs visible from this file. That routine is recompiled per instantiation, so the eight calls
//  below are eight copies of the whole call tree in this crate and in every crate that repeats it.
//  And the type list is bounded by the standard library plus the orphan rule: `decimal` is not in
//  Rust's std (`rust_decimal::Decimal`), `BigInteger` is not either (`num_bigint::BigInt`). Both
//  are fine crates; both are dependency edges that `INumber{T}` here does not have, because decimal
//  and BigInteger are in the BCL and the generic contract was designed alongside them.
//
//  Honesty note on the output shape: the float and double numbers printed below are the ones binary
//  floating point actually produces. SelfCheck asserts the difference between Mean(decimal, 0.1/0.2/
// 0.3) and Mean(double, 0.1/0.2/0.3) rather than hiding it — a showcase that made double look like
//  decimal would be lying about the one thing this module is best placed to show honestly.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using System.Numerics;

namespace Elysia.Numerics;

/// <summary>Runs the generic kernel over every numeric type in the showcase, and self-checks it.</summary>
/// <remarks>
/// No console output anywhere in this project: <see cref="Run"/> returns the lines and lets the host
/// decide where they go. No randomness and no clock — every number below is a pure function of the
/// source, so two runs are byte-identical and a diff between them is meaningful.
/// </remarks>
public static class NumericShowcase
{
    /// <summary>The number of values every generic sample in this file contains, for every <c>T</c>.</summary>
    private const int SampleSize = 5;

    /// <summary>Executes the demonstration and returns its output as ordered, human-readable lines.</summary>
    /// <returns>The result lines, ending with the measured allocation delta over
    /// <see cref="AllocationProbe.GenericOps"/> generic kernel invocations.</returns>
    /// <remarks>
    /// Eight instantiations of one kernel, eight of one capability report, one allocation
    /// measurement. The per-type lines come from <see cref="Profile{T}"/>, which knows nothing about
    /// any of the eight types.
    /// </remarks>
    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>
        {
            "Elysia.Numerics — one generic kernel, eight numeric types, no per-type arithmetic code",
            string.Empty,
            "Profile: same generic Sum/Mean/Range/Dot over the sample { 1 ... 5 }, ascending and descending",
        };

        // Eight calls to one generic method. The compiler emits eight constrained instantiation sites,
        // the runtime supplies eight arithmetics, and the source above them was written once.
        lines.Add(Profile<int>());
        lines.Add(Profile<long>());
        lines.Add(Profile<Int128>());
        lines.Add(Profile<Half>());
        lines.Add(Profile<float>());
        lines.Add(Profile<double>());
        lines.Add(Profile<decimal>());
        lines.Add(Profile<BigInteger>());

        lines.Add(string.Empty);
        lines.Add("Beyond 64 bits — the input has no machine word and no boxed equivalent:");
        lines.Add(WideBigIntegerLine());

        lines.Add(string.Empty);
        lines.Add("Capability report — every fact below is a static abstract member of INumber{T},");
        lines.Add("read through the type parameter. No reflection, no type switch, no registry:");

        string[] reports =
        [
            NumericKernel.Describe<int>(),
            NumericKernel.Describe<long>(),
            NumericKernel.Describe<Int128>(),
            NumericKernel.Describe<Half>(),
            NumericKernel.Describe<float>(),
            NumericKernel.Describe<double>(),
            NumericKernel.Describe<decimal>(),
            NumericKernel.Describe<BigInteger>(),
        ];
        foreach (string report in reports)
        {
            lines.Add("  " + report);
        }

        lines.Add(string.Empty);
        lines.Add("Note: 1/2 is 0 above for int, long, Int128 and BigInteger, and 0.5 for Half, float,");
        lines.Add("double and decimal. One expression, eight arithmetics — that is what the kernel buys.");

        lines.Add(string.Empty);
        lines.Add(AllocationLine());

        return lines;
    }

    /// <summary>Performs real assertions over the same kernel and reports the outcome.</summary>
    /// <param name="report">When this returns, the per-assertion results and a final verdict, one per
    /// line. Never <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every assertion held; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Each assertion would fail loudly if the generic path were secretly boxed, secretly 64-bit,
    /// secretly floating point or secretly per-type. In particular <c>Int128</c> is asserted past
    /// <c>long.MaxValue</c>, <see cref="System.Numerics.BigInteger"/> past 2^200, and the allocation
    /// delta is asserted to be exactly zero bytes.
    /// </remarks>
    public static bool SelfCheck(out string report)
    {
        var lines = new List<string>();
        bool ok = true;

        // ── 1. A known int span, asserted against values computed by hand in the assertion text.
        int[] oneToFive = [1, 2, 3, 4, 5];
        ReadOnlySpan<int> span = oneToFive;
        int[] descending = [5, 4, 3, 2, 1];

        int sum = NumericKernel.Sum(span);
        ok &= Assert(lines, "Sum(int, {1,2,3,4,5}) == 15", sum == 15, sum.ToString(CultureInfo.InvariantCulture));

        int mean = NumericKernel.Mean(span);
        ok &= Assert(
            lines,
            "Mean(int, {1,2,3,4,5}) == 3   [the required known-int-span check]",
            mean == 3,
            mean.ToString(CultureInfo.InvariantCulture));

        (int min, int max) = NumericKernel.Range(span);
        ok &= Assert(lines, "Range(int, {1,2,3,4,5}) == (1, 5)", min == 1 && max == 5, $"{min}, {max}");

        int dot = NumericKernel.Dot(span, descending);
        ok &= Assert(lines, "Dot(int, {1,2,3,4,5} · {5,4,3,2,1}) == 35", dot == 35, dot.ToString(CultureInfo.InvariantCulture));

        // ── 2. Empty input is defined, not an exception: a slice of unknown length is normal here.
        ok &= Assert(
            lines,
            "Sum/Mean/Range of an empty span are 0, 0, (0, 0)",
            NumericKernel.Sum(ReadOnlySpan<int>.Empty) == 0
                && NumericKernel.Mean(ReadOnlySpan<int>.Empty) == 0
                && NumericKernel.Range(ReadOnlySpan<int>.Empty) == (0, 0),
            "defined");

        // ── 3. decimal is base-10 and exact; double is base-2 and is not. Same kernel, both true.
        decimal[] tenths = [0.1m, 0.2m, 0.3m];
        ReadOnlySpan<decimal> tenthsSpan = tenths;
        decimal decimalMean = NumericKernel.Mean(tenthsSpan);
        ok &= Assert(
            lines,
            "Mean(decimal, {0.1, 0.2, 0.3}) == 0.2 exactly  [decimal ships in the BCL; Rust's std has none]",
            decimalMean == 0.2m,
            decimalMean.ToString(CultureInfo.InvariantCulture));

        double[] binaryTenths = [0.1, 0.2, 0.3];
        ReadOnlySpan<double> binaryTenthsSpan = binaryTenths;
        double doubleMean = NumericKernel.Mean(binaryTenthsSpan);
        ok &= Assert(
            lines,
            "Mean(double, {0.1, 0.2, 0.3}) != 0.2   [asserted as a difference, not papered over]",
            doubleMean != 0.2d,
            doubleMean.ToString("R", CultureInfo.InvariantCulture));

        // ── 4. Int128 crosses the 64-bit boundary; nothing in the kernel noticed. The value below is
        //       exactly representable in Int128 and is not representable in any 64-bit type.
        Int128[] wide = [(Int128)long.MaxValue, (Int128)long.MaxValue];
        ReadOnlySpan<Int128> wideSpan = wide;
        Int128 wideSum = NumericKernel.Sum(wideSpan);
        ok &= Assert(
            lines,
            "Sum(Int128, { long.MaxValue, long.MaxValue }) == 2^64-2 and is > long.MaxValue (no 64-bit type holds it)",
            wideSum == (Int128)long.MaxValue * 2 && wideSum > (Int128)long.MaxValue,
            wideSum.ToString(CultureInfo.InvariantCulture));

        // ── 4b. …and Int128 still wraps in two's complement at its own limit, exactly as int does.
        //        Nothing in the kernel chose that; overflow semantics belong to the type parameter.
        //        Asserting the wrap rather than avoiding it is the honest version of the claim: this
        //        is a 128-bit type, not an unbounded one. BigInteger, asserted next, is the unbounded
        //        one.
        Int128 wrapped = NumericKernel.Sum<Int128>([Int128.MaxValue, Int128.MaxValue]);
        ok &= Assert(
            lines,
            "Int128 wraps like every primitive: Sum(Int128, { MaxValue, MaxValue }) == -2 in an unchecked context",
            wrapped == -2,
            wrapped.ToString(CultureInfo.InvariantCulture));

        // ── 5. BigInteger flows through the identical kernel: INumber{T} carries no `struct`
        //       constraint, so a heap-allocated arbitrary-precision type is admissible.
        BigInteger[] big = [BigInteger.Pow(2, 200), BigInteger.Pow(2, 200)];
        ReadOnlySpan<BigInteger> bigSpan = big;
        BigInteger bigSum = NumericKernel.Sum(bigSpan);
        ok &= Assert(
            lines,
            "Sum(BigInteger, { 2^200, 2^200 }) == 2^201  [a reference type, no `struct` constraint]",
            bigSum == BigInteger.Pow(2, 201),
            $"{bigSum.ToString(CultureInfo.InvariantCulture).Length} digits");

        // ── 6. All eight instantiations agree on the same abstract input, formatted identically.
        string[] totals =
        [
            SumAsText<int>(),
            SumAsText<long>(),
            SumAsText<Int128>(),
            SumAsText<Half>(),
            SumAsText<float>(),
            SumAsText<double>(),
            SumAsText<decimal>(),
            SumAsText<BigInteger>(),
        ];
        ok &= Assert(
            lines,
            "one generic Sum, eight instantiations, all eight == 15",
            totals.All(total => total == "15"),
            string.Join(" / ", totals));

        // ── 7. Describe{T} answers from static abstract members, and the answer differs per type for
        //       the same source expression — only possible because those members bind at
        //       instantiation rather than at call time.
        string describeInt = NumericKernel.Describe<int>();
        string describeDouble = NumericKernel.Describe<double>();
        ok &= Assert(
            lines,
            "Describe<int>() sees 1/2=0 and IsInteger(1/2)=True; Describe<double>() sees 1/2=0.5 and IsInteger(1/2)=False",
            describeInt.Contains("1/2=0 ", StringComparison.Ordinal)
                && describeInt.Contains("IsInteger(1/2)=True", StringComparison.Ordinal)
                && describeDouble.Contains("1/2=0.5 ", StringComparison.Ordinal)
                && describeDouble.Contains("IsInteger(1/2)=False", StringComparison.Ordinal),
            string.Concat(describeInt, " || ", describeDouble));

        // ── 8. The claim that makes all of the above more than a curiosity: zero bytes allocated.
        long delta = AllocationProbe.Measure();
        ok &= Assert(
            lines,
            $"allocations during {AllocationProbe.GenericOps} generic ops == 0 bytes",
            delta == 0,
            $"{delta} bytes");

        lines.Add(ok ? "SelfCheck: PASS" : "SelfCheck: FAIL");
        report = string.Join(Environment.NewLine, lines);
        return ok;
    }

    /// <summary>Builds the one-line profile for <typeparamref name="T"/> by calling the generic kernel.</summary>
    /// <remarks>
    /// This method is the demonstration: no branch, no cast, no knowledge of <typeparamref name="T"/>
    /// beyond what <see cref="INumber{T}"/> guarantees. The type reaches it only as a type argument,
    /// which is exactly what Java's erased generics cannot carry without boxing and what Rust would
    /// compile a fresh copy of.
    /// </remarks>
    private static string Profile<T>() where T : INumber<T>
    {
        ReadOnlySpan<T> ascending = Ascending<T>(SampleSize);
        ReadOnlySpan<T> descending = Descending<T>(SampleSize);

        T sum = NumericKernel.Sum(ascending);
        T mean = NumericKernel.Mean(ascending);
        (T min, T max) = NumericKernel.Range(ascending);
        T dot = NumericKernel.Dot(ascending, descending);

        // Formatting happens here, outside every measured region, and is the only place in this module
        // where a generic value is converted to text.
        return $"{DisplayName<T>(),-10} Sum={sum} Mean={mean} Min={min} Max={max} Dot={dot}";
    }

    /// <summary>Demonstrates the kernel over <see cref="System.Numerics.BigInteger"/> values past 128 bits.</summary>
    /// <remarks>
    /// Java's closest analogue cannot enter a generic arithmetic routine at all: BigDecimal's
    /// operations are instance methods, not a static contract, so the loop in
    /// <see cref="NumericKernel.Sum{T}"/> has no Java equivalent for these types. In Rust this sample
    /// needs the num-bigint crate and a trait implementation this crate is not allowed to write for
    /// it under the orphan rule.
    /// </remarks>
    private static string WideBigIntegerLine()
    {
        BigInteger scale = BigInteger.Pow(2, 128);
        BigInteger[] wide = [scale * 2, scale * 4, scale * 6];

        BigInteger sum = NumericKernel.Sum<BigInteger>(wide);
        BigInteger mean = NumericKernel.Mean<BigInteger>(wide);
        (BigInteger min, BigInteger max) = NumericKernel.Range<BigInteger>(wide);

        return $"BigInteger Sum={sum} ({sum.ToString(CultureInfo.InvariantCulture).Length} digits) "
             + $"Mean={mean} ({mean.ToString(CultureInfo.InvariantCulture).Length} digits) "
             + $"Min={min} Max={max}";
    }

    /// <summary>Runs the allocation probe and formats its verdict.</summary>
    /// <returns>The literal line <c>allocations during 100000 generic ops: N bytes</c>.</returns>
    private static string AllocationLine()
    {
        long delta = AllocationProbe.Measure();

        // Reported verbatim — no rounding, no "approximately", no averaging. If the runtime ever
        // allocates, this line moves and the assertion in SelfCheck fails with it.
        return $"allocations during {AllocationProbe.GenericOps} generic ops: {delta} bytes";
    }

    /// <summary>Produces the ascending sample <c>{ 1, 2, ... , count }</c> for any numeric type.</summary>
    /// <remarks>
    /// Even the sample builder is generic: <c>T.CreateChecked</c> is the static factory from
    /// <see cref="INumberBase{T}"/>, so the generic path in this file contains no literal of any
    /// concrete numeric type.
    /// </remarks>
    private static T[] Ascending<T>(int count) where T : INumber<T>
    {
        var values = new T[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = T.CreateChecked(i + 1);
        }

        return values;
    }

    /// <summary>Produces the descending sample <c>{ count, ... , 2, 1 }</c> for any numeric type.</summary>
    private static T[] Descending<T>(int count) where T : INumber<T>
    {
        var values = new T[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = T.CreateChecked(count - i);
        }

        return values;
    }

    /// <summary>Returns the sum of the ascending sample as text, for the cross-type consistency check.</summary>
    private static string SumAsText<T>() where T : INumber<T>
    {
        ReadOnlySpan<T> span = Ascending<T>(SampleSize);
        return NumericKernel.Sum(span).ToString() ?? string.Empty;
    }

    /// <summary>Maps a runtime type name to the spelling a C# programmer would write, for display only.</summary>
    /// <remarks>
    /// Purely cosmetic, and the only place in the module that mentions a concrete type by name. It
    /// decides nothing: the arithmetic above it already ran through <see cref="INumber{T}"/> alone.
    /// </remarks>
    private static string DisplayName<T>() where T : INumber<T>
    {
        return typeof(T).Name switch
        {
            "Int32" => "int",
            "Int64" => "long",
            "Single" => "float",
            "Double" => "double",
            "Decimal" => "decimal",
            var name => name,
        };
    }

    /// <summary>Records one assertion outcome in the report and returns it, for folding into a verdict.</summary>
    private static bool Assert(List<string> lines, string what, bool passed, string actual)
    {
        lines.Add($"  [{(passed ? "PASS" : "FAIL")}] {what}  (actual: {actual})");
        return passed;
    }
}
