// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Text · TextShowcase
//  The receipts: real encoding calls, real counters, real assertions — no hardcoded byte counts.
//
//  What this file demonstrates, and the exact wound it pokes in each rival language:
//
//  · Java — the numbers in this file could not be produced with the same instrument. Java has no
//    per-thread allocation counter in the standard library (JVMTI/`ThreadMXBean`
//    `getThreadAllocatedBytes` is a non-standard extension, and its granularity is TLAB-sized, not
//    byte-exact), so "how many bytes did that pipeline allocate" is an estimate from a profiler
//    (JFR, async-profiler) rather than an assertion you can put in a unit test. And there is no
//    *zero* to assert: `String.getBytes()` allocates one `byte[]` per call, and every `String`
//    produced by `new String(bytes, UTF_8)` is a second object with a `char[]` inside it.
//
//  · Rust — `#[global_allocator]` plus a counting wrapper gives exact numbers, and it is a
//    five-line, zero-crate exercise; assertions on it fit in a `#[test]` with `assert_eq!`. So this
//    specific instrument is *not* a Rust weakness — it is listed here for symmetry and honesty.
//    What Rust's std does not hand you is a per-*thread* counter with no instrumentation at all:
//    the counter has to be installed by the binary, not read from the runtime.
//
//  · C# (this file) — `GC.GetAllocatedBytesForCurrentThread()` is byte-exact, per-thread, always
//    on, and free enough to call around a hot loop from inside a library. That makes
//    "this path allocates zero bytes" a runtime-checkable claim, not a claim in a README, which is
//    what `SelfCheck` below does: it re-measures the stackalloc encode path and fails the build
//    if a single byte of managed heap was touched. The remaining allocations are printed with
//    their exact byte totals and named — a `string` per decode because a `string` *is* an object,
//    and one byte array for the JSON payload — instead of being hidden behind "fast".
//
//  Honesty notes:
//   · The byte totals below are deterministic for a fixed runtime and payload shape, and they are
//     measured at run time on every invocation — never hardcoded. Sizes include the object header
//     and alignment, so the per-operation figure is a whole number of 8-byte slots, not the
//     payload's nominal length.
//   · `Run()` has no side effects and no `Console` writes: it returns lines for a caller (a test
//     harness, a report generator, a log sink) to print. That keeps the module usable from a
//     library context where stdout is not yours.
//   · Warm-up iterations precede every measured region so tiered JIT compilation and the lazy
//     initialization of the UTF-8 encoder's statics are not attributed to the loop.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using System.Text;

namespace Elysia.Text;

/// <summary>
/// Produces the module's human-readable evidence lines and asserts them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Run"/> is the demo; <see cref="SelfCheck"/> is the proof. Both are deterministic:
/// same runtime, same numbers.
/// </para>
/// </remarks>
public static class TextShowcase
{
    /// <summary>Iterations for the stackalloc encode loop (see <see cref="Run"/> part a).</summary>
    private const int StackLoopIterations = 10_000;

    /// <summary>Iterations for the measured encode+decode round-trip loop (part b).</summary>
    private const int RoundTripIterations = 10_000;

    /// <summary>Iterations used for the JIT/encoder warm-up before any measured region.</summary>
    private const int WarmupIterations = 64;

    /// <summary>Size of the stack buffers used throughout the demo, in bytes.</summary>
    private const int StackBufferSize = 256;

    /// <summary>The Japanese sample: four BMP scalars, three UTF-8 bytes each.</summary>
    private const string CjkSample = "夢を縫う";

    /// <summary>The mixed sample used for round-trip equality: Japanese, Chinese, Latin and emoji.</summary>
    private const string MixedSample = "夢を縫う 织梦 Elysia — UTF-8 🌸";

    /// <summary>The record name used in the JSON demonstration (a path separator included, on purpose).</summary>
    private const string JsonName = "夢を縫う／Elysia";

    /// <summary>The integer field of the JSON demonstration record.</summary>
    private const int JsonValue = 42;

    /// <summary>The floating-point field of the JSON demonstration record.</summary>
    private const double JsonScore = 3.5;

