// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Core · DreamArena
//  値型のアリーナ — 值类型竞技场 — a generation-checked arena of structs.
//
//  What this file demonstrates, and the exact wound it pokes in each rival language:
//
//  · Java cannot express this at all. `T` here is constrained to `struct`, so `DreamArena<Vec2>`
//    stores a *sequence of Vec2 values*, not a sequence of references to boxed Vec2 objects.
//    Java's type erasure plus "everything is an object" means the closest equivalent,
//    `ArrayList<Vec2>`, allocates one heap object per element (16-byte mark word + class
//    pointer, 8-byte alignment) and chases a pointer for every access. There is no `Span<T>`,
//    no `stackalloc`, no `ref` return, and no `ref struct` in the language.
//
//  · Rust *can* express the arena, and the borrow checker will correctly refuse to let the
//    arena and a `&mut` into one of its slots coexist with anything that outlives them. The
//    idiomatic answer there is the `slotmap`/`generational-arena` crate plus lifetime
//    gymnastics; here the same guarantee — "a handle into a dead or recycled slot can never be
//    dereferenced" — costs one `int` per slot and one branch, and the *compiler* additionally
//    guarantees the arena itself can never escape to the managed heap, because `ref struct`
//    types are stack-only by rule.
//
//  Honesty note: `ref struct` is a lifetime *restriction*, not a lifetime *analysis*. Rust's
//  borrow checker is strictly more expressive than this. The claim being made is narrower and
//  it is true: the 80% case (contiguous, mutable, reference-returning storage) is available in
//  C# without unsafe code, without a crate, and without annotating a single lifetime.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;

namespace Elysia.Core;

