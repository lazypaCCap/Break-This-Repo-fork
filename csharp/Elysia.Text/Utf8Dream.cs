// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Text · Utf8Dream
//  文字列は「バイト列のビュー」である — a string is a view over bytes, not an object per token.
//
//  What this file demonstrates, and the exact wound it pokes in each rival language:
//
//  · Java — `String` is a UTF-16 `char[]` wrapper, full stop. Every UTF-8 byte arriving from a
//    socket, a file or an mmap must first be decoded into a `char[]` and *then* wrapped in a
//    `String` object: one heap object per token, minimum, however short the token. `InputStream`
//    hands out a fresh `byte[]` per `read()`. There is no `Span<T>`; the nearest analogue,
//    `ByteBuffer`, cannot be a stack local, cannot be sliced without a new object, and cannot be
//    handed out as a *mutable* view of an encoded region. `sun.misc.Unsafe`/`VarHandle` can fake
//    it, but that is the unsupported escape hatch, not the language.
//
//  · Rust — `&[u8]` / `&str` slices are excellent, and this is Rust's home turf: nothing here
//    beats `str::from_utf8(&bytes)`. What Rust's *standard library* lacks is a compile-time
//    UTF-8 literal in the ordinary string syntax: `b"..."` is a byte literal but ASCII-plus-escapes
//    only, and `"..."` is `&str`, a different (and stronger) static guarantee. Where Rust pays is
//    the generic formatting route — `Display`/`Debug` monomorphise per type, so a heterogeneous
//    "format whatever T happens to be" helper needs a trait object or a macro, proc-macro crates
//    enter the build, and trait-resolution errors are famously long.
//
//  · C# (this file) — `"…"u8` is a *compile-time UTF-8 literal* that materialises into the
//    assembly's read-only data segment: no heap object, no per-call decode, not even a
//    `stackalloc`. `Span<byte>` and `ReadOnlySpan<char>` are `ref struct`s that can point into
//    stack memory, a pooled array, a pinned socket buffer or a string's own storage, and the
//    compiler forbids them from escaping to the heap — which is precisely the invariant that makes
//    "this allocates nothing" auditable rather than aspirational. `ISpanFormattable` gives the
//    generic formatting route Rust needs a macro for, and value types do not box through it.
//
//  Honesty notes, because they matter more than the pitch:
//   · `Decode` below *must* allocate. It returns a `System.String`, an immutable UTF-16 object on
//     the managed heap; there is no such thing as a free `String`. The win is that you pay that
//     price only when you genuinely need a `string` — instead of paying it once per token on the
//     way in, the way an `InputStream`/`String` pipeline does.
//   · .NET 8 additionally ships `IUtf8SpanFormattable`, which formats *straight into* `Span<byte>`
//     and skips the UTF-16 scratch buffer `TryFormatScore` uses. The showcase's frozen signature
//     is constrained to `ISpanFormattable`, so that is what is implemented; the claim being made
//     is the narrow, true one — the interface-based path exists, is generic, allocates no boxing
//     wrapper for value types, and requires no crate.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using System.Text;

namespace Elysia.Text;

/// <summary>
/// UTF-8-first text primitives: transcode into caller-supplied memory, decode only when a
/// <see cref="string"/> is really required, and format through the span-based formatting interface
/// without an intermediate heap object.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a thin, honest wrapper over a base-class-library primitive — nothing is
/// hand-rolled, nothing is <c>unsafe</c>. The value being showcased is the *shape* of the API:
/// bytes in, bytes out, and allocations only where they are visible in the signature.
/// </para>
/// </remarks>
public static class Utf8Dream
{
    /// <summary>
    /// Size of the UTF-16 scratch buffer used by <see cref="TryFormatScore{T}"/>. Sixty-four chars
    /// covers any primitive, <see cref="Guid"/> and ISO-8601 <see cref="DateTime"/> in every
    /// invariant format, so the scratch buffer is a stack local and never grows.
    /// </summary>
    private const int FormatScratchChars = 64;

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8 directly into <paramref name="destination"/> and
    /// returns the byte count.
    /// </summary>
    /// <param name="destination">
    /// Caller-owned memory: a <c>stackalloc</c> buffer, an <c>ArrayPool</c> lease, a slice of a
    /// pinned network buffer, or the tail of a larger frame being assembled.
    /// </param>
    /// <param name="text">The UTF-16 text to transcode.</param>
    /// <returns>The number of UTF-8 bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small to hold the encoded form.
    /// </exception>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="text"/> contains an unpaired surrogate.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the whole trick, and it fits in one line: the destination is a *parameter*, so the
    /// callee has nothing to allocate. The Java equivalent returns a fresh <c>byte[]</c>
    /// (<c>String.getBytes()</c>) or requires a <c>CharsetEncoder</c> relay through a
    /// <c>ByteBuffer</c> object. Rust's <c>str::as_bytes</c> is free but the idiomatic
    /// <c>to_string()</c>/<c>String</c> road allocates by default.
    /// </para>
    /// <para>
    /// Malformed input raises <see cref="EncoderFallbackException"/> rather than silently
    /// substituting U+FFFD; the replacement policy is a fallback you opt into, not a surprise.
    /// </para>
    /// </remarks>
    public static int WriteIntoSpan(Span<byte> destination, ReadOnlySpan<char> text)
    {
        // UTF8Encoding.GetBytes(ReadOnlySpan<char>, Span<byte>): transcode, return the count,
        // allocate nothing on the managed heap for well-formed input.
        return Encoding.UTF8.GetBytes(text, destination);
    }