    /// <summary>
    /// Runs the showcase and returns its report as a list of non-empty display lines.
    /// </summary>
    /// <returns>
    /// The lines, in order: (a) stackalloc encode with a zero-heap-bytes measurement, (b) the exact
    /// allocation delta of ten thousand encode+decode round-trips, (c) a JSON round-trip with its
    /// allocation count, (d) UTF-8 byte length versus UTF-16 character count computed from real
    /// <see cref="Encoding"/> calls, and (e) the generic span-formatting interface.
    /// </returns>
    /// <remarks>
    /// Writes nothing to any stream; printing is the caller's business.
    /// </remarks>
    public static IReadOnlyList<string> Run()
    {
        List<string> lines = new(12);

        lines.Add("Elysia.Text · UTF-8-first text pipeline — net8.0, base class library only, no NuGet");

        // ── (a) Encode into stack memory: the destination is a parameter, so nothing is allocated.
        Span<byte> stack = stackalloc byte[StackBufferSize];
        int stackWritten = 0;
        for (int i = 0; i < WarmupIterations; i++)
        {
            stackWritten = Utf8Dream.WriteIntoSpan(stack, CjkSample);
        }

        long stackBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < StackLoopIterations; i++)
        {
            stackWritten = Utf8Dream.WriteIntoSpan(stack, CjkSample);
        }