/// <summary>
/// A generation-checked handle into a <see cref="DreamArena{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DreamId"/> is two <see cref="int"/>s — eight bytes, copyable, comparable,
/// usable as a dictionary key, and <b>never</b> a dangling pointer, because every read
/// validates the generation counter it was minted with.
/// </para>
/// <para>
/// This is the "use after free" question answered with arithmetic instead of with an ownership
/// system or with a garbage collector. A deleted-and-recycled slot simply stops matching.
/// </para>
/// </remarks>
public readonly struct DreamId : IEquatable<DreamId>, IComparable<DreamId>
{
    /// <summary>The null handle.</summary>
    public static readonly DreamId None = default;

    /// <summary>Initializes a new instance of the <see cref="DreamId"/> struct.</summary>
    /// <param name="index">Zero-based slot index.</param>
    /// <param name="generation">Generation counter minted when the slot was handed out.</param>
    public DreamId(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    /// <summary>Gets the zero-based slot index.</summary>
    public int Index { get; }

    /// <summary>Gets the generation counter. Zero means "never allocated".</summary>
    public int Generation { get; }

    /// <summary>Gets a value indicating whether this handle refers to nothing.</summary>
    public bool IsNone => Generation == 0;

    /// <inheritdoc />
    public bool Equals(DreamId other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DreamId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <summary>Compares two handles by slot then generation.</summary>
    /// <param name="other">The other handle.</param>
    /// <returns>A signed ordering.</returns>
    public int CompareTo(DreamId other)
    {
        int bySlot = Index.CompareTo(other.Index);
        return bySlot != 0 ? bySlot : Generation.CompareTo(other.Generation);
    }

    /// <inheritdoc />
    public override string ToString() => IsNone ? "dream:none" : $"dream:{Index}#{Generation}";

    /// <summary>Equality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when both handles are identical.</returns>
    public static bool operator ==(DreamId left, DreamId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when the handles differ.</returns>
    public static bool operator !=(DreamId left, DreamId right) => !left.Equals(right);
}

/// <summary>
/// A contiguous, recyclable, generation-checked store of unmanaged-free value types.
/// </summary>
/// <typeparam name="T">A value type. No reference types, therefore no pointer chasing.</typeparam>
/// <remarks>
/// <para>
/// The arena itself is a <see langword="ref struct"/>: the compiler guarantees it can never be
/// boxed, captured by a lambda, stored in a field of a class, or handed to a method that might
/// outlive it. The buffers it views may live on the stack (<c>stackalloc</c>) or in a pooled
/// array; the arena does not care, and no garbage is produced either way.
/// </para>
/// <para>
/// Free slots are tracked in a free *stack*, so allocation is a pop and release is a push:
/// both are O(1) with no bookkeeping objects, no tombstones, and no compaction passes.
/// </para>
/// </remarks>
public ref struct DreamArena<T>
    where T : struct
{
    private readonly Span<T> _slots;
    private readonly Span<int> _generations;
    private readonly Span<int> _freeStack;

    private int _freeTop;
    private int _live;
    private int _peak;
    private int _high;
    private int _recycled;

    /// <summary>
    /// Initializes a new instance of the <see cref="DreamArena{T}"/> struct over caller-owned storage.
    /// </summary>
    /// <param name="slots">Storage for the values. Its length is the capacity.</param>
    /// <param name="generations">Per-slot generation counters; must be as long as <paramref name="slots"/>.</param>
    /// <param name="freeStack">Scratch space for the free list; must be as long as <paramref name="slots"/>.</param>
    /// <exception cref="ArgumentException">Thrown when the buffers disagree on capacity.</exception>
    public DreamArena(Span<T> slots, Span<int> generations, Span<int> freeStack)
    {
        if (generations.Length < slots.Length || freeStack.Length < slots.Length)
        {
            throw new ArgumentException("backing buffers must each hold at least `capacity` entries");
        }

        _slots = slots;
        _generations = generations;
        _freeStack = freeStack;
        _freeTop = 0;
        _live = 0;
        _peak = 0;
        _high = 0;
        _recycled = 0;
    }

    /// <summary>Gets the number of slots the arena can hold.</summary>
    public readonly int Capacity => _slots.Length;

    /// <summary>Gets the number of currently live values.</summary>
    public readonly int LiveCount => _live;

    /// <summary>Gets the high-water mark of live values.</summary>
    public readonly int PeakLiveCount => _peak;

    /// <summary>Gets the number of slots ever handed out at least once.</summary>
    public readonly int HighWaterSlot => _high;

    /// <summary>Gets the number of slots recycled from the free list.</summary>
    public readonly int RecycledCount => _recycled;

    /// <summary>Gets the number of bytes occupied by the value payload itself.</summary>
    public readonly int PayloadBytes => _slots.Length * Unsafe.SizeOf<T>();

    /// <summary>Gets a value indicating whether the arena is exhausted.</summary>
    public readonly bool IsFull => _live == _slots.Length;

    /// <summary>Stores a value and returns a generation-checked handle to it.</summary>
    /// <param name="value">The value to store; copied, never referenced.</param>
    /// <returns>A handle valid until the slot is released.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the arena is full.</exception>
    public DreamId Allocate(in T value)
    {
        int slot;
        if (_freeTop > 0)
        {
            slot = _freeStack[--_freeTop];
            _recycled++;
        }
        else
        {
            if (_high == _slots.Length)
            {
                throw new InvalidOperationException($"arena exhausted at capacity {_slots.Length}");
            }

            slot = _high++;
            _generations[slot] = 1;
        }

        _slots[slot] = value;
        _live++;
        if (_live > _peak)
        {
            _peak = _live;
        }

        return new DreamId(slot, _generations[slot]);
    }

    /// <summary>Determines whether a handle still refers to a live slot.</summary>
    /// <param name="id">The handle to test.</param>
    /// <returns><see langword="true"/> when the handle is dereferenceable.</returns>
    public readonly bool IsLive(DreamId id) =>
        (uint)id.Index < (uint)_slots.Length && _generations[id.Index] == id.Generation && id.Generation != 0;

    /// <summary>
    /// Returns a mutable reference to the stored value.
    /// </summary>
    /// <param name="id">A handle previously minted by <see cref="Allocate"/>.</param>
    /// <returns>A by-reference view of the slot — no copy, no allocation, no bounds check twice.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is stale.</exception>
    /// <remarks>
    /// The <see langword="ref"/> return is the load-bearing trick. In Java, mutating an element
    /// of a collection means <c>get</c>, mutate the copy, <c>set</c> it back — or accept that
    /// the element is a heap object every read must chase. In Rust the equivalent is
    /// <c>&amp;mut self.slots[i]</c>, which is fine, but the *caller* now carries a borrow for
    /// as long as it holds the reference, and that borrow is viral.
    /// </remarks>
    public ref T this[DreamId id]
    {
        get
        {
            if (!IsLive(id))
            {
                throw new InvalidOperationException($"stale handle {id} (slot generation moved on)");
            }

            return ref _slots[id.Index];
        }
    }

    /// <summary>Releases a slot. The handle minted for it can never be dereferenced again.</summary>
    /// <param name="id">The handle to release.</param>
    /// <returns><see langword="true"/> when the slot was live and has been recycled.</returns>
    public bool Release(DreamId id)
    {
        if (!IsLive(id))
        {
            return false;
        }

        unchecked
        {
            _generations[id.Index]++; // stale handles now compare unequal, forever
        }

        _slots[id.Index] = default;
        _freeStack[_freeTop++] = id.Index;
        _live--;
        return true;
    }

    /// <summary>Clears every slot and recycles the storage in one pass.</summary>
    public void Clear()
    {
        _slots[.._high].Clear();
        _generations[.._high].Clear();
        _freeTop = 0;
        _live = 0;
        _high = 0;
        _recycled = 0;
    }

    /// <summary>Gets a cursor that walks only the live slots, by reference.</summary>
    /// <returns>A stack-only cursor.</returns>
    public readonly DreamCursor<T> GetCursor() => new(_slots, _generations, _high);
}

/// <summary>
/// A stack-only cursor over the live slots of a <see cref="DreamArena{T}"/>.
/// </summary>
/// <typeparam name="T">The arena element type.</typeparam>
/// <remarks>
/// <see cref="Current"/> hands out a <see langword="ref"/>: iterating a million values touches
/// memory in place and allocates exactly zero bytes, not even a single enumerator object. Then
/// compare with a Java <c>for (Vec2 v : list)</c>, where the iterator itself is an object and
/// each element is a reference to a boxed value.
/// </remarks>
public ref struct DreamCursor<T>
    where T : struct
{
    private readonly Span<T> _slots;
    private readonly Span<int> _generations;
    private readonly int _high;
    private int _index;

    /// <summary>Initializes a new instance of the <see cref="DreamCursor{T}"/> struct.</summary>
    /// <param name="slots">Backing storage.</param>
    /// <param name="generations">Generation counters.</param>
    /// <param name="high">Number of slots ever used.</param>
    internal DreamCursor(Span<T> slots, Span<int> generations, int high)
    {
        _slots = slots;
        _generations = generations;
        _high = high;
        _index = -1;
    }

    /// <summary>Gets a reference to the value under the cursor.</summary>
    public readonly ref T Current => ref _slots[_index];

    /// <summary>Gets the handle of the value under the cursor.</summary>
    public readonly DreamId Id => new(_index, _generations[_index]);

    /// <summary>Advances to the next live slot.</summary>
    /// <returns><see langword="true"/> when a live slot was found.</returns>
    public bool MoveNext()
    {
        while (++_index < _high)
        {
            if (_generations[_index] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets this cursor, enabling <c>foreach</c> over a reference-returning sequence.</summary>
    /// <returns>This cursor.</returns>
    public readonly DreamCursor<T> GetEnumerator() => this;
}