    /// <summary>
    /// Decodes UTF-8 bytes into a new <see cref="string"/>.
    /// </summary>
    /// <param name="utf8">The UTF-8 bytes to decode.</param>
    /// <returns>A new UTF-16 string.</returns>
    /// <remarks>
    /// This is the only member of this class that must allocate, and it is the honest boundary:
    /// a <see cref="string"/> is an immutable UTF-16 heap object, so asking for one costs one
    /// object. Callers who can consume a <c>ReadOnlySpan<char></c> instead should never call this —
    /// that is the entire point of the UTF-8-first pipeline the showcase builds.
    /// </remarks>
    public static string Decode(ReadOnlySpan<byte> utf8)
    {
        return Encoding.UTF8.GetString(utf8);
    }

    /// <summary>
    /// Formats <paramref name="value"/> as invariant text and writes the UTF-8 bytes into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <typeparam name="T">
    /// Any type implementing <see cref="ISpanFormattable"/> — e.g. <see cref="int"/>,
    /// <see cref="long"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="Guid"/>,
    /// <see cref="DateTime"/> and <see cref="TimeSpan"/>.
    /// </typeparam>
    /// <param name="value">The value to format.</param>
    /// <param name="destination">The UTF-8 destination buffer.</param>
    /// <param name="written">
    /// The number of bytes written on success, otherwise zero.
    /// </param>
    /// <returns><see langword="true"/> if the value was formatted and fit; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// The generic constraint is what makes this possible without reflection and without a
    /// per-type macro: <c>value.TryFormat(…)</c> on a value type compiles to a <c>constrained.</c>
    /// call, so an <see cref="int"/> is formatted in place with no boxing wrapper and no
    /// <c>String.Format</c> argument array. Java has no equivalent — a "format anything" helper
    /// there takes <c>Object</c> and goes through <c>Formatter</c>, which boxes and allocates a
    /// <c>StringBuilder</c> plus a result <c>String</c>. In Rust the same helper is
    /// <c>fn f&lt;T: Display&gt;</c>, which monomorphises a copy of this function per type, or a
    /// <c>&amp;dyn Display</c>, which is a fat pointer and a virtual call.
    /// </para>
    /// <para>
    /// Two-step by necessity: <see cref="ISpanFormattable"/> formats into UTF-16
    /// (<c>Span&lt;char&gt;</c>), so the scratch buffer is transcoded to UTF-8 afterwards. The
    /// scratch buffer is a <c>stackalloc</c>, so neither step allocates. Types implementing
    /// <c>IUtf8SpanFormattable</c> (new in .NET 8) can skip the transcoding entirely.
    /// </para>
    /// <para>
    /// The method refuses rather than throws when the destination is too small — the same contract
    /// as <c>TryFormat</c> itself — so it is usable for capacity probing on a fixed-size frame.
    /// </para>
    /// </remarks>
    public static bool TryFormatScore<T>(T value, Span<byte> destination, out int written)
        where T : ISpanFormattable
    {
        Span<char> scratch = stackalloc char[FormatScratchChars];

        if (!value.TryFormat(scratch, out int charCount, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }

        int byteCount = Encoding.UTF8.GetByteCount(scratch[..charCount]);
        if (byteCount > destination.Length)
        {
            written = 0;
            return false;
        }

        written = Encoding.UTF8.GetBytes(scratch[..charCount], destination);
        return true;
    }

    /// <summary>
    /// Returns the showcase's UTF-8 literal, declared with the C# 11 <c>u8</c> suffix.
    /// </summary>
    /// <returns>
    /// A <see cref="ReadOnlySpan{Byte}"/> over the assembly's read-only data segment; length
    /// equals the UTF-8 byte count of the literal, not its UTF-16 character count.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The bytes live in the loaded image's static data, so this call allocates nothing and needs
    /// no <c>stackalloc</c>, no static <c>byte[]</c> field and no encoder invocation at run time.
    /// The span is immutable by type, so the compiler can hand it out freely.
    /// </para>
    /// <para>
    /// This is the piece Java has no notation for at all (a <c>byte[]</c> constant needs a
    /// <c>static final byte[]</c> plus an initialiser that runs, or an escape-laden
    /// <c>getBytes()</c> on first use), and the piece Rust expresses differently rather than
    /// identically: <c>b"…"</c> is <c>&amp;[u8; N]</c> bytes but only for ASCII plus escapes,
    /// while a non-ASCII <c>"…"</c> is statically valid <c>&amp;str</c>. The <c>u8</c> suffix
    /// keeps the literal cheap *and* spells out the encoding at the declaration site.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<byte> Literal()
    {
        return "夢を縫う — Elysia.Text, declared as a u8 literal"u8;
    }
}
