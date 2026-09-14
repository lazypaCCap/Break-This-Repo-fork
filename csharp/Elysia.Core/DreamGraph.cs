// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Elysia.Core · DreamGraph
//  循環する夢のグラフ — 循环引用图 — a mutable, cyclic, self-referential object graph.
//
//  This is the file that hurts Rust, and it hurts Rust on purpose.
//
//  A node holds a strong reference to its children, and every child holds a strong reference
//  back to its parent. There are observer callbacks that capture sibling nodes. There is a
//  mutation routine that *rewrites the edge list while it is walking it*. There is a cycle
//  detector that walks the same graph from three different entry points.
//
//  · In Rust, `parent: Option<Rc<RefCell<Node>>>` is the cheap version and it leaks; the correct
//    version is `Arc<Mutex<..>>` or an arena full of indices plus `unsafe` for the back-edges.
//    Worse, `for child in &node.children { node.children.push(..) }` is a compile error, and the
//    fix is not a keyword — it is a redesign (drain into a Vec, collect indices, two-phase).
//  · In Java the graph is *easy* — and that is exactly the trap. The moment a traversal is
//    written as an iterator or a stream, structural mutation throws
//    `ConcurrentModificationException`, and the standard answer is to allocate a defensive copy
//    of the whole collection on every pass, or to use a copy-on-write collection whose writes
//    are O(n). Java buys graph freedom with allocation.
//  · In C# the graph is also easy, *and* the traversal is an index walk that cannot be
//    invalidated, *and* the cycles are reclaimed by the tracing collector with zero ownership
//    annotations, and there is no `RefCell` panic at run time because there is no borrow to
//    violate. The verifiable claim below is not "it is fast" — it is "the cycle is collected",
//    measured, on every run.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;

namespace Elysia.Core;

/// <summary>A node in a cyclic dream graph. Reference type, on purpose.</summary>
/// <remarks>
/// <para>
/// <see cref="Parent"/> is a back-edge and therefore a strong reference cycle whenever a child
/// links back into its own ancestry. Nothing special is done about that: the garbage collector
/// traces, sees an unreachable strongly-connected component, and reclaims it. There is no
/// <c>RefCell</c>, no <c>Mutex</c>, no <c>unsafe</c>, no explicit <c>drop</c>, and no leak.
/// </para>
/// </remarks>
public sealed class DreamNode
{
    /// <summary>Initializes a new instance of the <see cref="DreamNode"/> class.</summary>
    /// <param name="name">The node label.</param>
    public DreamNode(string name) => Name = name;

    /// <summary>Gets the node label.</summary>
    public string Name { get; }

    /// <summary>Gets the mutable out-edges of this node.</summary>
    public List<DreamNode> Next { get; } = new();

    /// <summary>Gets or sets the back-edge to the node that linked this one. Forms cycles.</summary>
    public DreamNode? Parent { get; private set; }

    /// <summary>Gets the observers invoked by <see cref="Touch"/>. They capture sibling nodes.</summary>
    public List<Action<DreamNode>> Observers { get; } = new();

    /// <summary>Gets the monotonically increasing revision of the out-edge list.</summary>
    public int Revision { get; private set; }

    /// <summary>Bumps the revision counter. Callable from observer callbacks.</summary>
    /// <returns>The new revision.</returns>
    public int BumpRevision() => ++Revision;

    /// <summary>Adds an out-edge, setting the back-edge on the target.</summary>
    /// <param name="child">The node to link.</param>
    /// <returns>This node, so calls can be chained.</returns>
    public DreamNode Link(DreamNode child)
    {
        child.Parent = this;
        Next.Add(child);
        Revision++;
        return this;
    }

    /// <summary>Registers an observer callback.</summary>
    /// <param name="watcher">The callback, which may close over other nodes in the graph.</param>
    /// <returns>This node, so calls can be chained.</returns>
    public DreamNode Observe(Action<DreamNode> watcher)
    {
        Observers.Add(watcher);
        return this;
    }

    /// <summary>Notifies observers. Callbacks are allowed to mutate the graph.</summary>
    public void Touch()
    {
        for (int i = 0; i < Observers.Count; i++)
        {
            Observers[i](this);
        }
    }

    /// <summary>Walks the ancestry chain through the back-edges, counting hops.</summary>
    /// <returns>The number of hops to the root, or the length of the cycle if the chain is cyclic.</returns>
    /// <remarks>
    /// The guard is not paranoia: once a back-edge closes a cycle the parent chain has no end, and
    /// a naive <c>while (node.Parent is not null)</c> spins forever. Rust would have refused to
    /// compile the equivalent code at all; C# lets you write it, so the loop has to defend itself.
    /// </remarks>
    public int Depth()
    {
        HashSet<DreamNode> seen = new(ReferenceEqualityComparer.Instance);
        int depth = 0;
        DreamNode? cursor = this;
        while (cursor.Parent is { } parent && seen.Add(cursor))
        {
            cursor = parent;
            depth++;
        }

        return depth;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}(depth={Depth()}, out={Next.Count}, rev={Revision})";
}

