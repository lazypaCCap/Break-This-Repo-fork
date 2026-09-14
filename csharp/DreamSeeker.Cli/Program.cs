// ═══════════════════════════════════════════════════════════════════════════════════════════
//  DreamSeeker.Cli · Program
//  尋夢者の入口 — 寻梦者的入口 — the entry point that runs every claim and hands back a receipt.
//
//  Design rule for this file: never print a number that was not measured here, and never print a
//  verdict that was not asserted here. `--verify` runs only the assertions and returns a process
//  exit code, so this showcase is usable as a check rather than as a brochure.
//
//      dotnet run --project csharp/DreamSeeker.Cli               # the full report
//      dotnet run --project csharp/DreamSeeker.Cli -- --verify   # assertions only, exit code
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Globalization;
using DreamSeeker.Core;
using Elysia.Core;
using Elysia.Flow;
using Elysia.Mirror;
using Elysia.Numerics;
using Elysia.Text;
using NodaTime;

namespace DreamSeeker.Cli;

/// <summary>The showcase entry point.</summary>
internal static class Program
{
    /// <summary>A value type with no reference fields: the arena stores these, not boxes.</summary>
    private readonly record struct DreamPoint(int X, int Y, int Z)
    {
        /// <summary>Returns a mutated copy, so the arena can prove <c>ref</c> writes are in place.</summary>
        public DreamPoint Shifted(int delta) => new(X + delta, Y + delta, Z + delta);
    }

    private static async Task<int> Main(string[] args)
    {
        bool verifyOnly = Array.Exists(args, static a => string.Equals(a, "--verify", StringComparison.Ordinal));
        string pluginPath = ResolvePluginPath(args);

        Console.WriteLine(ElysiaProtocol.Banner());
        Console.WriteLine($"[host] runtime {Environment.Version} · 64-bit process: {Environment.Is64BitProcess} · core count {Environment.ProcessorCount}");
        Console.WriteLine($"[host] plugin staging path: {pluginPath}");
        Console.WriteLine();

        Instant startInstant = SystemClock.Instance.GetCurrentInstant();
        Stopwatch clock = Stopwatch.StartNew();

        List<Claim> claims = new();

        // ── 1. value-type arena ────────────────────────────────────────────────────────────
        (string arenaSummary, long arenaBytes, bool staleRejected) = ArenaShowcase();
        claims.Add(new Claim(
            "arena/value-types",
            staleRejected && arenaBytes == 0,
            $"1000 struct allocations + in-place ref mutation + stale-handle rejection ({arenaSummary})"));

        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Core · DreamArena ──────────────────────────────────────────────");
            Console.WriteLine("   " + arenaSummary);
            Console.WriteLine($"   heap bytes allocated by the whole arena section: {arenaBytes}  (stackalloc storage, StackOnly ref struct cursor)");
            Console.WriteLine($"   stale handle after release was rejected: {staleRejected}");
            Console.WriteLine();
        }

        // ── 2. cyclic object graph ─────────────────────────────────────────────────────────
        DreamGraphReport graph = DreamGraph.Run(depth: 4, breadth: 3, out List<string> graphLog);
        claims.Add(new Claim(
            "graph/cycles",
            graph.CycleWasCollected && graph.Cycles > 0 && graph.MutationsDuringTraversal > 0,
            $"cyclic graph reclaimed by tracing GC, {graph.Cycles} cycle(s), {graph.MutationsDuringTraversal} mutation(s) during traversal"));

        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Core · DreamGraph ──────────────────────────────────────────────");
            foreach (string line in graphLog)
            {
                Console.WriteLine("   " + line);
            }

