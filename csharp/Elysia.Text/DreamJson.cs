// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Text · DreamJson
//  Write JSON straight onto bytes; read JSON straight off bytes. No DTO, no intermediate string.
//
//  What this file demonstrates, and the exact wound it pokes in each rival language:
//
//  · Java — the ecosystem default is Jackson (or Gson), and both are overwhelmingly built on
//    `String`: the payload is decoded to a `String`, parsed into a tree or a reflective bean,
//    and re-encoded through a `StringBuilder`/`Writer` that ultimately yields another `String`
//    or `byte[]`. Streaming APIs (`JsonParser`, `JsonGenerator`) do exist — that is the fair
//    comparison — but the *typed* fast path is reflection over `Method`/`Field` objects with
//    per-call `setAccessible` and `Class<?>` lookups, and every property name is a `String`
//    constant that was already interned at class-load time.
//
//  · Rust — `serde_json` is excellent and it is *not* the standard library: it is a third-party
//    crate whose `#[derive(Serialize)]` runs a proc macro at build time, monomorphising a
//    specialized impl per type. That is fast (faster than this showcase's reflection-free-but-
//    non-source-generated path in some cases) and it is the honest counter-argument. The costs
//    are structural: crates plus proc-macro machinery in the dependency graph, a compile-time
//    and trait-error-message tax, and — because there is no runtime reflection fallback — a
//    *separate*, untyped `serde_json::Value` tree for genuinely dynamic payloads, with its own
//    `Map<String, Value>` that owns a `String` per key.
//
//  · C# (this file) — `System.Text.Json` is in the base class library. `Utf8JsonWriter` writes
//    UTF-8 bytes through an `IBufferWriter<byte>`: the same writer targets `stackalloc` memory,
//    an `ArrayPool<byte>` lease, or a socket send buffer, and it never materialises a `string`.
//    `Utf8JsonReader` is a `ref struct` over `ReadOnlySpan<byte>` that compares property names as
//    *byte spans* against `"name"u8`-style literals, so deserializing this record allocates
//    exactly one object: the final `Name` string. And when a dynamic, untyped tree is required,
//    `JsonDocument` parses the bytes *in place*, again without a `String` per key.
//
//  Honesty notes:
//   · This is the *manual* writer/reader path, chosen deliberately so the module has zero package
//     references. System.Text.Json's `JsonSerializer` is the higher-level path; in production the
//     zero-reflection form of it is a source-generated `JsonSerializerContext`
//     (`[JsonSerializable(typeof(T))]` + `[JsonSourceGenerationOptions]`), which emits the
//     `Utf8JsonWriter`/`Utf8JsonReader` code at compile time and removes reflection and
//     `JsonSerializerOptions` metadata lookup from the hot path. That generator ships with the SDK,
//     but wiring it in adds a build-time generator surface this showcase intentionally avoids.
//   · `Utf8JsonWriter` is not itself allocation-free: it rents a pooled internal buffer on
//     construction. The claim here is narrower and true — no *string* is produced anywhere on the
//     write path, and the output byte array is the only managed allocation the caller sees.
//   · The encoder used below is `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, so CJK and other
//     non-ASCII scalars stay raw UTF-8 instead of being emitted as `\uXXXX` escapes. The default
//     encoder escapes them for HTML-embedding safety; both go through the same zero-string writer,
//     the choice only changes what the bytes look like.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Elysia.Text;

/// <summary>
/// A tiny UTF-8 JSON record writer/reader: serialize to bytes, deserialize from bytes, with no
/// intermediate <see cref="string"/> on either path.
/// </summary>
/// <remarks>
/// <para>
/// The shape is fixed by the showcase: a flat object <c>{"name":…,"value":…,"score":…}</c>. Every
/// property name is compared as a UTF-8 byte span, never as a decoded <see cref="string"/>.
/// </para>
/// </remarks>
public static class DreamJson
{
    /// <summary>The <c>name</c> property name as a UTF-8 literal.</summary>
    private static ReadOnlySpan<byte> Utf8Name => "name"u8;

    /// <summary>The <c>value</c> property name as a UTF-8 literal.</summary>
    private static ReadOnlySpan<byte> Utf8Value => "value"u8;

    /// <summary>The <c>score</c> property name as a UTF-8 literal.</summary>
    private static ReadOnlySpan<byte> Utf8Score => "score"u8;