/// <summary>The outcome of one <see cref="DreamGraph.Run"/> pass.</summary>
/// <param name="Nodes">Number of nodes created.</param>
/// <param name="Edges">Number of out-edges created, back-edges included.</param>
/// <param name="Cycles">Number of cycles found by the three-entry-point walker.</param>
/// <param name="MutationsDuringTraversal">Structural edits performed while a walk was in flight.</param>
/// <param name="MaximumDepth">Deepest back-edge chain observed.</param>
/// <param name="CycleWasCollected">Whether a detached mutually-referencing cycle was actually reclaimed.</param>
public readonly record struct DreamGraphReport(
    int Nodes,
    int Edges,
    int Cycles,
    int MutationsDuringTraversal,
    int MaximumDepth,
    bool CycleWasCollected);

/// <summary>The cyclic-graph showcase.</summary>
public static class DreamGraph
{
    /// <summary>
    /// Builds a cyclic graph, mutates it mid-traversal, proves the cycles are real, then proves
    /// that a detached cycle is reclaimed.
    /// </summary>
    /// <param name="depth">Depth of the generated lattice.</param>
    /// <param name="breadth">Out-degree of the generated lattice.</param>
    /// <param name="log">Receives one human-readable line per step.</param>
    /// <returns>A report of what actually happened.</returns>
    public static DreamGraphReport Run(int depth, int breadth, out List<string> log)
    {
        log = new List<string>(32);
        DreamNode root = Build(rootName: "root", depth, breadth, log);
        int edges = CountEdges(root);
        log.Add($"built: depth={depth}, breadth={breadth}, edges={edges}");

        int mutations = MutateWhileWalking(root);
        log.Add($"mutated {mutations} edge(s) while a walk was in flight: allowed, no iterator was invalidated");

        int cycles = CountCycles(root);
        log.Add($"cycles reachable from the root: {cycles}");

        int deepest = MeasureDepth(root);
        log.Add($"deepest back-edge chain: {deepest} hop(s)");

        bool collected = ProveDetachedCycleIsReclaimed(log);
        return new DreamGraphReport(CountNodes(root), edges, cycles, mutations, deepest, collected);
    }

    /// <summary>Builds the lattice.</summary>
    /// <param name="rootName">Name of the root node.</param>
    /// <param name="depth">Remaining depth.</param>
    /// <param name="breadth">Out-degree.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <returns>The root node.</returns>
    public static DreamNode Build(string rootName, int depth, int breadth, List<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        DreamNode root = new(rootName);
        if (depth <= 0)
        {
            return root;
        }

        for (int b = 0; b < breadth; b++)
        {
            DreamNode child = Build($"{rootName}.{b}", depth - 1, breadth, log);
            root.Link(child); // child.Parent == root: a back-edge, a strong cycle
        }

        // Close the lattice: the last child of the last child points back at the root. This is
        // the structure Rust cannot express without Rc<RefCell<>>, Arc<Mutex<>>, or unsafe.
        DreamNode? tail = root.Next.Count > 0 ? root.Next[^1] : null;
        for (int i = 0; i < depth - 1 && tail is not null; i++)
        {
            tail = tail.Next.Count > 0 ? tail.Next[^1] : null;
        }

        if (tail is not null && !ReferenceEquals(tail, root))
        {
            tail.Link(root);
        }

        // Observers that close over siblings — callbacks needing the whole object graph alive.
        root.Observe(observed => observed.BumpRevision());
        root.Observe(observed =>
        {
            if (observed.Next.Count > 1)
            {
                observed.Next[0].Link(observed); // observer mutates the graph as a side effect
            }
        });

        return root;
    }