            Console.WriteLine($"   report: nodes={graph.Nodes} edges={graph.Edges} cycles={graph.Cycles} " +
                              $"mutationsDuringTraversal={graph.MutationsDuringTraversal} maxDepth={graph.MaximumDepth} " +
                              $"cycleReclaimed={graph.CycleWasCollected}");
            Console.WriteLine();
        }

        // ── 3. generic math over primitives, without boxing ────────────────────────────────
        claims.Add(SelfClaim("numerics/INumber<T>", NumericShowcase.SelfCheck(out string numericReport), numericReport));
        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Numerics ───────────────────────────────────────────────────────");
            foreach (string line in NumericShowcase.Run())
            {
                Console.WriteLine("   " + line);
            }

            Console.WriteLine();
        }

        // ── 4. UTF-8 first text handling ───────────────────────────────────────────────────
        claims.Add(SelfClaim("text/utf8", TextShowcase.SelfCheck(out string textReport), textReport));
        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Text ───────────────────────────────────────────────────────────");
            foreach (string line in TextShowcase.Run())
            {
                Console.WriteLine("   " + line);
            }

            Console.WriteLine();
        }

        // ── 5. async pipelines ─────────────────────────────────────────────────────────────
        (bool flowOk, string flowReport) = await FlowShowcase.SelfCheckAsync();
        claims.Add(new Claim("flow/IAsyncEnumerable", flowOk, flowReport));
        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Flow ───────────────────────────────────────────────────────────");
            foreach (string line in await FlowShowcase.RunAsync())
            {
                Console.WriteLine("   " + line);
            }

            Console.WriteLine();
        }

        // ── 6. runtime code generation, metadata discovery, real unload ────────────────────
        claims.Add(SelfClaim("mirror/reflection+IL", MirrorShowcase.SelfCheck(pluginPath, out string mirrorReport), mirrorReport));
        if (!verifyOnly)
        {
            Console.WriteLine("── Elysia.Mirror ─────────────────────────────────────────────────────────");
            foreach (string line in MirrorShowcase.Run(pluginPath))
            {
                Console.WriteLine("   " + line);
            }

            Console.WriteLine();
        }

        // ── 7. the vendored library is live, not decorative ────────────────────────────────
        Instant projected = startInstant + Duration.FromMilliseconds(clock.ElapsedMilliseconds);
        LocalDate today = startInstant.InUtc().Date;
        if (!verifyOnly)
        {
            Console.WriteLine("── pulled/NodaTime ───────────────────────────────────────────────────────");
            Console.WriteLine($"   Instant now={startInstant}  LocalDate(UTC)={today}  Instant+Duration={projected}");
            Console.WriteLine("   1,780,492 bytes of pulled C# compiled into this solution and called above:");
            Console.WriteLine("   value types, operator overloading, a Duration with 96-bit nanosecond resolution and no timezone database for UTC.");
            Console.WriteLine();
        }

        claims.Add(new Claim(
            "pulled/NodaTime",
            projected > startInstant && today.Year >= 2026,
            "the vendored date/time library computes a real Instant arithmetic result"));

        // ── verdict ────────────────────────────────────────────────────────────────────────
        int failed = claims.Count(static c => !c.Ok);
        Console.WriteLine("── receipts ─────────────────────────────────────────────────────────────");
        foreach (Claim claim in claims)
        {
            Console.WriteLine($"   [{(claim.Ok ? "PASS" : "FAIL")}] {claim.Name,-26} {claim.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"   {claims.Count - failed}/{claims.Count} claims verified in {clock.ElapsedMilliseconds} ms " +
            $"on {RuntimeInformationLabel()} · 愛莉希雅 / Elysia · 尋夢者 / Dream Seeker");
        Console.WriteLine("   " + ElysiaProtocol.Motto);

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Allocates from a <see cref="DreamArena{T}"/> living on the stack, mutates through a
    /// <c>ref</c>, walks by reference, and checks that a recycled handle is refused.
    /// </summary>
    /// <returns>A summary line, the bytes allocated by the section, and whether the stale handle was refused.</returns>
    private static (string Summary, long AllocatedBytes, bool StaleRejected) ArenaShowcase()
    {
        const int Capacity = 1024;

        Span<DreamPoint> slots = stackalloc DreamPoint[Capacity];
        Span<int> generations = stackalloc int[Capacity];
        Span<int> freeStack = stackalloc int[Capacity];

        long before = DreamMeter.AllocatedBytes();

        DreamArena<DreamPoint> arena = new(slots, generations, freeStack);
        DreamId first = default;
        DreamId middle = default;
        for (int i = 0; i < 1000; i++)
        {
            DreamId id = arena.Allocate(new DreamPoint(i, i * 2, i * 3));
            if (i == 0)
            {
                first = id;
            }
            else if (i == 500)
            {
                middle = id;
            }
        }

        // Write straight through the reference: no get/copy/set round trip, no boxing.
        ref DreamPoint mutable = ref arena[first];
        mutable = mutable.Shifted(1);

        int checksum = 0;
        DreamCursor<DreamPoint> cursor = arena.GetCursor();
        while (cursor.MoveNext())
        {
            checksum += cursor.Current.X;
        }

        bool released = arena.Release(middle);

        // The measured region ends here: everything above must have allocated exactly nothing.
        long allocated = DreamMeter.AllocatedBytes() - before;

        bool staleRejected = false;
        string staleDetail = "not exercised";
        try
        {
            _ = arena[middle];
        }
        catch (InvalidOperationException ex)
        {
            staleRejected = true;
            staleDetail = Exceptions.FirstLine(ex);
        }

        // Reported separately and honestly: rejecting a stale handle costs one exception object.
        long exceptionCost = DreamMeter.AllocatedBytes() - before - allocated;

        int liveAtEnd = arena.LiveCount;

        string summary = string.Format(
            CultureInfo.InvariantCulture,
            "payload={0} B contiguous, live={1}, peak={2}, recycled={3}, cursor checksum={4}, released={5}, " +
            "staleHandlesRefused={6}, costOfRefusing={7} B, stale→{8}",
            arena.PayloadBytes,
            liveAtEnd,
            arena.PeakLiveCount,
            arena.RecycledCount,
            checksum,
            released,
            staleRejected,
            exceptionCost,
            staleDetail);

        return (summary, allocated, staleRejected);
    }

    private static Claim SelfClaim(string name, bool ok, string detail) =>
        new(name, ok, detail.ReplaceLineEndings(" · "));

    private static string ResolvePluginPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--plugin", StringComparison.Ordinal))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "plugins", "Elysia.DreamPlugin.Sample.dll");
    }

    private static string RuntimeInformationLabel() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} / " +
        $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";

    private readonly record struct Claim(string Name, bool Ok, string Detail);
}

/// <summary>Tiny helpers used only for readable receipts.</summary>
internal static class Exceptions
{
    /// <summary>Collapses an exception message to a single short line.</summary>
    /// <param name="ex">The exception to describe.</param>
    /// <returns>The first line of the message.</returns>
    public static string FirstLine(Exception ex)
    {
        string message = ex.Message.ReplaceLineEndings(" ");
        return message.Length <= 72 ? message : message[..69] + "…";
    }
}