    /// <summary>
    /// Serializes one flat record to UTF-8 JSON bytes.
    /// </summary>
    /// <param name="name">The record name; encoded as a JSON string.</param>
    /// <param name="value">The record's integer value.</param>
    /// <param name="score">The record's floating-point score.</param>
    /// <returns>A new <see cref="byte"/> array holding the compact UTF-8 JSON object.</returns>
    /// <remarks>
    /// <para>
    /// The writer is pointed at a <see cref="PooledBufferWriter"/> — an <see cref="IBufferWriter{T}"/>
    /// over an <see cref="ArrayPool{T}"/> lease — so the growth strategy is "rent a bigger array and
    /// return the old one to the pool", not "grow a linked list of char buffers and then call
    /// <c>ToString()</c>", which is how this looks in a Java <c>StringBuilder</c> pipeline. The only
    /// managed object the caller pays for is the returned array.
    /// </para>
    /// <para>
    /// The name is written via the <c>ReadOnlySpan&lt;char&gt;</c> overload, which transcodes
    /// UTF-16 → UTF-8 inside the writer, so no <c>byte[]</c> and no copy of the name ever exists
    /// before it lands in the JSON stream.
    /// </para>
    /// </remarks>
    public static byte[] Serialize(string name, int value, double score)
    {
        ArgumentNullException.ThrowIfNull(name);

        var options = new JsonWriterOptions
        {
            // Compact output, and a relaxed encoder so non-ASCII scalars stay as raw UTF-8 bytes
            // rather than being escaped to \uXXXX. Not indented: indentation is whitespace that a
            // consumer has to skip, this is a wire format, not a log pretty-printer.
            Indented = false,
            SkipValidation = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using PooledBufferWriter buffer = new(initialCapacity: 256);
        using (Utf8JsonWriter writer = new(buffer, options))
        {
            writer.WriteStartObject();
            writer.WriteString(Utf8Name, name.AsSpan());
            writer.WriteNumber(Utf8Value, value);
            writer.WriteNumber(Utf8Score, score);
            writer.WriteEndObject();
        }

        // Flushed by the writer's Dispose above; copy out the exact length and give the lease back.
        return buffer.ToArray();
    }

    /// <summary>
    /// Deserializes one flat record from UTF-8 JSON bytes.
    /// </summary>
    /// <param name="utf8">The UTF-8 JSON object, normally the output of <see cref="Serialize"/>.</param>
    /// <returns>The record's <c>name</c>, <c>value</c> and <c>score</c>.</returns>
    /// <exception cref="JsonException">
    /// The payload is malformed, or one of the three required properties is missing.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <see cref="Utf8JsonReader"/> is a <c>ref struct</c>: it lives on the stack, it borrows the
    /// caller's span instead of copying it, and the compiler will not let it escape. Property names
    /// are matched with <c>SequenceEqual</c> against <c>u8</c> literals — a byte compare over the
    /// input buffer itself, so no property name string is ever allocated. Numbers are read as
    /// primitives (<see cref="Utf8JsonReader.GetInt32"/> / <see cref="Utf8JsonReader.GetDouble"/>)
    /// with no <c>double.Parse</c> on an intermediate <see cref="string"/>.
    /// </para>
    /// <para>
    /// The single allocation on this path is the returned <c>Name</c> string, which is unavoidable:
    /// the tuple's type says <see cref="string"/>, and a <see cref="string"/> is a heap object. A
    /// caller that can consume a <c>ReadOnlySpan&lt;char&gt;</c> could keep even that one — see
    /// <see cref="PeekScore"/> for a shape where nothing is materialised at all.
    /// </para>
    /// </remarks>
    public static (string Name, int Value, double Score) Deserialize(ReadOnlySpan<byte> utf8)
    {
        Utf8JsonReader reader = new(utf8);

        string? name = null;
        int value = 0;
        double score = 0.0;
        bool haveName = false;
        bool haveValue = false;
        bool haveScore = false;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            // ValueSpan for a PropertyName token is the raw UTF-8 bytes of the name, pointing into
            // the caller's buffer. Compare bytes; never decode into a string to compare.
            ReadOnlySpan<byte> property = reader.ValueSpan;

            if (property.SequenceEqual(Utf8Name))
            {
                if (!reader.Read())
                {
                    throw new JsonException("Truncated JSON: 'name' has no value.");
                }

                name = reader.GetString();
                haveName = name is not null;
            }
            else if (property.SequenceEqual(Utf8Value))
            {
                if (!reader.Read())
                {
                    throw new JsonException("Truncated JSON: 'value' has no value.");
                }

                value = reader.GetInt32();
                haveValue = true;
            }
            else if (property.SequenceEqual(Utf8Score))
            {
                if (!reader.Read())
                {
                    throw new JsonException("Truncated JSON: 'score' has no value.");
                }

                score = reader.GetDouble();
                haveScore = true;
            }
            else
            {
                // Unknown members are skipped in place: no block is materialised, no string parsed.
                reader.Skip();
            }
        }

        if (!haveName || !haveValue || !haveScore)
        {
            throw new JsonException("JSON payload is missing one of 'name', 'value', 'score'.");
        }

        return (name!, value, score);
    }

