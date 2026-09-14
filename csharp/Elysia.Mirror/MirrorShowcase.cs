// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Mirror · MirrorShowcase
//  鏡 — the orchestration of everything in this project, and its own receipt.
//
//  Nothing here is asserted in prose. Each claim is executed, measured, and printed, and the
//  same claims are re-checked as assertions by SelfCheck so the CLI can exit non-zero when a
//  claim stops being true on some other machine.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using System.Reflection;
using DreamSeeker.Core;

namespace Elysia.Mirror;

/// <summary>Runs the runtime-codegen demonstration and reports what actually happened.</summary>
public static class MirrorShowcase
{
    /// <summary>Names the steps this showcase performs, in order.</summary>
    public static readonly string[] Steps =
    {
        "scan the shared kernel for [DreamPlugin] metadata",
        "rank the discovered plugins over a fixed workload",
        "emit a brand-new plugin type as IL, call it, then retire it",
        "prove the generated code module was reclaimed (WeakReference + forced GC)",
        "load a compiled plugin from disk into a collectible context, then unload it",
        "duel reflection invoke vs bound delegate vs compiled expression tree",
    };

    /// <summary>Runs every step.</summary>
    /// <param name="diskPluginPath">
    /// Path to a compiled plugin assembly staged by the build, or <see langword="null"/> to skip
    /// the disk-loading step. When the path is missing the step is reported as skipped rather than
    /// silently disappearing — a skipped claim must be visible.
    /// </param>
    /// <returns>One human-readable line per observation.</returns>
    public static IReadOnlyList<string> Run(string? diskPluginPath = null)
    {
        List<string> lines = new(24);
        CultureInfo culture = CultureInfo.InvariantCulture;

        // ── 1 & 2: metadata discovery over the assembly that comes from the submodule ──────
        Assembly kernel = typeof(IDreamPlugin).Assembly;
        IReadOnlyList<(Type Type, IDreamPlugin Plugin, int Weight)> plugins =
            PluginCatalog.Discover(kernel, typeof(MirrorShowcase).Assembly);

        lines.Add($"discovery: scanned '{kernel.GetName().Name}' and found {plugins.Count} plugin(s) via [DreamPlugin]");
        foreach ((Type type, IDreamPlugin _, int weight) in plugins)
        {
            lines.Add($"  · {type.Assembly.GetName().Name}::{type.Name} weight={weight} (declared in compiled metadata)");
        }

        int[] workload = new int[16];
        for (int i = 0; i < workload.Length; i++)
        {
            workload[i] = i * 3;
        }

        foreach ((string name, int weight, long total, double mean) in PluginCatalog.Rank(plugins, workload))
        {
            lines.Add($"  · ranked {name,-24} weight={weight} workload.sum={total} workload.mean={mean.ToString("F4", culture)}");
        }

        // ── 3 & 4: a type that did not exist when the process started ──────────────────────
        DreamEmitReceipt emitted = DreamForge.EmitUseAndRetire(
            name: "elysia(runtime-emitted)",
            motto: "その場で生まれ、その場で消える。 — 生于此刻，逝于此刻。",
            multiplier: 5,
            offset: 11);

        lines.Add(
            $"emit: type generated at run time → Name='{emitted.Name}' Transform(21)={emitted.Sample} " +
            $"Score(3.5)={emitted.Score.ToString("F2", culture)}");
        lines.Add($"emit: motto carried inside the generated IL → {emitted.Motto}");
        lines.Add(
            emitted.Collected
                ? $"emit: generated code module reclaimed after {emitted.CollectionAttempts} forced collection(s) — VERIFIED"
                : $"emit: generated code module still alive after {emitted.CollectionAttempts} forced collection(s) — NOT reclaimed");

        // ── 5: a compiled plugin assembly, loaded and unloaded on demand ───────────────────
        DreamDiskReceipt disk = DreamForge.LoadUseAndUnload(diskPluginPath ?? string.Empty);
        if (disk.Message.StartsWith("not staged", StringComparison.Ordinal))
        {
            lines.Add($"disk: {disk.Message}");
        }
        else
        {
            lines.Add(
                $"disk: loaded '{disk.Name}' from {Path.GetFileName(diskPluginPath!)} → Transform(21)={disk.Sample} " +
                $"Score(0.25)={disk.Score.ToString("F2", culture)}");
            lines.Add($"disk: {disk.Message}");
        }

        // ── 6: reflection, delegate and generated code, measured on the same method ────────
        IDreamPlugin subject = new ReferenceDreamPlugin();
        DreamDuel duel = Weaver.Duel(subject, iterations: 200_000);

        lines.Add($"duel over {duel.Iterations:N0} calls to IDreamPlugin.Transform:");
        lines.Add($"  · MethodInfo.Invoke        {duel.InvokeNsPerOp,8:F1} ns/op  {duel.InvokeBytesPerOp,7:F2} B/op");
        lines.Add($"  · CreateDelegate<Func<>>   {duel.DelegateNsPerOp,8:F1} ns/op  {duel.DelegateBytesPerOp,7:F2} B/op");
        lines.Add($"  · Expression.Compile       {duel.ExpressionNsPerOp,8:F1} ns/op  {duel.ExpressionBytesPerOp,7:F2} B/op");

        double speedup = duel.InvokeNsPerOp / Math.Max(duel.DelegateNsPerOp, 0.0001);
        lines.Add(
            $"duel: the bound delegate is {speedup.ToString("F1", culture)}× the naive call and allocates " +
            $"{duel.InvokeBytesPerOp.ToString("F1", culture)} B/op less");

        (Func<int, int> compiled, string shape) = Weaver.CompileArithmetic(multiplier: 7, offset: 3, mask: 0xFF);
        lines.Add($"expression: built from data → {shape} → f(10)={compiled(10)}");

        return lines;
    }

