// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Mirror · Weaver
//  式を織る — 编织表达式 — turning reflection into machine code you can call like a method.
//
//  Java's reflective call is `Method.invoke(target, args)`: it takes an `Object[]` you must
//  allocate, it boxes every argument and every return value, and it throws
//  `InvocationTargetException` wrapping whatever the callee threw. The JVM has `invokedynamic`
//  and `MethodHandle` as the escape hatch, and they work — they are simply a second language
//  that lives beside the first one.
//
//  Rust cannot do this at all. There is no reflection to compile away; the closest analogue is
//  a proc macro over the source, which by definition runs before your program exists.
//
//  C# closes the loop in one line that stays *inside* the type system:
//      MethodInfo.CreateDelegate<Func<int,int>>(target)   // no boxing, no object[]
//      Expression.Lambda<Func<int,int>>(body, x).Compile() // build new code from a data structure
//  Both hand back an ordinary delegate, callable at ordinary call speed, and the compiler still
//  checked it. Below, all three tiers are measured on the same method, on this machine, now.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DreamSeeker.Core;

namespace Elysia.Mirror;

/// <summary>The measured cost of the three ways to call one method.</summary>
/// <param name="Iterations">How many calls each tier performed.</param>
/// <param name="InvokeNsPerOp">Nanoseconds per call through <see cref="MethodInfo.Invoke(object, object?[])"/>.</param>
/// <param name="InvokeBytesPerOp">Bytes allocated per call through <see cref="MethodInfo.Invoke(object, object?[])"/>.</param>
/// <param name="DelegateNsPerOp">Nanoseconds per call through a <see cref="MethodInfo.CreateDelegate{T}"/> delegate.</param>
/// <param name="DelegateBytesPerOp">Bytes allocated per call through that delegate.</param>
/// <param name="ExpressionNsPerOp">Nanoseconds per call through a freshly compiled expression tree.</param>
/// <param name="ExpressionBytesPerOp">Bytes allocated per call through the compiled expression tree.</param>
/// <param name="Checksum">Sum of every result, so the JIT cannot delete the work.</param>
public readonly record struct DreamDuel(
    int Iterations,
    double InvokeNsPerOp,
    double InvokeBytesPerOp,
    double DelegateNsPerOp,
    double DelegateBytesPerOp,
    double ExpressionNsPerOp,
    double ExpressionBytesPerOp,
    long Checksum);

/// <summary>Run-time code generation, demonstrated rather than asserted.</summary>
public static class Weaver
{
    /// <summary>Builds a delegate from an expression tree assembled out of data.</summary>
    /// <param name="multiplier">Constant to multiply by.</param>
    /// <param name="offset">Constant to add.</param>
    /// <param name="mask">Bit mask applied last.</param>
    /// <returns>The compiled function and its readable shape.</returns>
    /// <remarks>
    /// The expression tree is a *value*: it can be stored, loaded from configuration, sent over a
    /// wire protocol or produced by a plugin, and it still compiles down to real callable code
    /// that the JIT can inline. Doing this in Java means bytecode generation (ASM/ByteBuddy) or
    /// `MethodHandle` combinators; doing it in Rust means a macro, which means it must be known
    /// when the compiler runs.
    /// </remarks>
    public static (Func<int, int> Compiled, string Shape) CompileArithmetic(int multiplier, int offset, int mask)
    {
        ParameterExpression x = Expression.Parameter(typeof(int), "x");
        Expression body = Expression.And(
            Expression.Add(Expression.Multiply(x, Expression.Constant(multiplier)), Expression.Constant(offset)),
            Expression.Constant(mask));

        string shape = body.ToString();
        Func<int, int> compiled = Expression.Lambda<Func<int, int>>(body, x).Compile();
        return (compiled, shape);
    }

    /// <summary>
    /// Calls the same plugin method through reflection, through a bound delegate, and through a
    /// compile-from-data delegate, measuring nanoseconds and allocated bytes per call.
    /// </summary>
    /// <param name="plugin">The plugin whose <see cref="IDreamPlugin.Transform"/> is the subject.</param>
    /// <param name="iterations">Calls per tier.</param>
    /// <returns>The three-way measurement.</returns>
    public static DreamDuel Duel(IDreamPlugin plugin, int iterations)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "must be positive");
        }

        MethodInfo method = typeof(IDreamPlugin).GetMethod(nameof(IDreamPlugin.Transform))!;

        // ── tier 1: the naive reflective call ──────────────────────────────────────────────
        Func<int> invokeLoop = () =>
        {
            long sum = 0;
            object boxedTarget = plugin;
            for (int i = 0; i < iterations; i++)
            {
                sum += (int)method.Invoke(boxedTarget, new object[] { i & 0xFF })!;
            }

            return (int)sum;
        };

        // ── tier 2: the same method, bound to a delegate — no object[], no boxing ──────────
        Func<int, int> bound = method.CreateDelegate<Func<int, int>>(plugin);
        Func<int> boundLoop = () =>
        {
            long sum = 0;
            for (int i = 0; i < iterations; i++)
            {
                sum += bound(i & 0xFF);
            }

            return (int)sum;
        };

        // ── tier 3: code that did not exist a moment ago ──────────────────────────────────
        (Func<int, int> compiled, _) = CompileArithmetic(multiplier: 3, offset: 7, mask: 0xFFFF);
        Func<int> compiledLoop = () =>
        {
            long sum = 0;
            for (int i = 0; i < iterations; i++)
            {
                sum += compiled(i & 0xFF);
            }

            return (int)sum;
        };

        (double invokeNs, double invokeBytes, long checksum) = Measure(invokeLoop, iterations);
        (double boundNs, double boundBytes, _) = Measure(boundLoop, iterations);
        (double compiledNs, double compiledBytes, _) = Measure(compiledLoop, iterations);

        return new DreamDuel(iterations, invokeNs, invokeBytes, boundNs, boundBytes, compiledNs, compiledBytes, checksum);
    }

    /// <summary>Times and allocation-counts a loop, dividing the totals by the iteration count.</summary>
    /// <param name="body">The loop body. Run three times; the fastest run is reported.</param>
    /// <param name="iterations">Number of operations inside the loop.</param>
    /// <returns>Nanoseconds per op, bytes per op and the loop's own result.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double NanosecondsPerOp, double BytesPerOp, long Checksum) Measure(Func<int> body, int iterations)
    {
        double bestNs = double.MaxValue;
        double bytes = 0;
        long checksum = 0;

        for (int round = 0; round < 3; round++)
        {
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            checksum = body();
            long elapsed = Stopwatch.GetTimestamp();
            long afterBytes = GC.GetAllocatedBytesForCurrentThread();

            double ns = (elapsed - start) * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs)
            {
                bestNs = ns;
                bytes = afterBytes - beforeBytes;
            }
        }

        return (bestNs / iterations, bytes / iterations, checksum);
    }
}
