// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.DreamPlugin.Sample · KaleidoscopePlugin
//  万華鏡 — 万花筒 — a plugin compiled separately, staged beside the host, loaded by path at run
//  time into a collectible AssemblyLoadContext, called once, and then thrown away.
//
//  The equivalent in Rust is a cdylib plus a hand-written C ABI plus `libloading` plus a promise
//  that nobody kept a function pointer, and even then the mapping is not reliably unmapped. The
//  equivalent in Java is a JAR plus a URLClassLoader plus a leak detector, because the JVM will
//  quietly retain the classloader through some thread-local or static field and your "hot reload"
//  becomes a memory leak with a nicer name.
//
//  Here it is nine lines of ordinary C# that a human could have written at any time, and the
//  host can prove the context died by watching a WeakReference.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using DreamSeeker.Core;

namespace Elysia.DreamPlugin.Sample;

/// <summary>A plugin that folds a value through a small kaleidoscope of rotations.</summary>
/// <remarks>
/// Discovered twice over: once as metadata (the <see cref="DreamPluginAttribute"/>) when the host
/// scans a loaded assembly, and once as a file when the host loads it from disk by path.
/// </remarks>
[DreamPlugin(weight: 5)]
public sealed class KaleidoscopePlugin : IDreamPlugin
{
    /// <inheritdoc />
    public string Name => "kaleidoscope(staged)";

    /// <inheritdoc />
    public string Motto => "同じ光でも、角度が変われば別の夢になる。 — 同一束光，换个角度就是另一个梦。";

    /// <inheritdoc />
    public int Transform(int value) => unchecked((value * 31) ^ (value >> 3));

    /// <inheritdoc />
    public double Score(double value) => Math.Clamp(1.0 - Math.Abs(value - 0.5) * 2.0, 0.0, 1.0);
}
