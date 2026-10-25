#nullable enable
using System.Collections.Generic;
using T3.Core.DataTypes;
using T3.Core.Operator;

namespace T3.Core.Output;

/// <summary>
/// A graph node that supplies a laser frame to the output setup — the laser counterpart of
/// <see cref="IContentSupplier"/>. The node registers itself with <see cref="LaserContentRegistry"/>
/// so the Output Setup can list it as content; routing lives on the node's own OutputRef input
/// (it names the laser output it feeds), not on slices.
/// </summary>
public interface ILaserContentSupplier
{
    /// <summary>The current laser frame (ILDA points), or null when nothing is produced.</summary>
    StructuredList? GetLaserPoints(EvaluationContext context);
}

/// <summary>
/// The set of live <see cref="ILaserContentSupplier"/> instances. Suppliers add themselves on
/// construction and remove themselves on dispose, so the registry survives operator hot-reloads.
/// Thread-safe: membership changes from any thread (operator construction can run off the UI
/// thread), readers always get an immutable snapshot - enumeration can never throw mid-change.
/// Insertion order is preserved.
/// </summary>
public static class LaserContentRegistry
{
    /// <summary>Bumped whenever membership changes, so hosts can skip work only invalidated by
    /// a laser camera appearing or disappearing.</summary>
    public static int Version { get; private set; }

    public static void Register(ILaserContentSupplier supplier)
    {
        lock (_lock)
        {
            if (_suppliers.Contains(supplier))
                return;

            _suppliers.Add(supplier);
            Version++;
            PublishSnapshot();
        }
    }

    public static void Unregister(ILaserContentSupplier supplier)
    {
        lock (_lock)
        {
            if (!_suppliers.Remove(supplier))
                return;

            Version++;
            PublishSnapshot();
        }
    }

    /// <summary>The current members as an immutable snapshot.</summary>
    public static IReadOnlyList<ILaserContentSupplier> Suppliers => System.Threading.Volatile.Read(ref _snapshot);

    private static void PublishSnapshot()
    {
        // Called under _lock only. The volatile publish makes the new array visible to
        // lock-free readers in one piece.
        System.Threading.Volatile.Write(ref _snapshot, _suppliers.ToArray());
    }

    private static readonly object _lock = new();
    private static readonly List<ILaserContentSupplier> _suppliers = [];
    private static ILaserContentSupplier[] _snapshot = [];
}
