// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Mirror · PluginHost
//  鏡の国のプラグイン — 可卸载插件宿主 — invent a type at run time, use it, then throw it away.
//
//  This file is aimed squarely at Rust's softest spot, and it is not a micro-benchmark: it is a
//  capability that has no equivalent there at all.
//
//  · Rust has no reflection. `#[derive]` is a macro that runs before your program exists; there
//    is no way to enumerate the types inside a linked artifact at run time, no way to look a
//    type up by a string, and no way to *invent* a type that the compiler never saw. Plugin
//    systems in Rust are cdylib + a hand-written C ABI (libloading, abi_stable, stabby, …) and
//    a loaded cdylib generally cannot be unmapped safely once one `extern "C"` pointer escaped.
//  · Java has reflection and can *almost* do this, and the gap is documented in every JVM
//    framework's leak detector: a classloader is only unloadable while nothing — not a
//    thread-local, not a static field, not a `MethodHandle`, not a JDK cache — still points into
//    it. Also: `Method.invoke` boxes every argument and return value, and generic type
//    information is erased, so plugin authors write casts and take ClassCastExceptions at run
//    time that C# would have rejected at compile time.
//  · C# composes the whole stack instead of approximating it:
//      · `System.Reflection.Emit` invents the type;
//      · `AssemblyBuilderAccess.RunAndCollect` marks the *assembly itself* collectible;
//      · `AssemblyLoadContext(isCollectible: true)` owns a plugin loaded from disk and can be
//        unloaded on demand;
//      · `GC.Collect` plus `WeakReference.IsAlive` are the receipt — the same runtime that
//        traces your object graph is the one that can prove a code module became unreachable.
//
//  Both mechanisms are below, and both are verified by the caller every single run.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DreamSeeker.Core;

namespace Elysia.Mirror;

/// <summary>
/// A collectible load context for dream plugins read from disk.
/// </summary>
/// <remarks>
/// <para>
/// The context owns exactly one kind of artifact: assemblies that were not part of the host's
/// compile-time closure. Everything in it is disposable — including the context.
/// </para>
/// <para>
/// <see cref="Load"/> deliberately returns <see langword="null"/> for everything. That single
/// line is the whole trick: <i>"I do not have it, ask the default context."</i> It keeps the
/// plugin <b>contract</b> (<c>DreamSeeker.Core</c>, reached through the repository's
/// <c>elysia</c> symlink) loadable from the shared context, so a plugin on the far side of this
/// boundary shares the exact same <see cref="IDreamPlugin"/> type identity. Java approximates
/// the same discipline with parent-first delegation rules; Rust cannot express it at all and
/// pays for it with a C ABI.
/// </para>
/// </remarks>
public sealed class DreamPluginHost : AssemblyLoadContext
{
    /// <summary>Initializes a new instance of the <see cref="DreamPluginHost"/> class.</summary>
    /// <param name="name">Diagnostic name of the context.</param>
    public DreamPluginHost(string name = "elysia.dream-plugins")
        : base(name, isCollectible: true)
    {
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Defer every resolution to the default context. Returning a locally-loaded copy here
        // would break type identity with the plugin contract, which is the single most common
        // bug in hand-rolled plugin hosts.
        return null;
    }

    /// <summary>Reads a plugin assembly from disk and finds the first <see cref="IDreamPlugin"/> in it.</summary>
    /// <param name="pluginPath">Path to the plugin assembly.</param>
    /// <returns>The instantiated plugin.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the assembly contains no usable plugin.</exception>
    public IDreamPlugin LoadPluginFromDisk(string pluginPath)
    {
        if (!File.Exists(pluginPath))
        {
            throw new FileNotFoundException("plugin assembly not found", pluginPath);
        }

        Assembly assembly = LoadFromAssemblyPath(Path.GetFullPath(pluginPath));
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IDreamPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is not null &&
                Activator.CreateInstance(type) is IDreamPlugin plugin)
            {
                return plugin;
            }
        }

        throw new InvalidOperationException($"no IDreamPlugin implementation found in {pluginPath}");
    }
}