    /// <summary>
    /// Reads only the <c>score</c> member, without materialising the record — the zero-string-lookup
    /// shape a hot path would use.
    /// </summary>
    /// <param name="utf8">The UTF-8 JSON object.</param>
    /// <returns>The <c>score</c> value.</returns>
    /// <exception cref="JsonException">
    /// The payload is malformed, or it has no numeric <c>score</c> member.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <see cref="JsonDocument"/> views the caller's memory rather than copying it, and
    /// <see cref="JsonElement.TryGetProperty(ReadOnlySpan{byte}, out JsonElement)"/> looks the
    /// member up by UTF-8 bytes, so the property name is never decoded. It is the dynamic,
    /// reflection-free counterpart of Rust's <c>serde_json::Value</c> tree — except that here the
    /// tree is a view over the original bytes, not an owned `Map&lt;String, Value&gt;` rebuilt
    /// from them.
    /// </para>
    /// </remarks>
    internal static double PeekScore(ReadOnlyMemory<byte> utf8)
    {
        using JsonDocument document = JsonDocument.Parse(utf8);

        if (!document.RootElement.TryGetProperty(Utf8Score, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException("JSON payload has no numeric 'score' member.");
        }

        return element.GetDouble();
    }
}

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over an <see cref="ArrayPool{T}"/> lease: the growth strategy
/// <see cref="Utf8JsonWriter"/> needs in order to write without owning memory.
/// </summary>
/// <remarks>
/// <para>
/// This little class is where the "one writer, three destinations" claim is cashed in. The same
/// <see cref="Utf8JsonWriter"/> that writes into this pooled writer writes byte-for-byte identically
/// into a <c>stackalloc</c> region (via a stack-backed <see cref="IBufferWriter{T}"/>) or into a
/// socket, because the writer only ever sees spans and an <c>Advance</c> count. Java's equivalent
/// shape, <c>OutputStream</c>, is a virtual call per write with a mutable <c>byte[]</c> and an
/// object identity that cannot be a stack local.
/// </para>
/// <para>
/// Growth doubles by renting a larger array and copying once, which is amortised O(n) and — more
/// importantly here — does <b>not</b> allocate a fresh <see cref="byte"/> array per write the way a
/// naively doubled <c>List&lt;byte&gt;</c> or a Java <c>ByteArrayOutputStream</c> does. The lease is
/// returned in <see cref="Dispose"/>, so a steady-state server pays for the buffer once.
/// </para>
/// </remarks>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledBufferWriter"/> class.
    /// </summary>
    /// <param name="initialCapacity">Requested starting capacity; the pool may return more.</param>
    public PooledBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Gets the number of bytes written so far.</summary>
    public int WrittenCount => _written;

    /// <summary>Gets a view over the bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Advance past the end of the written region.");
        }

        _written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>
    /// Copies the written bytes into a right-sized array.
    /// </summary>
    /// <returns>A new array of exactly <see cref="WrittenCount"/> bytes.</returns>
    public byte[] ToArray()
    {
        return WrittenSpan.ToArray();
    }

    /// <summary>Returns the rented buffer to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        byte[] buffer = _buffer;
        _buffer = [];
        _written = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>Rents a larger buffer and copies the written bytes across.</summary>
    /// <param name="sizeHint">The minimum number of writable bytes the caller needs.</param>
    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= 0)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        int required = _written + sizeHint;
        int next = _buffer.Length * 2;
        while (next < required)
        {
            next *= 2;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(next);
        WrittenSpan.CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