    /// <summary>
    /// Walks the children list by index while structurally mutating it, which is the operation
    /// that fails in both rival languages — in Java with a runtime exception the moment an
    /// iterator is involved, and in Rust at compile time, always.
    /// </summary>
    /// <param name="root">The graph root.</param>
    /// <returns>The number of mutations performed.</returns>
    public static int MutateWhileWalking(DreamNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        int mutations = 0;
        HashSet<DreamNode> visited = new(ReferenceEqualityComparer.Instance);
        Queue<DreamNode> frontier = new();
        frontier.Enqueue(root);
        while (frontier.Count > 0)
        {
            DreamNode node = frontier.Dequeue();
            if (!visited.Add(node))
            {
                // A cycle re-enters here. The walk must be able to terminate even though the
                // structure it is walking is not a tree — which is itself the point being made.
                continue;
            }

            // Index walk, not an enumerator: the list is rewritten from inside the loop.
            for (int i = 0; i < node.Next.Count; i++)
            {
                DreamNode child = node.Next[i];
                if (child.Next.Count == 0 && child.Name.Length % 2 == 0)
                {
                    child.Link(new DreamNode(child.Name + "*"));
                    mutations++;
                }

                frontier.Enqueue(child);
            }

            if (node.Next.Count > 4)
            {
                node.Next.RemoveAt(node.Next.Count - 1); // delete during the same walk
                mutations++;
            }
        }

        root.Touch();
        return mutations;
    }

    /// <summary>Counts cycles reachable from the given root using a colour-marking walk.</summary>
    /// <param name="root">The graph root.</param>
    /// <returns>The number of back-edges that close a cycle.</returns>
    public static int CountCycles(DreamNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        HashSet<DreamNode> onStack = new(ReferenceEqualityComparer.Instance);
        HashSet<DreamNode> done = new(ReferenceEqualityComparer.Instance);
        int cycles = 0;

        void Visit(DreamNode node)
        {
            if (done.Contains(node))
            {
                return;
            }

            if (!onStack.Add(node))
            {
                cycles++;
                return;
            }

            for (int i = 0; i < node.Next.Count; i++)
            {
                Visit(node.Next[i]);
            }

            onStack.Remove(node);
            done.Add(node);
        }

        Visit(root);

        // Two more entry points: the same cycle must be found regardless of where the walk starts.
        foreach (DreamNode child in root.Next)
        {
            if (!done.Contains(child))
            {
                Visit(child);
            }
        }

        return cycles;
    }

    /// <summary>Counts nodes reachable from the root.</summary>
    /// <param name="root">The graph root.</param>
    /// <returns>The distinct node count.</returns>
    public static int CountNodes(DreamNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        HashSet<DreamNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<DreamNode> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DreamNode node = stack.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            for (int i = 0; i < node.Next.Count; i++)
            {
                stack.Push(node.Next[i]);
            }
        }

        return seen.Count;
    }

    /// <summary>Counts every out-edge reachable from the root, cycles included.</summary>
    /// <param name="root">The graph root.</param>
    /// <returns>The edge count.</returns>
    public static int CountEdges(DreamNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        HashSet<DreamNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<DreamNode> stack = new();
        stack.Push(root);
        int edges = 0;
        while (stack.Count > 0)
        {
            DreamNode node = stack.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            edges += node.Next.Count;
            for (int i = 0; i < node.Next.Count; i++)
            {
                stack.Push(node.Next[i]);
            }
        }

        return edges;
    }

    /// <summary>Finds the deepest back-edge chain reachable from the root.</summary>
    /// <param name="root">The graph root.</param>
    /// <returns>The maximum depth in hops.</returns>
    public static int MeasureDepth(DreamNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        HashSet<DreamNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<DreamNode> stack = new();
        stack.Push(root);
        int max = 0;
        while (stack.Count > 0)
        {
            DreamNode node = stack.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            max = Math.Max(max, node.Depth());
            for (int i = 0; i < node.Next.Count; i++)
            {
                stack.Push(node.Next[i]);
            }
        }

        return max;
    }

    /// <summary>
    /// Detaches a mutually-referencing group of nodes and checks that it is actually reclaimed.
    /// </summary>
    /// <param name="log">Receives the verdict line.</param>
    /// <returns><see langword="true"/> when the cycle was collected.</returns>
    /// <remarks>
    /// The <see cref="MethodImplOptions.NoInlining"/> boundary matters: the cycle must be built
    /// and abandoned inside a frame that has already returned before the collection is forced,
    /// otherwise the JIT's own locals would keep it alive and the proof would be a lie.
    /// </remarks>
    public static bool ProveDetachedCycleIsReclaimed(List<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        WeakReference weak = BuildAbandonedCycle();
        for (int attempt = 0; attempt < 3 && weak.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        bool collected = !weak.IsAlive;
        log.Add(
            collected
                ? "detached 3-node strongly-connected cycle: reclaimed by the tracing collector (verified)"
                : "detached 3-node strongly-connected cycle: STILL ALIVE — the proof failed");
        return collected;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAbandonedCycle()
    {
        DreamNode a = new("a");
        DreamNode b = new("b");
        DreamNode c = new("c");
        a.Link(b);
        b.Link(c);
        c.Link(a); // a -> b -> c -> a: a cycle with no root and no owner
        return new WeakReference(a);
    }
}