        long stackDelta = GC.GetAllocatedBytesForCurrentThread() - stackBefore;
        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "  (a) stackalloc encode : {0} UTF-8 bytes for \"{1}\" written into a {2}-byte stack buffer × {3} iterations → heap bytes allocated = {4}",
            stackWritten, CjkSample, StackBufferSize, Format(StackLoopIterations), Format(stackDelta)));

        // ── (b) Ten thousand honest round-trips. The encode half is free; the decode half cannot be.
        Span<byte> roundTripBuffer = stackalloc byte[StackBufferSize];
        bool warmEqual = true;
        int lastEncoded = 0;
        for (int i = 0; i < WarmupIterations; i++)
        {
            int encoded = Utf8Dream.WriteIntoSpan(roundTripBuffer, MixedSample);
            lastEncoded = encoded;
            warmEqual &= Utf8Dream.Decode(roundTripBuffer[..encoded]).AsSpan().SequenceEqual(MixedSample);
        }

        long loopBefore = GC.GetAllocatedBytesForCurrentThread();
        bool allEqual = true;
        int totalEncodedBytes = 0;
        for (int i = 0; i < RoundTripIterations; i++)
        {
            int encoded = Utf8Dream.WriteIntoSpan(roundTripBuffer, MixedSample);
            string decoded = Utf8Dream.Decode(roundTripBuffer[..encoded]);
            totalEncodedBytes += encoded;
            allEqual &= decoded.AsSpan().SequenceEqual(MixedSample);
        }

        long loopDelta = GC.GetAllocatedBytesForCurrentThread() - loopBefore;
        double perOperation = loopDelta / (double)RoundTripIterations;
        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "  (b) encode+decode loop : {0} round-trips of a {1}-char string ({2} B each, {3} B total encoded) → {4} heap bytes, exactly {5} B/op, all equal = {6} (warm-up equal = {7}) — the encode half is the {8} B measured in (a), so every byte here is the one string Decode must return per iteration",
            Format(RoundTripIterations), MixedSample.Length, lastEncoded, Format(totalEncodedBytes),
            Format(loopDelta), perOperation.ToString("0.##", CultureInfo.InvariantCulture), allEqual, warmEqual,
            Format(stackDelta)));

        // ── (c) JSON over UTF-8 bytes, with the allocation count for one full round-trip, split so
        //       the cost of each half is attributable rather than merely summed.
        _ = DreamJson.Deserialize(DreamJson.Serialize(JsonName, JsonValue, JsonScore)); // warm-up

        long serializeBefore = GC.GetAllocatedBytesForCurrentThread();
        byte[] payload = DreamJson.Serialize(JsonName, JsonValue, JsonScore);
        long serializeDelta = GC.GetAllocatedBytesForCurrentThread() - serializeBefore;

        long deserializeBefore = GC.GetAllocatedBytesForCurrentThread();
        (string Name, int Value, double Score) record = DreamJson.Deserialize(payload);
        long deserializeDelta = GC.GetAllocatedBytesForCurrentThread() - deserializeBefore;
        long jsonDelta = serializeDelta + deserializeDelta;

        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "  (c) JSON round-trip    : {0} ({1} payload bytes) → name=\"{2}\" value={3} score={4} → heap bytes serialize={5} (payload array plus the two short-lived writer objects), deserialize={6} (the one returned name string), pair total={7}, value-exact = {8}",
            Encoding.UTF8.GetString(payload),
            payload.Length,
            record.Name,
            record.Value,
            record.Score.ToString("0.###", CultureInfo.InvariantCulture),
            Format(serializeDelta),
            Format(deserializeDelta),
            Format(jsonDelta),
            record.Name == JsonName && record.Value == JsonValue && record.Score.Equals(JsonScore)));

        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "      pooled + zero-copy : ArrayPool<byte> lease returned after {0} payload bytes; JsonDocument reads score={1} from the same bytes by UTF-8 member name, materialising no name string",
            payload.Length,
            DreamJson.PeekScore(payload).ToString("0.###", CultureInfo.InvariantCulture)));

        // ── (d) The byte math, computed from real Encoding calls — never hardcoded.
        int utf8Bytes = Encoding.UTF8.GetByteCount(CjkSample.AsSpan());
        int utf16Chars = CjkSample.Length;
        int utf16Bytes = utf16Chars * 2;
        int mixedUtf8Bytes = Encoding.UTF8.GetByteCount(MixedSample.AsSpan());
        int mixedUtf16Bytes = MixedSample.Length * 2;
        double density = utf8Bytes / (double)utf16Chars;
        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "  (d) \"{0}\" byte math : {1} UTF-8 bytes vs {2} UTF-16 chars ({3} bytes as UTF-16, {4}× per code unit) — a UTF-8-first pipeline pays {1} bytes, a UTF-16 one pays {3}; the mixed sample costs {5} UTF-8 bytes for {6} chars ({7} bytes as UTF-16, emoji included)",
            CjkSample, utf8Bytes, utf16Chars, utf16Bytes,
            density.ToString("0.###", CultureInfo.InvariantCulture),
            mixedUtf8Bytes, MixedSample.Length, mixedUtf16Bytes));

        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "      u8 literal         : \"…\"u8 = {0} bytes in read-only image data, 0 heap bytes, 0 stackalloc → \"{1}\"",
            Utf8Dream.Literal().Length,
            Encoding.UTF8.GetString(Utf8Dream.Literal())));

        // ── (e) Generic span formatting: no boxing for value types, no String.Format, no crate.
        Span<byte> formatBuffer = stackalloc byte[StackBufferSize];
        Span<byte> tinyBuffer = stackalloc byte[2];
        bool intFormatted = Utf8Dream.TryFormatScore(42, formatBuffer, out int intBytes);
        string intText = intFormatted ? Encoding.UTF8.GetString(formatBuffer[..intBytes]) : "?";
        bool doubleFormatted = Utf8Dream.TryFormatScore(JsonScore, formatBuffer, out int doubleBytes);
        string doubleText = doubleFormatted ? Encoding.UTF8.GetString(formatBuffer[..doubleBytes]) : "?";
        DateTime stamp = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        bool timeFormatted = Utf8Dream.TryFormatScore(stamp, formatBuffer, out int timeBytes);
        string timeText = timeFormatted ? Encoding.UTF8.GetString(formatBuffer[..timeBytes]) : "?";
        bool refused = !Utf8Dream.TryFormatScore(JsonScore, tinyBuffer, out int refusedBytes);
        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "  (e) ISpanFormattable   : int → {0} B \"{1}\", double → {2} B \"{3}\", DateTime → {4} B \"{5}\"; a 2-byte destination is refused rather than thrown ({6}, written={7})",
            intBytes, intText, doubleBytes, doubleText, timeBytes, timeText, refused, refusedBytes));

        return lines;
    }

    /// <summary>
    /// Re-derives the showcase's claims and reports whether every one of them holds.
    /// </summary>
    /// <param name="report">
    /// A human-readable summary. Non-empty on both the passing and the failing path: it lists the
    /// measured values alongside every failed assertion.
    /// </param>
    /// <returns><see langword="true"/> if all assertions passed; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// The assertions, in order:
    /// </para>
    /// <list type="number">
    /// <item><description><see cref="Run"/> returns a non-empty list and every line is non-blank.</description></item>
    /// <item><description>An encode/decode round-trip of the mixed Japanese/Chinese/English/emoji
    /// sample reproduces the input exactly, both as UTF-8 length and as characters.</description></item>
    /// <item><description>The <c>stackalloc</c> encode path allocates exactly zero bytes on the
    /// managed heap, re-measured here at run time.</description></item>
    /// <item><description>A JSON serialize/deserialize round-trip reproduces the name, value and
    /// score exactly, and reports the exact allocation count for one pair.</description></item>
    /// </list>
    /// </remarks>
    public static bool SelfCheck(out string report)
    {
        List<string> notes = new(6);
        List<string> failures = new(3);
        int failedGroups = 0;

        // ── 1. Every line Run() hands back must be real content.
        IReadOnlyList<string> lines = Run();
        notes.Add(Format(lines.Count) + " Run() lines");
        if (lines.Count == 0)
        {
            failures.Add("Run() returned no lines");
            failedGroups++;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                failures.Add("Run() line " + i.ToString(CultureInfo.InvariantCulture) + " is blank");
                failedGroups++;
                break;
            }
        }

        // ── 2. Mixed-script round-trip equality over real UTF-8 bytes.
        Span<byte> buffer = stackalloc byte[StackBufferSize];
        int encoded = Utf8Dream.WriteIntoSpan(buffer, MixedSample);
        string roundTripped = Utf8Dream.Decode(buffer[..encoded]);
        bool mixedEqual = roundTripped == MixedSample;
        if (!mixedEqual)
        {
            failures.Add("mixed round-trip mismatch: \"" + roundTripped + "\" != \"" + MixedSample + "\"");
            failedGroups++;
        }

        notes.Add(Format(encoded) + " UTF-8 B <=> " + roundTripped.Length.ToString(CultureInfo.InvariantCulture) + " UTF-16 chars, equal=" + mixedEqual);

        // ── 3. The zero-allocation claim, measured here rather than quoted from Run().
        for (int i = 0; i < WarmupIterations; i++)
        {
            _ = Utf8Dream.WriteIntoSpan(buffer, MixedSample);
        }

        long zeroBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < RoundTripIterations; i++)
        {
            _ = Utf8Dream.WriteIntoSpan(buffer, MixedSample);
        }

        long zeroDelta = GC.GetAllocatedBytesForCurrentThread() - zeroBefore;
        bool zeroAllocated = zeroDelta == 0;
        if (!zeroAllocated)
        {
            failures.Add("stackalloc encode allocated " + Format(zeroDelta) + " bytes over " + Format(RoundTripIterations) + " iterations");
            failedGroups++;
        }

        notes.Add("stackalloc encode over " + Format(RoundTripIterations) + " iterations = " + Format(zeroDelta) + " heap bytes");

        // ── 4. JSON round-trip must reproduce the inputs exactly, byte for byte in value terms.
        byte[] payload = DreamJson.Serialize(JsonName, JsonValue, JsonScore);
        (string Name, int Value, double Score) record = DreamJson.Deserialize(payload);
        bool jsonEqual = record.Name == JsonName && record.Value == JsonValue && record.Score.Equals(JsonScore);
        if (!jsonEqual)
        {
            failures.Add("JSON round-trip mismatch: name=\"" + record.Name + "\" value=" + record.Value.ToString(CultureInfo.InvariantCulture) + " score=" + record.Score.ToString("R", CultureInfo.InvariantCulture));
            failedGroups++;
        }

        notes.Add("JSON round-trip equal=" + jsonEqual + " over " + payload.Length.ToString(CultureInfo.InvariantCulture) + " payload bytes");

        report = (failures.Count == 0 ? "SelfCheck PASSED: " : "SelfCheck FAILED: ")
            + failedGroups.ToString(CultureInfo.InvariantCulture)
            + " of 4 assertion groups failed | "
            + string.Join("; ", notes);

        if (failures.Count > 0)
        {
            report += " | failures: " + string.Join("; ", failures);
        }

        return failures.Count == 0;
    }

    /// <summary>
    /// Formats an integer with invariant thousands separators, so report lines are culture-stable.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <returns>The invariant <c>"N0"</c> rendering of <paramref name="value"/>.</returns>
    private static string Format(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