    /// <summary>Asserts the claims the showcase makes. Every assertion is executed.</summary>
    /// <param name="diskPluginPath">Path to the staged plugin assembly, or <see langword="null"/>.</param>
    /// <param name="report">A one-line summary of what was verified.</param>
    /// <returns><see langword="true"/> when every claim held.</returns>
    public static bool SelfCheck(string? diskPluginPath, out string report)
    {
        List<string> failures = new();

        Assembly kernel = typeof(IDreamPlugin).Assembly;
        IReadOnlyList<(Type Type, IDreamPlugin Plugin, int Weight)> plugins =
            PluginCatalog.Discover(kernel, typeof(MirrorShowcase).Assembly);
        if (plugins.Count < 1)
        {
            failures.Add("metadata discovery found no plugin in the submodule kernel");
        }

        IDreamPlugin reference = new ReferenceDreamPlugin();
        if (reference.Transform(4) != (4 * 3) + 7)
        {
            failures.Add("the reference plugin's arithmetic does not match its source");
        }

        DreamEmitReceipt emitted = DreamForge.EmitUseAndRetire("check", "check", multiplier: 5, offset: 11);
        if (emitted.Sample != (21 * 5) + 11)
        {
            failures.Add($"generated IL computed {emitted.Sample}, expected {(21 * 5) + 11}");
        }

        if (Math.Abs(emitted.Score - 1.0) > 1e-12)
        {
            failures.Add($"generated Score(3.5) clamped to {emitted.Score}, expected 1.0");
        }

        if (!emitted.Collected)
        {
            failures.Add("the run-and-collect generated assembly was not reclaimed");
        }

        if (plugins.Count > 0)
        {
            IReadOnlyList<(string Name, int Weight, long Total, double Mean)> ranked =
                PluginCatalog.Rank(plugins, new[] { 1, 2, 3 });
            if (ranked.Count != plugins.Count)
            {
                failures.Add("ranking dropped plugins");
            }
        }

        if (!string.IsNullOrEmpty(diskPluginPath) && File.Exists(diskPluginPath))
        {
            DreamDiskReceipt disk = DreamForge.LoadUseAndUnload(diskPluginPath);
            if (!disk.Unloaded)
            {
                failures.Add("the collectible plugin context was not reclaimed");
            }

            if (disk.Sample == 0)
            {
                failures.Add("the disk-loaded plugin returned a zero transform");
            }
        }

        DreamDuel duel = Weaver.Duel(reference, iterations: 50_000);
        if (duel.DelegateBytesPerOp > 0.001)
        {
            failures.Add($"a bound delegate allocated {duel.DelegateBytesPerOp:F3} B/op, expected 0");
        }

        if (duel.InvokeBytesPerOp <= 0)
        {
            failures.Add("naive reflection allocated nothing, which would mean it was optimised away");
        }

        (Func<int, int> compiled, _) = Weaver.CompileArithmetic(multiplier: 7, offset: 3, mask: 0xFF);
        if (compiled(10) != (((10 * 7) + 3) & 0xFF))
        {
            failures.Add("the compiled expression tree evaluated incorrectly");
        }

        report = failures.Count == 0
            ? $"mirror: {plugins.Count} discovered by metadata, emitted IL verified, code module reclaimed after {emitted.CollectionAttempts} collection(s), delegate {duel.DelegateNsPerOp:F1} ns/op vs invoke {duel.InvokeNsPerOp:F1} ns/op"
            : "mirror: FAILED — " + string.Join("; ", failures);

        return failures.Count == 0;
    }
}