/// <summary>Emitted-IL plugin generation.</summary>
/// <remarks>
/// The dynamic assembly is created with <see cref="AssemblyBuilderAccess.RunAndCollect"/>, which
/// is the runtime's promise that this code module may be reclaimed once nothing references it.
/// On .NET 8 the emit API has no overload that accepts a load context, so collectibility for
/// generated code is requested per-assembly here, while downloaded/plugin code uses the
/// collectible <see cref="DreamPluginHost"/> above. Two mechanisms, one idea: a code module is
/// just another object, and objects are collectable.
/// </remarks>
public static class DreamForge
{
    /// <summary>Emits an assembly containing one plugin type.</summary>
    /// <param name="pluginName">Name reported by the plugin.</param>
    /// <param name="motto">Motto reported by the plugin.</param>
    /// <param name="multiplier">Constant baked into the generated arithmetic.</param>
    /// <param name="offset">Constant added after multiplication.</param>
    /// <returns>The generated type, ready to instantiate.</returns>
    public static Type EmitPluginType(string pluginName, string motto, int multiplier, int offset)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"elysia.dream.plugin.{Math.Abs(pluginName.GetHashCode()):x8}"),
            AssemblyBuilderAccess.RunAndCollect);

        ModuleBuilder module = assembly.DefineDynamicModule("dream");
        TypeBuilder builder = module.DefineType(
            "Elysia.Mirror.Generated.DreamPlugin",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            new[] { typeof(IDreamPlugin) });

        builder.DefineDefaultConstructor(
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);

        EmitStringGetter(builder, "get_Name", pluginName);
        EmitStringGetter(builder, "get_Motto", motto);
        EmitTransform(builder, multiplier, offset);
        EmitScore(builder);

        return builder.CreateType()!;
    }

    private static void EmitStringGetter(TypeBuilder builder, string methodName, string literal)
    {
        MethodBuilder method = builder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(string),
            Type.EmptyTypes);

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, literal);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(method, typeof(IDreamPlugin).GetMethod(methodName)!);
    }

    private static void EmitTransform(TypeBuilder builder, int multiplier, int offset)
    {
        MethodBuilder method = builder.DefineMethod(
            nameof(IDreamPlugin.Transform),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(int),
            new[] { typeof(int) });

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, multiplier);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I4, offset);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(method, typeof(IDreamPlugin).GetMethod(nameof(IDreamPlugin.Transform))!);
    }

    private static void EmitScore(TypeBuilder builder)
    {
        MethodBuilder method = builder.DefineMethod(
            nameof(IDreamPlugin.Score),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(double),
            new[] { typeof(double) });

        MethodInfo clamp = typeof(Math).GetMethod(
            nameof(Math.Clamp),
            new[] { typeof(double), typeof(double), typeof(double) })!;

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ldc_R8, 1.0);
        il.Emit(OpCodes.Call, clamp);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(method, typeof(IDreamPlugin).GetMethod(nameof(IDreamPlugin.Score))!);
    }

    /// <summary>
    /// Generates a plugin, calls it, drops every reference to it and then proves it was collected.
    /// </summary>
    /// <param name="name">Name for the generated plugin.</param>
    /// <param name="motto">Motto for the generated plugin.</param>
    /// <param name="multiplier">Constant baked into the generated arithmetic.</param>
    /// <param name="offset">Constant added by the generated arithmetic.</param>
    /// <returns>A receipt describing exactly what happened.</returns>
    /// <remarks>
    /// The proof is deliberately split across two frames. <see cref="EmitAndAbandon"/> creates the
    /// code module and returns; only then, in <see cref="EmitUseAndRetire"/>, is the collection
    /// forced. Doing it in one frame would leave the creating frame's locals alive as GC roots and
    /// the "collected" flag would be a lie — which is exactly the class of mistake that makes
    /// hot-reload implementations leak in every language.
    /// </remarks>
    public static DreamEmitReceipt EmitUseAndRetire(string name, string motto, int multiplier, int offset)
    {
        (DreamEmitReceipt staged, WeakReference watch) = EmitAndAbandon(name, motto, multiplier, offset);
        int attempts = Collect(watch);
        return staged with { Collected = !watch.IsAlive, CollectionAttempts = attempts };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DreamEmitReceipt Receipt, WeakReference Watch) EmitAndAbandon(
        string name, string motto, int multiplier, int offset)
    {
        Type generated = EmitPluginType(name, motto, multiplier, offset);
        WeakReference generatedWatch = new(generated);

        IDreamPlugin plugin = (IDreamPlugin)Activator.CreateInstance(generated)!;
        string reportedName = plugin.Name;
        string reportedMotto = plugin.Motto;
        int sample = plugin.Transform(21);
        double score = plugin.Score(3.5);

        // Burn every strong reference we hold, reflection handles included. This frame is about to
        // return, taking whatever is left of them with it.
        plugin = null!;
        generated = null!;

        return (new DreamEmitReceipt(reportedName, reportedMotto, sample, score, false, 0), generatedWatch);
    }

    /// <summary>
    /// Loads a real plugin assembly from disk into a collectible context, uses it, unloads the
    /// context, and proves the context was reclaimed.
    /// </summary>
    /// <param name="pluginPath">Path to a compiled plugin assembly.</param>
    /// <returns>A receipt, or a receipt with an explanatory message when the plugin is absent.</returns>
    public static DreamDiskReceipt LoadUseAndUnload(string pluginPath)
    {
        if (!File.Exists(pluginPath))
        {
            return new DreamDiskReceipt(
                Path.GetFileName(pluginPath), string.Empty, 0, 0.0, false, 0,
                $"not staged ({pluginPath} does not exist)");
        }

        (DreamDiskReceipt staged, WeakReference watch) = LoadAndAbandon(pluginPath);
        int attempts = Collect(watch);
        bool unloaded = !watch.IsAlive;

        return staged with
        {
            Unloaded = unloaded,
            CollectionAttempts = attempts,
            Message = unloaded
                ? $"unloaded after {attempts} forced collection(s) — VERIFIED"
                : $"still alive after {attempts} forced collection(s) — NOT reclaimed",
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DreamDiskReceipt Receipt, WeakReference Watch) LoadAndAbandon(string pluginPath)
    {
        DreamPluginHost host = new("elysia.disk-plugins");
        WeakReference hostWatch = new(host);

        IDreamPlugin plugin = host.LoadPluginFromDisk(pluginPath);
        string name = plugin.Name;
        string motto = plugin.Motto;
        int transformed = plugin.Transform(21);
        double score = plugin.Score(0.25);

        // The context must stop being reachable for the unload to mean anything at all.
        plugin = null!;
        host.Unload();
        host = null!;

        return (new DreamDiskReceipt(name, motto, transformed, score, false, 0, string.Empty), hostWatch);
    }

    private static int Collect(WeakReference watch)
    {
        int attempts = 0;
        for (; attempts < 16 && watch.IsAlive; attempts++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        return attempts;
    }
}

/// <summary>What one emit → invoke → collect cycle produced.</summary>
/// <param name="Name">Name reported by the generated plugin.</param>
/// <param name="Motto">Motto reported by the generated plugin.</param>
/// <param name="Sample">Result of one <see cref="IDreamPlugin.Transform"/> call.</param>
/// <param name="Score">Result of one <see cref="IDreamPlugin.Score"/> call.</param>
/// <param name="Collected">Whether the generated code module was actually reclaimed.</param>
/// <param name="CollectionAttempts">How many forced collections the proof needed.</param>
public readonly record struct DreamEmitReceipt(
    string Name,
    string Motto,
    int Sample,
    double Score,
    bool Collected,
    int CollectionAttempts);

/// <summary>What one load → invoke → unload cycle produced.</summary>
/// <param name="Name">Name reported by the plugin.</param>
/// <param name="Motto">Motto reported by the plugin.</param>
/// <param name="Sample">Result of one <see cref="IDreamPlugin.Transform"/> call.</param>
/// <param name="Score">Result of one <see cref="IDreamPlugin.Score"/> call.</param>
/// <param name="Unloaded">Whether the collectible context was reclaimed.</param>
/// <param name="CollectionAttempts">How many forced collections the proof needed.</param>
/// <param name="Message">Human-readable verdict, including the reason when not staged.</param>
public readonly record struct DreamDiskReceipt(
    string Name,
    string Motto,
    int Sample,
    double Score,
    bool Unloaded,
    int CollectionAttempts,
    string Message);

/// <summary>Metadata-driven plugin discovery.</summary>
/// <remarks>
/// The second half of the trick: a plugin may also arrive as <i>data</i> — an attribute on a
/// compiled type — and be found by scanning. Rust has no equivalent (attributes are compile-time
/// only and vanish from the binary), and Java's equivalent is a classpath scan whose cost and
/// classloader lifetime are the reason every JVM plugin framework eventually grows a leak
/// detector.
/// </remarks>
public static class PluginCatalog
{
    /// <summary>Scans assemblies for types marked with <see cref="DreamPluginAttribute"/>.</summary>
    /// <param name="assemblies">Assemblies to scan. Null entries are skipped.</param>
    /// <returns>Discovered plugins ordered by descending weight, then by name.</returns>
    public static IReadOnlyList<(Type Type, IDreamPlugin Plugin, int Weight)> Discover(params Assembly?[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        List<(Type, IDreamPlugin, int)> found = new();
        foreach (Assembly? assembly in assemblies)
        {
            if (assembly is null)
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(static t => t is not null).Cast<Type>().ToArray();
            }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IDreamPlugin).IsAssignableFrom(type))
                {
                    continue;
                }

                DreamPluginAttribute? attribute = type.GetCustomAttribute<DreamPluginAttribute>();
                if (attribute is null || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not IDreamPlugin plugin)
                {
                    continue;
                }

                found.Add((type, plugin, attribute.Weight));
            }
        }

        return found
            .OrderByDescending(static entry => entry.Item3)
            .ThenBy(static entry => entry.Item2.Name, StringComparer.Ordinal)
            .Select(static entry => (entry.Item1, entry.Item2, entry.Item3))
            .ToArray();
    }

    /// <summary>Ranks plugins by applying each one to a fixed workload.</summary>
    /// <param name="plugins">The plugins to rank.</param>
    /// <param name="workload">Input values.</param>
    /// <returns>Name, weight, summed output and mean score per plugin.</returns>
    public static IReadOnlyList<(string Name, int Weight, long Total, double Mean)> Rank(
        IReadOnlyList<(Type Type, IDreamPlugin Plugin, int Weight)> plugins,
        ReadOnlySpan<int> workload)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        List<(string, int, long, double)> ranked = new(plugins.Count);
        foreach ((Type _, IDreamPlugin plugin, int weight) in plugins)
        {
            long total = 0;
            double scoreSum = 0;
            for (int i = 0; i < workload.Length; i++)
            {
                total += plugin.Transform(workload[i]);
                scoreSum += plugin.Score(Math.Min(workload[i] / 100.0, 1.0));
            }

            ranked.Add((plugin.Name, weight, total, workload.Length == 0 ? 0 : scoreSum / workload.Length));
        }

        return ranked
            .OrderByDescending(static entry => entry.Item2)
            .ThenBy(static entry => entry.Item1, StringComparer.Ordinal)
            .Select(static entry => (entry.Item1, entry.Item2, entry.Item3, entry.Item4))
            .ToArray();
    }
}
